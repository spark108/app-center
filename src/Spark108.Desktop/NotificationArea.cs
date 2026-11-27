using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Spark108.Core;
using Forms = System.Windows.Forms;

namespace Spark108.Desktop;

internal sealed class NotificationArea : IDisposable
{
    private readonly MainWindow window;
    private readonly Forms.NotifyIcon icon;
    private readonly ContextMenu menu;
    private readonly string settings = Path.Combine(LocalPackages.Root, "tray.json");
    private bool showRecent = true;

    public NotificationArea(MainWindow owner)
    {
        window = owner;
        if (File.Exists(settings))
        {
            try { showRecent = JsonSerializer.Deserialize<bool>(File.ReadAllText(settings)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/Spark108.Desktop;component/TrayMenu.xaml") };
        menu = (ContextMenu)resources["TrayMenu"];
        icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Центр приложений",
            Visible = true
        };
        icon.MouseUp += MouseUp;
    }

    private void MouseUp(object? sender, Forms.MouseEventArgs e) => window.Dispatcher.InvokeAsync(() =>
    {
        if (e.Button == Forms.MouseButtons.Left)
            window.ShowFromTray();
        else if (e.Button == Forms.MouseButtons.Right)
            Open();
    });

    private void Open()
    {
        menu.IsOpen = false;
        menu.Items.Clear();
        var recent = showRecent ? window.TrayApplications() : [];
        foreach (var (id, name) in recent)
            Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 },
                () => window.LaunchFromTray(id), !window.IsBusy);
        if (recent.Length != 0)
            menu.Items.Add(new Separator());
        Add("Настройка", Settings);
        menu.Items.Add(new Separator());
        Add("Показать Центр приложений", window.ShowFromTray);
        Add("Выход", window.ExitFromTray);
        menu.IsOpen = true;
    }

    private void Add(object name, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = name, IsEnabled = enabled };
        item.Click += (_, _) => { menu.IsOpen = false; action(); };
        menu.Items.Add(item);
    }

    private void Settings()
    {
        window.ShowFromTray();
        var dialog = new Window
        {
            Title = "Настройка", Owner = window, Width = 390, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(37, 38, 38)), FontFamily = new FontFamily("Segoe UI"),
            Foreground = Brushes.White
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        var recent = new CheckBox { Content = "Показывать недавние приложения в трее", IsChecked = showRecent,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var clear = new Button { Content = "Очистить недавние", IsEnabled = window.SignedIn, Padding = new Thickness(9, 5, 9, 5) };
        clear.Click += (_, _) => { window.ClearRecentApplications(); clear.IsEnabled = false; };
        var save = new Button { Content = "Сохранить", IsDefault = true, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(9, 5, 9, 5) };
        save.Click += (_, _) =>
        {
            showRecent = recent.IsChecked == true;
            Save(settings, showRecent);
            dialog.Close();
        };
        buttons.Children.Add(clear);
        buttons.Children.Add(save);
        content.Children.Add(recent);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.ShowDialog();
    }

    public static string[] Recent(string subject)
    {
        var path = RecentPath(subject);
        if (!File.Exists(path))
            return [];
        try
        {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
                .Where(id => id is not null && PackageRules.ValidId(id)).Distinct().Take(5).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public static void Record(string subject, string id) =>
        Save(RecentPath(subject), Recent(subject).Prepend(id).Distinct().Take(5).ToArray());

    public static void Clear(string subject) => Save(RecentPath(subject), Array.Empty<string>());

    private static string RecentPath(string subject) => Path.Combine(LocalPackages.Root,
        $"recent-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))}.json");

    private static void Save<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        menu.IsOpen = false;
        icon.MouseUp -= MouseUp;
        icon.Visible = false;
        icon.Dispose();
    }
}
