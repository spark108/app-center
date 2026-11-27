using System.IO;
using System.Threading;
using System.Windows;
using Spark108.Core;

namespace Spark108.Desktop;

public partial class App : Application
{
    private Mutex? instance;
    private InstanceCommands? commands;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var readiness = e.Args.Length == 2 && e.Args[0] == "--update-ready" &&
            Guid.TryParseExact(e.Args[1], "N", out _) ? e.Args[1] : null;
        var uninstall = e.Args.Length == 2 && e.Args[0] == "--uninstall" &&
            PackageRules.ValidId(e.Args[1]) ? e.Args[1] : null;
        using var update = new Mutex(false, LocalPackages.UpdateMutex);
        if (readiness is null && !Acquire(update))
        {
            MessageBox.Show("Подождите, Центр приложений обновляется.", "Центр приложений");
            Shutdown(1);
            return;
        }
        if (readiness is null)
            update.ReleaseMutex();

        var candidate = new Mutex(false, LocalPackages.InstanceMutex);
        if (!Acquire(candidate))
        {
            candidate.Dispose();
            var forwarded = readiness is null && InstanceCommands.Forward(uninstall is null ? "show" : "uninstall " + uninstall);
            if (!forwarded && uninstall is not null)
                MessageBox.Show("Откройте Центр приложений и удалите программу из библиотеки.", "Удаление");
            Shutdown(forwarded ? 0 : 1);
            return;
        }
        instance = candidate;

        try
        {
            if (uninstall is not null)
            {
                UninstallStandalone(uninstall);
                return;
            }
            if (readiness is not null &&
                LocalPackages.Read(LocalPackages.Client)?.Version != PackageManager.ClientVersion)
                throw new InvalidDataException("Версия запущенного клиента отличается от подготовленной.");
            var window = new MainWindow();
            MainWindow = window;
            commands = new InstanceCommands(window);
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
                MessageBox.Show(ex.Message, "Не удалось открыть Центр приложений", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void UninstallStandalone(string id)
    {
        try
        {
            if (!PackageManager.Installed().TryGetValue(id, out var package) || package.Kind == PackageKind.AppCenter)
                throw new InvalidOperationException("Установка программы не найдена.");
            if (MessageBox.Show($"Удалить «{package.Name}» с этого компьютера?", "Удаление",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await PackageManager.Remove(package);
            Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Удаление", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.PrepareSystemExit();
        base.OnSessionEnding(e);
    }

    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        commands?.Dispose();
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
