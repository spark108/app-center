using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Spark108.Core;

namespace Spark108.Desktop;

internal sealed class PackageManager(ApiClient api)
{
    public static string ClientVersion =>
        typeof(PackageManager).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static bool CanUpdateClient =>
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(
            LocalPackages.Client, StringComparison.OrdinalIgnoreCase) &&
        File.Exists(Path.Combine(LocalPackages.Client, PackageRules.UpdaterExe));

    public static Dictionary<string, InstalledPackage> Installed()
    {
        Directory.CreateDirectory(LocalPackages.Apps);
        foreach (var backup in Directory.EnumerateDirectories(LocalPackages.Apps, ".*.previous"))
        {
            var directory = Path.GetFileName(backup);
            var id = directory[1..^".previous".Length];
            if (!PackageRules.ValidId(id))
                continue;
            var target = AppDirectory(id);
            if (!Directory.Exists(target))
                Directory.Move(backup, target);
            else if (TryRead(target) is { } current && current.Id == id)
            {
                if (current.Pending)
                {
                    DiscardPending(target);
                    Directory.Move(backup, target);
                }
                else
                    TryDelete(backup);
            }
        }
        var installed = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(LocalPackages.Apps))
        {
            var id = Path.GetFileName(directory);
            if (!PackageRules.ValidId(id))
                continue;
            try
            {
                if (LocalPackages.Read(directory) is not { } record || record.Id != id)
                    continue;
                if (record.Pending)
                {
                    DiscardPending(directory);
                    continue;
                }
                if (record.UninstallKey is null || PackageRegistration.FindNative(record.UninstallKey) is not null)
                {
                    installed.Add(id, record);
                    PackageRegistration.Sync(record);
                }
            }
            catch (JsonException) { }
        }
        var client = CanUpdateClient ? TryRead(LocalPackages.Client) : null;
        installed[PackageRules.AppCenterId] = new InstalledPackage(PackageRules.AppCenterId, "Центр приложений",
            "Клиент каталога приложений.", PackageKind.AppCenter, ClientVersion, null, client?.Dependencies ?? []);
        return installed;
    }

    private static InstalledPackage? TryRead(string directory)
    {
        try { return LocalPackages.Read(directory); }
        catch (JsonException) { return null; }
    }

    private static void DiscardPending(string directory)
    {
        Directory.CreateDirectory(LocalPackages.Staging);
        var temporary = Path.Combine(LocalPackages.Staging, Guid.NewGuid().ToString("N"));
        Directory.Move(directory, temporary);
        TryDelete(temporary);
    }

    public static void CleanupStaging()
    {
        if (!Directory.Exists(LocalPackages.Staging))
            return;
        foreach (var directory in Directory.EnumerateDirectories(LocalPackages.Staging))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                TryDelete(directory);
    }

    public async Task<bool> Install(string id, ReleaseInfo target, Action<string, int> report, CancellationToken ct)
    {
        var items = await api.Plan(id, target.Id, ct);
        if (items.Length == 0 || items[^1].App.Id != id || items[^1].Release.Id != target.Id)
            throw new InvalidDataException("Сервер вернул некорректный план установки.");

        var installed = Installed();
        var required = items.Where(item => !installed.TryGetValue(item.App.Id, out var existing) ||
            Version.Parse(item.Release.Version) > Version.Parse(existing.Version) ||
            (item.App.Id == id && item.App.Kind != PackageKind.AppCenter &&
                (item.Release.Version != existing.Version || !EntryPointPresent(existing) ||
                    !item.App.Dependencies.ToHashSet().SetEquals(existing.Dependencies)))).ToArray();
        var selfUpdate = required.Any(x => x.App.Kind == PackageKind.AppCenter);
        if (selfUpdate && !CanUpdateClient)
            throw new InvalidOperationException("Установите клиент с главной страницы Центра приложений, чтобы включить самообновление.");
        if (required.Length == 0)
            return false;

        var operation = Guid.NewGuid().ToString("N");
        var stage = Directory.CreateDirectory(Path.Combine(LocalPackages.Staging, operation)).FullName;
        var prepared = new List<(InstallItem Item, string Folder)>();
        var installedNow = new Stack<(string Target, string Backup, string Staged)>();
        var handedOff = false;
        try
        {
            for (var index = 0; index < required.Length; index++)
            {
                var item = required[index];
                var component = item.App.Id;
                if (!PackageRules.ValidId(component) || component != item.Release.AppId ||
                    !PackageRules.ValidVersion(item.Release.Version) ||
                    (item.App.Kind == PackageKind.AppCenter) != (component == PackageRules.AppCenterId))
                    throw new InvalidDataException("План содержит недопустимый пакет.");
                var from = index * 100 / required.Length;
                var to = (index + 1) * 100 / required.Length;
                var source = item.App.Kind == PackageKind.AppCenter ? LocalPackages.Client : AppDirectory(component);
                var folder = Directory.CreateDirectory(item.App.Kind == PackageKind.AppCenter
                    ? Path.Combine(stage, "next") : Path.Combine(stage, "packages", component)).FullName;
                report($"Подготовка файлов · {item.App.Name}", from);
                if (!installed.ContainsKey(component) || !Directory.Exists(source) ||
                    !await PackageUpdates.Prepare(api, item, source, folder,
                        value => report($"Обновление файлов · {item.App.Name}", from + (to - from) * value / 100), ct))
                {
                    var zip = Path.Combine(stage, component + ".zip");
                    await api.Download(item.Release, zip,
                        new Progress<int>(value => report($"Скачивание · {item.App.Name}", from + (to - from) * value / 100)), ct);
                    report($"Распаковка · {item.App.Name}", from);
                    await Task.Run(() => Extract(zip, folder, item, ct), ct);
                    File.Delete(zip);
                }
                LocalPackages.Write(folder, new InstalledPackage(component, item.App.Name, item.App.Description,
                    item.App.Kind, item.Release.Version, item.Release.EntryPoint, item.App.Dependencies,
                    item.Release.Installer, Pending: item.Release.Installer is not null));
                prepared.Add((item, folder));
            }
            report("Применение изменений", 100);
            ct.ThrowIfCancellationRequested();
            foreach (var (item, folder) in prepared.Where(x => x.Item.App.Kind != PackageKind.AppCenter))
            {
                ct.ThrowIfCancellationRequested();
                var destination = AppDirectory(item.App.Id);
                CheckNotRunning(destination);
                if (installed.TryGetValue(item.App.Id, out var prior))
                    CheckNativeNotRunning(prior);
                var backup = BackupDirectory(item.App.Id);
                if (Directory.Exists(destination) && LocalPackages.Read(destination)?.Id != item.App.Id)
                    throw new InvalidOperationException("Папка назначения уже занята другой установкой.");
                if (Directory.Exists(destination))
                    Directory.Move(destination, backup);
                try { Directory.Move(folder, destination); }
                catch
                {
                    if (Directory.Exists(backup))
                        Directory.Move(backup, destination);
                    throw;
                }
                installedNow.Push((destination, backup, folder));
                if (item.Release.Installer is { } setup)
                {
                    report($"Установщик · {item.App.Name}", 100);
                    var key = await NativeSetup.Install(item.App.Id, destination, setup);
                    var record = LocalPackages.Read(destination)! with { UninstallKey = key, Pending = false };
                    LocalPackages.Write(destination, record);
                    Confirm(installedNow);
                }
            }
            if (selfUpdate)
            {
                var helper = Path.Combine(stage, PackageRules.UpdaterExe);
                File.Copy(Path.Combine(LocalPackages.Client, PackageRules.UpdaterExe), helper);
                var start = new ProcessStartInfo(helper) { WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                start.ArgumentList.Add(operation);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить обновление клиента.");
                handedOff = true;
            }
            Confirm(installedNow);
            return handedOff;
        }
        catch
        {
            while (installedNow.TryPop(out var applied))
            {
                Directory.Move(applied.Target, applied.Staged);
                if (Directory.Exists(applied.Backup))
                    Directory.Move(applied.Backup, applied.Target);
            }
            throw;
        }
        finally
        {
            if (!handedOff)
                TryDelete(stage);
        }
    }

    private static void Confirm(Stack<(string Target, string Backup, string Staged)> installed)
    {
        while (installed.TryPop(out var applied))
        {
            TryDelete(applied.Backup);
            if (TryRead(applied.Target) is { Pending: false } record)
                PackageRegistration.Sync(record);
        }
    }

    private static void Extract(string file, string destination, InstallItem item, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(file);
        PackageRules.CheckArchive(zip, item.Release.EntryPoint,
            item.App.Kind == PackageKind.AppCenter ? item.Release.Version : null,
            item.Release.Installer is { } setup ? [setup.Install.Path] : null);
        var buffer = new byte[128 * 1024];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = Path.Combine(destination, entry.FullName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory ? path : Path.GetDirectoryName(path)!);
            if (directory) continue;
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew);
            var length = entry.Length;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                length -= read;
                if (length < 0)
                    throw new InvalidDataException("Распакованный файл превышает заявленный размер.");
                output.Write(buffer, 0, read);
            }
            if (length != 0)
                throw new InvalidDataException("ZIP содержит неполный файл.");
        }
    }

    public static async Task Remove(InstalledPackage package)
    {
        if (package.Kind == PackageKind.AppCenter || package.Id == PackageRules.AppCenterId)
            throw new InvalidOperationException("Клиент удаляется средствами первоначальной установки.");
        var dependent = Installed().Values.FirstOrDefault(x => x.Dependencies.Contains(package.Id));
        if (dependent is not null)
            throw new InvalidOperationException($"Компонент нужен приложению «{dependent.Name}».");
        var folder = AppDirectory(package.Id);
        CheckNotRunning(folder);
        CheckNativeNotRunning(package);
        Directory.CreateDirectory(LocalPackages.Staging);
        await NativeSetup.Uninstall(package, folder);
        if (package.Installer is not null && Directory.Exists(NativeSetup.InstallDirectory(package.Id)))
            DiscardPending(NativeSetup.InstallDirectory(package.Id));
        var trash = Path.Combine(LocalPackages.Staging, Guid.NewGuid().ToString("N"));
        Directory.Move(folder, trash);
        PackageRegistration.Remove(package.Id);
        TryDelete(trash);
    }

    public static void Launch(InstalledPackage package)
    {
        if (!CanLaunch(package))
            throw new InvalidOperationException("Файл запуска отсутствует. Переустановите приложение.");
        var exe = EntryPoint(package)!;
        var known = Installed();
        var pending = new Stack<string>(package.Dependencies);
        var visited = new HashSet<string>();
        while (pending.TryPop(out var dependency))
        {
            if (!visited.Add(dependency))
                continue;
            if (!known.TryGetValue(dependency, out var component))
                throw new InvalidOperationException($"Требуемый компонент «{dependency}» отсутствует. Восстановите установку.");
            foreach (var child in component.Dependencies)
                pending.Push(child);
        }
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        start.Environment["SPARK108_PACKAGES_DIR"] = LocalPackages.Apps;
        Process.Start(start)?.Dispose();
    }

    public static bool EntryPointPresent(InstalledPackage package) =>
        package.Kind != PackageKind.Application || package.Installer is { Launch: null } || CanLaunch(package);

    public static bool CanLaunch(InstalledPackage package) => package.Kind == PackageKind.Application &&
        EntryPoint(package) is { } exe && File.Exists(exe);

    private static string? EntryPoint(InstalledPackage package)
    {
        if ((package.Installer?.Launch ?? package.EntryPoint) is not { } entry ||
            !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        try { return NativeSetup.Resolve(entry, package.Id, AppDirectory(package.Id)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static void CheckNativeNotRunning(InstalledPackage package)
    {
        if (package.Installer is null)
            return;
        CheckNotRunning(NativeSetup.InstallDirectory(package.Id));
        if (EntryPoint(package) is { } path && File.Exists(path))
            CheckNotRunning(path, file: true);
    }

    private static string AppDirectory(string id)
    {
        if (!PackageRules.ValidId(id))
            throw new InvalidDataException("Недопустимый идентификатор приложения.");
        return Path.Combine(LocalPackages.Apps, id);
    }

    private static string BackupDirectory(string id) => Path.Combine(LocalPackages.Apps, $".{id}.previous");

    private static void CheckNotRunning(string folder, bool file = false)
    {
        if (!file && !Directory.Exists(folder))
            return;
        var prefix = folder + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? exe = null;
                try { exe = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                if (file ? folder.Equals(exe, StringComparison.OrdinalIgnoreCase) : exe?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException("Закройте приложение перед обновлением или удалением.");
            }
        }
    }

    public static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
