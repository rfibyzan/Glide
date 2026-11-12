using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Glide.App.Win32;
using Glide.Core.Search;
using Glide.Core.Settings;

namespace Glide.App.Ui;

/// <summary>
/// The Options window, laid out after Listary's: a page list on the left
/// (General, Appearance, Hotkey, File Search, Priorities, Web Search,
/// Commands, Integration, About) and OK / Cancel / Apply at the bottom.
///
/// It edits a copy of the settings. Apply saves the copy, writes the startup
/// entry and hands it to the launcher, which re-registers the shortcut,
/// re-themes, and turns hooks and the tray icon on or off without a restart.
///
/// Drawn with GDI under a custom title bar. Text boxes are native EDIT
/// controls colored for the theme, because typing, selection and IME are
/// not worth reimplementing. The window exists only while it is open.
/// </summary>
internal sealed unsafe class SettingsWindow
{
    private const string ClassName = "Glide.Options";
    private const int WindowWidth = 800;
    private const int WindowHeight = 580;
    private const int TitleHeight = 40;
    private const int SidebarWidth = 196;
    private const int FooterHeight = 60;
    private const int ContentPad = 28;

    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmCtlColorEdit = 0x0133;
    private const uint WmSetFont = 0x0030;
    private const uint WmClose = 0x0010;
    private const uint EnChange = 0x0300;
    private const int HtClient = 1;
    private const int HtCaption = 2;

    private static SettingsWindow? s_instance;
    private static readonly NativeMethods.WndProc WindowProcDelegate = WindowProc;
    private static bool s_classRegistered;

    private readonly SearchWindow _owner;
    private readonly AppSettings _draft;
    private bool _autoStart;
    private Palette _palette;

    private IntPtr _hwnd;
    private int _dpi = 96;
    private Page _page = Page.General;
    private readonly List<Control> _controls = [];
    private readonly Dictionary<IntPtr, Action<string>> _edits = [];
    private Control? _hover;
    private HotkeyField? _recording;
    private string? _status;
    private bool _statusIsError;
    private TitleButton _titleHover;
    private bool _trackingMouse;

    private int _filterIndex;
    private int _webIndex;
    private int _commandIndex;

    private IntPtr _fontTitle;
    private IntPtr _fontSection;
    private IntPtr _fontBody;
    private IntPtr _fontSmall;
    private IntPtr _fontGlyph;
    private IntPtr _fontGlyphSmall;
    private IntPtr _brushBackground;
    private IntPtr _brushSidebar;
    private IntPtr _brushRaised;
    private IntPtr _brushSelection;
    private IntPtr _brushBorder;
    private IntPtr _brushAccent;
    private IntPtr _brushAccentSoft;
    private IntPtr _brushClose;
    private IntPtr _logo;

    private IntPtr _backDc;
    private IntPtr _backBitmap;
    private IntPtr _previousBitmap;
    private int _backWidth;
    private int _backHeight;

    private enum Page
    {
        General,
        Appearance,
        Hotkey,
        FileSearch,
        Priorities,
        WebSearch,
        Commands,
        Integration,
        About,
    }

    private enum TitleButton
    {
        None,
        Minimize,
        Close,
    }

    private static readonly (Page Page, string Glyph, string Label)[] Pages =
    [
        (Page.General, "", "General"),
        (Page.Appearance, "", "Appearance"),
        (Page.Hotkey, "", "Hotkey"),
        (Page.FileSearch, "", "File Search"),
        (Page.Priorities, "", "Priorities"),
        (Page.WebSearch, "", "Web Search"),
        (Page.Commands, "", "Commands"),
        (Page.Integration, "", "Integration"),
        (Page.About, "", "About"),
    ];

    private SettingsWindow(SearchWindow owner)
    {
        _owner = owner;
        _draft = SettingsManager.Clone(owner.CurrentSettings);
        _autoStart = AutoStart.IsEnabled();
        _palette = Palette.For(IsDark(_draft.Theme));
    }

    public static void Show(SearchWindow owner)
    {
        if (s_instance is not null && s_instance._hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(s_instance._hwnd, SwRestore);
            NativeMethods.SetForegroundWindow(s_instance._hwnd);
            return;
        }

        var window = new SettingsWindow(owner);
        s_instance = window;
        window.Create();
        window.BuildPage();
        NativeMethods.ShowWindow(window._hwnd, NativeMethods.SwShow);
        NativeMethods.SetForegroundWindow(window._hwnd);
    }

    // ------------------------------------------------------------------
    // Window

    private void Create()
    {
        IntPtr instance = NativeMethods.GetModuleHandleW(null);

        if (!s_classRegistered)
        {
            var windowClass = new NativeMethods.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
                Instance = instance,
                Cursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)NativeMethods.IdcArrow),
                Icon = UiNative.LoadAppIcon(32),
                IconSmall = UiNative.LoadAppIcon(16),
                ClassName = ClassName,
            };

            NativeMethods.RegisterClassExW(ref windowClass);
            s_classRegistered = true;
        }

        // A captioned window keeps the DWM shadow, snap and taskbar button;
        // WM_NCCALCSIZE then gives the whole window to the client area, where
        // the title bar is drawn.
        const int Style = WsCaption | WsSysMenu | WsMinimizeBox | WsClipChildren;
        _hwnd = NativeMethods.CreateWindowExW(WsExAppWindow, ClassName, "Glide Options", Style, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi >= 72)
        {
            _dpi = (int)dpi;
        }

        CreateResources();
        UiNative.StyleWindow(_hwnd, _palette);
        Center();
    }

    private void Center()
    {
        NativeMethods.GetCursorPos(out NativeMethods.Point cursor);
        IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            info.Work = new NativeMethods.Rect { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
        }

        int width = Scale(WindowWidth);
        int height = Scale(WindowHeight);
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            info.Work.Left + ((info.Work.Width - width) / 2),
            info.Work.Top + ((info.Work.Height - height) / 2),
            width,
            height,
            NativeMethods.SwpNoZOrder | SwpFrameChanged);
    }

    private void CreateResources()
    {
        string text = UiNative.TextFontFace(display: false);
        _fontTitle = CreateFont(15, NativeMethods.FwSemiBold, UiNative.TextFontFace(display: true));
        _fontSection = CreateFont(10.5, NativeMethods.FwSemiBold, text);
        _fontBody = CreateFont(10, NativeMethods.FwNormal, text);
        _fontSmall = CreateFont(9, NativeMethods.FwNormal, text);
        _fontGlyph = CreateFont(11, NativeMethods.FwNormal, UiNative.IconFontFace);
        _fontGlyphSmall = CreateFont(8, NativeMethods.FwNormal, UiNative.IconFontFace);
        _logo = UiNative.LoadAppIcon(Scale(16));
        CreateBrushes();
    }

    private void CreateBrushes()
    {
        _brushBackground = Brush(_palette.Background);
        _brushSidebar = Brush(_palette.Surface);
        _brushRaised = Brush(_palette.Raised);
        _brushSelection = Brush(_palette.Selection);
        _brushBorder = Brush(_palette.Border);
        _brushAccent = Brush(_palette.Accent);
        _brushAccentSoft = Brush(_palette.AccentSoft);
        _brushClose = Brush(0xC42B1C);
    }

    private void DeleteBrushes()
    {
        foreach (IntPtr brush in new[] { _brushBackground, _brushSidebar, _brushRaised, _brushSelection, _brushBorder, _brushAccent, _brushAccentSoft, _brushClose })
        {
            if (brush != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(brush);
            }
        }
    }

    private void DestroyResources()
    {
        DeleteBrushes();

        foreach (IntPtr font in new[] { _fontTitle, _fontSection, _fontBody, _fontSmall, _fontGlyph, _fontGlyphSmall })
        {
            if (font != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(font);
            }
        }

        if (_logo != IntPtr.Zero)
        {
            UiNative.DestroyIcon(_logo);
        }

        if (_backDc != IntPtr.Zero)
        {
            if (_backBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_backDc, _previousBitmap);
                NativeMethods.DeleteObject(_backBitmap);
            }

            NativeMethods.DeleteDC(_backDc);
        }
    }

    /// <summary>Switches colors when the theme is changed on the Appearance page.</summary>
    private void Retheme()
    {
        Palette next = Palette.For(IsDark(_draft.Theme));
        if (next == _palette)
        {
            return;
        }

        _palette = next;
        DeleteBrushes();
        CreateBrushes();
        UiNative.StyleWindow(_hwnd, _palette);
        BuildPage();
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        SettingsWindow? window = s_instance;
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
            case WmNcCalcSize when wParam != IntPtr.Zero:
                // The whole window is client area; the title bar is ours.
                return IntPtr.Zero;

            case WmNcHitTest:
                return HitTest(lParam);

            case NativeMethods.WmPaint:
                Paint();
                return IntPtr.Zero;

            case NativeMethods.WmEraseBkgnd:
                return 1;

            case NativeMethods.WmMouseMove:
                OnMouseMove(X(lParam), Y(lParam));
                return IntPtr.Zero;

            case NativeMethods.WmMouseLeave:
                _trackingMouse = false;
                SetHover(null, TitleButton.None);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonUp:
                OnClick(X(lParam), Y(lParam));
                return IntPtr.Zero;

            case NativeMethods.WmKeyDown:
            case NativeMethods.WmSysKeyDown:
                if (OnKeyDown((uint)wParam))
                {
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WmChar:
            case NativeMethods.WmSysChar:
                if (_recording is not null)
                {
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WmCommand:
                if ((((long)wParam >> 16) & 0xFFFF) == EnChange && _edits.TryGetValue(lParam, out Action<string>? setter))
                {
                    setter(GetText(lParam));
                }

                return IntPtr.Zero;

            case WmCtlColorEdit:
                SetTextColor(wParam, Palette.ToColorRef(_palette.Ink));
                SetBkColor(wParam, Palette.ToColorRef(_palette.Raised));
                return _brushRaised;

            case WmClose:
                Close();
                return IntPtr.Zero;

            case NativeMethods.WmDestroy:
                DestroyResources();
                if (s_instance == this)
                {
                    s_instance = null;
                }

                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(_hwnd, message, wParam, lParam);
    }

    private IntPtr HitTest(IntPtr lParam)
    {
        var point = new NativeMethods.Point { X = X(lParam), Y = Y(lParam) };
        ScreenToClient(_hwnd, ref point);

        if (point.Y >= 0 && point.Y < Scale(TitleHeight) && TitleButtonAt(point.X, point.Y) == TitleButton.None)
        {
            return HtCaption;
        }

        return HtClient;
    }

    private void Close()
    {
        _recording = null;
        IntPtr hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        NativeMethods.DestroyWindow(hwnd);
        s_instance = null;
    }

    // ------------------------------------------------------------------
    // Applying

    /// <summary>
    /// Saves the draft and makes it live. The launcher may refuse the main
    /// shortcut when another program owns it; the page then says so and the
    /// draft keeps the user's choice for another try.
    /// </summary>
    private bool Apply()
    {
        AppSettings settings = SettingsManager.Validate(SettingsManager.Clone(_draft));
        string? error = _owner.ApplySettings(settings);

        SettingsManager.Save(SettingsManager.Clone(_owner.CurrentSettings));
        bool startupOk = AutoStart.Set(_autoStart, Environment.ProcessPath ?? "glide.exe");

        if (error is not null)
        {
            SetStatus(error + " Pick another shortcut.", isError: true);
            return false;
        }

        if (!startupOk)
        {
            SetStatus("Settings saved, but Windows refused the startup entry.", isError: true);
            return false;
        }

        SetStatus("Settings saved and applied.", isError: false);
        return true;
    }

    private void SetStatus(string text, bool isError)
    {
        _status = text;
        _statusIsError = isError;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // ------------------------------------------------------------------
    // Pages

    private int ContentLeft => Scale(SidebarWidth) + Scale(ContentPad);

    private int ContentRight
    {
        get
        {
            NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);
            return client.Width - Scale(ContentPad);
        }
    }

    private void BuildPage()
    {
        foreach (IntPtr edit in _edits.Keys)
        {
            NativeMethods.DestroyWindow(edit);
        }

        _edits.Clear();
        _controls.Clear();
        _hover = null;
        _recording = null;

        var layout = new Layout(this, ContentLeft, Scale(TitleHeight) + Scale(22), ContentRight);

        switch (_page)
        {
            case Page.General:
                BuildGeneral(layout);
                break;
            case Page.Appearance:
                BuildAppearance(layout);
                break;
            case Page.Hotkey:
                BuildHotkey(layout);
                break;
            case Page.FileSearch:
                BuildFileSearch(layout);
                break;
            case Page.Priorities:
                BuildPriorities(layout);
                break;
            case Page.WebSearch:
                BuildWebSearch(layout);
                break;
            case Page.Commands:
                BuildCommands(layout);
                break;
            case Page.Integration:
                BuildIntegration(layout);
                break;
            case Page.About:
                BuildAbout(layout);
                break;
        }

        BuildFooter();
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void BuildGeneral(Layout layout)
    {
        layout.Title("General");
        layout.Check("Start Glide when Windows starts", () => _autoStart, v => _autoStart = v);
        layout.Check("Show Glide in the notification area", () => _draft.ShowTrayIcon, v => _draft.ShowTrayIcon = v);
        layout.Check("Hide the search window when it loses focus", () => _draft.HideWhenLosingFocus, v => _draft.HideWhenLosingFocus = v);

        layout.Section("Open folder with");
        layout.Note("Leave the program empty to open folders in File Explorer. %1 stands for the folder.");
        layout.Text("Program", () => _draft.OpenFolderWith.Path, v => _draft.OpenFolderWith.Path = v);
        layout.Text("Arguments", () => _draft.OpenFolderWith.Arguments, v => _draft.OpenFolderWith.Arguments = v);
    }

    private void BuildAppearance(Layout layout)
    {
        layout.Title("Appearance");
        layout.Section("Theme");

        string[] themes = ["system", "light", "dark"];
        string[] labels = ["Use the Windows setting", "Light", "Dark"];

        for (int i = 0; i < themes.Length; i++)
        {
            string theme = themes[i];
            layout.Radio(labels[i], () => _draft.Theme == theme, () =>
            {
                _draft.Theme = theme;
                Retheme();
            });
        }

        layout.Section("Results");
        layout.Choice(
            "Rows shown at once",
            ["4", "5", "6", "7", "8"],
            () => Math.Clamp(_draft.MaxResults, 4, 8) - 4,
            i => _draft.MaxResults = i + 4);
    }

    private void BuildHotkey(Layout layout)
    {
        layout.Title("Hotkey");
        layout.Hotkey(
            "Show Glide",
            () => Describe(_draft.Hotkey),
            spec =>
            {
                if (HotkeyParser.TryParseSpec(spec, out HotkeySettings parsed))
                {
                    _draft.Hotkey.Modifiers = parsed.Modifiers;
                    _draft.Hotkey.Key = parsed.Key;
                }
            });

        layout.Button("Reset to default", () =>
        {
            var defaults = new HotkeySettings();
            _draft.Hotkey.Modifiers = defaults.Modifiers;
            _draft.Hotkey.Key = defaults.Key;
        });

        layout.Choice(
            "Pressing it while Glide is open",
            ["Hides Glide", "Keeps Glide open"],
            () => _draft.Hotkey.PressAgainHides ? 0 : 1,
            i => _draft.Hotkey.PressAgainHides = i == 0);

        layout.Check("Ignore the hotkey while a full-screen app is in front", () => _draft.Hotkey.DisableInFullScreen, v => _draft.Hotkey.DisableInFullScreen = v);

        layout.Section("Quick Switch");
        layout.Hotkey(
            "Jump a dialog to the Explorer folder",
            () => _draft.Integration.QuickSwitchHotkey,
            spec => _draft.Integration.QuickSwitchHotkey = spec);
        layout.Note("Registered only while an Open or Save dialog is in front, so the keys keep their meaning everywhere else.");

        layout.Section("In the search window");
        layout.Note("Enter open  ·  Ctrl+Enter open folder  ·  Shift+Enter copy path  ·  Ctrl+1…8 open a top result  ·  Tab browse into a folder  ·  F2 actions  ·  Esc hide");
    }

    private void BuildFileSearch(Layout layout)
    {
        layout.Title("File Search");
        layout.Section("Search filters");
        layout.Note("Type a keyword with a colon, such as doc: report, to search one kind of file.");

        List<SearchFilterSettings> filters = [.. _draft.SearchFilters];
        _filterIndex = Math.Clamp(_filterIndex, -1, filters.Count - 1);

        layout.List(
            [("Keyword", 0.18), ("Name", 0.27), ("Finds", 0.55)],
            filters.Count,
            (row, column) => column switch
            {
                0 => filters[row].Keyword + ":",
                1 => filters[row].Name,
                _ => filters[row].FoldersOnly ? "folders"
                    : filters[row].Extensions.Length == 0 ? "files"
                    : string.Join(", ", filters[row].Extensions),
            },
            () => _filterIndex,
            i => _filterIndex = i,
            rows: 5);

        layout.ListButtons(
            add: () =>
            {
                filters.Add(new SearchFilterSettings { Keyword = "new", Name = "New filter", Extensions = ["txt"] });
                _draft.SearchFilters = [.. filters];
                _filterIndex = filters.Count - 1;
            },
            remove: _filterIndex < 0 ? null : () =>
            {
                filters.RemoveAt(_filterIndex);
                _draft.SearchFilters = [.. filters];
                _filterIndex = Math.Min(_filterIndex, filters.Count - 1);
            },
            up: _filterIndex <= 0 ? null : () => Swap(filters, _filterIndex, --_filterIndex, f => _draft.SearchFilters = f),
            down: _filterIndex < 0 || _filterIndex >= filters.Count - 1 ? null : () => Swap(filters, _filterIndex, ++_filterIndex, f => _draft.SearchFilters = f));

        if (_filterIndex >= 0)
        {
            SearchFilterSettings filter = filters[_filterIndex];
            layout.TextPair(
                ("Keyword", () => filter.Keyword, v => filter.Keyword = v.Trim().TrimEnd(':')),
                ("Name", () => filter.Name, v => filter.Name = v));
            layout.Text(
                "Extensions",
                () => string.Join(", ", filter.Extensions),
                v => filter.Extensions = v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('.').ToLowerInvariant()).ToArray());
            layout.Choice(
                "Finds",
                ["Files and folders", "Files only", "Folders only"],
                () => filter.FoldersOnly ? 2 : filter.FilesOnly ? 1 : 0,
                i =>
                {
                    filter.FilesOnly = i == 1;
                    filter.FoldersOnly = i == 2;
                });
        }

        layout.Check("Hide system and hidden files when browsing a folder", () => _draft.FileSearch.HideSystemAndHidden, v => _draft.FileSearch.HideSystemAndHidden = v);
    }

    private void BuildPriorities(Layout layout)
    {
        layout.Title("Priorities");
        layout.Note("One folder per line. Environment variables such as %USERPROFILE% work.");

        layout.MultilineText("High priority: listed first", () => Lines(_draft.FileSearch.HighPriorityPaths), v => _draft.FileSearch.HighPriorityPaths = SplitLines(v), lines: 3);
        layout.MultilineText("Low priority: listed last", () => Lines(_draft.FileSearch.RarelyUsedPaths), v => _draft.FileSearch.RarelyUsedPaths = SplitLines(v), lines: 4);
        layout.MultilineText("Never shown", () => Lines(_draft.FileSearch.DoNotIndexPaths), v => _draft.FileSearch.DoNotIndexPaths = SplitLines(v), lines: 3);
    }

    private void BuildWebSearch(Layout layout)
    {
        layout.Title("Web Search");
        layout.Note("Type the keyword, a space and your search: gg weather. {query} in the URL is replaced by your search. The first site is also offered when nothing on disk matches.");

        List<WebSearchSettings> sites = [.. _draft.WebSearches];
        _webIndex = Math.Clamp(_webIndex, -1, sites.Count - 1);

        layout.List(
            [("Keyword", 0.16), ("Name", 0.24), ("URL", 0.60)],
            sites.Count,
            (row, column) => column switch
            {
                0 => sites[row].Keyword,
                1 => sites[row].Name,
                _ => sites[row].Url,
            },
            () => _webIndex,
            i => _webIndex = i,
            rows: 6);

        layout.ListButtons(
            add: () =>
            {
                sites.Add(new WebSearchSettings { Keyword = "new", Name = "New site", Url = "https://example.com/search?q={query}" });
                _draft.WebSearches = [.. sites];
                _webIndex = sites.Count - 1;
            },
            remove: _webIndex < 0 ? null : () =>
            {
                sites.RemoveAt(_webIndex);
                _draft.WebSearches = [.. sites];
                _webIndex = Math.Min(_webIndex, sites.Count - 1);
            },
            up: _webIndex <= 0 ? null : () => Swap(sites, _webIndex, --_webIndex, s => _draft.WebSearches = s),
            down: _webIndex < 0 || _webIndex >= sites.Count - 1 ? null : () => Swap(sites, _webIndex, ++_webIndex, s => _draft.WebSearches = s));

        if (_webIndex >= 0)
        {
            WebSearchSettings site = sites[_webIndex];
            layout.TextPair(
                ("Keyword", () => site.Keyword, v => site.Keyword = v.Trim()),
                ("Name", () => site.Name, v => site.Name = v));
            layout.Text("URL", () => site.Url, v => site.Url = v.Trim());
        }
    }

    private void BuildCommands(Layout layout)
    {
        layout.Title("Commands");
        layout.Note("Built in: " + string.Join(", ", CommandCatalog.BuiltIn.Select(c => c.Title)) + ". Type a few letters of a name to run it.");

        layout.Section("Your commands");
        List<CustomCommandSettings> commands = [.. _draft.CustomCommands];
        _commandIndex = Math.Clamp(_commandIndex, -1, commands.Count - 1);

        layout.List(
            [("Name", 0.28), ("Program, file or URL", 0.47), ("Arguments", 0.25)],
            commands.Count,
            (row, column) => column switch
            {
                0 => commands[row].Title,
                1 => commands[row].Target,
                _ => commands[row].Arguments,
            },
            () => _commandIndex,
            i => _commandIndex = i,
            rows: 5);

        layout.ListButtons(
            add: () =>
            {
                commands.Add(new CustomCommandSettings { Title = "Notepad", Target = "notepad.exe" });
                _draft.CustomCommands = [.. commands];
                _commandIndex = commands.Count - 1;
            },
            remove: _commandIndex < 0 ? null : () =>
            {
                commands.RemoveAt(_commandIndex);
                _draft.CustomCommands = [.. commands];
                _commandIndex = Math.Min(_commandIndex, commands.Count - 1);
            },
            up: null,
            down: null);

        if (_commandIndex >= 0)
        {
            CustomCommandSettings command = commands[_commandIndex];
            layout.Text("Name", () => command.Title, v => command.Title = v);
            layout.Text("Program, file or URL", () => command.Target, v => command.Target = v.Trim());
            layout.Text("Arguments", () => command.Arguments, v => command.Arguments = v);
        }
    }

    private void BuildIntegration(Layout layout)
    {
        layout.Title("Integration");

        layout.Section("File Explorer");
        layout.Check("Type in a folder to search", () => _draft.Integration.ExplorerTypeToSearch, v => _draft.Integration.ExplorerTypeToSearch = v);
        layout.Note("A search bar opens inside the window; a folder you pick opens in that same window.");

        layout.Section("Open and Save dialogs");
        layout.Check("Keep a search bar under the dialog while it is open", () => _draft.Integration.DialogSearchBar, v => _draft.Integration.DialogSearchBar = v);
        layout.Check("Type in the file list to search", () => _draft.Integration.DialogTypeToSearch, v => _draft.Integration.DialogTypeToSearch = v);
        layout.Check("Send the chosen file or folder to the dialog", () => _draft.Integration.EnableFileDialogIntegration, v => _draft.Integration.EnableFileDialogIntegration = v);
        layout.Check("Quick Switch to the folder open in Explorer (shortcut on the Hotkey page)", () => _draft.Integration.EnableQuickSwitch, v => _draft.Integration.EnableQuickSwitch = v);
        layout.Note("Glide drives Explorer and dialogs from outside; nothing is loaded into other programs.");
    }

    private void BuildAbout(Layout layout)
    {
        layout.Title("Glide");
        string version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.6";
        layout.Note($"Version {version}. A file search launcher in one small native process.");

        (double workingSet, double privateBytes) = MemoryUse();
        layout.Note($"Using {workingSet:0.0} MB of memory right now ({privateBytes:0.0} MB private).");

        layout.Section("Files");
        layout.Button("Open settings folder", () => NativeMethods.ShellExecuteW(IntPtr.Zero, "open", SettingsManager.SettingsDirectory, null, null, NativeMethods.SwShow));
        layout.Button("Open index folder", () => NativeMethods.ShellExecuteW(IntPtr.Zero, "open", Glide.App.Search.IndexLocator.Directory, null, null, NativeMethods.SwShow));
    }

    private void BuildFooter()
    {
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);
        int buttonWidth = Scale(96);
        int height = Scale(32);
        int top = client.Height - Scale(FooterHeight) + ((Scale(FooterHeight) - height) / 2);
        int right = client.Width - Scale(ContentPad);

        _controls.Add(new Button(this, "Apply", () => Apply(), primary: false) { Bounds = Rect(right - buttonWidth, top, right, top + height) });
        right -= buttonWidth + Scale(8);
        _controls.Add(new Button(this, "Cancel", Close, primary: false) { Bounds = Rect(right - buttonWidth, top, right, top + height) });
        right -= buttonWidth + Scale(8);
        _controls.Add(new Button(
            this,
            "OK",
            () =>
            {
                if (Apply())
                {
                    Close();
                }
            },
            primary: true)
        { Bounds = Rect(right - buttonWidth, top, right, top + height) });
    }

    private static void Swap<T>(List<T> items, int from, int to, Action<T[]> store)
    {
        (items[from], items[to]) = (items[to], items[from]);
        store([.. items]);
    }

    private static string Lines(string[] values) => string.Join("\r\n", values);

    private static string[] SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Describe(HotkeySettings hotkey) =>
        HotkeyParser.TryParse(hotkey, out uint mods, out uint vk) ? HotkeyParser.ToSpec(mods, vk) : string.Join("+", hotkey.Modifiers.Append(hotkey.Key));

    // ------------------------------------------------------------------
    // Input

    private void OnMouseMove(int x, int y)
    {
        if (!_trackingMouse)
        {
            var track = new NativeMethods.TrackMouseEventStructure
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.TrackMouseEventStructure>(),
                dwFlags = NativeMethods.TmeLeave,
                hwndTrack = _hwnd,
            };
            NativeMethods.TrackMouseEvent(ref track);
            _trackingMouse = true;
        }

        SetHover(ControlAt(x, y), TitleButtonAt(x, y));
    }

    private void SetHover(Control? control, TitleButton button)
    {
        if (control != _hover || button != _titleHover)
        {
            _hover = control;
            _titleHover = button;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void OnClick(int x, int y)
    {
        switch (TitleButtonAt(x, y))
        {
            case TitleButton.Close:
                Close();
                return;
            case TitleButton.Minimize:
                NativeMethods.ShowWindow(_hwnd, SwMinimize);
                return;
        }

        int index = NavIndexAt(x, y);
        if (index >= 0)
        {
            _page = Pages[index].Page;
            _status = null;
            BuildPage();
            return;
        }

        Control? control = ControlAt(x, y);
        if (control is not HotkeyField)
        {
            _recording = null;
        }

        NativeMethods.SetFocus(_hwnd);
        control?.Click(x, y);

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    /// <summary>Records a shortcut while a hotkey field is waiting for one.</summary>
    private bool OnKeyDown(uint vk)
    {
        if (_recording is null)
        {
            return false;
        }

        switch (vk)
        {
            case 0x1B: // Escape cancels
                _recording = null;
                NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
                return true;

            case 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5:
                // A modifier alone: keep waiting for the key.
                return true;
        }

        uint mods = 0;
        if ((GetKeyState(0x11) & 0x8000) != 0)
        {
            mods |= HotkeyModifiers.Control;
        }

        if ((GetKeyState(0x10) & 0x8000) != 0)
        {
            mods |= HotkeyModifiers.Shift;
        }

        if ((GetKeyState(0x12) & 0x8000) != 0)
        {
            mods |= HotkeyModifiers.Alt;
        }

        if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0)
        {
            mods |= HotkeyModifiers.Win;
        }

        // A global shortcut needs a modifier, except the function keys, and
        // only keys that survive a save and reload are accepted.
        bool functionKey = vk is >= 0x70 and <= 0x7B;
        string spec = HotkeyParser.ToSpec(mods, vk);

        if ((mods == 0 && !functionKey) ||
            !HotkeyParser.TryParseSpec(spec, out HotkeySettings parsed) ||
            !HotkeyParser.TryParse(parsed, out uint checkMods, out uint checkVk) ||
            checkMods != mods || checkVk != vk)
        {
            SetStatus("That combination cannot be a shortcut. Hold Ctrl, Alt, Shift or Win with a letter, digit, F-key, Space or /.", isError: true);
            return true;
        }

        _recording.Set(spec);
        _recording = null;
        _status = null;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        return true;
    }

    private Control? ControlAt(int x, int y)
    {
        foreach (Control control in _controls)
        {
            if (control.Enabled && Contains(control.Bounds, x, y))
            {
                return control;
            }
        }

        return null;
    }

    private static bool Contains(NativeMethods.Rect rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

    private int NavIndexAt(int x, int y)
    {
        if (x >= Scale(SidebarWidth) || y < Scale(TitleHeight) + Scale(12))
        {
            return -1;
        }

        int index = (y - Scale(TitleHeight) - Scale(12)) / Scale(38);
        return index >= 0 && index < Pages.Length ? index : -1;
    }

    private TitleButton TitleButtonAt(int x, int y)
    {
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);
        if (y < 0 || y >= Scale(TitleHeight))
        {
            return TitleButton.None;
        }

        int width = Scale(46);
        if (x >= client.Width - width)
        {
            return TitleButton.Close;
        }

        return x >= client.Width - (2 * width) ? TitleButton.Minimize : TitleButton.None;
    }

    // ------------------------------------------------------------------
    // Painting

    private void Paint()
    {
        NativeMethods.BeginPaint(_hwnd, out NativeMethods.PaintStruct paint);
        NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect client);
        EnsureBackBuffer(paint.Hdc, client.Width, client.Height);
        IntPtr dc = _backDc;

        Fill(dc, 0, 0, client.Width, client.Height, _brushBackground);
        Fill(dc, 0, 0, Scale(SidebarWidth), client.Height, _brushSidebar);
        NativeMethods.SetBkMode(dc, NativeMethods.Transparent);

        PaintTitleBar(dc, client.Width);
        PaintNavigation(dc);

        foreach (Control control in _controls)
        {
            control.Paint(dc);
        }

        // Footer rule and status line.
        int footerTop = client.Height - Scale(FooterHeight);
        Fill(dc, Scale(SidebarWidth), footerTop, client.Width, footerTop + Scale(1), _brushBorder);

        if (_status is not null)
        {
            NativeMethods.SelectObject(dc, _fontSmall);
            SetColor(dc, _statusIsError ? 0xF87171 : _palette.Accent);
            var rect = Rect(ContentLeft, footerTop + Scale(6), client.Width - Scale(350), client.Height - Scale(6));
            NativeMethods.DrawTextW(dc, _status, _status.Length, ref rect, NativeMethods.DtLeft | DtWordBreak | NativeMethods.DtNoPrefix);
        }

        NativeMethods.BitBlt(paint.Hdc, 0, 0, client.Width, client.Height, dc, 0, 0, NativeMethods.Srccopy);
        NativeMethods.EndPaint(_hwnd, ref paint);
    }

    private void PaintTitleBar(IntPtr dc, int width)
    {
        int height = Scale(TitleHeight);

        if (_logo != IntPtr.Zero)
        {
            int size = Scale(16);
            UiNative.DrawIconEx(dc, Scale(14), (height - size) / 2, _logo, size, size, 0, IntPtr.Zero, 0x0003);
        }

        NativeMethods.SelectObject(dc, _fontSmall);
        SetColor(dc, _palette.Ink);
        DrawText(dc, "Glide Options", Scale(40), 0, width / 2, height);

        int buttonWidth = Scale(46);
        PaintTitleButton(dc, width - buttonWidth, buttonWidth, height, "", _titleHover == TitleButton.Close, close: true);
        PaintTitleButton(dc, width - (2 * buttonWidth), buttonWidth, height, "", _titleHover == TitleButton.Minimize, close: false);
    }

    private void PaintTitleButton(IntPtr dc, int left, int width, int height, string glyph, bool hover, bool close)
    {
        if (hover)
        {
            Fill(dc, left, 0, left + width, height, close ? _brushClose : _brushSelection);
        }

        NativeMethods.SelectObject(dc, _fontGlyphSmall);
        SetColor(dc, hover && close ? 0xFFFFFF : _palette.Ink);
        DrawCentered(dc, glyph, left, 0, left + width, height);
    }

    private void PaintNavigation(IntPtr dc)
    {
        int top = Scale(TitleHeight) + Scale(12);
        int itemHeight = Scale(38);

        for (int i = 0; i < Pages.Length; i++, top += itemHeight)
        {
            bool selected = Pages[i].Page == _page;
            int left = Scale(8);
            int right = Scale(SidebarWidth) - Scale(8);

            if (selected)
            {
                UiNative.FillRounded(dc, left, top + Scale(2), right, top + itemHeight - Scale(2), Scale(6), _brushSelection);
                UiNative.FillRounded(dc, left, top + Scale(11), left + Scale(3), top + itemHeight - Scale(11), Scale(2), _brushAccent);
            }

            NativeMethods.SelectObject(dc, _fontGlyph);
            SetColor(dc, selected ? _palette.Accent : _palette.Dim);
            DrawCentered(dc, Pages[i].Glyph, left + Scale(10), top, left + Scale(32), top + itemHeight);

            NativeMethods.SelectObject(dc, _fontBody);
            SetColor(dc, selected ? _palette.Ink : _palette.Dim);
            DrawText(dc, Pages[i].Label, left + Scale(42), top, right, top + itemHeight);
        }
    }

    private void EnsureBackBuffer(IntPtr hdc, int width, int height)
    {
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

        _backBitmap = NativeMethods.CreateCompatibleBitmap(hdc, Math.Max(1, width), Math.Max(1, height));
        _previousBitmap = NativeMethods.SelectObject(_backDc, _backBitmap);
        _backWidth = width;
        _backHeight = height;
    }

    // ------------------------------------------------------------------
    // Helpers

    private static void Fill(IntPtr dc, int left, int top, int right, int bottom, IntPtr brush)
    {
        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        NativeMethods.FillRect(dc, ref rect, brush);
    }

    private static void SetColor(IntPtr dc, int rgb) => NativeMethods.SetTextColor(dc, Palette.ToColorRef(rgb));

    private static void DrawText(IntPtr dc, string text, int left, int top, int right, int bottom)
    {
        if (text.Length == 0 || right <= left)
        {
            return;
        }

        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        NativeMethods.DrawTextW(dc, text, text.Length, ref rect, NativeMethods.DtLeft | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix | NativeMethods.DtEndEllipsis);
    }

    private static void DrawCentered(IntPtr dc, string text, int left, int top, int right, int bottom)
    {
        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        NativeMethods.DrawTextW(dc, text, text.Length, ref rect, NativeMethods.DtCenter | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix);
    }

    private static NativeMethods.Rect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static IntPtr Brush(int rgb) => NativeMethods.CreateSolidBrush(Palette.ToColorRef(rgb));

    private IntPtr CreateFont(double points, int weight, string face) =>
        NativeMethods.CreateFontW(-(int)Math.Round(points * _dpi / 72.0), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);

    private int Scale(int value) => (int)Math.Round(value * _dpi / 96.0);

    private static int X(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);

    private static int Y(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private static string GetText(IntPtr hwnd)
    {
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        fixed (char* pointer = buffer)
        {
            int read = GetWindowTextW(hwnd, pointer, buffer.Length);
            return new string(pointer, 0, read);
        }
    }

    private static bool IsDark(string theme) =>
        theme == "dark" || (theme == "system" && SystemUsesDarkMode());

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows only.")]
    private static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static (double WorkingSetMb, double PrivateMb) MemoryUse()
    {
        var counters = new ProcessMemoryCounters { Size = (uint)sizeof(ProcessMemoryCounters) };
        if (!GetProcessMemoryInfo(GetCurrentProcess(), &counters, counters.Size))
        {
            return (0, 0);
        }

        return (counters.WorkingSetSize / 1048576.0, counters.PrivateUsage / 1048576.0);
    }

    /// <summary>Creates a themed native text box as a child of the window.</summary>
    private void CreateEdit(NativeMethods.Rect bounds, string text, bool multiline, Action<string> setter)
    {
        int style = multiline
            ? WsChild | WsVisible | WsTabStop | WsVScroll | EsMultiline | EsAutoVScroll | EsWantReturn
            : WsChild | WsVisible | WsTabStop | EsAutoHScroll;

        IntPtr edit = NativeMethods.CreateWindowExW(0, "EDIT", text, style, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, _hwnd, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
        if (edit == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SendMessageW(edit, WmSetFont, _fontBody, 1);
        SetWindowTheme(edit, _palette.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        _edits[edit] = setter;
    }

    private int MeasureNote(string text, int width)
    {
        IntPtr dc = GetDC(_hwnd);
        IntPtr old = NativeMethods.SelectObject(dc, _fontSmall);
        var rect = new NativeMethods.Rect { Left = 0, Top = 0, Right = width, Bottom = 0 };
        NativeMethods.DrawTextW(dc, text, text.Length, ref rect, NativeMethods.DtLeft | DtWordBreak | NativeMethods.DtNoPrefix | DtCalcRect);
        NativeMethods.SelectObject(dc, old);
        ReleaseDC(_hwnd, dc);
        return rect.Bottom;
    }

    // ------------------------------------------------------------------
    // Controls

    private abstract class Control(SettingsWindow owner)
    {
        protected SettingsWindow Owner { get; } = owner;

        public NativeMethods.Rect Bounds { get; set; }

        public virtual bool Enabled => true;

        protected bool Hovered => Owner._hover == this;

        public abstract void Paint(IntPtr dc);

        public virtual void Click(int x, int y)
        {
        }
    }

    private sealed class Label(SettingsWindow owner, string text, IntPtr font, int color, bool wrap) : Control(owner)
    {
        public override bool Enabled => false;

        public override void Paint(IntPtr dc)
        {
            NativeMethods.SelectObject(dc, font);
            SetColor(dc, color);
            var rect = Bounds;
            uint flags = wrap
                ? NativeMethods.DtLeft | DtWordBreak | NativeMethods.DtNoPrefix
                : NativeMethods.DtLeft | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix | NativeMethods.DtEndEllipsis;
            NativeMethods.DrawTextW(dc, text, text.Length, ref rect, flags);
        }
    }

    private sealed class Check(SettingsWindow owner, string text, Func<bool> get, Action<bool> set, bool radio) : Control(owner)
    {
        public override void Paint(IntPtr dc)
        {
            int size = Owner.Scale(18);
            int top = Bounds.Top + ((Bounds.Bottom - Bounds.Top - size) / 2);
            int radius = radio ? size / 2 : Owner.Scale(4);

            if (get())
            {
                UiNative.FillRounded(dc, Bounds.Left, top, Bounds.Left + size, top + size, radius, Owner._brushAccent);

                if (radio)
                {
                    int dot = Owner.Scale(6);
                    int dotLeft = Bounds.Left + ((size - dot) / 2);
                    int dotTop = top + ((size - dot) / 2);
                    UiNative.FillRounded(dc, dotLeft, dotTop, dotLeft + dot, dotTop + dot, dot / 2, Owner._brushBackground);
                }
                else
                {
                    NativeMethods.SelectObject(dc, Owner._fontGlyphSmall);
                    SetColor(dc, Palette.Dark.Background);
                    DrawCentered(dc, "", Bounds.Left, top, Bounds.Left + size, top + size);
                }
            }
            else
            {
                int inset = Owner.Scale(1);
                UiNative.FillRounded(dc, Bounds.Left, top, Bounds.Left + size, top + size, radius, Hovered ? Owner._brushAccent : Owner._brushBorder);
                UiNative.FillRounded(dc, Bounds.Left + inset, top + inset, Bounds.Left + size - inset, top + size - inset, Math.Max(1, radius - inset), Owner._brushRaised);
            }

            NativeMethods.SelectObject(dc, Owner._fontBody);
            SetColor(dc, Owner._palette.Ink);
            DrawText(dc, text, Bounds.Left + size + Owner.Scale(10), Bounds.Top, Bounds.Right, Bounds.Bottom);
        }

        public override void Click(int x, int y)
        {
            set(radio || !get());
        }
    }

    private sealed class Button(SettingsWindow owner, string text, Action click, bool primary) : Control(owner)
    {
        public bool IsEnabled { get; init; } = true;

        public override bool Enabled => IsEnabled;

        public override void Paint(IntPtr dc)
        {
            IntPtr fill = primary ? Owner._brushAccent : Hovered && IsEnabled ? Owner._brushSelection : Owner._brushRaised;
            UiNative.FillRounded(dc, Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom, Owner.Scale(5), fill);

            NativeMethods.SelectObject(dc, Owner._fontBody);
            SetColor(dc, primary ? Palette.Dark.Background : IsEnabled ? Owner._palette.Ink : Owner._palette.Placeholder);
            DrawCentered(dc, text, Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom);
        }

        public override void Click(int x, int y) => click();
    }

    /// <summary>A label and a value box; clicking opens the choices as a menu.</summary>
    private sealed class Choice(SettingsWindow owner, string label, string[] options, Func<int> get, Action<int> set, int boxLeft) : Control(owner)
    {
        public override void Paint(IntPtr dc)
        {
            NativeMethods.SelectObject(dc, Owner._fontBody);
            SetColor(dc, Owner._palette.Ink);
            DrawText(dc, label, Bounds.Left, Bounds.Top, boxLeft - Owner.Scale(12), Bounds.Bottom);

            int right = Math.Min(Bounds.Right, boxLeft + Owner.Scale(260));
            UiNative.FillRounded(dc, boxLeft, Bounds.Top + Owner.Scale(2), right, Bounds.Bottom - Owner.Scale(2), Owner.Scale(5), Hovered ? Owner._brushSelection : Owner._brushRaised);
            int index = Math.Clamp(get(), 0, options.Length - 1);
            DrawText(dc, options[index], boxLeft + Owner.Scale(12), Bounds.Top, right - Owner.Scale(30), Bounds.Bottom);

            NativeMethods.SelectObject(dc, Owner._fontGlyphSmall);
            SetColor(dc, Owner._palette.Dim);
            DrawCentered(dc, "", right - Owner.Scale(30), Bounds.Top, right - Owner.Scale(6), Bounds.Bottom);
        }

        public override void Click(int x, int y)
        {
            IntPtr menu = NativeMethods.CreatePopupMenu();
            for (int i = 0; i < options.Length; i++)
            {
                NativeMethods.AppendMenuW(menu, NativeMethods.MfString | (i == get() ? NativeMethods.MfChecked : 0), (UIntPtr)(i + 1), options[i]);
            }

            var point = new NativeMethods.Point { X = boxLeft, Y = Bounds.Bottom };
            ClientToScreen(Owner._hwnd, ref point);
            int picked = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TpmRetcmd | NativeMethods.TpmNonotify | NativeMethods.TpmLeftAlign, point.X, point.Y, Owner._hwnd, IntPtr.Zero);
            NativeMethods.DestroyMenu(menu);

            if (picked > 0)
            {
                set(picked - 1);
                Owner.BuildPage();
            }
        }
    }

    /// <summary>Shows a shortcut; clicked, it waits for the next key combination.</summary>
    private sealed class HotkeyField(SettingsWindow owner, string label, Func<string> get, Action<string> set, int boxLeft) : Control(owner)
    {
        public void Set(string spec) => set(spec);

        public override void Paint(IntPtr dc)
        {
            bool recording = Owner._recording == this;
            int right = Math.Min(Bounds.Right, boxLeft + Owner.Scale(320));
            int top = Bounds.Top + Owner.Scale(2);
            int bottom = Bounds.Bottom - Owner.Scale(2);

            NativeMethods.SelectObject(dc, Owner._fontBody);
            SetColor(dc, Owner._palette.Ink);
            DrawText(dc, label, Bounds.Left, Bounds.Top, boxLeft - Owner.Scale(12), Bounds.Bottom);

            if (recording)
            {
                int inset = Owner.Scale(1);
                UiNative.FillRounded(dc, boxLeft, top, right, bottom, Owner.Scale(5), Owner._brushAccent);
                UiNative.FillRounded(dc, boxLeft + inset, top + inset, right - inset, bottom - inset, Owner.Scale(4), Owner._brushAccentSoft);
            }
            else
            {
                UiNative.FillRounded(dc, boxLeft, top, right, bottom, Owner.Scale(5), Hovered ? Owner._brushSelection : Owner._brushRaised);
            }

            SetColor(dc, recording ? Owner._palette.Accent : Owner._palette.Ink);
            string text = recording ? "Press the new shortcut…   Esc cancels" : get();
            DrawText(dc, text, boxLeft + Owner.Scale(12), Bounds.Top, right - Owner.Scale(8), Bounds.Bottom);
        }

        public override void Click(int x, int y)
        {
            Owner._recording = this;
        }
    }

    /// <summary>A table with a selected row; selecting rebuilds the page to show the row's fields.</summary>
    private sealed class ListTable(
        SettingsWindow owner,
        (string Header, double Share)[] columns,
        int count,
        Func<int, int, string> cell,
        Func<int> selected,
        Action<int> select,
        int rows) : Control(owner)
    {
        private int RowHeight => Owner.Scale(28);

        private int First => Math.Max(0, Math.Min(selected(), count - 1) - (rows - 1));

        public override void Paint(IntPtr dc)
        {
            UiNative.FillRounded(dc, Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom, Owner.Scale(6), Owner._brushRaised);

            int width = Bounds.Right - Bounds.Left - Owner.Scale(16);
            int top = Bounds.Top;

            NativeMethods.SelectObject(dc, Owner._fontSmall);
            SetColor(dc, Owner._palette.Dim);
            int x = Bounds.Left + Owner.Scale(12);
            foreach ((string header, double share) in columns)
            {
                int columnWidth = (int)(width * share);
                DrawText(dc, header, x, top, x + columnWidth - Owner.Scale(8), top + RowHeight);
                x += columnWidth;
            }

            Fill(dc, Bounds.Left + Owner.Scale(6), top + RowHeight, Bounds.Right - Owner.Scale(6), top + RowHeight + Owner.Scale(1), Owner._brushBorder);
            top += RowHeight + Owner.Scale(2);

            NativeMethods.SelectObject(dc, Owner._fontBody);
            int first = First;

            for (int row = first; row < Math.Min(count, first + rows); row++, top += RowHeight)
            {
                if (row == selected())
                {
                    UiNative.FillRounded(dc, Bounds.Left + Owner.Scale(4), top, Bounds.Right - Owner.Scale(4), top + RowHeight, Owner.Scale(4), Owner._brushSelection);
                    UiNative.FillRounded(dc, Bounds.Left + Owner.Scale(6), top + Owner.Scale(8), Bounds.Left + Owner.Scale(9), top + RowHeight - Owner.Scale(8), Owner.Scale(1), Owner._brushAccent);
                }

                x = Bounds.Left + Owner.Scale(12);
                for (int column = 0; column < columns.Length; column++)
                {
                    int columnWidth = (int)(width * columns[column].Share);
                    SetColor(dc, column == 0 ? Owner._palette.Ink : Owner._palette.Dim);
                    DrawText(dc, cell(row, column), x, top, x + columnWidth - Owner.Scale(8), top + RowHeight);
                    x += columnWidth;
                }
            }

            if (count == 0)
            {
                SetColor(dc, Owner._palette.Placeholder);
                DrawText(dc, "Nothing yet. Add one below.", Bounds.Left + Owner.Scale(12), top, Bounds.Right, top + RowHeight);
            }
        }

        public override void Click(int x, int y)
        {
            int offset = y - Bounds.Top - RowHeight - Owner.Scale(2);
            if (offset < 0)
            {
                return;
            }

            int row = First + (offset / RowHeight);
            if (row < count && row != selected())
            {
                select(row);
                Owner.BuildPage();
            }
        }
    }

    /// <summary>The label and the rounded frame drawn behind a native text box.</summary>
    private sealed class TextFrame(SettingsWindow owner, string label, int boxLeft, int boxTop) : Control(owner)
    {
        public override bool Enabled => false;

        public override void Paint(IntPtr dc)
        {
            NativeMethods.SelectObject(dc, Owner._fontBody);
            SetColor(dc, Owner._palette.Ink);
            if (boxTop > Bounds.Top)
            {
                DrawText(dc, label, Bounds.Left, Bounds.Top, Bounds.Right, boxTop);
            }
            else
            {
                DrawText(dc, label, Bounds.Left, Bounds.Top, boxLeft - Owner.Scale(12), Bounds.Bottom);
            }

            UiNative.FillRounded(dc, boxLeft, boxTop + Owner.Scale(2), Bounds.Right, Bounds.Bottom - Owner.Scale(2), Owner.Scale(5), Owner._brushRaised);
        }
    }

    /// <summary>Places controls top to bottom down the content column.</summary>
    private sealed class Layout(SettingsWindow owner, int left, int top, int right)
    {
        private int _y = top;

        private int FieldLeft => left + owner.Scale(230);

        private int Row => owner.Scale(34);

        private int Gap => owner.Scale(6);

        public void Title(string text)
        {
            Add(new Label(owner, text, owner._fontTitle, owner._palette.Ink, wrap: false), owner.Scale(34));
            _y += owner.Scale(4);
        }

        public void Section(string text)
        {
            _y += owner.Scale(10);
            Add(new Label(owner, text, owner._fontSection, owner._palette.Ink, wrap: false), owner.Scale(24));
        }

        public void Note(string text)
        {
            int height = owner.MeasureNote(text, right - left) + owner.Scale(2);
            Add(new Label(owner, text, owner._fontSmall, owner._palette.Dim, wrap: true), height);
        }

        public void Check(string text, Func<bool> get, Action<bool> set) =>
            Add(new Check(owner, text, get, set, radio: false), Row);

        public void Radio(string text, Func<bool> get, Action select) =>
            Add(new Check(owner, text, get, _ => select(), radio: true), Row);

        public void Choice(string label, string[] options, Func<int> get, Action<int> set) =>
            Add(new Choice(owner, label, options, get, set, FieldLeft), Row);

        public void Hotkey(string label, Func<string> get, Action<string> set) =>
            Add(new HotkeyField(owner, label, get, set, FieldLeft), Row);

        public void Button(string text, Action click)
        {
            int width = owner.Scale(Math.Max(120, (text.Length * 8) + 32));
            owner._controls.Add(new Button(owner, text, click, primary: false)
            {
                Bounds = SettingsWindow.Rect(left, _y, left + width, _y + owner.Scale(30)),
            });
            _y += owner.Scale(30) + Gap + owner.Scale(2);
        }

        public void Text(string label, Func<string> get, Action<string> set)
        {
            owner._controls.Add(new TextFrame(owner, label, FieldLeft, _y) { Bounds = SettingsWindow.Rect(left, _y, right, _y + Row) });

            int pad = owner.Scale(10);
            int textHeight = owner.Scale(20);
            int textTop = _y + ((Row - textHeight) / 2);
            owner.CreateEdit(SettingsWindow.Rect(FieldLeft + pad, textTop, right - pad, textTop + textHeight), get(), multiline: false, set);
            _y += Row + Gap;
        }

        /// <summary>Two short text boxes on one line.</summary>
        public void TextPair((string Label, Func<string> Get, Action<string> Set) first, (string Label, Func<string> Get, Action<string> Set) second)
        {
            int middle = (left + right) / 2;
            int labelWidth = owner.Scale(76);
            int pad = owner.Scale(10);
            int textHeight = owner.Scale(20);
            int textTop = _y + ((Row - textHeight) / 2);

            owner._controls.Add(new TextFrame(owner, first.Label, left + labelWidth, _y) { Bounds = SettingsWindow.Rect(left, _y, middle - owner.Scale(10), _y + Row) });
            owner.CreateEdit(SettingsWindow.Rect(left + labelWidth + pad, textTop, middle - owner.Scale(10) - pad, textTop + textHeight), first.Get(), multiline: false, first.Set);

            owner._controls.Add(new TextFrame(owner, second.Label, middle + owner.Scale(10) + labelWidth, _y) { Bounds = SettingsWindow.Rect(middle + owner.Scale(10), _y, right, _y + Row) });
            owner.CreateEdit(SettingsWindow.Rect(middle + owner.Scale(10) + labelWidth + pad, textTop, right - pad, textTop + textHeight), second.Get(), multiline: false, second.Set);

            _y += Row + Gap;
        }

        public void MultilineText(string label, Func<string> get, Action<string> set, int lines)
        {
            int labelHeight = owner.Scale(26);
            int boxHeight = (lines * owner.Scale(18)) + owner.Scale(16);
            owner._controls.Add(new TextFrame(owner, label, left, _y + labelHeight) { Bounds = SettingsWindow.Rect(left, _y, right, _y + labelHeight + boxHeight) });

            owner.CreateEdit(
                SettingsWindow.Rect(left + owner.Scale(10), _y + labelHeight + owner.Scale(8), right - owner.Scale(4), _y + labelHeight + boxHeight - owner.Scale(8)),
                get(),
                multiline: true,
                set);
            _y += labelHeight + boxHeight + Gap;
        }

        public void List((string Header, double Share)[] columns, int count, Func<int, int, string> cell, Func<int> selected, Action<int> select, int rows)
        {
            int height = (owner.Scale(28) * (rows + 1)) + owner.Scale(8);
            Add(new ListTable(owner, columns, count, cell, selected, select, rows), height);
        }

        public void ListButtons(Action add, Action? remove, Action? up, Action? down)
        {
            int x = left;
            int width = owner.Scale(92);
            int height = owner.Scale(28);

            void Place(string text, Action? action)
            {
                owner._controls.Add(new Button(
                    owner,
                    text,
                    () =>
                    {
                        action?.Invoke();
                        owner.BuildPage();
                    },
                    primary: false)
                {
                    Bounds = SettingsWindow.Rect(x, _y, x + width, _y + height),
                    IsEnabled = action is not null,
                });
                x += width + owner.Scale(6);
            }

            Place("Add", add);
            Place("Remove", remove);
            if (up is not null || down is not null)
            {
                Place("Move up", up);
                Place("Move down", down);
            }

            _y += height + Gap + owner.Scale(4);
        }

        private void Add(Control control, int height)
        {
            control.Bounds = SettingsWindow.Rect(left, _y, right, _y + height);
            owner._controls.Add(control);
            _y += height + Gap;
        }
    }

    // ------------------------------------------------------------------
    // Native

    private const int WsCaption = 0x00C00000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsClipChildren = 0x02000000;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsTabStop = 0x00010000;
    private const int WsVScroll = 0x00200000;
    private const int WsExAppWindow = 0x00040000;
    private const int EsMultiline = 0x0004;
    private const int EsAutoVScroll = 0x0040;
    private const int EsAutoHScroll = 0x0080;
    private const int EsWantReturn = 0x1000;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const int SwpFrameChanged = 0x0020;
    private const uint DtWordBreak = 0x0010;
    private const uint DtCalcRect = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ProcessMemoryCounters* counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr dc, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr dc, int color);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativeMethods.Point point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativeMethods.Point point);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, char* buffer, int capacity);
}
