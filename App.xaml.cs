using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;

namespace Spotlight;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private MainWindow? _mainWindow;
    private AppSettings _settings = new();
    private bool _ownsMutex;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "Spotlight";
    private const string BackgroundArgument = "--background";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UpdateTheme();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        _instanceMutex = new Mutex(true, @"Local\LightweightSpotlight", out bool isFirstInstance);
        _ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "Spotlight is already running. Use its hotkey or notification-area icon to open it.",
                "Spotlight",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _mainWindow = new MainWindow(_settings);
        MainWindow = _mainWindow;
        _trayIcon = CreateTrayIcon();
        _mainWindow.Show();
        if (e.Args.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase))
            _mainWindow.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
        if (_ownsMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(UpdateTheme);
    }

    private void UpdateTheme()
    {
        bool isDark = Convert.ToInt32(
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1)) == 0;

        Resources["WindowBackgroundBrush"] = Brush(isDark ? 32 : 243, isDark ? 32 : 243, isDark ? 32 : 243);
        Resources["WindowBorderBrush"] = Brush(isDark ? 58 : 214, isDark ? 58 : 214, isDark ? 58 : 214);
        Resources["SearchBackgroundBrush"] = Brush(isDark ? 43 : 255, isDark ? 43 : 255, isDark ? 43 : 255);
        Resources["SearchBorderBrush"] = Brush(isDark ? 69 : 200, isDark ? 69 : 200, isDark ? 69 : 200);
        Resources["PrimaryTextBrush"] = Brush(isDark ? 242 : 32, isDark ? 242 : 32, isDark ? 242 : 32);
        Resources["SecondaryTextBrush"] = Brush(isDark ? 184 : 102, isDark ? 184 : 102, isDark ? 184 : 102);
        Resources["SelectedItemBrush"] = Brush(isDark ? 55 : 229, isDark ? 55 : 241, isDark ? 55 : 251);
        Resources["HoverItemBrush"] = Brush(isDark ? 46 : 237, isDark ? 46 : 237, isDark ? 46 : 237);
        Resources["IconBackgroundBrush"] = Brush(isDark ? 56 : 229, isDark ? 56 : 229, isDark ? 56 : 229);
        Resources["IconGlyphBrush"] = Brush(isDark ? 212 : 80, isDark ? 212 : 80, isDark ? 212 : 80);
    }

    private static SolidColorBrush Brush(int red, int green, int blue)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)red, (byte)green, (byte)blue));
        brush.Freeze();
        return brush;
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Spotlight", null, (_, _) => ShowLauncher());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateSettingsMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _mainWindow?.AllowClose();
            Shutdown();
        });

        string? executablePath = Environment.ProcessPath;
        _trayIconImage = executablePath is null ? null : Icon.ExtractAssociatedIcon(executablePath);
        var icon = new NotifyIcon
        {
            Icon = _trayIconImage ?? SystemIcons.Application,
            Text = "Spotlight - Alt+Space",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowLauncher();
        return icon;
    }

    private ToolStripMenuItem CreateSettingsMenu()
    {
        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(CreateSettingItem(
            "Start with Windows",
            _settings.StartWithWindows,
            current => current with { StartWithWindows = !current.StartWithWindows }));
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(CreateSettingItem(
            "Search files",
            _settings.SearchFiles,
            current => current with { SearchFiles = !current.SearchFiles }));
        return settings;
    }

    private ToolStripMenuItem CreateSettingItem(
        string label,
        bool isChecked,
        Func<AppSettings, AppSettings> update)
    {
        var item = new ToolStripMenuItem(label) { CheckOnClick = true, Checked = isChecked };
        item.Click += (_, _) => ApplySettingChange(item, update);
        return item;
    }

    private void ApplySettingChange(ToolStripMenuItem item, Func<AppSettings, AppSettings> update)
    {
        AppSettings previous = _settings;
        AppSettings updated = update(previous);
        try
        {
            if (updated.StartWithWindows != previous.StartWithWindows)
                UpdateStartupRegistration(updated);

            updated.Save();
            _settings = updated;
            _mainWindow?.ApplySettings(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or InvalidOperationException)
        {
            item.Checked = !item.Checked;
            try
            {
                UpdateStartupRegistration(previous);
            }
            catch (Exception rollbackException) when (rollbackException is IOException or
                                                       UnauthorizedAccessException or
                                                       System.Security.SecurityException)
            {
                Debug.WriteLine($"Could not restore the previous startup setting: {rollbackException.Message}");
            }

            System.Windows.MessageBox.Show(
                $"Could not apply “{item.Text}”.\n\n{ex.Message}",
                "Spotlight settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void UpdateStartupRegistration(AppSettings settings)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's startup settings.");
        if (!settings.StartWithWindows)
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            return;
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the Spotlight executable path.");
        key.SetValue(
            StartupValueName,
            $"\"{executablePath}\" {BackgroundArgument}",
            RegistryValueKind.String);
    }

    private void ShowLauncher()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.ShowLauncher();
    }
}
