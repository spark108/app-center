using System.Diagnostics;
using Spark108.Core;

if (args.Length != 2 || !int.TryParse(args[0], out var parentPid) || parentPid <= 0 ||
    !Guid.TryParseExact(args[1], "N", out var operation))
    return 2;

var operationId = operation.ToString("N");
var stage = Path.Combine(LocalPackages.Staging, operationId);
var next = Path.Combine(stage, "next");
if (!Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(stage, StringComparison.OrdinalIgnoreCase))
    return 2;

using var guard = new Mutex(false, LocalPackages.UpdateMutex);
if (!Acquire(guard))
    return 2;
var movedOld = false;
var movedNew = false;
var parentExited = false;
Process? child = null;
try
{
    try
    {
        using var parent = Process.GetProcessById(parentPid);
        if (!parent.WaitForExit(30000))
            throw new IOException("Предыдущий экземпляр клиента не завершился.");
    }
    catch (ArgumentException) { }
    parentExited = true;
    if (LocalPackages.Read(next) is not { Id: PackageRules.AppCenterId, Kind: PackageKind.AppCenter } ||
        !File.Exists(Path.Combine(next, PackageRules.AppCenterExe)))
        throw new InvalidDataException("Не найдена подготовленная версия App Center.");

    if (Directory.Exists(LocalPackages.ClientBackup))
    {
        if (!Directory.Exists(LocalPackages.Client))
            Directory.Move(LocalPackages.ClientBackup, LocalPackages.Client);
        else
            Directory.Delete(LocalPackages.ClientBackup, true);
    }
    Directory.Move(LocalPackages.Client, LocalPackages.ClientBackup);
    movedOld = true;
    Directory.Move(next, LocalPackages.Client);
    movedNew = true;
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, LocalPackages.ReadyEvent(operationId));
    child = Launch(operationId);
    var expires = Stopwatch.StartNew();
    while (!ready.WaitOne(200))
    {
        if (child.HasExited || expires.Elapsed >= TimeSpan.FromSeconds(25))
            throw new InvalidOperationException("Новая версия App Center не запустилась.");
    }
}
catch (Exception ex)
{
    var message = ex.Message;
    try
    {
        if (child is not null && !child.HasExited)
        {
            child.Kill();
            child.WaitForExit();
        }
        if (movedNew)
            Directory.Move(LocalPackages.Client, next);
        if (movedOld)
            Directory.Move(LocalPackages.ClientBackup, LocalPackages.Client);
    }
    catch (Exception restoreError)
    {
        message += Environment.NewLine + restoreError.Message;
    }
    File.WriteAllText(LocalPackages.UpdateFailure, message);
    guard.ReleaseMutex();
    if (parentExited && File.Exists(Path.Combine(LocalPackages.Client, PackageRules.AppCenterExe)))
        Launch(null).Dispose();
    return 1;
}
finally
{
    child?.Dispose();
}

guard.ReleaseMutex();
try { Directory.Delete(LocalPackages.ClientBackup, true); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
return 0;

static Process Launch(string? operation)
{
    var start = new ProcessStartInfo(Path.Combine(LocalPackages.Client, PackageRules.AppCenterExe))
    {
        WorkingDirectory = LocalPackages.Client, UseShellExecute = false
    };
    if (operation is not null)
    {
        start.ArgumentList.Add("--update-ready");
        start.ArgumentList.Add(operation);
    }
    return Process.Start(start) ?? throw new InvalidOperationException("Не удалось открыть App Center.");
}

static bool Acquire(Mutex mutex)
{
    try { return mutex.WaitOne(0); }
    catch (AbandonedMutexException) { return true; }
}
