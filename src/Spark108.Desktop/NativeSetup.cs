using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Spark108.Core;

namespace Spark108.Desktop;

internal static class NativeSetup
{
    public static string InstallDirectory(string id)
    {
        if (!PackageRules.ValidId(id))
            throw new InvalidDataException("Недопустимый идентификатор пакета.");
        return Path.Combine(LocalPackages.Native, id);
    }

    public static async Task<string?> Install(string id, string folder, InstallerSettings setup)
    {
        Directory.CreateDirectory(InstallDirectory(id));
        var command = PackagePath(folder, setup.Install.Path);
        var key = setup.RegistryKey ?? (IsMsi(command) ? ProductCode(command) : null);
        await RunCommand(setup.Install, id, folder, removing: false, insidePackage: true);
        return key is not null && PackageRegistration.FindNative(key) is not null ? key : null;
    }

    public static async Task Uninstall(InstalledPackage package, string folder)
    {
        if (package.Installer is not { } setup)
            return;
        if (setup.Uninstall is { } command)
        {
            await RunCommand(command, package.Id, folder, removing: true, insidePackage: false);
            return;
        }
        var registered = package.UninstallKey is { } key ? PackageRegistration.FindNative(key) : null;
        if (registered is { WindowsInstaller: true } && Guid.TryParse(package.UninstallKey, out var product))
        {
            await Run(MsiExecutable, ["/x", product.ToString("B"), "/norestart"], folder, false, true, true);
            return;
        }
        if (registered?.Command is { Length: > 0 } nativeCommand)
        {
            var args = ParseRegistered(nativeCommand);
            await Run(args[0], args[1..], folder, false, false, true, shell: true);
            return;
        }
        if (IsMsi(setup.Install.Path) && package.UninstallKey is null)
        {
            await RunCommand(setup.Install with { Arguments = [] }, package.Id, folder, removing: true, insidePackage: true);
            return;
        }
        var managed = InstallDirectory(package.Id);
        if ((!Directory.Exists(managed) || !Directory.EnumerateFileSystemEntries(managed).Any()) &&
            (setup.Launch is null || PackageRules.ExternalWindowsPath(setup.Launch)))
            throw new InvalidOperationException("Для удаления программы укажите её штатный деинсталлятор в версии пакета.");
    }

    public static string Resolve(string target, string id, string package)
    {
        if (!PackageRules.ExternalWindowsPath(target))
            return PackagePath(package, target);
        var path = Expand(target, id, package).Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Не удалось определить путь к установленной программе.");
        return Path.GetFullPath(path);
    }

    private static string Expand(string value, string id, string package) => Environment.ExpandEnvironmentVariables(
        value.Replace("{installDir}", InstallDirectory(id), StringComparison.OrdinalIgnoreCase)
            .Replace("{packageDir}", package, StringComparison.OrdinalIgnoreCase));

    private static string PackagePath(string folder, string relative)
    {
        if (!PackageRules.ValidPackagePath(relative) || PackageRules.ExternalWindowsPath(relative))
            throw new InvalidDataException("Недопустимый путь внутри пакета.");
        var path = folder;
        foreach (var part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Путь установщика указывает на ссылку.");
        }
        return path;
    }

    private static bool IsMsi(string path) => path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    private static string MsiExecutable => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    private static Task RunCommand(PackageCommand command, string id, string folder, bool removing, bool insidePackage)
    {
        var file = insidePackage ? PackagePath(folder, command.Path) : Resolve(command.Path, id, folder);
        if (!PackageRules.InstallerExtension(file) || !File.Exists(file))
            throw new InvalidDataException("Файл установки или удаления отсутствует.");
        var args = command.Arguments.Select(arg => Expand(arg, id, folder)).ToArray();
        var work = Path.GetDirectoryName(file)!;
        if (IsMsi(file))
            return Run(MsiExecutable, [removing ? "/x" : "/i", file, "/norestart", .. args], work,
                command.Elevated, true, removing);
        if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            return Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-File", file, .. args], work, command.Elevated, false, removing);
        return Run(file, args, work, command.Elevated, false, removing);
    }

    private static async Task Run(string executable, string[] args, string work, bool elevated, bool msi, bool removing,
        bool shell = false)
    {
        if (!Path.IsPathFullyQualified(executable))
            executable = Path.Combine(Environment.SystemDirectory, Path.GetFileName(executable));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = work, UseShellExecute = elevated || shell, Verb = elevated ? "runas" : ""
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установщик.");
        await process.WaitForExitAsync();
        if (process.ExitCode is not (0 or 1641 or 3010) && !(msi && removing && process.ExitCode is 1605 or 1614))
            throw new InvalidOperationException($"Установщик завершился с кодом {process.ExitCode}.");
    }

    private static string[] ParseRegistered(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (!command.StartsWith('"') && Path.IsPathFullyQualified(command))
        {
            var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
                command = '"' + command[..(end + 4)] + '"' + command[(end + 4)..];
        }
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero || count == 0)
            throw new InvalidOperationException("Команда удаления Windows недоступна.");
        try
        {
            return Enumerable.Range(0, count).Select(i =>
                Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "").ToArray();
        }
        finally { LocalFree(pointer); }
    }

    private static string? ProductCode(string path)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var database) != 0)
            return null;
        try
        {
            if (MsiDatabaseOpenViewW(database, "SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'", out var view) != 0)
                return null;
            try
            {
                if (MsiViewExecute(view, 0) != 0 || MsiViewFetch(view, out var record) != 0)
                    return null;
                try
                {
                    uint length = 64;
                    var value = new StringBuilder((int)length);
                    return MsiRecordGetStringW(record, 1, value, ref length) == 0 && Guid.TryParse(value.ToString(), out var id)
                        ? id.ToString("B").ToUpperInvariant() : null;
                }
                finally { MsiCloseHandle(record); }
            }
            finally { MsiCloseHandle(view); }
        }
        finally { MsiCloseHandle(database); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder data, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(uint handle);
}
