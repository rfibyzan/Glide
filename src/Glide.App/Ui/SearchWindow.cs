using System.Globalization;
using System.Runtime.InteropServices;
using Glide.App.Search;
using Glide.App.Win32;
using Glide.Core;
using Glide.Core.Index;
using Glide.Core.Search;
using Glide.Core.Snapshot;
using Glide.Core.Settings;

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
    private const uint WmSettingChange = 0x001A;

    private const int HotkeyId = 1;
    private const int FocusCheckTimerId = 2;
    private const int VisibleRows = 8;
    private const int MaxQueryLength = 200;
    private const int ActivationGraceMs = 400;

    // Light theme
    private const int LightInkColor = 0x141414;
    private const int LightDimColor = 0x6B6B6B;
    private const int LightPlaceholderColor = 0x767676;
    private const int LightBackgroundColor = 0xFCFCFC;
    private const int LightSelectionColor = 0xEDF1F4;
    private const int LightAccentColor = 0x0B6E63;
    private const int LightBorderColor = 0xDCDCDC;

    // Dark theme
    private const int DarkInkColor = 0xE0E0E0;
    private const int DarkDimColor = 0x9E9E9E;
    private const int DarkPlaceholderColor = 0x8A8A8A;
    private const int DarkBackgroundColor = 0x1E1E1E;
    private const int DarkSelectionColor = 0x2D2D2D;
    private const int DarkAccentColor = 0x4DB6AC;
    private const int DarkBorderColor = 0x3D3D3D;

    private static SearchWindow? s_instance;
    private static readonly NativeMethods.WndProc WindowProcDelegate = WindowProc;

    private readonly EntryStore? _snapshot;
    private readonly int _maxEntries;
    private readonly int _maxResults;
    private readonly string[] _indexPaths;
    private EntryStore? _store;
    private string _query = string.Empty;
    private string? _notice;
    private SearchResult[] _results = [];
    private int _selected;
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
        string? hotkeyOverride)
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
        window.StartIndexing(scanRoot);

        if (showOnStart || window._notice is not null)
        {
            window.Show();
        }

        return window.MessageLoop();
    }

    private void ApplyTheme(string theme)
    {
        // "system", "dark", "light"
        if (theme == "dark" || (theme == "system" && IsSystemDarkMode()))
        {
            _isDarkMode = true;
            _inkColor = DarkInkColor;
            _dimColor = DarkDimColor;
            _placeholderColor = DarkPlaceholderColor;
            _backgroundColor = DarkBackgroundColor;
            _selectionColor = DarkSelectionColor;
            _accentColor = DarkAccentColor;
            _borderColor = DarkBorderColor;
        }
        else
        {
            _isDarkMode = false;
            _inkColor = LightInkColor;
            _dimColor = LightDimColor;
            _placeholderColor = LightPlaceholderColor;
            _backgroundColor = LightBackgroundColor;
            _selectionColor = LightSelectionColor;
            _accentColor = LightAccentColor;
            _borderColor = LightBorderColor;
        }
    }

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
            NativeMethods.WsExTopmost,
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

    private void SetNotice(string message)
    {
        _notice = message;
        Console.Error.WriteLine($"Glide: {message}");

        if (_hwnd != IntPtr.Zero)
        {
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
        _fontQuery = CreateFont(13.5, NativeMethods.FwNormal);
        _fontName = CreateFont(12, NativeMethods.FwSemiBold);
        _fontPath = CreateFont(9.5, NativeMethods.FwNormal);

        _brushBackground = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_backgroundColor >> 16 & 0xFF, _backgroundColor >> 8 & 0xFF, _backgroundColor & 0xFF));
        _brushSelection = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_selectionColor >> 16 & 0xFF, _selectionColor >> 8 & 0xFF, _selectionColor & 0xFF));
        _brushAccent = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_accentColor >> 16 & 0xFF, _accentColor >> 8 & 0xFF, _accentColor & 0xFF));
        _brushBorder = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_borderColor >> 16 & 0xFF, _borderColor >> 8 & 0xFF, _borderColor & 0xFF));
    }

    private IntPtr CreateFont(double points, int weight)
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
            "Segoe UI");
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
        switch (message)
        {
            case NativeMethods.WmPaint:
                Paint();
                return IntPtr.Zero;

            case NativeMethods.WmEraseBkgnd:
                return new IntPtr(1);

            case NativeMethods.WmKeyDown:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;

            case NativeMethods.WmChar:
                OnChar((char)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case NativeMethods.WmHotkey:
                if ((int)wParam == HotkeyId)
                {
                    if (_visible)
                    {
                        Hide();
                    }
                    else
                    {
                        Show();
                    }
                }

                return IntPtr.Zero;

            case WmShowGlide:
                Show();
                return IntPtr.Zero;

            case WmIndexReady:
                _indexing = false;
                Refresh();
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDown:
                SelectRowAt(lParam);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonUp:
                SelectRowAt(lParam);
                OpenSelected();
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDblClk:
                SelectRowAt(lParam);
                OpenSelected();
                return IntPtr.Zero;

            case NativeMethods.WmRButtonUp:
                SelectRowAt(lParam);
                ShowActionsMenu();
                return IntPtr.Zero;

            case NativeMethods.WmActivate:
                OnActivate((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case NativeMethods.WmTimer:
                if (wParam.ToInt64() == FocusCheckTimerId)
                {
                    NativeMethods.KillTimer(_hwnd, (UIntPtr)FocusCheckTimerId);

                    if (_visible && _notice is null && NativeMethods.GetForegroundWindow() != _hwnd)
                    {
                        Hide();
                    }
                }

                return IntPtr.Zero;

            case NativeMethods.WmDestroy:
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            case NativeMethods.WmSize:
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WmSettingChange:
                // Handle system theme change
                bool wasDark = _isDarkMode;
                ApplyTheme(_settings.Theme);
                if (wasDark != _isDarkMode)
                {
                    RecreateResources();
                    NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProcW(_hwnd, message, wParam, lParam);
        }
    }

    private void OnKeyDown(int key)
    {
        bool control = (NativeMethods.GetKeyState(NativeMethods.VkControl) & 0x8000) != 0;
        bool shift = (NativeMethods.GetKeyState(NativeMethods.VkShift) & 0x8000) != 0;

        switch (key)
        {
            case NativeMethods.VkEscape:
                Hide();
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

            case NativeMethods.VkUp:
                MoveSelection(-1);
                break;

            case NativeMethods.VkDown:
                MoveSelection(1);
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

        _selected = (_selected + delta + _results.Length) % _results.Length;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void SelectRowAt(IntPtr lParam)
    {
        if (_results.Length == 0)
        {
            return;
        }

        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        int top = Scale(50) + Scale(1);
        int noticeRows = _notice is null ? 0 : 1;
        int row = ((y - top) / Scale(52)) - noticeRows;

        if (row >= 0 && row < _results.Length)
        {
            _selected = row;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void OpenSelected()
    {
        // If calculator result is showing and selected, copy it
        if (_calculatorResult is not null && _selected == 0)
        {
            CopyToClipboard(_calculatorResult);
            return;
        }

        if (_results.Length == 0)
        {
            return;
        }

        string path = _results[Math.Clamp(_selected, 0, _results.Length - 1)].Entry.Path;
        Hide();
        NativeMethods.ShellExecuteW(IntPtr.Zero, "open", path, null, null, NativeMethods.SwShow);
        HistoryStore.Default.Add(path);
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
            Hide();
            NativeMethods.ShellExecuteW(IntPtr.Zero, "open", folder, null, null, NativeMethods.SwShow);
            HistoryStore.Default.Add(folder);
        }
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
            return;
        }

        string path = _results[Math.Clamp(_selected, 0, _results.Length - 1)].Entry.Path;
        ShowContextMenu(path);
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
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString | (isBookmarked ? NativeMethods.MfChecked : 0), (UIntPtr)id++, isBookmarked ? "Remove Bookmark" : "Add Bookmark");
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MfString, (UIntPtr)id++, "Properties");

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
        }
    }

    private void ShowFileProperties(string path)
    {
        // Use ShellExecute with "properties" verb
        NativeMethods.ShellExecuteW(IntPtr.Zero, "properties", path, null, null, NativeMethods.SwShow);
    }

    private string? _calculatorResult;

    private void Refresh()
    {
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

        if (_query.Length == 0)
        {
            // Show history when query is empty
            string[] history = HistoryStore.Default.Items;
            if (history.Length > 0)
            {
                int limit = Math.Min(history.Length, _maxResults);
                _results = new SearchResult[limit];
                for (int i = 0; i < limit; i++)
                {
                    string path = history[i];
                    _results[i] = new SearchResult(
                        new FileEntry(path, false, 0, DateTime.MinValue),
                        0,
                        [],
                        SearchResultKind.History);
                }
            }
            else if (store is not null)
            {
                _results = store.Search(_query, _maxResults).ToArray();
            }
            else if (_indexPaths.Length > 0)
            {
                _results = [.. IndexSearchSource.SearchMany(_indexPaths, _query, _maxResults)];
            }
            else
            {
                _results = [];
            }
        }
        else
        {
            if (store is not null)
            {
                _results = store.Search(_query, _maxResults).ToArray();
            }
            else if (_indexPaths.Length > 0)
            {
                _results = [.. IndexSearchSource.SearchMany(_indexPaths, _query, _maxResults)];
            }
            else
            {
                _results = [];
            }
        }

        // Merge bookmarks matched ahead of file results
        _results = MergeBookmarks(_query, _results);

        _selected = 0;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
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
                    new FileEntry(bookmark, false, 0, DateTime.MinValue),
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

        // Trim to VisibleRows if needed
        if (merged.Length > _maxResults)
        {
            Array.Resize(ref merged, _maxResults);
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

    private void Show()
    {
        _visible = true;
        _shownAt = Environment.TickCount64;

        PlaceWindow();
        _query = string.Empty;
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
        if (state is 1 or 2 || !_visible)
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
        NativeMethods.KillTimer(_hwnd, (UIntPtr)FocusCheckTimerId);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
    }

    private void PlaceWindow()
    {
        NativeMethods.GetCursorPos(out NativeMethods.Point cursor);
        IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            info.Work = new NativeMethods.Rect { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
        }

        int width = Math.Min(Scale(720), info.Work.Width - Scale(40));
        int height = Scale(460);
        int x = info.Work.Left + ((info.Work.Width - width) / 2);
        int y = info.Work.Top + (info.Work.Height / 6);

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndTopmost,
            x,
            y,
            width,
            height,
            0);
    }

    private void Paint()
    {
        NativeMethods.BeginPaint(_hwnd, out NativeMethods.PaintStruct paint);
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);

        EnsureBackBuffer(paint.Hdc, client.Width, client.Height);
        IntPtr dc = _backDc;

        var full = new NativeMethods.Rect { Left = 0, Top = 0, Right = client.Width, Bottom = client.Height };
        NativeMethods.FillRect(dc, ref full, _brushBackground);

        int pad = Scale(16);
        int header = Scale(50);
        int rowHeight = Scale(52);
        int textRight = client.Width - pad;

        NativeMethods.SetBkMode(dc, NativeMethods.Transparent);

        IntPtr previousFont = NativeMethods.SelectObject(dc, _fontQuery);

        if (_query.Length == 0)
        {
            NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_placeholderColor >> 16 & 0xFF, _placeholderColor >> 8 & 0xFF, _placeholderColor & 0xFF));
            DrawText(dc, "Search files and folders", pad, 0, textRight, header);
        }
        else
        {
            NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_inkColor >> 16 & 0xFF, _inkColor >> 8 & 0xFF, _inkColor & 0xFF));
            DrawText(dc, _query, pad, 0, textRight, header);

            if (NativeMethods.GetTextExtentPoint32W(dc, _query, _query.Length, out NativeMethods.Size size))
            {
                int caretTop = (header - Scale(20)) / 2;
                Fill(dc, pad + size.Cx + Scale(1), caretTop, pad + size.Cx + Scale(3), caretTop + Scale(20), _brushAccent);
            }
        }

        Fill(dc, 0, header, client.Width, header + Scale(1), _brushBorder);

        int rowsTop = header + Scale(1);
        int noticeRows = _notice is null ? 0 : 1;

        if (_notice is not null)
        {
            NativeMethods.SelectObject(dc, _fontQuery);
            NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_accentColor >> 16 & 0xFF, _accentColor >> 8 & 0xFF, _accentColor & 0xFF));
            DrawText(dc, _notice, pad, rowsTop, textRight, rowsTop + rowHeight);
            Fill(dc, 0, rowsTop + rowHeight, client.Width, rowsTop + rowHeight + Scale(1), _brushBorder);
        }

        if (_store is null && _indexPaths.Length == 0)
        {
            NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_dimColor >> 16 & 0xFF, _dimColor >> 8 & 0xFF, _dimColor & 0xFF));
            string message = _indexing ? $"Indexing {_scanRoot}" : "Indexing";
            int messageTop = rowsTop + (noticeRows * rowHeight);
            DrawText(dc, message, pad, messageTop, textRight, messageTop + rowHeight);
        }
        else if (_calculatorResult is not null)
        {
            // Show calculator result as first row
            int top = rowsTop + (noticeRows * rowHeight);
            if (top < client.Height)
            {
                if (0 == _selected)
                {
                    Fill(dc, 0, top, client.Width, top + rowHeight, _brushSelection);
                    Fill(dc, 0, top, Scale(3), top + rowHeight, _brushAccent);
                }

                NativeMethods.SelectObject(dc, _fontName);
                NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_accentColor >> 16 & 0xFF, _accentColor >> 8 & 0xFF, _accentColor & 0xFF));
                DrawText(dc, _calculatorResult, pad + Scale(6), top + Scale(4), textRight, top + Scale(30));

                NativeMethods.SelectObject(dc, _fontPath);
                NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_dimColor >> 16 & 0xFF, _dimColor >> 8 & 0xFF, _dimColor & 0xFF));
                DrawText(dc, "Calculator  •  Enter to copy", pad + Scale(6), top + Scale(27), textRight, top + rowHeight - Scale(2));
            }
        }
        else if (_results.Length == 0)
        {
            NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_dimColor >> 16 & 0xFF, _dimColor >> 8 & 0xFF, _dimColor & 0xFF));
            string empty = _notice ?? (_query.Length == 0 ? "Nothing here yet" : "No matches");
            int emptyTop = rowsTop + (noticeRows * rowHeight);
            DrawText(dc, empty, pad, emptyTop, textRight, emptyTop + rowHeight);
        }
        else
        {
            for (int i = 0; i < _results.Length; i++)
            {
                int top = rowsTop + ((i + noticeRows) * rowHeight);
                if (top >= client.Height)
                {
                    break;
                }

                if (i == _selected)
                {
                    Fill(dc, 0, top, client.Width, top + rowHeight, _brushSelection);
                    Fill(dc, 0, top, Scale(3), top + rowHeight, _brushAccent);
                }

                FileEntry entry = _results[i].Entry;
                string name = Path.GetFileName(entry.Path);
                if (name.Length == 0)
                {
                    name = entry.Path;
                }

                string location = Path.GetDirectoryName(entry.Path) ?? string.Empty;

                // Tag comes from where the row was produced, not from its score:
                // index matches can score above any of the synthetic values.
                bool isBookmark = _results[i].Kind == SearchResultKind.Bookmark;
                bool isHistory = _results[i].Kind == SearchResultKind.History;

                NativeMethods.SelectObject(dc, _fontName);
                NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_inkColor >> 16 & 0xFF, _inkColor >> 8 & 0xFF, _inkColor & 0xFF));
                DrawText(dc, name, pad + Scale(6), top + Scale(4), textRight, top + Scale(30));

                NativeMethods.SelectObject(dc, _fontPath);
                NativeMethods.SetTextColor(dc, NativeMethods.Rgb(_dimColor >> 16 & 0xFF, _dimColor >> 8 & 0xFF, _dimColor & 0xFF));

                string subtitle = location;
                if (isBookmark)
                {
                    subtitle = "Bookmark  •  " + location;
                }
                else if (isHistory)
                {
                    subtitle = "History  •  " + location;
                }

                DrawText(dc, subtitle, pad + Scale(6), top + Scale(27), textRight, top + rowHeight - Scale(2));
            }
        }

        NativeMethods.SelectObject(dc, previousFont);
        NativeMethods.BitBlt(paint.Hdc, 0, 0, client.Width, client.Height, dc, 0, 0, NativeMethods.Srccopy);
        NativeMethods.EndPaint(_hwnd, ref paint);
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

        foreach (IntPtr brush in new[] { _brushBackground, _brushSelection, _brushAccent, _brushBorder })
        {
            if (brush != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(brush);
            }
        }

        foreach (IntPtr font in new[] { _fontQuery, _fontName, _fontPath })
        {
            if (font != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(font);
            }
        }
    }

    private void RecreateResources()
    {
        // Delete old brushes
        if (_brushBackground != IntPtr.Zero) NativeMethods.DeleteObject(_brushBackground);
        if (_brushSelection != IntPtr.Zero) NativeMethods.DeleteObject(_brushSelection);
        if (_brushAccent != IntPtr.Zero) NativeMethods.DeleteObject(_brushAccent);
        if (_brushBorder != IntPtr.Zero) NativeMethods.DeleteObject(_brushBorder);

        // Recreate with new colors
        _brushBackground = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_backgroundColor >> 16 & 0xFF, _backgroundColor >> 8 & 0xFF, _backgroundColor & 0xFF));
        _brushSelection = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_selectionColor >> 16 & 0xFF, _selectionColor >> 8 & 0xFF, _selectionColor & 0xFF));
        _brushAccent = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_accentColor >> 16 & 0xFF, _accentColor >> 8 & 0xFF, _accentColor & 0xFF));
        _brushBorder = NativeMethods.CreateSolidBrush(NativeMethods.Rgb(_borderColor >> 16 & 0xFF, _borderColor >> 8 & 0xFF, _borderColor & 0xFF));
    }

    private int Scale(int value)
    {
        return (int)Math.Round(value * _dpi / 96.0);
    }
}
