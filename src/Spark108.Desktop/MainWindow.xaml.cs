using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Spark108.Core;

namespace Spark108.Desktop;

public partial class MainWindow : Window
{
    private enum Section { Catalog, Library, Updates }
    private sealed record Card(CatalogApp? App, InstalledPackage? Installed, string Id, string Name, string Status,
        string Icon, Brush TileBrush)
    {
        public string AccessibleName => $"{Name} · {Status}";
    }

    private readonly CancellationTokenSource lifetime = new();
    private readonly ApiClient api;
    private readonly PackageManager packages;
    private UserProfile? user;
    private CatalogApp[] catalog = [];
    private Dictionary<string, InstalledPackage> installed = [];
    private CancellationTokenSource? operation;
    private Section section;
    private int versionRequest;
    private bool allowClose;

    public MainWindow()
    {
        InitializeComponent();
        api = new ApiClient();
        packages = new PackageManager(api);
        ClientBuild.Text = $"Версия клиента {PackageManager.ClientVersion}";
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowLoaded;
        PackageManager.CleanupStaging();
        if (api.HasSession)
            await Work("Проверяем сеанс…", Refresh);
        if (File.Exists(LocalPackages.UpdateFailure))
        {
            try
            {
                SetStatus("Не удалось обновить App Center. " + File.ReadAllText(LocalPackages.UpdateFailure), true);
                File.Delete(LocalPackages.UpdateFailure);
            }
            catch (IOException) { }
        }
    }

    private async void LoginClick(object sender, RoutedEventArgs e) =>
        await Work("Завершите вход через Alathea Auth в браузере…", async ct =>
        {
            await api.Login(ct);
            await Refresh(ct);
            Activate();
        });

    private async void LogoutClick(object sender, RoutedEventArgs e) =>
        await Work("Завершение сеанса…", async ct =>
        {
            try { await api.Logout(ct); }
            finally { SetSignedOut(); SetStatus("Вы вышли из учётной записи."); }
        });

    private void SetSignedOut()
    {
        user = null;
        catalog = [];
        installed = [];
        versionRequest++;
        Cards.ItemsSource = null;
        Versions.ItemsSource = null;
        ApplySession();
    }

    private void ApplySession()
    {
        var signedIn = user is not null;
        Welcome.Visibility = Show(!signedIn);
        Workspace.Visibility = Show(signedIn);
        Navigation.Visibility = Show(signedIn);
        AccountPanel.Visibility = Show(signedIn);
        AccountName.Text = user?.Name;
    }

    private async Task Refresh(CancellationToken ct)
    {
        var profile = await api.Me(ct);
        var items = await api.Catalog(ct);
        var local = PackageManager.Installed();
        user = profile;
        catalog = items;
        installed = local;
        ApplySession();
        RenderCards();
        var updates = catalog.Count(HasUpdate);
        SetStatus(updates > 0 ? $"Доступны обновления · {updates}" : "Каталог актуален");
    }

    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private bool HasUpdate(CatalogApp app) => app.Latest is { } last && installed.TryGetValue(app.Id, out var current) &&
        Version.Parse(last.Version) > Version.Parse(current.Version);

    private void RenderCards(string? select = null)
    {
        if (user is null)
            return;
        select ??= (Cards.SelectedItem as Card)?.Id;
        var visible = catalog.ToDictionary(x => x.Id);
        IEnumerable<string> ids = section switch
        {
            Section.Library => installed.Keys,
            Section.Updates => catalog.Where(HasUpdate).Select(x => x.Id),
            _ => visible.Keys
        };
        var query = Search.Text.Trim();
        var cards = ids.Select(id =>
        {
            visible.TryGetValue(id, out var entry);
            installed.TryGetValue(id, out var local);
            return MakeCard(id, entry, local);
        }).Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            x.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Id != PackageRules.AppCenterId)
            .ThenBy(x => x.App?.Kind ?? x.Installed!.Kind)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SectionTitle.Text = section switch
        {
            Section.Library => "Библиотека",
            Section.Updates => "Обновления",
            _ => "Каталог приложений"
        };
        CatalogButton.Background = NavBackground(section == Section.Catalog);
        LibraryButton.Background = NavBackground(section == Section.Library);
        UpdatesButton.Background = NavBackground(section == Section.Updates);
        var count = catalog.Count(HasUpdate);
        UpdatesButton.ToolTip = count > 0 ? $"Обновления · {count}" : "Обновления";
        Cards.ItemsSource = cards;
        Cards.SelectedItem = cards.FirstOrDefault(x => x.Id == select) ?? cards.FirstOrDefault();
        RenderSelection();
    }

    private static Brush NavBackground(bool selected) => selected ? new SolidColorBrush(Color.FromRgb(37, 55, 79)) : Brushes.Transparent;

    private Card MakeCard(string id, CatalogApp? entry, InstalledPackage? local)
    {
        var kind = entry?.Kind ?? local!.Kind;
        var (glyph, color) = kind switch
        {
            PackageKind.AppCenter => ("\uE80A", Color.FromRgb(55, 98, 133)),
            PackageKind.Component => ("\uE7B8", Color.FromRgb(72, 73, 126)),
            _ => ("\uE8A7", Color.FromRgb(40, 102, 108)),
        };
        var status = entry is null ? $"Установлено · {local!.Version} · доступ закрыт" :
            HasUpdate(entry) ? $"Обновление · {entry.Latest!.Version}" :
            local is not null ? $"Установлено · {local.Version}" :
            entry.Latest is not null ? $"Версия {entry.Latest.Version}" : "Версия готовится к публикации";
        return new Card(entry, local, id, entry?.Name ?? local!.Name, status, glyph, new SolidColorBrush(color));
    }

    private void RenderSelection()
    {
        var card = Cards.SelectedItem as Card;
        Details.Visibility = Show(card is not null);
        EmptySelection.Visibility = Show(card is null);
        if (card is null)
        {
            var filtered = Search.Text.Trim().Length > 0;
            EmptyTitle.Text = filtered ? "Ничего не найдено" : section switch
            {
                Section.Updates => "Обновлений нет",
                Section.Library => "Библиотека пуста",
                _ => "Каталог пока пуст"
            };
            EmptyHint.Text = filtered ? "Попробуйте изменить запрос." : section switch
            {
                Section.Updates => "У установленных приложений актуальные версии.",
                Section.Library => "Установите приложение из каталога.",
                _ => "Доступные вам программы появятся здесь."
            };
            return;
        }
        var item = card.App;
        var kind = item?.Kind ?? card.Installed!.Kind;
        AppType.Text = kind switch
        {
            PackageKind.AppCenter => "СЛУЖЕБНОЕ ПРИЛОЖЕНИЕ",
            PackageKind.Component => "СЛУЖЕБНЫЙ КОМПОНЕНТ",
            _ => "ПРИЛОЖЕНИЕ ДЛЯ WINDOWS"
        };
        AppTitle.Text = card.Name;
        AppTitle.ToolTip = card.Name;
        AppDescription.Text = item?.Description ?? card.Installed?.Description;
        var names = (item?.Dependencies ?? card.Installed?.Dependencies ?? [])
            .Select(id => catalog.FirstOrDefault(x => x.Id == id)?.Name ?? id).ToArray();
        DependencyText.Text = names.Length == 0 ? "" : "Компоненты · " + string.Join(", ", names);
        DependencyText.Visibility = Show(names.Length != 0);
        InstalledLabel.Text = card.Installed is not null ? $"Установлено {card.Installed.Version}" : "";
        if (item is null)
            InstalledLabel.Text += " · Нет доступа в каталоге";
        RenderActions();
    }

    private async void AppChanged(object sender, SelectionChangedEventArgs e)
    {
        var request = ++versionRequest;
        var card = Cards.SelectedItem as Card;
        var latest = card?.App?.Latest;
        Versions.ItemsSource = latest is null ? Array.Empty<ReleaseInfo>() : new[] { latest };
        Versions.SelectedIndex = latest is null ? -1 : 0;
        RenderSelection();
        if (card?.App is null || latest is null || user is null || card.App.Kind == PackageKind.AppCenter)
            return;
        try
        {
            var releases = await api.Releases(card.Id, lifetime.Token);
            if (request != versionRequest)
                return;
            var selected = (Versions.SelectedItem as ReleaseInfo)?.Id;
            Versions.ItemsSource = releases;
            Versions.SelectedItem = releases.FirstOrDefault(x => x.Id == selected) ?? releases.FirstOrDefault();
        }
        catch (LoginRequiredException) when (request == versionRequest)
        {
            api.Forget();
            SetSignedOut();
            SetStatus("Сеанс завершён. Войдите снова.", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or LoginRequiredException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (request == versionRequest && operation is null && !lifetime.IsCancellationRequested)
                SetStatus("Не удалось загрузить список версий.", true);
        }
    }

    private void RenderActions()
    {
        if (Cards?.SelectedItem is not Card card || Versions is null)
            return;
        var item = card.App;
        var local = card.Installed;
        var release = Versions.SelectedItem as ReleaseInfo;
        var self = card.Id == PackageRules.AppCenterId;
        var busy = operation is not null;
        var broken = local is not null && !PackageManager.EntryPointPresent(local);
        var repair = item is not null && local is not null &&
            (broken || !item.Dependencies.ToHashSet().SetEquals(local.Dependencies) || item.Dependencies.Any(id => !installed.ContainsKey(id)));
        VersionLabel.Visibility = Show(Versions.Items.Count > 0);
        Versions.Visibility = VersionLabel.Visibility;
        ReleaseNotes.Text = string.IsNullOrWhiteSpace(release?.Notes) ? "" : "Что нового · " + release.Notes;
        ReleaseNotes.Visibility = Show(ReleaseNotes.Text.Length > 0);
        var update = local is not null && release is not null && Version.Parse(release.Version) > Version.Parse(local.Version);
        ApplyButton.Visibility = Show(release is not null && (self ? update : local is null || local.Version != release.Version || repair || item?.Kind == PackageKind.Component));
        ApplyButton.Content = local is null ? "Установить" : update ? (self ? "Обновить App Center" : "Обновить") :
            local.Version == release?.Version ? (broken ? "Восстановить" : "Проверить компоненты") : $"Установить {release?.Version}";
        LaunchButton.Visibility = Show(local?.Kind == PackageKind.Application && item is not null && !broken);
        RemoveButton.Visibility = Show(local is not null && !self);
        DownloadButton.Visibility = Show(release is not null);
        ApplyButton.IsEnabled = !busy && (!self || PackageManager.CanUpdateClient);
        ApplyButton.ToolTip = self && !PackageManager.CanUpdateClient ? "Для самообновления установите клиент из пакета App Center." : null;
        Versions.IsEnabled = LaunchButton.IsEnabled = RemoveButton.IsEnabled = DownloadButton.IsEnabled = !busy;
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (Cards.SelectedItem is not Card card || Versions.SelectedItem is not ReleaseInfo release)
            return;
        await Work($"Подготовка · {card.Name}", async ct =>
        {
            if (await packages.Install(card.Id, release, Report, ct))
            {
                SetStatus("Перезапуск App Center…");
                allowClose = true;
                Application.Current.Shutdown();
                return;
            }
            installed = PackageManager.Installed();
            RenderCards(card.Id);
            SetStatus($"Готово · {card.Name} {release.Version}");
        });
    }

    private async void DownloadClick(object sender, RoutedEventArgs e)
    {
        if (Versions.SelectedItem is not ReleaseInfo release)
            return;
        var dialog = new SaveFileDialog
        {
            FileName = $"{release.AppId}-{release.Version}.zip", Filter = "ZIP (*.zip)|*.zip", DefaultExt = "zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await Work("Скачивание ZIP…", async ct =>
        {
            var partial = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await api.Download(release, partial, new Progress<int>(value => Report("Скачивание ZIP…", value)), ct);
                File.Move(partial, dialog.FileName, true);
            }
            finally { File.Delete(partial); }
            SetStatus("ZIP скачан. Контрольная сумма совпадает.");
        });
    }

    private async void LaunchClick(object sender, RoutedEventArgs e)
    {
        if (Cards.SelectedItem is not Card { Installed: { } local, App: not null })
            return;
        await Work($"Проверяем доступ · {local.Name}", async ct =>
        {
            await api.Releases(local.Id, ct);
            PackageManager.Launch(local);
            SetStatus($"Открыто · {local.Name}");
        });
    }

    private async void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (Cards.SelectedItem is not Card { Installed: { } local } ||
            MessageBox.Show(this, $"Удалить «{local.Name}» с этого компьютера?", "Удаление",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Work($"Удаление · {local.Name}", async ct =>
        {
            await api.Me(ct);
            await Task.Run(() => PackageManager.Remove(local), ct);
            installed = PackageManager.Installed();
            RenderCards(local.Id);
            SetStatus($"Удалено · {local.Name}");
        });
    }

    private async Task Work(string message, Func<CancellationToken, Task> run)
    {
        if (operation is not null)
            return;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = pending;
        SetBusy(true);
        SetStatus(message);
        try { await run(pending.Token); }
        catch (LoginRequiredException ex)
        {
            api.Forget();
            SetSignedOut();
            SetStatus(ex.Message, true);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { SetStatus("Операция отменена."); }
        catch (OperationCanceledException) { SetStatus("Время ожидания истекло. Повторите операцию.", true); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            operation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SignIn.IsEnabled = RefreshButton.IsEnabled = LogOutButton.IsEnabled = Cards.IsEnabled =
            Navigation.IsEnabled = Search.IsEnabled = !busy;
        CancelButton.Visibility = OperationProgress.Visibility = Show(busy);
        OperationProgress.IsIndeterminate = busy;
        RenderActions();
    }

    private void Report(string message, int progress)
    {
        if (operation is null)
            return;
        SetStatus(message);
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Value = progress;
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 163, 153)) : (Brush)FindResource("Muted");
    }

    private void SwitchTo(Section next)
    {
        section = next;
        if (Search.Text.Length != 0)
            Search.Clear();
        else
            RenderCards();
    }

    private void CatalogClick(object sender, RoutedEventArgs e) => SwitchTo(Section.Catalog);
    private void LibraryClick(object sender, RoutedEventArgs e) => SwitchTo(Section.Library);
    private void UpdatesClick(object sender, RoutedEventArgs e) => SwitchTo(Section.Updates);
    private void SearchChanged(object sender, TextChangedEventArgs e) => RenderCards();
    private void VersionChanged(object sender, SelectionChangedEventArgs e) => RenderActions();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Work("Проверяем обновления…", Refresh);
    private void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximized();
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (operation is not null && !allowClose)
        {
            e.Cancel = true;
            operation.Cancel();
            return;
        }
        lifetime.Cancel();
        api.Dispose();
        lifetime.Dispose();
    }

    private void ScrollCards(object sender, MouseWheelEventArgs e)
    {
        if (VisualTreeHelper.GetChildrenCount(Cards) > 0 && FindScroll(VisualTreeHelper.GetChild(Cards, 0)) is { } scroll)
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll)
            return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found)
                return found;
        return null;
    }
}
