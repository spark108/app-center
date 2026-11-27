using System.IO;
using System.Threading;
using System.Windows;
using Spark108.Core;

namespace Spark108.Desktop;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var readiness = e.Args.Length == 2 && e.Args[0] == "--update-ready" &&
            Guid.TryParseExact(e.Args[1], "N", out _) ? e.Args[1] : null;
        using var update = new Mutex(false, LocalPackages.UpdateMutex);
        if (readiness is null && !Acquire(update))
        {
            MessageBox.Show("Подождите, App Center обновляется.", "Spark108 App Center");
            Shutdown(1);
            return;
        }
        if (readiness is null)
            update.ReleaseMutex();

        var candidate = new Mutex(false, LocalPackages.InstanceMutex);
        if (!Acquire(candidate))
        {
            candidate.Dispose();
            Shutdown(1);
            return;
        }
        instance = candidate;

        try
        {
            if (readiness is not null &&
                LocalPackages.Read(LocalPackages.Client)?.Version != PackageManager.ClientVersion)
                throw new InvalidDataException("Версия запущенного клиента отличается от подготовленной.");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (readiness is not null)
            {
                using var ready = EventWaitHandle.OpenExisting(LocalPackages.ReadyEvent(readiness));
                ready.Set();
            }
        }
        catch (Exception ex)
        {
            if (readiness is null)
                MessageBox.Show(ex.Message, "Не удалось открыть Spark108 App Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
