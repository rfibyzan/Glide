using System.Globalization;
using System.Runtime.InteropServices;
using Glide.App.Integration;
using Glide.App.Search;
using Glide.App.Win32;
using Glide.Core;
using Glide.Core.Index;
using Glide.Core.Search;
using Glide.Core.Snapshot;
using Glide.Core.Settings;
using Glide.App.Ui;

namespace Glide.App.Ui;

/// <summary>
/// The launcher window. One native popup, custom-drawn with GDI, no child
/// controls: the query line and the result rows are painted directly, which
/// keeps the process free of a UI framework.
/// </summary>
internal sealed class SearchWindow
{
    internal const string ClassName = "Glide.SearchWindow";
    private const uint WmApp = 0x8000;
    internal const uint WmShowGlide = WmApp + 1;
    internal const uint WmIndexReady = WmApp + 2;
    internal const uint WmShowSettings = WmApp + 3;
    internal const uint WmSearchDone = WmApp + 5;
    internal const uint WmExplorerType = WmApp + 6;
    internal const uint WmTray = WmApp + 7;
    private const uint WmSettingChange = 0x001A;

    private const int HotkeyId = 1;
    private const int FocusCheckTimerId = 2;
    private const int QuickSwitchHotkeyId = 3;
    private const int VisibleRows = 8;
    private const int MaxQueryResults = 100;
    private const int MaxQueryLength = 200;
    private const int ActivationGraceMs = 400;

    /// <summary>Ctrl+1 … Ctrl+8 open the first eight results, wherever the list is scrolled.</summary>
    private const int ShortcutCount = 8;

    private static SearchWindow? s_instance;
    private static readonly NativeMethods.WndProc WindowProcDelegate = WindowProc;

    private readonly EntryStore? _snapshot;
    private readonly int _maxEntries;
    private readonly int _maxResults;
    private string[] _indexPaths;
    private EntryStore? _store;
    private string _query = string.Empty;
    private string? _notice;
    private SearchResult[] _results = [];
    private int _selected;
    private int _scrollOffset;
    private int _hovered = -1;
    private bool _hoveredSettings;
    private bool _trackingMouse;
    private int _lastMouseX = -1;
    private int _lastMouseY = -1;

    // Dragging a row out: armed on button down over a file or folder row,
    // started once the pointer leaves the system drag rectangle.
    private int _dragRow = -1;
    private int _dragStartX;
    private int _dragStartY;
    private bool _dragging;
    private bool _visible;
    private long _shownAt;
    private bool _indexing;
    private string _scanRoot = string.Empty;

    // Settings reference for theme change handling
    private AppSettings _settings = new();

    private IntPtr _hwnd;
    private int _dpi = 96;

    private IntPtr _fontQuery;
    private IntPtr _fontName;
    private IntPtr _fontPath;

    private IntPtr _brushBackground;
    private IntPtr _brushSelection;
    private IntPtr _brushAccent;
    private IntPtr _brushBorder;

    private IntPtr _backDc;
    private IntPtr _backBitmap;
    private IntPtr _previousBitmap;
    private int _backWidth;
    private int _backHeight;

    // Current theme state
    private bool _isDarkMode;

    // Current colors (switch based on theme)
    private int _inkColor;
    private int _dimColor;
    private int _placeholderColor;
    private int _backgroundColor;
    private int _selectionColor;
    private int _accentColor;
    private int _borderColor;

    private SearchWindow(EntryStore? snapshot, string[] indexPaths, int maxEntries, int maxResults)
    {
        _snapshot = snapshot;
        _indexPaths = indexPaths;
        _maxEntries = maxEntries;
        _maxResults = Math.Clamp(maxResults, 1, VisibleRows);
    }

    public static int Run(
        string? snapshotPath,
        string? scanRoot,
        string[] indexPaths,
        bool showOnStart,
        int maxEntries,
        AppSettings settings,
        string? hotkeyOverride,
        bool showSettingsOnStart = false)
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);

        EntryStore? snapshot = snapshotPath is null ? null : EntryStore.FromFile(snapshotPath);
        var window = new SearchWindow(snapshot, indexPaths, maxEntries, settings.MaxResults);
        s_instance = window;
        window._settings = settings;

        // Apply theme from settings or override
        window.ApplyTheme(settings.Theme);
        window.CreateNativeWindow(settings, hotkeyOverride);
        window.CreateResources();
        window.StartIntegration();
        window.StartIndexing(scanRoot);

        if (showSettingsOnStart)
        {
            SettingsWindow.Show(window);
        }
        else if (showOnStart || window._notice is not null)
        {
            window.Show();
        }

        return window.MessageLoop();
    }

    private void ApplyTheme(string theme)
    {
        // "system", "dark", "light"
        Palette previous = _palette;
        _isDarkMode = theme == "dark" || (theme == "system" && IsSystemDarkMode());
        _palette = Palette.For(_isDarkMode);

        _inkColor = _palette.Ink;
        _dimColor = _palette.Dim;
        _placeholderColor = _palette.Placeholder;
        _backgroundColor = _palette.Background;
        _selectionColor = _palette.Selection;
        _accentColor = _palette.Accent;
        _borderColor = _palette.Border;

        // A theme picked in Settings must repaint with new brushes, not only
        // new color numbers. WM_SETTINGCHANGE arrives for unrelated settings
        // too, so nothing is recreated when the palette did not change.
        if (_hwnd != IntPtr.Zero && _brushBackground != IntPtr.Zero && _palette != previous)
        {
            RecreateResources();
            UiNative.StyleWindow(_hwnd, _palette);
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private Palette _palette = Palette.Dark;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RequestShow()
    {
        IntPtr existing = NativeMethods.FindWindowW(ClassName, null);
        if (existing != IntPtr.Zero)
        {
            NativeMethods.SendMessageW(existing, WmShowGlide, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void RequestSettings()
    {
        IntPtr existing = NativeMethods.FindWindowW(ClassName, null);
        if (existing != IntPtr.Zero)
        {
            NativeMethods.SendMessageW(existing, WmShowSettings, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void CreateNativeWindow(AppSettings settings, string? hotkeyOverride)
    {
        IntPtr instance = NativeMethods.GetModuleHandleW(null);

        var windowClass = new NativeMethods.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            Style = NativeMethods.CsDropShadow,
            WndProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
            Instance = instance,
            Cursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)NativeMethods.IdcArrow),
            Background = IntPtr.Zero,
            ClassName = ClassName,
        };

        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _hwnd = NativeMethods.CreateWindowExW(
            // A tool window: the launcher and its bars are popups, not apps, so
            // they get no taskbar button and no Alt+Tab entry.
            NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow,
            ClassName,
            "Glide",
            NativeMethods.WsPopup,
            0,
            0,
            100,
            100,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi >= 72)
        {
            _dpi = (int)dpi;
        }

        UiNative.StyleWindow(_hwnd, _palette);
        RegisterHotkey(settings, hotkeyOverride);
    }

    private void RegisterHotkey(AppSettings settings, string? hotkeyOverride)
    {
        HotkeySettings requested = hotkeyOverride is null
            ? settings.Hotkey
            : HotkeyParser.TryParseSpec(hotkeyOverride, out HotkeySettings parsed) ? parsed : settings.Hotkey;

        bool parsedRequest = HotkeyParser.TryParse(requested, out uint mods, out uint vk);

        if (parsedRequest && NativeMethods.RegisterHotKey(_hwnd, HotkeyId, mods, vk))
        {
            return;
        }

        string wanted = DescribeHotkey(requested);
        uint fallbackMods = NativeMethods.ModControl | NativeMethods.ModAlt;
        const uint FallbackKey = 0x20;

        // The configured combination is unusable. A launcher nobody can summon is
        // worse than one on a different shortcut, so fall back and say so.
        bool alreadyFallback = parsedRequest && mods == fallbackMods && vk == FallbackKey;

        if (!alreadyFallback &&
            NativeMethods.RegisterHotKey(_hwnd, HotkeyId, fallbackMods, FallbackKey))
        {
            SetNotice(
                $"{wanted} is taken by another application. Glide listens on Ctrl+Alt+Space; " +
                "edit %APPDATA%\\Glide\\settings.json to change it.");
            return;
        }

        SetNotice($"{wanted} is taken by another application. Change the shortcut in %APPDATA%\\Glide\\settings.json.");
    }

    /// <summary>
    /// Tray icon, type-to-search and the dialog-scoped Quick Switch shortcut.
    /// All are optional: a failure leaves the main shortcut working.
    /// </summary>
    private void StartIntegration()
    {
        // The Explorer folder query is COM; the window thread is its apartment.
        _ = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
        ApplyIntegrationSettings();
    }

    /// <summary>
    /// Brings the tray icon, the keyboard hook and Quick Switch in line with
    /// <see cref="_settings"/>. Safe to call again after the Options window
    /// changed them.
    /// </summary>
    private void ApplyIntegrationSettings()
    {
        if (_settings.ShowTrayIcon && _tray is null)
        {
            _tray = new TrayIcon(_hwnd, WmTray, "Glide", _palette.Accent);
            _tray.Show();
        }
        else if (!_settings.ShowTrayIcon && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }

        ActivationHooks.ConfigureTypeToSearch(
            _hwnd,
            WmExplorerType,
            _settings.Integration.ExplorerTypeToSearch,
            _settings.Integration.DialogTypeToSearch);

        if (_quickSwitchRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, QuickSwitchHotkeyId);
            _quickSwitchRegistered = false;
        }

        _quickSwitchEnabled = _settings.Integration.EnableQuickSwitch &&
            HotkeyParser.TryParseSpec(_settings.Integration.QuickSwitchHotkey, out HotkeySettings quick) &&
            HotkeyParser.TryParse(quick, out _quickSwitchMods, out _quickSwitchKey);

        if (!_settings.Integration.DialogSearchBar && _docked)
        {
            Undock();
        }

        AppLog.Write($"integration: dialog bar={_settings.Integration.DialogSearchBar}, quick switch={_quickSwitchEnabled}, explorer typing={_settings.Integration.ExplorerTypeToSearch}, dialog typing={_settings.Integration.DialogTypeToSearch}");

        if (_quickSwitchEnabled || _settings.Integration.DialogSearchBar)
        {
            ActivationHooks.WatchForeground(OnForegroundChanged);
            OnForegroundChanged(NativeMethods.GetForegroundWindow());
        }
        else
        {
            ActivationHooks.StopWatchingForeground();
        }
    }

    private bool _quickSwitchEnabled;

    /// <summary>
    /// Applies settings from the Options window: saved already, now made
    /// live. Returns an error for the shortcut when it cannot be registered,
    /// in which case the previous shortcut stays active.
    /// </summary>
    internal string? ApplySettings(AppSettings settings)
    {
        string? error = null;

        // The main shortcut: register the new one before giving up the old.
        if (HotkeyParser.TryParse(settings.Hotkey, out uint mods, out uint vk))
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            if (NativeMethods.RegisterHotKey(_hwnd, HotkeyId, mods, vk))
            {
                _notice = null;
            }
            else
            {
                error = $"{HotkeyParser.ToSpec(mods, vk)} is already used by another application.";
                if (HotkeyParser.TryParse(_settings.Hotkey, out uint oldMods, out uint oldVk))
                {
                    NativeMethods.RegisterHotKey(_hwnd, HotkeyId, oldMods, oldVk);
                }

                settings.Hotkey = _settings.Hotkey;
            }
        }

        _settings = settings;
        PriorityEvaluator.Initialize(settings.FileSearch);
        ApplyTheme(settings.Theme);
        ApplyIntegrationSettings();
        _icons.Clear();

        return error;
    }

    /// <summary>The settings the Options window starts from.</summary>
    internal AppSettings CurrentSettings => _settings;

    /// <summary>Opens the Options window on the current settings.</summary>
    private void ShowOptions()
    {
        Hide();
        SettingsWindow.Show(this);
    }

    private uint _quickSwitchMods;
    private uint _quickSwitchKey;
    private bool _quickSwitchRegistered;

    /// <summary>
    /// Registers the Quick Switch shortcut while a file dialog is in front and
    /// releases it otherwise, so Ctrl+G keeps working in every other program.
    /// </summary>
    private void OnForegroundChanged(IntPtr foreground)
    {
        if (foreground == _hwnd)
        {
            return;
        }

        StopDialogRetry();
        bool dialog = FileDialogIntegration.IsFileDialog(foreground);
        if (IsDialogClass(foreground))
        {
            AppLog.Write($"dialog 0x{foreground:X} came to the front; file dialog={dialog}");
        }

        // A dialog is reported as in front the moment it is activated, which
        // can be before it has built its file list and name box. Look again
        // for a moment rather than deciding on a half-built window.
        if (!dialog && IsDialogClass(foreground))
        {
            _dialogRetryWindow = foreground;
            _dialogRetries = DialogRetryCount;
            NativeMethods.SetTimer(_hwnd, (UIntPtr)DialogRetryTimerId, DialogRetryMs, IntPtr.Zero);
        }

        if (_quickSwitchEnabled && dialog && !_quickSwitchRegistered)
        {
            _quickSwitchRegistered = NativeMethods.RegisterHotKey(_hwnd, QuickSwitchHotkeyId, _quickSwitchMods | ModNoRepeat, _quickSwitchKey);
        }
        else if (!dialog && _quickSwitchRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, QuickSwitchHotkeyId);
            _quickSwitchRegistered = false;
        }

        // The docked bar: shown under a file dialog while that dialog is in
        // front, put away as soon as anything else is.
        if (dialog && _settings.Integration.DialogSearchBar)
        {
            DockTo(foreground);
        }
        else if (_docked)
        {
            Undock();
        }
    }

    // ------------------------------------------------------------------
    // The bar docked under a file dialog
    //
    // It appears without taking focus as soon as an Open or Save dialog comes
    // to the front, follows the dialog when it moves, and stays until the
    // dialog closes or another program comes to the front. Passive, it is one
    // search line; clicked or typed into, it lists the folders open in
    // Explorer and recent places, and searches like the launcher.

    private bool _docked;
    private bool _passive;

    private const int DialogRetryTimerId = 5;
    private const uint DialogRetryMs = 150;
    private const int DialogRetryCount = 14;
    private IntPtr _dialogRetryWindow;
    private int _dialogRetries;

    private static bool IsDialogClass(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(16);
        return NativeMethods.GetClassNameW(hwnd, name, name.Capacity) > 0 && name.ToString() == "#32770";
    }

    /// <summary>One more look at a dialog that was not a recognizable file dialog yet.</summary>
    private void RetryDialog()
    {
        IntPtr window = _dialogRetryWindow;

        if (_dialogRetries <= 0 || NativeMethods.GetForegroundWindow() != window || !NativeMethods.IsWindow(window))
        {
            StopDialogRetry();
            return;
        }

        _dialogRetries--;

        if (FileDialogIntegration.IsFileDialog(window))
        {
            StopDialogRetry();
            AppLog.Write($"file dialog 0x{window:X} recognized after it came to the front");
            OnForegroundChanged(window);
        }
        else if (_dialogRetries == 0)
        {
            StopDialogRetry();
            AppLog.Write($"dialog 0x{window:X} is not a file dialog");
        }
    }

    private void StopDialogRetry()
    {
        NativeMethods.KillTimer(_hwnd, (UIntPtr)DialogRetryTimerId);
        _dialogRetries = 0;
        _dialogRetryWindow = IntPtr.Zero;
    }

    private void DockTo(IntPtr dialog)
    {
        bool captured = FileDialogIntegration.Capture(dialog);

        if (_docked && _barTarget == dialog && _visible)
        {
            // The dialog is reported in front again. If the bar still has the
            // keyboard the user is typing into it; leave the search alone.
            if (!_passive && GetFocus() == _hwnd)
            {
                return;
            }

            GoPassive();
            return;
        }

        _docked = true;
        _passive = true;
        AppLog.Write("bar docked, passive");
        _barMode = BarMode.Dialog;
        _barTarget = dialog;
        _explorerTarget = IntPtr.Zero;
        _query = string.Empty;
        _searchCancel?.Cancel();
        _searching = false;
        _results = [];
        _selected = 0;
        _scrollOffset = 0;
        _filterLabel = null;

        PlaceWindow();
        _visible = true;
        NativeMethods.KillTimer(_hwnd, (UIntPtr)FocusCheckTimerId);
        NativeMethods.ShowWindow(_hwnd, SwShowNoActivate);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);

        ActivationHooks.TrackWindow(dialog, OnDockedDialogMoved, Undock);
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect bar);
        AppLog.Write($"docked under file dialog 0x{dialog:X} (captured={captured}) at {_anchorX},{_anchorY} size {bar.Width}x{bar.Height}, input at bottom={_inputAtBottom}");
    }

    private void OnDockedDialogMoved()
    {
        if (_docked && _visible && NativeMethods.IsWindow(_barTarget))
        {
            PlaceWindow();
        }
    }

    /// <summary>Back to the bare search line, focus left with the dialog.</summary>
    private void GoPassive([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        _passive = true;
        AppLog.Write($"bar passive ({caller})");
        _query = string.Empty;
        _searchCancel?.Cancel();
        _searching = false;
        _filterLabel = null;
        ApplyResults([]);
    }

    /// <summary>The user clicked or typed into the docked bar: it becomes a search.</summary>
    private void Activate(string initialQuery, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        _passive = false;
        AppLog.Write($"bar activated by {caller} with \"{initialQuery}\"");
        _query = initialQuery;
        _shownAt = Environment.TickCount64;
        FileDialogIntegration.Capture(_barTarget);
        ForceForeground();
        Refresh();
    }

    private void Undock()
    {
        if (!_docked)
        {
            return;
        }

        _docked = false;
        _passive = false;
        AppLog.Write("bar undocked");
        ActivationHooks.StopTrackingWindow();

        if (_visible && _barMode == BarMode.Dialog)
        {
            Hide();
        }
    }

    private const int SwShowNoActivate = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    /// <summary>
    /// Quick Switch: jump the dialog in front to the folder of the most recent
    /// Explorer window. With no Explorer window open, Glide opens over the
    /// dialog instead, listing recent folders to pick from.
    /// </summary>
    private void QuickSwitch()
    {
        IntPtr dialog = NativeMethods.GetForegroundWindow();
        List<string> folders = ExplorerFolders.GetOpenFolders();

        if (folders.Count > 0 && FileDialogIntegration.TrySwitchTo(dialog, folders[0]))
        {
            return;
        }

        Show();
    }

    /// <summary>
    /// True when the foreground window covers its whole monitor, such as a game
    /// or a full-screen video. Double Ctrl is ignored there, like Listary does.
    /// </summary>
    private bool IsForegroundFullScreen()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd || foreground == GetShellWindow() ||
            !GetWindowRect(foreground, out NativeMethods.Rect bounds))
        {
            return false;
        }

        var className = new System.Text.StringBuilder(32);
        if (NativeMethods.GetClassNameW(foreground, className, className.Capacity) > 0 &&
            className.ToString() is "Progman" or "WorkerW")
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(foreground, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };

        return NativeMethods.GetMonitorInfoW(monitor, ref info) &&
            bounds.Left <= info.Monitor.Left && bounds.Top <= info.Monitor.Top &&
            bounds.Right >= info.Monitor.Right && bounds.Bottom >= info.Monitor.Bottom;
    }

    private const uint InputMouse = 0;

    /// <summary>INPUT with the MOUSEINPUT union member, 40 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        private readonly uint _unionAlignment;
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref Input input, int size);

    private const uint CoinitApartmentThreaded = 0x2;

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    private const uint ModNoRepeat = 0x4000;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private void SetNotice(string message)
    {
        _notice = message;
        Console.Error.WriteLine($"Glide: {message}");

        if (_hwnd != IntPtr.Zero)
        {
            if (_visible)
            {
                UpdateWindowSize();
            }

            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private static string DescribeHotkey(HotkeySettings settings)
    {
        if (HotkeyParser.TryParse(settings, out uint mods, out uint vk))
        {
            return HotkeyParser.ToSpec(mods, vk);
        }

        return string.Join("+", settings.Modifiers.Append(settings.Key));
    }

    private void CreateResources()
    {
        _fontQuery = CreateFont(13, NativeMethods.FwNormal, UiNative.TextFontFace(display: true));
        _fontName = CreateFont(10.5, NativeMethods.FwSemiBold, UiNative.TextFontFace(display: false));
        _fontPath = CreateFont(8.5, NativeMethods.FwNormal, UiNative.TextFontFace(display: false));
        _fontGlyph = CreateFont(12, NativeMethods.FwNormal, UiNative.IconFontFace);
        _fontGlyphLarge = CreateFont(15, NativeMethods.FwNormal, UiNative.IconFontFace);
        _fontGlyphSmall = CreateFont(10, NativeMethods.FwNormal, UiNative.IconFontFace);
        _fontQueryCompact = CreateFont(11, NativeMethods.FwNormal, UiNative.TextFontFace(display: false));
        _fontRow = CreateFont(9.5, NativeMethods.FwNormal, UiNative.TextFontFace(display: false));

        CreateBrushes();
    }

    private void CreateBrushes()
    {
        _brushBackground = Brush(_palette.Background);
        _brushSelection = Brush(_palette.Selection);
        _brushAccent = Brush(_palette.Accent);
        _brushBorder = Brush(_palette.Border);
        _brushRaised = Brush(_palette.Raised);
        _brushAccentSoft = Brush(_palette.AccentSoft);
        _brushThumb = Brush(_palette.Placeholder);
    }

    private static IntPtr Brush(int rgb) => NativeMethods.CreateSolidBrush(Palette.ToColorRef(rgb));

    private static void SetColor(IntPtr dc, int rgb) => NativeMethods.SetTextColor(dc, Palette.ToColorRef(rgb));

    private IntPtr _fontGlyph;
    private IntPtr _fontGlyphLarge;
    private IntPtr _fontGlyphSmall;
    private IntPtr _fontQueryCompact;
    private IntPtr _fontRow;
    private IntPtr _brushRaised;
    private IntPtr _brushAccentSoft;
    private IntPtr _brushThumb;
    private readonly IconCache _icons = new();

    private IntPtr CreateFont(double points, int weight, string face = "Segoe UI")
    {
        int height = -(int)Math.Round(points * _dpi / 72.0);
        return NativeMethods.CreateFontW(
            height,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            1,
            0,
            0,
            5,
            0,
            face);
    }

    private string[] GetEffectiveIndexPaths()
    {
        if (_snapshot is not null)
        {
            return [];
        }

        if (_indexPaths.Length == 0)
        {
            _indexPaths = IndexLocator.Find();
        }

        return _indexPaths;
    }

    private void StartIndexing(string? scanRoot)
    {
        if (_snapshot is not null)
        {
            _store = _snapshot;
            Refresh();
            return;
        }

        if (_indexPaths.Length > 0)
        {
            Console.Error.WriteLine($"Glide: searching {_indexPaths.Length} index file(s) in {IndexLocator.Directory}");
            _scanRoot = Path.GetDirectoryName(_indexPaths[0]) ?? IndexLocator.Directory;
            Refresh();
            return;
        }

        _scanRoot = string.IsNullOrWhiteSpace(scanRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : scanRoot;
        _indexing = true;

        Task.Run(() =>
        {
            long start = Environment.TickCount64;

            try
            {
                EntryStore scanned = DirectoryScanner.Scan(_scanRoot, _maxEntries);
                Volatile.Write(ref _store, scanned);
                Console.Error.WriteLine($"Glide: indexed {scanned.Count} entries under {_scanRoot} in {Environment.TickCount64 - start} ms");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                Volatile.Write(ref _store, new EntryStore([]));
                Console.Error.WriteLine($"Glide: scan failed: {ex.Message}");
            }
            finally
            {
                NativeMethods.PostMessageW(_hwnd, WmIndexReady, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    private int MessageLoop()
    {
        while (true)
        {
            int result = NativeMethods.GetMessageW(out NativeMethods.Msg message, IntPtr.Zero, 0, 0);
            if (result <= 0)
            {
                break;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        DestroyResources();
        return 0;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        SearchWindow? window = s_instance;
        if (window is not null && window._hwnd == hwnd)
        {
            return window.HandleMessage(message, wParam, lParam);
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private IntPtr HandleMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_tray is not null && message == TrayIcon.TaskbarCreatedMessage)
        {
            // Explorer restarted and lost every notification icon.
            _tray.Show();
            return IntPtr.Zero;
        }

        switch (message)
        {
            case NativeMethods.WmPaint:
                Paint();
                return IntPtr.Zero;

            case NativeMethods.WmEraseBkgnd:
                return new IntPtr(1);

            case NativeMethods.WmKeyDown:
            case NativeMethods.WmSysKeyDown:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;

            case NativeMethods.WmChar:
                OnChar((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case NativeMethods.WmHotkey:
                if ((int)wParam == HotkeyId)
                {
                    OnMainHotkey();
                }
                else if ((int)wParam == QuickSwitchHotkeyId)
                {
                    QuickSwitch();
                }

                return IntPtr.Zero;

            case WmExplorerType:
                ShowFromExplorer((char)(wParam.ToInt64() & 0xFFFF), lParam);
                return IntPtr.Zero;

            case WmTray:
                OnTrayMessage((uint)(lParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case WmSearchDone:
                OnSearchDone();
                return IntPtr.Zero;

            case WmShowGlide:
                Show();
                return IntPtr.Zero;

            case WmShowSettings:
                ShowOptions();
                return IntPtr.Zero;

            case WmIndexReady:
                _indexing = false;
                Refresh();
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case NativeMethods.WmMouseWheel:
                short wheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ScrollBy(wheelDelta > 0 ? -3 : 3);
                return IntPtr.Zero;

            case NativeMethods.WmMouseMove:
                if (TryStartDrag(wParam, lParam))
                {
                    return IntPtr.Zero;
                }

                OnMouseMove(lParam);
                return IntPtr.Zero;

            case NativeMethods.WmMouseLeave:
                _trackingMouse = false;
                _hovered = -1;
                _hoveredSettings = false;
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDown:
                SelectRowAt(lParam);
                ArmDrag(lParam);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonUp:
                _dragRow = -1;
                if (IsSettingsButtonClick(lParam))
                {
                    ShowOptions();
                    return IntPtr.Zero;
                }
                if (SelectRowAt(lParam))
                {
                    OpenSelected();
                }
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDblClk:
                if (SelectRowAt(lParam))
                {
                    OpenSelected();
                }
                return IntPtr.Zero;

            case NativeMethods.WmRButtonUp:
                SelectRowAt(lParam);
                ShowActionsMenu();
                return IntPtr.Zero;

            case NativeMethods.WmActivate:
                OnActivate((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case NativeMethods.WmTimer:
                if (wParam.ToInt64() == DialogRetryTimerId)
                {
                    RetryDialog();
                    return IntPtr.Zero;
                }

                if (wParam.ToInt64() == TrimTimerId)
                {
                    TrimMemory();
                    return IntPtr.Zero;
                }

                if (wParam.ToInt64() == FocusCheckTimerId)
                {
                    NativeMethods.KillTimer(_hwnd, (UIntPtr)FocusCheckTimerId);

                    if (_visible && !_dragging && _notice is null && _settings.HideWhenLosingFocus && NativeMethods.GetForegroundWindow() != _hwnd)
                    {
                        Hide();
                    }
                }

                return IntPtr.Zero;

            case NativeMethods.WmDestroy:
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
                NativeMethods.UnregisterHotKey(_hwnd, QuickSwitchHotkeyId);
                ActivationHooks.StopWatchingForeground();
                ActivationHooks.StopKeyboardHook();
                _tray?.Dispose();
                _tray = null;
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            case NativeMethods.WmSize:
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WmSettingChange:
                // Handle system theme change
                ApplyTheme(_settings.Theme);
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProcW(_hwnd, message, wParam, lParam);
        }
    }

    private void OnKeyDown(int key)
    {
        _lastMouseX = -1;
        _lastMouseY = -1;
        _hovered = -1;

        bool control = (NativeMethods.GetKeyState(NativeMethods.VkControl) & 0x8000) != 0;
        bool shift = (NativeMethods.GetKeyState(NativeMethods.VkShift) & 0x8000) != 0;

        switch (key)
        {
            case NativeMethods.VkEscape:
                if (_docked)
                {
                    // Back to the dialog; the bar stays under it.
                    GoPassive();
                    NativeMethods.SetForegroundWindow(_barTarget);
                }
                else
                {
                    Hide();
                }
                break;

            case NativeMethods.VkReturn:
                if (control)
                {
                    OpenContainingFolder();
                }
                else if (shift)
                {
                    CopyPathToClipboard();
                }
                else
                {
                    OpenSelected();
                }
                break;

            case NativeMethods.VkTab:
                CompleteSelectedFolder();
                break;

            case NativeMethods.VkUp:
                MoveSelection(-1);
                break;

            case NativeMethods.VkDown:
                MoveSelection(1);
                break;

            case NativeMethods.VkPrior: // Page Up
                PageSelection(-1);
                break;

            case NativeMethods.VkNext: // Page Down
                PageSelection(1);
                break;

            case NativeMethods.VkHome:
                SelectFirst();
                break;

            case NativeMethods.VkEnd:
                SelectLast();
                break;

            case >= 0x31 and <= 0x38 when control: // Ctrl+1 to Ctrl+8: the top eight results
                int digit = key - 0x31;
                int targetResult = digit;
                if (targetResult < _results.Length)
                {
                    _selected = targetResult;
                    OpenSelected();
                }
                break;

            case NativeMethods.VkBack:
                Backspace();
                break;

            case NativeMethods.VkV when control:
                Paste();
                break;

            case NativeMethods.VkA when control:
                // Select all - copy current query to clipboard
                CopyQueryToClipboard();
                break;

            case NativeMethods.VkC when control:
                CopyPathToClipboard();
                break;

            case NativeMethods.VkF2:
            case NativeMethods.VkApps: // Context Menu key
                ShowActionsMenu();
                break;
        }
    }

    private void OnChar(char character)
    {
        if (character < 32 || character == 127 || _query.Length >= MaxQueryLength)
        {
            return;
        }

        _query += character;
        Refresh();
    }

    private void Backspace()
    {
        if (_query.Length == 0)
        {
            return;
        }

        int remove = _query.Length >= 2 && char.IsLowSurrogate(_query[^1]) && char.IsHighSurrogate(_query[^2])
            ? 2
            : 1;

        _query = _query[..^remove];
        Refresh();
    }

    private void Paste()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText) || !NativeMethods.OpenClipboard(_hwnd))
        {
            return;
        }

        try
        {
            IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            IntPtr pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            try
            {
                string? text = Marshal.PtrToStringUni(pointer);
                if (text is null)
                {
                    return;
                }

                string single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (single.Length == 0)
                {
                    return;
                }

                int room = MaxQueryLength - _query.Length;
                _query += single.Length <= room ? single : single[..room];
                Refresh();
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private void MoveSelection(int delta)
    {
        if (_results.Length == 0)
        {
            return;
        }

        int prevSelected = _selected;
        _selected = Math.Clamp(_selected + delta, 0, _results.Length - 1);

        int visibleCount = GetVisibleRowCount();

        if (_selected < _scrollOffset)
        {
            _scrollOffset = _selected;
        }
        else if (_selected >= _scrollOffset + visibleCount)
        {
            _scrollOffset = _selected - visibleCount + 1;
        }

        if (_selected != prevSelected)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void PageSelection(int direction)
    {
        if (_results.Length == 0)
        {
            return;
        }

        int visibleCount = GetVisibleRowCount();
        MoveSelection(direction * visibleCount);
    }

    private void SelectFirst()
    {
        if (_results.Length == 0)
        {
            return;
        }

        _selected = 0;
        _scrollOffset = 0;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void SelectLast()
    {
        if (_results.Length == 0)
        {
            return;
        }

        _selected = _results.Length - 1;
        int visibleCount = GetVisibleRowCount();
        _scrollOffset = Math.Max(0, _results.Length - visibleCount);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void ScrollBy(int delta)
    {
        if (_results.Length == 0)
        {
            return;
        }

        int visibleCount = GetVisibleRowCount();
        int maxScroll = Math.Max(0, _results.Length - visibleCount);
        int newOffset = Math.Clamp(_scrollOffset + delta, 0, maxScroll);
        if (newOffset != _scrollOffset)
        {
            _scrollOffset = newOffset;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private int GetVisibleRowCount()
    {
        return Math.Max(1, Math.Min(_maxRows, _maxResults) - (_notice is null ? 0 : 1));
    }

    private bool IsSettingsButtonClick(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);
        int top = HeaderTop(client.Height);
        return y >= top && y <= top + HeaderHeight && x >= client.Width - SidePadding - Scale(30);
    }

    private void OnMouseMove(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (x == _lastMouseX && y == _lastMouseY)
        {
            return;
        }

        _lastMouseX = x;
        _lastMouseY = y;

        if (!_trackingMouse)
        {
            var tme = new NativeMethods.TrackMouseEventStructure
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.TrackMouseEventStructure>(),
                dwFlags = NativeMethods.TmeLeave,
                hwndTrack = _hwnd,
                dwHoverTime = 0
            };
            NativeMethods.TrackMouseEvent(ref tme);
            _trackingMouse = true;
        }

        bool wasHoveredSettings = _hoveredSettings;
        int prevHovered = _hovered;

        _hoveredSettings = IsSettingsButtonClick(lParam);
        _hovered = GetRowAt(lParam);

        if (_hovered != -1 && _hovered != _selected)
        {
            _selected = _hovered;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
        else if (_hovered != prevHovered || _hoveredSettings != wasHoveredSettings)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private int GetRowAt(IntPtr lParam)
    {
        if (_results.Length == 0 && _calculatorResult is null)
        {
            return -1;
        }

        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);

        int headerTop = HeaderTop(client.Height);
        if (y >= headerTop && y < headerTop + HeaderHeight)
        {
            return -1;
        }

        int noticeRows = _notice is null ? 0 : 1;
        int offset = y - ListTop;
        if (offset < 0)
        {
            return -1;
        }

        int visualRow = (offset / RowHeight) - noticeRows;
        if (visualRow < 0)
        {
            return -1;
        }

        // The calculator row stands alone; results are not listed under it.
        if (_calculatorResult is not null)
        {
            return visualRow == 0 ? 0 : -1;
        }

        int targetIndex = _scrollOffset + visualRow;
        if (visualRow < GetVisibleRowCount() && targetIndex >= 0 && targetIndex < _results.Length)
        {
            return targetIndex;
        }

        return -1;
    }

    /// <summary>
    /// Remembers where the button went down when it went down on a file or
    /// folder row; calculator, web and command rows have nothing to drag.
    /// </summary>
    private void ArmDrag(IntPtr lParam)
    {
        _dragRow = -1;
        if (_calculatorResult is not null || _searching)
        {
            return;
        }

        int row = GetRowAt(lParam);
        if (row < 0 || row >= _results.Length ||
            _results[row].Kind is SearchResultKind.WebSearch or SearchResultKind.Command)
        {
            return;
        }

        _dragRow = row;
        _dragStartX = (short)(lParam.ToInt64() & 0xFFFF);
        _dragStartY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);    }

    /// <summary>
    /// Starts the shell drag once the pointer, button still down, has moved
    /// far enough from where it was pressed. Returns true when it handled the
    /// move, so hover selection does not jump to another row mid-press.
    /// </summary>
    private bool TryStartDrag(IntPtr wParam, IntPtr lParam)
    {
        const long MkLButton = 0x0001;

        if (_dragRow == -1)
        {
            return false;
        }

        if ((wParam.ToInt64() & MkLButton) == 0)
        {
            _dragRow = -1;
            return false;
        }

        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        if (!FileDrag.PassedThreshold(_dragStartX, _dragStartY, x, y))
        {
            return true;
        }

        int row = _dragRow;
        _dragRow = -1;
        if (row >= _results.Length)
        {
            return true;
        }

        string path = _results[row].Entry.Path;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            AppLog.Write($"drag: not on disk, skipped: {path}");
            return true;
        }

        // The drag loop is modal and swallows the button-up, so the press that
        // started it never reaches "open". _dragging also keeps the launcher
        // from hiding while the pointer is over other windows.
        _dragging = true;
        bool dropped;
        try
        {
            dropped = FileDrag.Start(_hwnd, path);
        }
        finally
        {
            _dragging = false;
        }

        if (dropped)
        {
            HistoryStore.Default.Add(path);
            if (!_docked)
            {
                Hide();
            }
        }

        return true;
    }

    private bool SelectRowAt(IntPtr lParam)
    {
        int row = GetRowAt(lParam);
        if (row != -1)
        {
            _selected = row;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
            return true;
        }

        return false;
    }

    private void OpenSelected()
    {
        // If calculator result is showing and selected, copy it
        if (_calculatorResult is not null && _selected == 0)
        {
            CopyToClipboard(_calculatorResult);
            return;
        }

        if (_searching)
        {
            // The rows on screen belong to an earlier keystroke.
            _openWhenReady = true;
            return;
        }

        if (_results.Length == 0)
        {
            return;
        }

        SearchResult selected = _results[Math.Clamp(_selected, 0, _results.Length - 1)];
        string path = selected.Entry.Path;

        switch (selected.Kind)
        {
            case SearchResultKind.WebSearch:
                Hide();
                FileDialogIntegration.ReleaseCapturedDialog();
                NativeMethods.ShellExecuteW(IntPtr.Zero, "open", path, null, null, NativeMethods.SwShow);
                return;

            case SearchResultKind.Command:
                Hide();
                FileDialogIntegration.ReleaseCapturedDialog();
                RunCommand(path, selected.Title);
                return;
        }

        IntPtr explorer = _explorerTarget;
        Hide();

        // Opened by typing in Explorer: a folder opens in that same window.
        if (explorer != IntPtr.Zero &&
            (selected.Entry.IsDirectory || selected.Kind == SearchResultKind.OpenedFolder || Directory.Exists(path)) &&
            NavigateExplorer(explorer, path))
        {
            HistoryStore.Default.Add(path);
            return;
        }

        if (_settings.Integration.EnableFileDialogIntegration &&
            FileDialogIntegration.TryInjectPath(path))
        {
            HistoryStore.Default.Add(path);
            return;
        }

        if (selected.Entry.IsDirectory || Directory.Exists(path))
        {
            OpenFolder(path);
        }
        else
        {
            NativeMethods.ShellExecuteW(IntPtr.Zero, "open", path, null, null, NativeMethods.SwShow);
        }

        HistoryStore.Default.Add(path);
    }

    private TrayIcon? _tray;

    private void OnTrayMessage(uint mouseMessage)
    {
        const uint LeftButtonUp = 0x0202;
        const uint RightButtonUp = 0x0205;
        const uint ContextMenu = 0x007B;

        switch (mouseMessage)
        {
            case LeftButtonUp:
                Show();
                break;

            case RightButtonUp:
            case ContextMenu:
                ShowTrayMenu();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        const int OpenId = 1;
        const int SettingsId = 2;
        const int ExitId = 3;
        const uint WmNull = 0;

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AppendMenuW(menu, NativeMethods.MfString, (UIntPtr)OpenId, "Open Glide");
        NativeMethods.AppendMenuW(menu, NativeMethods.MfString, (UIntPtr)SettingsId, "Settings...");
        NativeMethods.AppendMenuW(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MfString, (UIntPtr)ExitId, "Exit");

        // The menu only closes on an outside click if the owner is the
        // foreground window while it is open.
        NativeMethods.GetCursorPos(out NativeMethods.Point point);
        NativeMethods.SetForegroundWindow(_hwnd);
        int command = NativeMethods.TrackPopupMenuEx(
            menu,
            NativeMethods.TpmRetcmd | NativeMethods.TpmNonotify | NativeMethods.TpmRightButton | NativeMethods.TpmBottomAlign,
            point.X,
            point.Y,
            _hwnd,
            IntPtr.Zero);
        NativeMethods.PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);

        switch (command)
        {
            case OpenId:
                Show();
                break;

            case SettingsId:
                ShowOptions();
                break;

            case ExitId:
                NativeMethods.DestroyWindow(_hwnd);
                break;
        }
    }

    /// <summary>
    /// The main shortcut, with Listary's two options: ignored over a
    /// full-screen app, and either hiding Glide or keeping it open when
    /// pressed again.
    /// </summary>
    private void OnMainHotkey()
    {
        if (_visible)
        {
            if (_settings.Hotkey.PressAgainHides)
            {
                Hide();
            }
            else
            {
                ForceForeground();
            }

            return;
        }

        if (_settings.Hotkey.DisableInFullScreen && IsForegroundFullScreen())
        {
            return;
        }

        Show();
    }

    private void RunCommand(string target, string? title = null)
    {
        switch (target)
        {
            case CommandCatalog.SettingsTarget:
                ShowOptions();
                break;

            case CommandCatalog.ExitTarget:
                NativeMethods.DestroyWindow(_hwnd);
                break;

            default:
                CustomCommandSettings? custom = _settings.CustomCommands.FirstOrDefault(c => c.Title == title && c.Target == target);
                string? arguments = string.IsNullOrWhiteSpace(custom?.Arguments) ? null : custom.Arguments;
                NativeMethods.ShellExecuteW(IntPtr.Zero, "open", target, arguments, null, NativeMethods.SwShow);
                break;
        }
    }

    /// <summary>
    /// Opens a folder with the file manager chosen in Options (Listary's
    /// "Open folder with"), or with Explorer when none is set.
    /// </summary>
    private void OpenFolder(string folder, string? select = null)
    {
        string manager = _settings.OpenFolderWith.Path.Trim().Trim('"');

        if (manager.Length > 0)
        {
            string arguments = _settings.OpenFolderWith.Arguments.Replace("%1", folder.TrimEnd('\\'), StringComparison.Ordinal);
            IntPtr result = NativeMethods.ShellExecuteW(IntPtr.Zero, "open", manager, arguments, null, NativeMethods.SwShow);
            if ((long)result > 32)
            {
                return;
            }
        }

        if (select is not null && OpenFolderAndSelect(select))
        {
            return;
        }

        NativeMethods.ShellExecuteW(IntPtr.Zero, "open", folder, null, null, NativeMethods.SwShow);
    }

    /// <summary>
    /// Opens the item's parent folder in Explorer with the item selected and
    /// scrolled into view (what "Open file location" does), so a large folder
    /// needs no scrolling.
    /// </summary>
    private static bool OpenFolderAndSelect(string path)
    {
        IntPtr pidl = NativeMethods.ILCreateFromPathW(path);
        if (pidl == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) >= 0;
        }
        finally
        {
            NativeMethods.ILFree(pidl);
        }
    }

    /// <summary>Built-in commands followed by the user's own.</summary>
    private List<LauncherCommand> AllCommands()
    {
        var commands = new List<LauncherCommand>(CommandCatalog.BuiltIn);
        foreach (CustomCommandSettings custom in _settings.CustomCommands)
        {
            commands.Add(new LauncherCommand(custom.Title, custom.Target, [], custom.Arguments));
        }

        return commands;
    }

    /// <summary>
    /// Tab on a folder row types that folder into the query, so the next
    /// keystrokes browse inside it, like completing a path in a shell.
    /// </summary>
    private void CompleteSelectedFolder()
    {
        if (_results.Length == 0)
        {
            return;
        }

        SearchResult selected = _results[Math.Clamp(_selected, 0, _results.Length - 1)];
        if (selected.Kind is SearchResultKind.WebSearch or SearchResultKind.Command)
        {
            return;
        }

        string path = selected.Entry.Path;
        if (!selected.Entry.IsDirectory && !Directory.Exists(path))
        {
            return;
        }

        _query = path.EndsWith('\\') ? path : path + "\\";
        Refresh();
    }

    /// <summary>Opens Windows Terminal in the folder, or Command Prompt where it is missing.</summary>
    private static void OpenInTerminal(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (!Directory.Exists(folder))
        {
            return;
        }

        IntPtr result = NativeMethods.ShellExecuteW(IntPtr.Zero, "open", "wt.exe", $"-d \"{folder.TrimEnd('\\')}\\.\"", folder, NativeMethods.SwShow);
        if ((long)result <= 32)
        {
            NativeMethods.ShellExecuteW(IntPtr.Zero, "open", "cmd.exe", null, folder, NativeMethods.SwShow);
        }
    }

    private void OpenContainingFolder()
    {
        if (_results.Length == 0)
        {
            return;
        }

        string path = _results[Math.Clamp(_selected, 0, _results.Length - 1)].Entry.Path;
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            IntPtr explorer = _explorerTarget;
            Hide();

            bool handled = (explorer != IntPtr.Zero && NavigateExplorer(explorer, folder, path)) ||
                (_settings.Integration.EnableFileDialogIntegration && FileDialogIntegration.TryInjectPath(folder));

            if (!handled)
            {
                OpenFolder(folder, path);
            }

            HistoryStore.Default.Add(folder);
        }
    }

    /// <summary>The Explorer window Glide was opened from by typing in it, or zero.</summary>
    private IntPtr _explorerTarget;

    /// <summary>
    /// A letter typed in an Explorer file list: open Glide with it, and
    /// remember the window so a chosen folder opens there.
    /// </summary>
    private void ShowFromExplorer(char typed, IntPtr window)
    {
        if (_docked && _visible && window == _barTarget && _passive)
        {
            // Typing in the dialog's file list moves into its docked bar.
            Activate(typed.ToString());
            return;
        }

        if (_visible)
        {
            // Letters typed quickly arrive before the bar has taken focus;
            // they belong to the query, not to Explorer.
            if (window == _barTarget && _query.Length < MaxQueryLength)
            {
                _query += typed;
                Refresh();
            }

            return;
        }

        var className = new System.Text.StringBuilder(16);
        NativeMethods.GetClassNameW(window, className, className.Capacity);
        BarMode mode = className.ToString() == "#32770" ? BarMode.Dialog : BarMode.Explorer;

        Show(window, typed.ToString(), mode);
    }

    /// <summary>The Explorer window or file dialog a bar belongs to, or zero.</summary>
    private IntPtr _barTarget;

    private static bool NavigateExplorer(IntPtr explorer, string folder, string? select = null)
    {
        if (!NativeMethods.IsWindow(explorer) || !ExplorerFolders.Navigate(explorer, folder, select))
        {
            return false;
        }

        NativeMethods.SetForegroundWindow(explorer);
        return true;
    }

    private void CopyPathToClipboard()
    {
        if (_results.Length == 0)
        {
            return;
        }

        string path = _results[Math.Clamp(_selected, 0, _results.Length - 1)].Entry.Path;
        CopyToClipboard(path);
    }

    private void CopyQueryToClipboard()
    {
        if (_query.Length == 0)
        {
            return;
        }

        CopyToClipboard(_query);
    }

    private void CopyToClipboard(string text)
    {
        if (!NativeMethods.OpenClipboard(_hwnd))
        {
            return;
        }

        try
        {
            NativeMethods.EmptyClipboard();

            // Allocate global memory for the string
            int byteCount = (text.Length + 1) * 2; // Unicode = 2 bytes per char
            IntPtr hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (uint)byteCount);
            if (hGlobal == IntPtr.Zero)
            {
                return;
            }

            IntPtr ptr = NativeMethods.GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(hGlobal);
                return;
            }

            try
            {
                // Copy string to global memory
                byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hGlobal);
            }

            NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, hGlobal);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private void ShowActionsMenu()
    {
        if (_results.Length == 0)
        {
            ShowEmptyContextMenu();
            return;
        }

        SearchResult selected = _results[Math.Clamp(_selected, 0, _results.Length - 1)];
        if (selected.Kind is SearchResultKind.WebSearch or SearchResultKind.Command)
        {
            ShowEmptyContextMenu();
            return;
        }

        ShowContextMenu(selected.Entry.Path);
    }

    private void ShowEmptyContextMenu()
    {
        NativeMethods.GetCursorPos(out NativeMethods.Point pt);
        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)1005, "Settings...");

        NativeMethods.SetForegroundWindow(_hwnd);
        int cmd = NativeMethods.TrackPopupMenuEx(
            hMenu,
            NativeMethods.TpmRetcmd | NativeMethods.TpmNonotify | NativeMethods.TpmRightButton,
            pt.X,
            pt.Y,
            _hwnd,
            IntPtr.Zero);

        NativeMethods.DestroyMenu(hMenu);
        if (cmd == 1005)
        {
            ShowOptions();
        }
    }

    private void ShowContextMenu(string path)
    {
        NativeMethods.GetCursorPos(out NativeMethods.Point pt);

        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        bool isBookmarked = BookmarkStore.Default.Contains(path);

        // Build menu items
        int id = 1000;
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)id++, "Open");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)id++, "Open Containing Folder");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)id++, "Copy Path");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)1006, "Open in Terminal");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString | (isBookmarked ? NativeMethods.MfChecked : 0), (UIntPtr)id++, isBookmarked ? "Remove Bookmark" : "Add Bookmark");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)id++, "Properties");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)1005, "Settings...");

        // Track the selected path for the menu handler
        _contextMenuPath = path;

        // Show the menu
        NativeMethods.SetForegroundWindow(_hwnd);
        int cmd = NativeMethods.TrackPopupMenuEx(
            hMenu,
            NativeMethods.TpmRetcmd | NativeMethods.TpmNonotify | NativeMethods.TpmRightButton,
            pt.X,
            pt.Y,
            _hwnd,
            IntPtr.Zero);

        _contextMenuPath = null;
        NativeMethods.DestroyMenu(hMenu);

        // Handle the selected command
        HandleContextMenuCommand(cmd, path);
    }

    private string? _contextMenuPath;

    private void HandleContextMenuCommand(int cmd, string path)
    {
        switch (cmd)
        {
            case 1000: // Open
                OpenSelected();
                break;
            case 1001: // Open Containing Folder
                OpenContainingFolder();
                break;
            case 1002: // Copy Path
                CopyPathToClipboard();
                break;
            case 1003: // Add/Remove Bookmark
                if (BookmarkStore.Default.Contains(path))
                {
                    BookmarkStore.Default.Remove(path);
                }
                else
                {
                    BookmarkStore.Default.Add(path);
                }
                Refresh();
                break;
            case 1004: // Properties
                ShowFileProperties(path);
                break;
            case 1005: // Settings
                ShowOptions();
                break;
            case 1006: // Open in Terminal
                Hide();
                OpenInTerminal(path);
                break;
        }
    }

    private void ShowFileProperties(string path)
    {
        // Use ShellExecute with "properties" verb
        // Ensure we have an absolute path
        string fullPath = Path.GetFullPath(path);
        IntPtr result = NativeMethods.ShellExecuteW(IntPtr.Zero, "properties", fullPath, null, null, NativeMethods.SwShow);

        // If properties verb fails (returns <= 32), try alternative approach
        if ((long)result <= 32)
        {
            // Try with the parent directory
            string? parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                NativeMethods.ShellExecuteW(IntPtr.Zero, "properties", fullPath, null, parentDir, NativeMethods.SwShow);
            }
        }
    }

    private string? _calculatorResult;

    private void Refresh()
    {
        // Text in a passive docked bar means the user is typing into it,
        // whichever way the keys arrived: it is a search now. Activation can
        // bounce back to the dialog while the bar keeps the keyboard, and a
        // bar left passive would never show its list.
        if (_docked && _passive && _query.Length > 0)
        {
            _passive = false;
            FileDialogIntegration.Capture(_barTarget);
            AppLog.Write($"bar taken out of passive by typing \"{_query}\"");
        }

        _calculatorResult = null;

        // Check if query is a calculator expression
        if (IsCalculatorQuery(_query.AsSpan()))
        {
            if (Calculator.TryEvaluate(GetCalculatorExpression(_query.AsSpan()), out double result))
            {
                if (!double.IsNaN(result))
                {
                    _calculatorResult = FormatCalculatorResult(result);
                }
                else
                {
                    _calculatorResult = "Cannot divide by zero";
                }
            }
        }

        EntryStore? store = Volatile.Read(ref _store);
        string[] indexPaths = GetEffectiveIndexPaths();

        QueryIntent intent = _calculatorResult is null
            ? QueryIntentParser.Parse(_query, _settings.SearchFilters, _settings.WebSearches)
            : new QueryIntent(QueryKind.Search, _query, null, null, null, null);
        _filterLabel = intent.Kind == QueryKind.Browse ? null : intent.FilterName;

        // Every keystroke starts a new generation. Whatever an older search
        // was doing is cancelled, and its results are dropped if they still
        // arrive, so the list always matches the text on screen.
        int generation = ++_generation;
        _searchCancel?.Cancel();
        _searchCancel = null;
        _searching = false;
        _openWhenReady = false;

        if (intent.Kind == QueryKind.Web)
        {
            ApplyResults([WebRow(intent)]);
            return;
        }

        if (_query.Length == 0)
        {
            // History and Explorer folders: no index scan, and the Explorer
            // query is COM that belongs to this thread.
            ApplyResults(EmptyQueryRows(indexPaths, store));
            return;
        }

        if (intent.Kind == QueryKind.Browse)
        {
            bool showHidden = !_settings.FileSearch.HideSystemAndHidden;
            StartSearch(generation, _ => [.. FolderBrowser.List(intent.Folder!, intent.Text, MaxQueryResults, showHidden)]);
            return;
        }

        string query = _query;
        bool isCalculation = _calculatorResult is not null;
        WebSearchSettings[] webSearches = _settings.WebSearches;
        List<LauncherCommand> allCommands = AllCommands();
        string[] recent = HistoryStore.Default.Items;

        StartSearch(generation, cancellation =>
        {
            SearchResult[] files;

            if (indexPaths.Length > 0)
            {
                files = [.. IndexSearchSource.SearchMany(indexPaths, intent.Text, MaxQueryResults, intent.Constraints, cancellation)];
            }
            else if (store is not null)
            {
                files = intent.Constraints is null
                    ? store.Search(intent.Text, MaxQueryResults).ToArray()
                    : store.Search(intent.Text, MaxQueryResults * 4)
                        .Where(r => intent.Constraints.Accepts(r.Entry.Path, r.Entry.IsDirectory))
                        .Take(MaxQueryResults)
                        .ToArray();
            }
            else
            {
                files = [];
            }

            // What the user opened before comes first among good matches.
            files = RecentBoost.Apply(files, recent, intent.Text, MaxQueryResults, intent.Constraints);

            // Commands and bookmarks lead plain queries; a filtered query asked
            // for files and gets only files.
            if (intent.Constraints is null)
            {
                files = MergeBookmarks(intent.Text, files);
                files = [.. CommandRows(intent.Text, allCommands), .. files];

                // Nothing on disk: offer the first web search, as Listary does.
                if (files.Length == 0 && webSearches.Length > 0 && !isCalculation)
                {
                    WebSearchSettings web = webSearches[0];
                    files = [WebRow(new QueryIntent(QueryKind.Web, query.Trim(), null, web.Name, web, null))];
                }
            }

            return files;
        });

        // Paint the new query text now; the rows follow when the scan ends.
        UpdateWindowSize();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private sealed record PendingResults(int Generation, SearchResult[] Results);

    private int _generation;
    private CancellationTokenSource? _searchCancel;
    private PendingResults? _pending;
    private bool _searching;
    private bool _openWhenReady;

    /// <summary>
    /// Runs one query off the window thread. The window keeps taking keys and
    /// painting while the index is scanned; the results come back through
    /// <see cref="WmSearchDone"/> and are applied only if no newer query
    /// started meanwhile.
    /// </summary>
    private void StartSearch(int generation, Func<CancellationToken, SearchResult[]> search)
    {
        // Not disposed on purpose: a cancelled source owns no timer or handle,
        // and disposing it under a running Parallel.For can throw there.
        var source = new CancellationTokenSource();
        _searchCancel = source;
        _searching = true;
        CancellationToken cancellation = source.Token;
        IntPtr hwnd = _hwnd;

        Task.Run(() =>
        {
            SearchResult[] results;

            try
            {
                results = search(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the window must get an answer; a search
                // that never reports back leaves the list waiting forever.
                AppLog.Write($"search for \"{generation}\" failed: {ex}");
                results = [];
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Volatile.Write(ref _pending, new PendingResults(generation, results));
            NativeMethods.PostMessageW(hwnd, WmSearchDone, generation, IntPtr.Zero);
        });
    }

    private void OnSearchDone()
    {
        PendingResults? pending = Interlocked.Exchange(ref _pending, null);
        if (pending is not null && _docked)
        {
            AppLog.Write($"bar search {pending.Generation} done: {pending.Results.Length} rows (current {_generation}, passive={_passive}, visible={_visible})");
        }

        if (pending is null || pending.Generation != _generation)
        {
            return;
        }

        _searching = false;
        _searchCancel = null;
        ApplyResults(pending.Results);

        // Enter pressed while the scan was running opens the top result of
        // the query the user actually typed, not of the previous keystroke.
        if (_openWhenReady)
        {
            _openWhenReady = false;
            OpenSelected();
        }
    }

    private void ApplyResults(SearchResult[] results)
    {
        _results = results;
        _selected = 0;
        _scrollOffset = 0;
        UpdateWindowSize();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>Shown next to the query when a filter such as <c>doc:</c> is active.</summary>
    private string? _filterLabel;

    private static SearchResult WebRow(QueryIntent intent)
    {
        return new SearchResult(
            new FileEntry(intent.WebUrl ?? string.Empty, false, 0, default),
            0,
            [],
            SearchResultKind.WebSearch,
            $"Search {intent.FilterName} for “{intent.Text}”");
    }

    private static SearchResult[] CommandRows(string query, IReadOnlyList<LauncherCommand> catalog)
    {
        List<LauncherCommand> commands = CommandCatalog.Match(query, catalog);
        if (commands.Count == 0)
        {
            return [];
        }

        var rows = new SearchResult[commands.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new SearchResult(
                new FileEntry(commands[i].Target, false, 0, default),
                0,
                [],
                SearchResultKind.Command,
                commands[i].Title);
        }

        return rows;
    }

    /// <summary>
    /// The empty query. Over a file dialog the folders open in Explorer come
    /// first, which is Listary's Quick Switch list; history follows.
    /// </summary>
    private SearchResult[] EmptyQueryRows(string[] indexPaths, EntryStore? store)
    {
        var rows = new List<SearchResult>();

        if (FileDialogIntegration.HasCapturedDialog)
        {
            foreach (string folder in ExplorerFolders.GetOpenFolders())
            {
                rows.Add(new SearchResult(new FileEntry(folder, true, 0, default), 0, [], SearchResultKind.OpenedFolder));
            }
        }

        foreach (string path in HistoryStore.Default.Items)
        {
            if (rows.Count >= MaxQueryResults)
            {
                break;
            }

            if (!rows.Exists(r => string.Equals(r.Entry.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                // Only local fixed drives are checked: a history entry on a
                // disconnected share would stall for the network timeout.
                if (HistoryStore.IsKnownMissing(path))
                {
                    HistoryStore.Default.Remove(path);
                    continue;
                }

                bool isDirectory = HistoryStore.IsKnownDirectory(path);
                rows.Add(new SearchResult(new FileEntry(path, isDirectory, 0, DateTime.MinValue), 0, [], SearchResultKind.History));
            }
        }

        if (rows.Count > 0)
        {
            return [.. rows];
        }

        if (indexPaths.Length > 0)
        {
            return [.. IndexSearchSource.SearchMany(indexPaths, string.Empty, MaxQueryResults)];
        }

        return store is null ? [] : store.Search(string.Empty, MaxQueryResults).ToArray();
    }

    private SearchResult[] MergeBookmarks(string query, SearchResult[] results)
    {
        if (query.Length == 0)
        {
            return results;
        }

        string[] bookmarks = BookmarkStore.Default.Items;
        if (bookmarks.Length == 0)
        {
            return results;
        }

        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matchedBookmarks = new List<SearchResult>();

        foreach (string bookmark in bookmarks)
        {
            if (!MatchesQuery(bookmark, queryTerms))
            {
                continue;
            }

            bool alreadyListed = false;

            foreach (SearchResult result in results)
            {
                if (string.Equals(result.Entry.Path, bookmark, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyListed = true;
                    break;
                }
            }

            if (!alreadyListed)
            {
                matchedBookmarks.Add(new SearchResult(
                    new FileEntry(bookmark, HistoryStore.IsKnownDirectory(bookmark), 0, DateTime.MinValue),
                    0,
                    [],
                    SearchResultKind.Bookmark));
            }
        }

        if (matchedBookmarks.Count == 0)
        {
            return results;
        }

        // Merge: bookmarks first, then file results
        var merged = new SearchResult[matchedBookmarks.Count + results.Length];
        matchedBookmarks.CopyTo(merged, 0);
        results.CopyTo(merged, matchedBookmarks.Count);

        // Trim to MaxQueryResults if needed
        if (merged.Length > MaxQueryResults)
        {
            Array.Resize(ref merged, MaxQueryResults);
        }

        return merged;
    }

    private static bool MatchesQuery(string path, string[] queryTerms)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        foreach (string term in queryTerms)
        {
            if (!name.Contains(term.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsCalculatorQuery(ReadOnlySpan<char> query)
    {
        if (query.Length == 0)
        {
            return false;
        }

        // Leading '=' explicitly means calculator
        if (query[0] == '=')
        {
            return true;
        }

        // Expression-only: starts with digit, minus, or parenthesis
        char c = query[0];
        return char.IsDigit(c) || c == '-' || c == '(';
    }

    private static ReadOnlySpan<char> GetCalculatorExpression(ReadOnlySpan<char> query)
    {
        if (query.Length > 0 && query[0] == '=')
        {
            return query[1..].Trim();
        }
        return query.Trim();
    }

    private static string FormatCalculatorResult(double value)
    {
        // Show as integer if whole number, otherwise decimal
        if (value == Math.Floor(value) && Math.Abs(value) < 1e12)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    private void Show(IntPtr target = default, string initialQuery = "", BarMode mode = BarMode.None)
    {
        if (_docked)
        {
            // The launcher takes over; the dialog gets its bar back when it
            // next comes to the front.
            _docked = false;
            _passive = false;
            ActivationHooks.StopTrackingWindow();
        }

        _barMode = target == IntPtr.Zero ? BarMode.None : mode;
        _barTarget = target;
        _explorerTarget = _barMode == BarMode.Explorer ? target : IntPtr.Zero;

        if (_settings.Integration.EnableFileDialogIntegration)
        {
            FileDialogIntegration.CaptureActiveDialog(_hwnd);
        }

        _visible = true;
        _shownAt = Environment.TickCount64;

        PlaceWindow();
        _query = initialQuery;
        Refresh();

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
        ForceForeground();
        NativeMethods.SetTimer(_hwnd, (UIntPtr)FocusCheckTimerId, ActivationGraceMs, IntPtr.Zero);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>
    /// Windows refuses SetForegroundWindow when another process owns the
    /// foreground, then reports WA_INACTIVE immediately. Attaching to the
    /// foreground thread lets the hotkey actually take focus.
    /// </summary>
    private void ForceForeground()
    {
        if (NativeMethods.SetForegroundWindow(_hwnd))
        {
            NativeMethods.SetFocus(_hwnd);
            return;
        }

        // Double Ctrl arrives through a keyboard hook, which unlike WM_HOTKEY
        // grants no foreground rights. A zero-length mouse input makes this
        // process the source of the last input, which the foreground lock
        // accepts; it moves nothing and clicks nothing.
        var nudge = new Input { Type = InputMouse };
        if (SendInput(1, ref nudge, Marshal.SizeOf<Input>()) == 1 && NativeMethods.SetForegroundWindow(_hwnd))
        {
            NativeMethods.SetFocus(_hwnd);
            return;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return;
        }

        // The foreground lock refused the plain request. Attaching to the owning
        // thread lets it through; the detach is what keeps activation messages
        // flowing afterwards, so both calls must stay paired.
        if (!NativeMethods.AttachThreadInput(currentThread, foregroundThread, true))
        {
            return;
        }

        try
        {
            NativeMethods.BringWindowToTop(_hwnd);
            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.SetFocus(_hwnd);
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private void OnActivate(int state)
    {
        if (_docked && _visible)
        {
            // Clicked: the bar becomes a search. Left: it goes back to being a
            // search line under the dialog; the foreground watch hides it if
            // something other than the dialog came to the front.
            if (state is 1 or 2)
            {
                if (_passive)
                {
                    Activate(string.Empty);
                }
            }
            else if (!_passive)
            {
                GoPassive();
            }

            return;
        }

        if (state is 1 or 2 || !_visible || !_settings.HideWhenLosingFocus || _dragging)
        {
            return;
        }

        // Windows can report a deactivation a few milliseconds after Show(),
        // before the foreground request settles. The timer scheduled by Show()
        // re-checks focus once the grace period is over, so an early report is
        // not treated as the user leaving the launcher.
        if (Environment.TickCount64 - _shownAt > ActivationGraceMs)
        {
            Hide();
        }
    }

    private void Hide()
    {
        _visible = false;
        _searchCancel?.Cancel();
        _searchCancel = null;
        _searching = false;
        _openWhenReady = false;
        NativeMethods.KillTimer(_hwnd, (UIntPtr)FocusCheckTimerId);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);

        // Hidden is where a launcher spends its day; give memory back once the
        // user has really left, not between two quick searches.
        NativeMethods.SetTimer(_hwnd, (UIntPtr)TrimTimerId, TrimDelayMs, IntPtr.Zero);
    }

    private const int TrimTimerId = 4;
    private const uint TrimDelayMs = 2000;

    /// <summary>
    /// Releases what a search left behind: cached icons, the scan buffers and
    /// result strings (an aggressive compacting GC returns free regions to
    /// Windows), then the working set, so the hidden process holds only what it
    /// touches to wake up.
    /// </summary>
    private void TrimMemory()
    {
        NativeMethods.KillTimer(_hwnd, (UIntPtr)TrimTimerId);
        if (_visible)
        {
            return;
        }

        _icons.Clear();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    // Layout. The window is as tall as its content: the search line alone
    // when there is nothing to list, growing one row at a time up to the row
    // limit. Normally the search line is on top and the window keeps its top
    // edge; as an Explorer bar the search line is at the bottom and the window
    // keeps its bottom edge, growing upward inside the Explorer window.
    //
    // The two bars (opened by typing in Explorer or in a file dialog) are
    // compact, like Listary's: single-line rows, a short search line, a few
    // hundred pixels wide, so the window they belong to stays visible.
    private enum BarMode
    {
        /// <summary>The launcher, centered near the top of the screen.</summary>
        None,

        /// <summary>Inside an Explorer window, bottom right, search line at the bottom.</summary>
        Explorer,

        /// <summary>Under an Open/Save dialog, search line on top.</summary>
        Dialog,
    }

    private BarMode _barMode;
    private bool _inputAtBottom;

    private bool IsCompact => _barMode != BarMode.None;

    private int HeaderHeight => IsCompact ? Scale(38) : Scale(54);

    private int RowHeight => IsCompact ? Scale(30) : Scale(50);

    private int ListPadding => IsCompact ? Scale(4) : Scale(6);

    private int SidePadding => IsCompact ? Scale(10) : Scale(16);

    private int _anchorX;
    private int _anchorY;
    private int _windowWidth;
    private int _maxRows = VisibleRows;

    private int HeaderTop(int clientHeight) => _inputAtBottom ? clientHeight - HeaderHeight : 0;

    private int ListTop => _inputAtBottom ? ListPadding : HeaderHeight + ListPadding;

    /// <summary>Rows painted below (or above) the search line, including messages.</summary>
    private int ContentRows()
    {
        if (_docked && _passive)
        {
            return 0;
        }

        int rows = _notice is null ? 0 : 1;

        if (_store is null && GetEffectiveIndexPaths().Length == 0)
        {
            return rows + 1;
        }

        if (_calculatorResult is not null)
        {
            return rows + 1;
        }

        if (_results.Length == 0)
        {
            return rows + (_query.Length > 0 && !_searching ? 1 : 0);
        }

        // Sized by the whole list, not by what is left below the scroll
        // position, so the window does not shrink while scrolling.
        return rows + Math.Min(_results.Length, GetVisibleRowCount());
    }

    private void PlaceWindow()
    {
        _maxRows = VisibleRows;
        _inputAtBottom = false;

        NativeMethods.Rect work = WorkAreaOf(_barTarget != IntPtr.Zero ? _barTarget : IntPtr.Zero);

        if (_barMode != BarMode.None && UiNative.GetWindowRect(_barTarget, out NativeMethods.Rect owner))
        {
            if (_barMode == BarMode.Explorer)
            {
                PlaceInside(owner);
            }
            else
            {
                // Under the dialog, starting a quarter of the way across, the
                // way Listary attaches to Open/Save dialogs. Without room
                // below, it sits inside the dialog's bottom-right corner.
                _windowWidth = Math.Min(Scale(480), Math.Max(Scale(320), owner.Width * 3 / 5));
                _anchorX = Math.Clamp(owner.Left + (owner.Width / 4), work.Left, Math.Max(work.Left, work.Right - _windowWidth));
                _anchorY = owner.Bottom + Scale(2);

                int roomBelow = (work.Bottom - _anchorY - HeaderHeight - (2 * ListPadding)) / RowHeight;
                if (roomBelow >= 4)
                {
                    _maxRows = Math.Min(roomBelow, VisibleRows);
                }
                else
                {
                    PlaceInside(owner);
                }
            }
        }
        else
        {
            _barMode = BarMode.None;
            _windowWidth = Math.Min(Scale(680), work.Width - Scale(40));
            _anchorX = work.Left + ((work.Width - _windowWidth) / 2);
            _anchorY = work.Top + (work.Height / 5);
        }

        UpdateWindowSize(force: true);
    }

    /// <summary>
    /// Inside <paramref name="owner"/>, bottom right, above its status bar,
    /// with the search line at the bottom and rows growing upward. Narrow
    /// enough to leave most of the owner's file list in view.
    /// </summary>
    private void PlaceInside(NativeMethods.Rect owner)
    {
        _inputAtBottom = true;
        _windowWidth = Math.Min(Scale(440), Math.Max(Scale(300), owner.Width * 2 / 5));
        _anchorX = owner.Right - _windowWidth - Scale(14);
        _anchorY = owner.Bottom - Scale(32);

        int room = (_anchorY - owner.Top - Scale(130) - HeaderHeight) / RowHeight;
        _maxRows = Math.Clamp(room, 3, VisibleRows);
    }

    private static NativeMethods.Rect WorkAreaOf(IntPtr window)
    {
        IntPtr monitor;

        if (window != IntPtr.Zero)
        {
            monitor = MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        }
        else
        {
            NativeMethods.GetCursorPos(out NativeMethods.Point cursor);
            monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        }

        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        return NativeMethods.GetMonitorInfoW(monitor, ref info)
            ? info.Work
            : new NativeMethods.Rect { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
    }

    /// <summary>Resizes the window to its content, keeping the anchored edge still.</summary>
    private void UpdateWindowSize(bool force = false)
    {
        int rows = ContentRows();
        int height = HeaderHeight + (rows == 0 ? 0 : (rows * RowHeight) + (2 * ListPadding) + Scale(1));

        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);
        if (!force && client.Height == height && client.Width == _windowWidth)
        {
            return;
        }

        int y = _inputAtBottom ? _anchorY - height : _anchorY;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndTopmost,
            _anchorX,
            y,
            _windowWidth,
            height,
            0);
    }

    private void Paint()
    {
        NativeMethods.BeginPaint(_hwnd, out NativeMethods.PaintStruct paint);
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);

        EnsureBackBuffer(paint.Hdc, client.Width, client.Height);
        IntPtr dc = _backDc;

        Fill(dc, 0, 0, client.Width, client.Height, _brushBackground);
        NativeMethods.SetBkMode(dc, NativeMethods.Transparent);
        IntPtr previousFont = NativeMethods.SelectObject(dc, _fontQuery);

        PaintHeader(dc, client.Width, HeaderTop(client.Height));

        int rows = ContentRows();
        if (rows > 0)
        {
            // Hairline between the search line and the list.
            int line = _inputAtBottom ? HeaderTop(client.Height) - Scale(1) : HeaderHeight;
            Fill(dc, SidePadding, line, client.Width - SidePadding, line + Scale(1), _brushBorder);

            PaintList(dc, client.Width);
        }

        NativeMethods.SelectObject(dc, previousFont);
        NativeMethods.BitBlt(paint.Hdc, 0, 0, client.Width, client.Height, dc, 0, 0, NativeMethods.Srccopy);
        NativeMethods.EndPaint(_hwnd, ref paint);
    }

    private void PaintHeader(IntPtr dc, int width, int top)
    {
        int pad = SidePadding;
        int bottom = top + HeaderHeight;

        // Search glyph on the left; the settings glyph only on the launcher,
        // the bars keep their search line bare.
        int glyphWidth = IsCompact ? Scale(16) : Scale(22);
        NativeMethods.SelectObject(dc, IsCompact ? _fontGlyph : _fontGlyphLarge);
        SetColor(dc, _query.Length > 0 ? _palette.Accent : _palette.Placeholder);
        DrawGlyph(dc, UiNative.GlyphSearch, pad, top, pad + glyphWidth, bottom);

        int gearLeft = width - pad;
        if (!IsCompact)
        {
            int gearRight = width - pad;
            gearLeft = gearRight - Scale(22);
            NativeMethods.SelectObject(dc, _fontGlyph);
            SetColor(dc, _hoveredSettings ? _palette.Accent : _palette.Placeholder);
            DrawGlyph(dc, UiNative.GlyphSettings, gearLeft, top, gearRight, bottom);
        }

        int textLeft = pad + glyphWidth + (IsCompact ? Scale(8) : Scale(12));
        int textRight = gearLeft - Scale(10);
        int caretInset = (HeaderHeight - (IsCompact ? Scale(16) : Scale(20))) / 2;

        // Active filter as a pill before the gear: "Documents", "Google".
        if (_filterLabel is not null && _query.Length > 0)
        {
            NativeMethods.SelectObject(dc, _fontPath);
            if (NativeMethods.GetTextExtentPoint32W(dc, _filterLabel, _filterLabel.Length, out NativeMethods.Size chip))
            {
                int chipRight = textRight;
                int chipLeft = chipRight - chip.Cx - Scale(20);
                int chipTop = top + ((HeaderHeight - Scale(24)) / 2);

                UiNative.FillRounded(dc, chipLeft, chipTop, chipRight, chipTop + Scale(24), Scale(12), _brushAccentSoft);
                SetColor(dc, _palette.Accent);
                DrawText(dc, _filterLabel, chipLeft + Scale(10), chipTop, chipRight - Scale(8), chipTop + Scale(24));

                textRight = chipLeft - Scale(10);
            }
        }

        NativeMethods.SelectObject(dc, IsCompact ? _fontQueryCompact : _fontQuery);

        if (_query.Length == 0)
        {
            SetColor(dc, _palette.Placeholder);
            DrawText(dc, "Search files and apps", textLeft, top, textRight, bottom);
            if (!_passive)
            {
                Fill(dc, textLeft, top + caretInset, textLeft + Scale(2), bottom - caretInset, _brushAccent);
            }

            return;
        }

        SetColor(dc, _palette.Ink);
        DrawText(dc, _query, textLeft, top, textRight, bottom);

        if (NativeMethods.GetTextExtentPoint32W(dc, _query, _query.Length, out NativeMethods.Size size))
        {
            int caretX = Math.Min(textLeft + size.Cx + Scale(2), textRight);
            Fill(dc, caretX, top + caretInset, caretX + Scale(2), bottom - caretInset, _brushAccent);
        }
    }

    /// <summary>
    /// One line per result for the bars: icon, name with the matched letters
    /// lit, the folder in grey to the right (shortened in the middle), and the
    /// Ctrl+N hint. Same layout as Listary's Explorer and dialog bar.
    /// </summary>
    private void PaintCompactRow(IntPtr dc, int width, int top, SearchResult result, string name, string? hint)
    {
        int left = IconLeft + IconSize + Scale(8);
        int right = width - SidePadding - Scale(6);

        if (hint is not null)
        {
            NativeMethods.SelectObject(dc, _fontPath);
            SetColor(dc, _palette.Placeholder);
            var hintRect = new NativeMethods.Rect { Left = right - Scale(44), Top = top, Right = right, Bottom = top + RowHeight };
            NativeMethods.DrawTextW(dc, hint, hint.Length, ref hintRect, NativeMethods.DtRight | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix);
            right -= Scale(50);
        }

        // The name gets what it needs up to two thirds of the row; the folder
        // takes what is left, with "…" in the middle of the path.
        NativeMethods.SelectObject(dc, _fontRow);
        int nameWidth = right - left;
        if (NativeMethods.GetTextExtentPoint32W(dc, name, name.Length, out NativeMethods.Size nameSize))
        {
            nameWidth = Math.Min(nameSize.Cx + Scale(2), (right - left) * 2 / 3);
        }

        int[] highlight = result.Title is null ? result.NamePositions : [];
        DrawHighlighted(dc, name, highlight, left, top, left + nameWidth, top + RowHeight, _palette.Ink);

        string folder = result.Kind switch
        {
            SearchResultKind.WebSearch => "Web",
            SearchResultKind.Command => "Command",
            _ => Path.GetDirectoryName(result.Entry.Path.TrimEnd('\\')) ?? string.Empty,
        };

        int folderLeft = left + nameWidth + Scale(12);
        if (folder.Length > 0 && right - folderLeft > Scale(40))
        {
            NativeMethods.SelectObject(dc, _fontPath);
            SetColor(dc, _palette.Dim);
            var rect = new NativeMethods.Rect { Left = folderLeft, Top = top, Right = right, Bottom = top + RowHeight };
            NativeMethods.DrawTextW(dc, folder, folder.Length, ref rect, NativeMethods.DtRight | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix | PathEllipsis);
        }
    }

    private const uint PathEllipsis = 0x00004000;

    private void PaintList(IntPtr dc, int width)
    {
        int pad = SidePadding;
        int rowHeight = RowHeight;
        int top = ListTop;

        if (_notice is not null)
        {
            NativeMethods.SelectObject(dc, _fontPath);
            SetColor(dc, _palette.Accent);
            DrawText(dc, _notice, pad, top, width - pad, top + rowHeight);
            top += rowHeight;
        }

        if (_store is null && GetEffectiveIndexPaths().Length == 0)
        {
            NativeMethods.SelectObject(dc, _fontPath);
            SetColor(dc, _palette.Dim);
            DrawText(dc, _indexing ? $"Indexing {_scanRoot}" : "Indexing", pad, top, width - pad, top + rowHeight);
            return;
        }

        if (_calculatorResult is not null)
        {
            PaintRowBackground(dc, width, top, selected: _selected == 0);
            PaintGlyphIcon(dc, UiNative.GlyphCalculator, top);
            PaintTwoLines(dc, width, top, _calculatorResult, [], "Calculator  ·  Enter to copy", _palette.Accent, hint: null);
            return;
        }

        if (_results.Length == 0)
        {
            if (_query.Length > 0 && !_searching)
            {
                NativeMethods.SelectObject(dc, _fontPath);
                SetColor(dc, _palette.Dim);
                DrawText(dc, "No matches", pad + Scale(44), top, width - pad, top + rowHeight);
            }

            return;
        }

        int visibleCount = GetVisibleRowCount();
        int last = Math.Min(_results.Length, _scrollOffset + visibleCount);

        for (int i = _scrollOffset; i < last; i++, top += rowHeight)
        {
            SearchResult result = _results[i];
            FileEntry entry = result.Entry;

            PaintRowBackground(dc, width, top, selected: i == _selected);

            switch (result.Kind)
            {
                case SearchResultKind.WebSearch:
                    PaintGlyphIcon(dc, UiNative.GlyphGlobe, top);
                    break;

                case SearchResultKind.Command:
                    PaintGlyphIcon(dc, entry.Path == CommandCatalog.SettingsTarget ? UiNative.GlyphSettings : UiNative.GlyphCommand, top);
                    break;

                default:
                    PaintFileIcon(dc, entry, top);
                    break;
            }

            string name = result.Title ?? Path.GetFileName(entry.Path.TrimEnd('\\'));
            if (name.Length == 0)
            {
                name = entry.Path;
            }

            string subtitle = result.Kind switch
            {
                SearchResultKind.WebSearch => entry.Path,
                SearchResultKind.Command => "Command",
                SearchResultKind.OpenedFolder => "Open in Explorer  ·  " + entry.Path,
                SearchResultKind.History => "Recent  ·  " + (Path.GetDirectoryName(entry.Path) ?? entry.Path),
                SearchResultKind.Bookmark => "Bookmark  ·  " + (Path.GetDirectoryName(entry.Path) ?? entry.Path),
                _ => Path.GetDirectoryName(entry.Path) ?? string.Empty,
            };

            // Shortcuts belong to the first eight results, not to the rows that
            // happen to be on screen after scrolling.
            string? hint = i < ShortcutCount ? $"Ctrl+{i + 1}" : null;

            if (IsCompact)
            {
                PaintCompactRow(dc, width, top, result, name, hint);
                continue;
            }

            int[] highlight = result.Title is null ? result.NamePositions : [];
            PaintTwoLines(dc, width, top, name, highlight, subtitle, _palette.Ink, hint);
        }

        // A thin rounded scroll thumb when the list is longer than the window.
        if (_results.Length > visibleCount)
        {
            int trackTop = ListTop + (_notice is null ? 0 : rowHeight);
            int trackHeight = visibleCount * rowHeight;
            int thumbHeight = Math.Max(Scale(24), trackHeight * visibleCount / _results.Length);
            int maxScroll = _results.Length - visibleCount;
            int thumbTop = trackTop + (int)((long)_scrollOffset * (trackHeight - thumbHeight) / maxScroll);
            int right = width - Scale(4);

            UiNative.FillRounded(dc, right - Scale(3), thumbTop, right, thumbTop + thumbHeight, Scale(2), _brushThumb);
        }
    }

    private void PaintRowBackground(IntPtr dc, int width, int top, bool selected)
    {
        if (!selected)
        {
            return;
        }

        int inset = IsCompact ? Scale(4) : Scale(6);
        int radius = IsCompact ? Scale(5) : Scale(8);
        UiNative.FillRounded(dc, inset, top + Scale(1), width - inset - Scale(6), top + RowHeight - Scale(1), radius, _brushSelection);

        // Accent pill at the left edge of the selected row.
        int pill = IsCompact ? Scale(12) : Scale(18);
        int pillTop = top + ((RowHeight - pill) / 2);
        UiNative.FillRounded(dc, inset + Scale(2), pillTop, inset + Scale(5), pillTop + pill, Scale(2), _brushAccent);
    }

    private int IconLeft => SidePadding + (IsCompact ? Scale(4) : Scale(2));

    private int IconSize => IsCompact ? Scale(16) : Scale(28);

    private void PaintFileIcon(IntPtr dc, FileEntry entry, int top)
    {
        IntPtr icon = _icons.Get(entry.Path, entry.IsDirectory || entry.Path.EndsWith('\\'), small: IsCompact);
        if (icon == IntPtr.Zero)
        {
            return;
        }

        const uint DiNormal = 0x0003;
        int size = IconSize;
        UiNative.DrawIconEx(dc, IconLeft, top + ((RowHeight - size) / 2), icon, size, size, 0, IntPtr.Zero, DiNormal);
    }

    private void PaintGlyphIcon(IntPtr dc, string glyph, int top)
    {
        int size = IconSize;
        int iconTop = top + ((RowHeight - size) / 2);

        if (!IsCompact)
        {
            UiNative.FillRounded(dc, IconLeft, iconTop, IconLeft + size, iconTop + size, Scale(7), _brushRaised);
        }

        NativeMethods.SelectObject(dc, IsCompact ? _fontGlyphSmall : _fontGlyph);
        SetColor(dc, _palette.Accent);
        DrawGlyph(dc, glyph, IconLeft, iconTop, IconLeft + size, iconTop + size);
    }

    /// <summary>Name on the first line, location on the second, shortcut hint on the right.</summary>
    private void PaintTwoLines(IntPtr dc, int width, int top, string title, int[] highlight, string subtitle, int titleColor, string? hint)
    {
        int left = IconLeft + IconSize + Scale(12);
        int right = width - SidePadding - (hint is null ? Scale(4) : Scale(56));
        int mid = top + (RowHeight / 2);

        NativeMethods.SelectObject(dc, _fontName);
        DrawHighlighted(dc, title, highlight, left, top + Scale(5), right, mid + Scale(2), titleColor);

        NativeMethods.SelectObject(dc, _fontPath);
        SetColor(dc, _palette.Dim);
        DrawText(dc, subtitle, left, mid, right, top + RowHeight - Scale(6));

        if (hint is not null)
        {
            SetColor(dc, _palette.Placeholder);
            var hintRect = new NativeMethods.Rect { Left = right, Top = top, Right = width - SidePadding - Scale(6), Bottom = top + RowHeight };
            NativeMethods.DrawTextW(dc, hint, hint.Length, ref hintRect, NativeMethods.DtRight | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix);
        }
    }

    /// <summary>
    /// Draws the name with the characters the query matched in the accent
    /// color. A name too long for the row is drawn plain with an ellipsis,
    /// because highlighting runs cannot be cut cleanly.
    /// </summary>
    private void DrawHighlighted(IntPtr dc, string text, int[] positions, int left, int top, int right, int bottom, int color)
    {
        SetColor(dc, color);

        if (positions.Length == 0 ||
            !NativeMethods.GetTextExtentPoint32W(dc, text, text.Length, out NativeMethods.Size full) ||
            full.Cx > right - left)
        {
            DrawText(dc, text, left, top, right, bottom);
            return;
        }

        var matched = new HashSet<int>(positions);
        int x = left;
        int start = 0;

        while (start < text.Length)
        {
            bool isMatch = matched.Contains(start);
            int end = start + 1;
            while (end < text.Length && matched.Contains(end) == isMatch)
            {
                end++;
            }

            string run = text[start..end];
            SetColor(dc, isMatch ? _palette.Accent : color);
            DrawText(dc, run, x, top, right, bottom);

            if (NativeMethods.GetTextExtentPoint32W(dc, run, run.Length, out NativeMethods.Size size))
            {
                x += size.Cx;
            }

            start = end;
        }
    }

    private static void DrawGlyph(IntPtr dc, string glyph, int left, int top, int right, int bottom)
    {
        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        NativeMethods.DrawTextW(dc, glyph, glyph.Length, ref rect, NativeMethods.DtCenter | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix);
    }

    private static void DrawText(IntPtr dc, string text, int left, int top, int right, int bottom)
    {
        if (text.Length == 0 || right <= left)
        {
            return;
        }

        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        NativeMethods.DrawTextW(
            dc,
            text,
            text.Length,
            ref rect,
            NativeMethods.DtLeft | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix | NativeMethods.DtEndEllipsis);
    }

    private static void Fill(IntPtr dc, int left, int top, int right, int bottom, IntPtr brush)
    {
        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        NativeMethods.FillRect(dc, ref rect, brush);
    }

    private void EnsureBackBuffer(IntPtr hdc, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_backDc != IntPtr.Zero && _backWidth == width && _backHeight == height)
        {
            return;
        }

        if (_backDc == IntPtr.Zero)
        {
            _backDc = NativeMethods.CreateCompatibleDC(hdc);
        }

        if (_backBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_backDc, _previousBitmap);
            NativeMethods.DeleteObject(_backBitmap);
        }

        _backBitmap = NativeMethods.CreateCompatibleBitmap(hdc, width, height);
        _previousBitmap = NativeMethods.SelectObject(_backDc, _backBitmap);
        _backWidth = width;
        _backHeight = height;
    }

    private void DestroyResources()
    {
        if (_backDc != IntPtr.Zero)
        {
            if (_backBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_backDc, _previousBitmap);
                NativeMethods.DeleteObject(_backBitmap);
                _backBitmap = IntPtr.Zero;
            }

            NativeMethods.DeleteDC(_backDc);
            _backDc = IntPtr.Zero;
        }

        DeleteBrushes();

        foreach (IntPtr font in new[] { _fontQuery, _fontName, _fontPath, _fontGlyph, _fontGlyphLarge, _fontGlyphSmall, _fontQueryCompact, _fontRow })
        {
            if (font != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(font);
            }
        }

        _icons.Dispose();
    }

    private void DeleteBrushes()
    {
        foreach (IntPtr brush in new[] { _brushBackground, _brushSelection, _brushAccent, _brushBorder, _brushRaised, _brushAccentSoft, _brushThumb })
        {
            if (brush != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(brush);
            }
        }
    }

    private void RecreateResources()
    {
        DeleteBrushes();
        CreateBrushes();
    }

    private int Scale(int value)
    {
        return (int)Math.Round(value * _dpi / 96.0);
    }
}
