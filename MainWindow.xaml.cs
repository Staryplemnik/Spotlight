using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Spotlight;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x5317;
    private const int WmHotkey = 0x0312;
    private const uint VkSpace = 0x20;
    private const int MaximumFiles = 2500;
    private const int MaximumShortcuts = 2000;
    private const int MaximumCachedIcons = 8;

    private readonly ObservableCollection<SearchResult> _results = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _workingSetTimer;
    private AppSettings _settings;
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _iconCacheLock = new();
    private int _iconSlotsReserved;
    private HwndSource? _source;
    private List<SearchResult> _apps = [];
    private SearchIndex _appIndex = SearchIndex.Empty;
    private SearchIndex _fileIndex = SearchIndex.Empty;
    private bool _altHotkeyRegistered;
    private bool _indexReady;
    private bool _isUpdatingResults;
    private bool _allowClose;
    private readonly string _appCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spotlight",
        "apps.json");

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Spotlight.ico", UriKind.Absolute));
        ResultsList.ItemsSource = _results;
        _apps = LoadCachedApps();
        _appIndex = SearchIndex.Create(_apps);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            UpdateResults();
        };
        _workingSetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _workingSetTimer.Tick += (_, _) =>
        {
            _workingSetTimer.Stop();
            if (!IsVisible && !EmptyWorkingSet(GetCurrentProcess()))
                Debug.WriteLine($"Could not trim Spotlight's idle working set (error {Marshal.GetLastWin32Error()}).");
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _workingSetTimer.Stop();
            else
                _workingSetTimer.Start();
        };

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            UpdateResults();
            _ = Task.Run(() =>
            {
                List<SearchResult> apps = LoadApps();
                SearchIndex appIndex = SearchIndex.Create(apps);
                SaveCachedApps(apps);
                Dispatcher.Invoke(() =>
                {
                    _apps = apps;
                    _appIndex = appIndex;
                    UpdateResults();
                });
                if (_settings.SearchFiles)
                    IndexFiles();
                else
                    _indexReady = true;
            });
        };
        SourceInitialized += (_, _) => InitializeGlobalHotkeys();
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) =>
        {
            if (_source is not null && _altHotkeyRegistered)
                UnregisterHotKey(_source.Handle, HotkeyId);
            _source?.RemoveHook(WindowProc);
        };
    }

    public void ShowLauncher()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void AllowClose() => _allowClose = true;

    public void ApplySettings(AppSettings settings)
    {
        bool shouldIndexFiles = !_settings.SearchFiles && settings.SearchFiles;
        _settings = settings;
        if (!settings.SearchFiles)
        {
            _fileIndex = SearchIndex.Empty;
            _indexReady = true;
        }
        else if (shouldIndexFiles)
        {
            _indexReady = false;
            _ = Task.Run(IndexFiles);
        }

        UpdateResults();
    }

    private void InitializeGlobalHotkeys()
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source.AddHook(WindowProc);
        _altHotkeyRegistered = RegisterHotKey(
            _source.Handle,
            HotkeyId,
            ModAlt | ModNoRepeat,
            VkSpace);
        if (!_altHotkeyRegistered)
        {
            int error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"Could not register Alt+Space (error {error}).");
            StatusText.Text = $"Alt+Space unavailable (Windows error {error}) · use the tray icon";
        }

        UpdateHotkeyStatus();
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleLauncher();
            handled = true;
        }

        return nint.Zero;
    }

    private void ToggleLauncher()
    {
        if (IsVisible)
            Hide();
        else
            ShowLauncher();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (ResultsList.Items.Count > 0)
            {
                ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ResultsList.Items.Count > 0)
            {
                ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelectedResult();
            e.Handled = true;
        }
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        SearchBox.Focus();
    }

    private void ResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUpdatingResults && ResultsList.SelectedItem is not null)
            SearchBox.Focus();
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedResult();

    private async Task LoadVisibleAppIconsAsync(IEnumerable<SearchResult> results)
    {
        SearchResult[] apps = results.Where(result => result.Kind == "App" && result.Icon is null).ToArray();
        await Task.WhenAll(apps.Select(async app =>
        {
            ImageSource? icon;
            lock (_iconCacheLock)
            {
                _iconCache.TryGetValue(app.Target, out icon);
                if (icon is null)
                {
                    if (_iconCache.Count + _iconSlotsReserved >= MaximumCachedIcons)
                        return;
                    _iconSlotsReserved++;
                }
            }

            if (icon is null)
            {
                icon = await Task.Run(() => ExtractIcon(app.Target));
                lock (_iconCacheLock)
                {
                    if (icon is null)
                    {
                        _iconSlotsReserved--;
                    }
                    else if (_iconCache.TryAdd(app.Target, icon))
                    {
                        _iconSlotsReserved--;
                    }
                    else
                    {
                        _iconSlotsReserved--;
                        icon = _iconCache[app.Target];
                    }
                }
            }

            if (icon is not null)
                await Dispatcher.InvokeAsync(() => app.Icon = icon);
        }));
    }

    private void UpdateResults()
    {
        _isUpdatingResults = true;
        string query = SearchBox.Text.Trim();
        var matches = new List<SearchResult>();
        if (query.Length == 0)
        {
            matches.AddRange(_apps.Take(100));
        }
        else
        {
            matches.AddRange(_appIndex.Find(query, 20));

            if (query.Length > 1)
                matches.AddRange(_fileIndex.Find(query, 10));

            matches.Add(new SearchResult($"Search the web for “{query}”", "Open your default browser", "Web", "W", query));
        }

        _results.Clear();
        foreach (SearchResult match in matches)
            _results.Add(match);
        ResultsList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        _isUpdatingResults = false;
        _ = LoadVisibleAppIconsAsync(matches);
        UpdateHotkeyStatus();
    }

    private void OpenSelectedResult()
    {
        if (ResultsList.SelectedItem is not SearchResult result)
        {
            string query = SearchBox.Text.Trim();
            if (query.Length > 0)
                OpenWebSearch(query);
            return;
        }

        try
        {
            if (result.Kind == "Web")
            {
                OpenWebSearch(result.Target);
            }
            else
                Process.Start(new ProcessStartInfo(result.Target) { UseShellExecute = true });

            Hide();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                   FileNotFoundException or System.Security.SecurityException)
        {
            System.Windows.MessageBox.Show(
                $"Could not open “{result.Title}”.\n\n{ex.Message}",
                "Spotlight",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void OpenWebSearch(string query)
    {
        string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private List<SearchResult> LoadApps()
    {
        var apps = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] shortcutRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        ];

        foreach (string root in shortcutRoots.Where(Directory.Exists))
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0 && apps.Count < MaximumShortcuts)
            {
                (string directory, int depth) = pending.Pop();
                try
                {
                    foreach (string shortcut in Directory.EnumerateFiles(directory, "*.lnk"))
                    {
                        if (seen.Add(shortcut))
                        {
                            string name = Path.GetFileNameWithoutExtension(shortcut).Trim();
                            if (name.Length > 0 &&
                                !string.Equals(name, "Calculator", StringComparison.OrdinalIgnoreCase) &&
                                seenNames.Add(name))
                                apps.Add(new SearchResult(name, "Application", "App", "A", shortcut));
                        }
                    }

                    if (depth < 5)
                    {
                        foreach (string child in Directory.EnumerateDirectories(directory))
                        {
                            try
                            {
                                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                    pending.Push((child, depth + 1));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                Debug.WriteLine($"Could not inspect {child}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not scan shortcuts in {directory}: {ex.Message}");
                }
            }
        }

        foreach ((string name, string executable) in new[]
                 {
                     ("Notepad", "notepad.exe"),
                     ("File Explorer", "explorer.exe")
                 })
        {
            if (seenNames.Add(name))
                apps.Add(new SearchResult(name, "Windows app", "App", "A", executable));
        }

        apps.Sort((left, right) => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));
        return apps;
    }

    private List<SearchResult> LoadCachedApps()
    {
        try
        {
            if (!File.Exists(_appCachePath))
                return [];

            AppCacheEntry[]? cached = JsonSerializer.Deserialize<AppCacheEntry[]>(File.ReadAllText(_appCachePath));
            return cached?
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Title) &&
                                !string.Equals(entry.Title.Trim(), "Calculator", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(entry.Target))
                .Select(entry => new SearchResult(entry.Title, entry.Detail, "App", "A", entry.Target))
                .OrderBy(app => app.Title, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Could not load the app cache: {ex.Message}");
            return [];
        }
    }

    private void SaveCachedApps(List<SearchResult> apps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_appCachePath)!);
            AppCacheEntry[] entries = apps
                .Select(app => new AppCacheEntry(app.Title, app.Detail, app.Target))
                .ToArray();
            File.WriteAllText(_appCachePath, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save the app cache: {ex.Message}");
        }
    }

    private static ImageSource? ExtractIcon(string path)
    {
        var info = new ShellFileInfo();
        nint result = SHGetFileInfo(
            path,
            0,
            ref info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellGetIcon | ShellSmallIcon);
        if (result == 0 || info.Icon == 0)
            return null;

        try
        {
            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    private void IndexFiles()
    {
        var indexedFiles = new List<SearchResult>();
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        ];

        foreach (string root in roots.Where(Directory.Exists))
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0 && indexedFiles.Count < MaximumFiles)
            {
                (string directory, int depth) = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        string name = Path.GetFileName(file);
                        indexedFiles.Add(new SearchResult(name, file, "File", "F", file));
                        if (indexedFiles.Count >= MaximumFiles)
                            break;
                    }

                    if (depth >= 1)
                        continue;

                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                pending.Push((child, depth + 1));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Debug.WriteLine($"Could not inspect {child}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not index {directory}: {ex.Message}");
                }
            }
        }

        Dispatcher.Invoke(() =>
        {
            _fileIndex = _settings.SearchFiles ? SearchIndex.Create(indexedFiles) : SearchIndex.Empty;
            _indexReady = true;
            UpdateResults();
        });
    }

    private void UpdateHotkeyStatus()
    {
        string hotkeys = _altHotkeyRegistered
            ? "Alt+Space to toggle · Windows shortcuts pass through"
            : "Alt+Space unavailable · use the tray icon to reopen";
        StatusText.Text = $"{hotkeys} · {(_indexReady ? "Esc to close" : "indexing files…")}";
    }

    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint process);

    private const uint ShellGetIcon = 0x00000100;
    private const uint ShellSmallIcon = 0x00000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? TypeName;
    }
}

public sealed class SearchResult(string title, string detail, string kind, string glyph, string target) : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Kind { get; } = kind;
    public string Glyph { get; } = kind == "App" ? GetInitial(title) : glyph;
    public string Target { get; } = target;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string GetInitial(string title)
    {
        foreach (char character in title.Trim())
        {
            if (char.IsLetterOrDigit(character))
                return character.ToString().ToUpperInvariant();
        }

        return "?";
    }
}

internal sealed record AppCacheEntry(string Title, string Detail, string Target);

internal sealed class SearchIndex
{
    private readonly SearchResult[] _items;

    private SearchIndex(SearchResult[] items) => _items = items;

    public static SearchIndex Empty { get; } = new([]);

    public static SearchIndex Create(IEnumerable<SearchResult> results)
    {
        SearchResult[] items = results.ToArray();
        Array.Sort(items, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Title, right.Title));
        return new SearchIndex(items);
    }

    public IReadOnlyList<SearchResult> Find(string query, int limit)
    {
        if (query.Length == 0 || limit <= 0)
            return [];

        var results = new List<SearchResult>(limit);
        int firstPrefixMatch = LowerBound(query);
        for (int index = firstPrefixMatch;
             index < _items.Length && results.Count < limit &&
             _items[index].Title.StartsWith(query, StringComparison.OrdinalIgnoreCase);
             index++)
        {
            results.Add(_items[index]);
        }

        foreach (SearchResult candidate in _items)
        {
            if (results.Count == limit)
                break;
            if (!candidate.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) &&
                candidate.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add(candidate);
        }

        return results;
    }

    private int LowerBound(string query)
    {
        int low = 0;
        int high = _items.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (StringComparer.OrdinalIgnoreCase.Compare(_items[middle].Title, query) < 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
