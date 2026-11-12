using System.Runtime.InteropServices;

namespace Glide.App.Integration;

/// <summary>
/// Reads and drives File Explorer windows from outside Explorer.
///
/// Listary does this from inside: its hook DLL (exports <c>InstallHook</c>,
/// <c>HookShellWindow</c>; strings <c>OpenFolderPidl</c>, <c>SHELLDLL_DefView</c>)
/// is loaded into explorer.exe by a global hook and navigates there. Glide
/// does not inject, so it uses the one route the shell supports across
/// processes: <c>IShellWindows</c> hands out each Explorer tab as an
/// <c>IWebBrowser2</c> automation object, and automation calls (<c>HWND</c>,
/// <c>LocationURL</c>, <c>Navigate2</c>) are marshaled into explorer.exe.
/// Members are bound by name through <c>IDispatch</c>, so no vtable layout is
/// assumed. Nothing is cached; calls must come from the window thread, which
/// initialized COM.
/// </summary>
internal static unsafe class ExplorerFolders
{
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IidShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");

    private const uint ClsctxAll = 0x17;
    private const ushort VtI4 = 3;
    private const ushort VtBstr = 8;
    private const ushort VtI8 = 20;
    private const ushort DispatchMethod = 1;
    private const ushort DispatchPropertyGet = 2;
    private const uint GwHwndNext = 2;

    private const int SlotRelease = 2;
    private const int SlotShellWindowsCount = 7;
    private const int SlotShellWindowsItem = 8;
    private const int SlotGetIdsOfNames = 5;
    private const int SlotInvoke = 6;

    /// <summary>One Explorer tab: its frame window, whether it is the tab on screen, and its folder.</summary>
    public readonly record struct ExplorerTab(IntPtr Frame, bool Active, string? Path);

    /// <summary>
    /// Distinct file system folders shown in Explorer, ordered by the z-order
    /// of their windows (topmost first) and, within a window, the visible tab
    /// first. Virtual folders such as This PC have no path and are skipped.
    /// </summary>
    public static List<string> GetOpenFolders()
    {
        List<ExplorerTab> tabs = GetTabs();
        var rank = ZOrder();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(tabs.Count);

        foreach (ExplorerTab tab in tabs
            .Select((t, i) => (Tab: t, Order: i))
            .OrderBy(t => rank.TryGetValue(t.Tab.Frame, out int r) ? r : int.MaxValue)
            .ThenByDescending(t => t.Tab.Active)
            .ThenBy(t => t.Order)
            .Select(t => t.Tab))
        {
            if (tab.Path is not null && seen.Add(tab.Path))
            {
                result.Add(tab.Path);
            }
        }

        return result;
    }

    /// <summary>Every Explorer tab with its frame, for diagnostics and navigation.</summary>
    public static List<ExplorerTab> GetTabs()
    {
        var raw = new List<(IntPtr Frame, string? Path, string? Name)>();

        EnumerateBrowsers(browser =>
        {
            IntPtr frame = GetHwnd(browser);
            string? path = UrlToPath(GetString(browser, "LocationURL"));
            string? name = GetString(browser, "LocationName");
            raw.Add((frame, path, name));
            return true;
        });

        // Windows 11 puts several tabs in one frame. The frame's title is the
        // visible tab's name (or its path, when Explorer shows full paths).
        var tabs = new List<ExplorerTab>(raw.Count);

        foreach (IGrouping<IntPtr, (IntPtr Frame, string? Path, string? Name)> frame in raw.GroupBy(t => t.Frame))
        {
            string title = WindowText(frame.Key);
            int active = -1;
            int index = 0;

            foreach (var tab in frame)
            {
                if (active < 0 && TitleMatches(title, tab.Name, tab.Path))
                {
                    active = index;
                }

                index++;
            }

            index = 0;
            foreach (var tab in frame)
            {
                tabs.Add(new ExplorerTab(tab.Frame, index == Math.Max(active, 0), tab.Path));
                index++;
            }
        }

        return tabs;
    }

    /// <summary>
    /// Navigates the visible tab of the Explorer window <paramref name="frame"/>
    /// to <paramref name="folder"/>, in place. Returns false when that window is
    /// gone, so the caller can open a new window instead.
    /// </summary>
    public static bool Navigate(IntPtr frame, string folder)
    {
        if (frame == IntPtr.Zero || !Directory.Exists(folder))
        {
            return false;
        }

        string title = WindowText(frame);
        IntPtr fallback = IntPtr.Zero;
        bool navigated = false;

        EnumerateBrowsers(browser =>
        {
            if (GetHwnd(browser) != frame)
            {
                return true;
            }

            if (TitleMatches(title, GetString(browser, "LocationName"), UrlToPath(GetString(browser, "LocationURL"))))
            {
                navigated = NavigateTo(browser, folder);
                return false;
            }

            if (fallback == IntPtr.Zero)
            {
                // Keep the first tab of the frame in case no title matches.
                AddRef(browser);
                fallback = browser;
            }

            return true;
        });

        if (!navigated && fallback != IntPtr.Zero)
        {
            navigated = NavigateTo(fallback, folder);
        }

        Release(fallback);
        return navigated;
    }

    private static bool TitleMatches(string title, string? name, string? path)
    {
        if (title.Length == 0)
        {
            return false;
        }

        return (!string.IsNullOrEmpty(name) && (title.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                                 title.StartsWith(name + " -", StringComparison.OrdinalIgnoreCase))) ||
               (!string.IsNullOrEmpty(path) && title.StartsWith(path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool NavigateTo(IntPtr browser, string folder)
    {
        IntPtr bstr = SysAllocString(folder);
        try
        {
            var argument = new Variant { Type = VtBstr, Pointer = bstr };
            return Invoke(browser, "Navigate2", DispatchMethod, &argument, 1, null);
        }
        finally
        {
            SysFreeString(bstr);
        }
    }

    private static void EnumerateBrowsers(Func<IntPtr, bool> visit)
    {
        IntPtr shellWindows = IntPtr.Zero;

        try
        {
            Guid clsid = ClsidShellWindows;
            Guid iid = IidShellWindows;

            if (CoCreateInstance(&clsid, IntPtr.Zero, ClsctxAll, &iid, &shellWindows) < 0 || shellWindows == IntPtr.Zero)
            {
                return;
            }

            int count = 0;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(shellWindows, SlotShellWindowsCount))(shellWindows, &count) < 0)
            {
                return;
            }

            for (int i = 0; i < Math.Min(count, 64); i++)
            {
                var index = new Variant { Type = VtI4, Pointer = i };
                IntPtr browser = IntPtr.Zero;

                if (((delegate* unmanaged[Stdcall]<IntPtr, Variant, IntPtr*, int>)Slot(shellWindows, SlotShellWindowsItem))(shellWindows, index, &browser) < 0 ||
                    browser == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (!visit(browser))
                    {
                        return;
                    }
                }
                finally
                {
                    Release(browser);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Glide: reading Explorer windows failed: {ex.Message}");
        }
        finally
        {
            Release(shellWindows);
        }
    }

    private static IntPtr GetHwnd(IntPtr browser)
    {
        Variant result = default;
        if (!Invoke(browser, "HWND", DispatchPropertyGet, null, 0, &result))
        {
            return IntPtr.Zero;
        }

        // HWND comes back as VT_I8 on 64-bit Windows, VT_I4 on older builds.
        ushort type = result.Type;
        IntPtr value = result.Pointer;
        VariantClear(&result);

        return type switch
        {
            VtI8 => value,
            VtI4 => (IntPtr)(int)(long)value,
            _ => IntPtr.Zero,
        };
    }

    private static string? GetString(IntPtr browser, string member)
    {
        Variant result = default;
        if (!Invoke(browser, member, DispatchPropertyGet, null, 0, &result))
        {
            return null;
        }

        try
        {
            return result.Type == VtBstr && result.Pointer != IntPtr.Zero ? Marshal.PtrToStringBSTR(result.Pointer) : null;
        }
        finally
        {
            VariantClear(&result);
        }
    }

    private static bool Invoke(IntPtr dispatch, string member, ushort flags, Variant* arguments, int count, Variant* result)
    {
        Guid none = Guid.Empty;
        int id = 0;

        fixed (char* name = member)
        {
            char* names = name;
            var getIds = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, char**, uint, uint, int*, int>)Slot(dispatch, SlotGetIdsOfNames);
            if (getIds(dispatch, &none, &names, 1, 0, &id) < 0)
            {
                return false;
            }
        }

        var parameters = new DispParams { Arguments = arguments, Count = (uint)count };
        var invoke = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, DispParams*, Variant*, IntPtr, uint*, int>)Slot(dispatch, SlotInvoke);
        return invoke(dispatch, id, &none, 0, flags, &parameters, result, IntPtr.Zero, null) >= 0;
    }

    /// <summary><c>file:///D:/Macro%20Game</c> to <c>D:\Macro Game</c>; null for virtual folders.</summary>
    private static string? UrlToPath(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        char* buffer = stackalloc char[2048];
        uint length = 2048;
        return PathCreateFromUrlW(url, buffer, &length, 0) >= 0 ? new string(buffer, 0, (int)length) : null;
    }

    private static Dictionary<IntPtr, int> ZOrder()
    {
        var rank = new Dictionary<IntPtr, int>();
        int position = 0;

        for (IntPtr hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero && position < 4096; hwnd = GetWindow(hwnd, GwHwndNext))
        {
            rank.TryAdd(hwnd, position++);
        }

        return rank;
    }

    private static string WindowText(IntPtr hwnd)
    {
        char* buffer = stackalloc char[512];
        int length = hwnd == IntPtr.Zero ? 0 : GetWindowTextW(hwnd, buffer, 512);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static IntPtr Slot(IntPtr instance, int slot)
    {
        IntPtr* table = *(IntPtr**)instance;
        return table[slot];
    }

    private static void AddRef(IntPtr instance)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 1))(instance);
    }

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, SlotRelease))(instance);
        }
    }

    /// <summary>VARIANT, the 24-byte x64 layout, with the union read as one pointer-sized value.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public Variant* Arguments;
        public int* NamedArguments;
        public uint Count;
        public uint NamedCount;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* instance);

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SysAllocString(string text);

    [DllImport("oleaut32.dll")]
    private static extern void SysFreeString(IntPtr text);

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(Variant* variant);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int PathCreateFromUrlW(string url, char* path, uint* length, uint reserved);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, char* buffer, int capacity);
}
