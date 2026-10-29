using System.Runtime.InteropServices;
using Glide.App.Search;
using Glide.App.Win32;
using Glide.Core.Index;
using Glide.Core.Search;
using Glide.Core.Snapshot;

namespace Glide.App.Ui;

/// <summary>
/// The launcher window. One native popup, custom-drawn with GDI, no child
/// controls: the query line and the result rows are painted directly, which
/// keeps the process free of a UI framework.
/// </summary>
internal sealed class SearchWindow
{
    internal const string ClassName = "Glide.SearchWindow";
    internal const uint WmShowGlide = NativeMethods.WmApp + 1;
    internal const uint WmIndexReady = NativeMethods.WmApp + 2;

    private const int HotkeyId = 1;
    private const int FocusCheckTimerId = 2;
    private const int VisibleRows = 8;
    private const int MaxQueryLength = 200;
    private const int ActivationGraceMs = 400;

    private const int InkColor = 0x141414;
    private const int DimColor = 0x6B6B6B;
    private const int PlaceholderColor = 0x8A8A8A;
    private const int BackgroundColor = 0xFCFCFC;
    private const int SelectionColor = 0xEDF1F4;
    private const int AccentColor = 0x0B6E63;
    private const int BorderColor = 0xDCDCDC;

    private static SearchWindow? s_instance;
    private static readonly NativeMethods.WndProc WindowProcDelegate = WindowProc;

    private readonly EntryStore? _snapshot;
    private readonly int _maxEntries;
    private readonly string[] _indexPaths;
    private EntryStore? _store;
    private string _query = string.Empty;
    private SearchResult[] _results = [];
    private int _selected;
    private bool _visible;
    private long _shownAt;
    private bool _indexing;
    private string _scanRoot = string.Empty;

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

    private SearchWindow(EntryStore? snapshot, string[] indexPaths, int maxEntries)
    {
        _snapshot = snapshot;
        _indexPaths = indexPaths;
        _maxEntries = maxEntries;
    }

    public static int Run(
        string? snapshotPath,
        string? scanRoot,
        string[] indexPaths,
        bool showOnStart,
        int maxEntries)
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);

        EntryStore? snapshot = snapshotPath is null ? null : EntryStore.FromFile(snapshotPath);
        var window = new SearchWindow(snapshot, indexPaths, maxEntries);
        s_instance = window;

        window.CreateNativeWindow();
        window.CreateResources();
        window.StartIndexing(scanRoot);

        if (showOnStart)
        {
            window.Show();
        }

        return window.MessageLoop();
    }

    public static void RequestShow()
    {
        IntPtr existing = NativeMethods.FindWindowW(ClassName, null);
        if (existing != IntPtr.Zero)
        {
            NativeMethods.SendMessageW(existing, WmShowGlide, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void CreateNativeWindow()
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

        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt, 0x20))
        {
            Console.Error.WriteLine("Glide: Ctrl+Alt+Space is already taken; start Glide again to open the window.");
        }
    }

    private void CreateResources()
    {
        _fontQuery = CreateFont(13.5, NativeMethods.FwNormal);
        _fontName = CreateFont(12, NativeMethods.FwSemiBold);
        _fontPath = CreateFont(9.5, NativeMethods.FwNormal);

        _brushBackground = NativeMethods.CreateSolidBrush(Rgb(BackgroundColor));
        _brushSelection = NativeMethods.CreateSolidBrush(Rgb(SelectionColor));
        _brushAccent = NativeMethods.CreateSolidBrush(Rgb(AccentColor));
        _brushBorder = NativeMethods.CreateSolidBrush(Rgb(BorderColor));
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

            case NativeMethods.WmLButtonDblClk:
                SelectRowAt(lParam);
                OpenSelected();
                return IntPtr.Zero;

            case NativeMethods.WmActivate:
                OnActivate((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case NativeMethods.WmTimer:
                if (wParam.ToInt64() == FocusCheckTimerId)
                {
                    NativeMethods.KillTimer(_hwnd, (UIntPtr)FocusCheckTimerId);

                    if (_visible && NativeMethods.GetForegroundWindow() != _hwnd)
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

            default:
                return NativeMethods.DefWindowProcW(_hwnd, message, wParam, lParam);
        }
    }

    private void OnKeyDown(int key)
    {
        bool control = (NativeMethods.GetKeyState(NativeMethods.VkControl) & 0x8000) != 0;

        switch (key)
        {
            case NativeMethods.VkEscape:
                Hide();
                break;

            case NativeMethods.VkReturn:
                OpenSelected();
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
        int row = (y - top) / Scale(52);

        if (row >= 0 && row < _results.Length)
        {
            _selected = row;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void OpenSelected()
    {
        if (_results.Length == 0)
        {
            return;
        }

        string path = _results[Math.Clamp(_selected, 0, _results.Length - 1)].Entry.Path;
        Hide();
        NativeMethods.ShellExecuteW(IntPtr.Zero, "open", path, null, null, NativeMethods.SwShow);
    }

    private void Refresh()
    {
        EntryStore? store = Volatile.Read(ref _store);

        if (store is not null)
        {
            _results = store.Search(_query, VisibleRows).ToArray();
        }
        else if (_indexPaths.Length > 0)
        {
            _results = [.. IndexSearchSource.SearchMany(_indexPaths, _query, VisibleRows)];
        }
        else
        {
            _results = [];
        }

        _selected = 0;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
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
            NativeMethods.SetTextColor(dc, Rgb(PlaceholderColor));
            DrawText(dc, "Search files and folders", pad, 0, textRight, header);
        }
        else
        {
            NativeMethods.SetTextColor(dc, Rgb(InkColor));
            DrawText(dc, _query, pad, 0, textRight, header);

            if (NativeMethods.GetTextExtentPoint32W(dc, _query, _query.Length, out NativeMethods.Size size))
            {
                int caretTop = (header - Scale(20)) / 2;
                Fill(dc, pad + size.Cx + Scale(1), caretTop, pad + size.Cx + Scale(3), caretTop + Scale(20), _brushAccent);
            }
        }

        Fill(dc, 0, header, client.Width, header + Scale(1), _brushBorder);

        int rowsTop = header + Scale(1);

        if (_store is null && _indexPaths.Length == 0)
        {
            NativeMethods.SetTextColor(dc, Rgb(DimColor));
            string message = _indexing ? $"Indexing {_scanRoot}" : "Indexing";
            DrawText(dc, message, pad, rowsTop, textRight, rowsTop + rowHeight);
        }
        else if (_results.Length == 0)
        {
            NativeMethods.SetTextColor(dc, Rgb(DimColor));
            DrawText(dc, _query.Length == 0 ? "Nothing here yet" : "No matches", pad, rowsTop, textRight, rowsTop + rowHeight);
        }
        else
        {
            for (int i = 0; i < _results.Length; i++)
            {
                int top = rowsTop + (i * rowHeight);
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

                NativeMethods.SelectObject(dc, _fontName);
                NativeMethods.SetTextColor(dc, Rgb(InkColor));
                DrawText(dc, name, pad + Scale(6), top + Scale(4), textRight, top + Scale(30));

                NativeMethods.SelectObject(dc, _fontPath);
                NativeMethods.SetTextColor(dc, Rgb(DimColor));
                DrawText(dc, location, pad + Scale(6), top + Scale(27), textRight, top + rowHeight - Scale(2));
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

    private int Scale(int value)
    {
        return (int)Math.Round(value * _dpi / 96.0);
    }

    private static int Rgb(int value)
    {
        return NativeMethods.Rgb(value >> 16 & 0xFF, value >> 8 & 0xFF, value & 0xFF);
    }
}
