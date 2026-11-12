using System.Runtime.InteropServices;
using Glide.App.Win32;

namespace Glide.App.Integration;

/// <summary>
/// Two system hooks that cost no injection, because both run in this process:
///
/// <list type="bullet">
/// <item><b>Type to search.</b> A low-level keyboard hook notices a letter
/// typed into the file list of File Explorer or an Open/Save dialog. It runs
/// on its own small thread with its own message loop, so a slow search on the
/// window thread can never delay keyboard input for the whole session
/// (Windows drops a low-level hook that answers late).</item>
/// <item><b>Foreground watch.</b> An out-of-context WinEvent hook reports which
/// window became active, which lets the Quick Switch shortcut exist only while
/// a file dialog is in front.</item>
/// </list>
/// </summary>
internal static unsafe class ActivationHooks
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint VkControl = 0x11;
    private const uint WmQuit = 0x0012;

    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private static IntPtr s_target;
    private static uint s_hookThreadId;
    private static Thread? s_hookThread;

    private static IntPtr s_winEventHook;
    private static Action<IntPtr>? s_onForeground;

    private static uint s_typeMessage;
    private static volatile bool s_explorerEnabled;
    private static volatile bool s_dialogEnabled;

    /// <summary>
    /// Turns type-to-search on or off for Explorer and for file dialogs. The
    /// hook thread starts the first time either is on and then only reads
    /// these flags, so the Options window can change them at any time.
    /// A typed letter is swallowed and posted as <paramref name="typeMessage"/>
    /// with the character in wParam and the window in lParam.
    /// </summary>
    public static void ConfigureTypeToSearch(IntPtr target, uint typeMessage, bool explorer, bool dialogs)
    {
        s_target = target;
        s_typeMessage = typeMessage;
        s_explorerEnabled = explorer;
        s_dialogEnabled = dialogs;

        if (s_hookThread is not null || (!explorer && !dialogs))
        {
            return;
        }

        s_hookThread = new Thread(HookThread, maxStackSize: 128 * 1024)
        {
            IsBackground = true,
            Name = "Glide keyboard hook",
        };
        s_hookThread.Start();
    }

    public static void StopKeyboardHook()
    {
        if (s_hookThreadId != 0)
        {
            PostThreadMessageW(s_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        s_hookThread = null;
        s_hookThreadId = 0;
    }

    /// <summary>
    /// Calls <paramref name="onForeground"/> on the calling thread whenever a
    /// window of another process becomes the foreground window. The calling
    /// thread must pump messages.
    /// </summary>
    public static void WatchForeground(Action<IntPtr> onForeground)
    {
        s_onForeground = onForeground;

        if (s_winEventHook == IntPtr.Zero)
        {
            s_winEventHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                &OnWinEvent,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);

            AppLog.Write(s_winEventHook == IntPtr.Zero ? "foreground watch could not be installed" : "foreground watch installed");
        }
    }

    private static IntPtr s_trackedWindow;
    private static IntPtr s_moveHook;
    private static IntPtr s_destroyHook;
    private static Action? s_onMoved;
    private static Action? s_onClosed;

    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventSystemMinimizeStart = 0x0016;

    /// <summary>
    /// Follows one window of another process: <paramref name="onMoved"/> when
    /// it moves or resizes, <paramref name="onClosed"/> when it is destroyed or
    /// minimized. The hooks are limited to that window's thread, so the many
    /// events of other programs never reach Glide. Replaces any earlier window.
    /// </summary>
    public static void TrackWindow(IntPtr window, Action onMoved, Action onClosed)
    {
        StopTrackingWindow();

        uint thread = NativeMethods.GetWindowThreadProcessId(window, out uint process);
        if (thread == 0)
        {
            return;
        }

        s_trackedWindow = window;
        s_onMoved = onMoved;
        s_onClosed = onClosed;

        s_moveHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, &OnTrackedEvent, process, thread, WinEventOutOfContext);
        s_destroyHook = SetWinEventHook(EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, &OnTrackedEvent, process, thread, WinEventOutOfContext);
        s_minimizeHook = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeStart, IntPtr.Zero, &OnTrackedEvent, process, thread, WinEventOutOfContext);
    }

    private static IntPtr s_minimizeHook;

    public static void StopTrackingWindow()
    {
        foreach (IntPtr hook in new[] { s_moveHook, s_destroyHook, s_minimizeHook })
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }

        s_moveHook = IntPtr.Zero;
        s_destroyHook = IntPtr.Zero;
        s_minimizeHook = IntPtr.Zero;
        s_trackedWindow = IntPtr.Zero;
        s_onMoved = null;
        s_onClosed = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnTrackedEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        // Only the window itself (OBJID_WINDOW), not its controls.
        if (hwnd != s_trackedWindow || objectId != 0 || childId != 0)
        {
            return;
        }

        try
        {
            if (eventType == EventObjectLocationChange)
            {
                s_onMoved?.Invoke();
            }
            else
            {
                s_onClosed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"window tracking failed: {ex}");
        }
    }

    public static void StopWatchingForeground()
    {
        if (s_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(s_winEventHook);
            s_winEventHook = IntPtr.Zero;
        }

        s_onForeground = null;
    }

    private static void HookThread()
    {
        s_hookThreadId = NativeMethods.GetCurrentThreadId();
        IntPtr module = NativeMethods.GetModuleHandleW(null);

        IntPtr keyboard = SetWindowsHookExW(WhKeyboardLl, &OnKeyboard, module, 0);
        if (keyboard == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Glide: keyboard hook unavailable (error {Marshal.GetLastWin32Error()})");
            return;
        }

        try
        {
            while (NativeMethods.GetMessageW(out NativeMethods.Msg message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(keyboard);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (uint)wParam == WmKeyDown && (s_explorerEnabled || s_dialogEnabled))
        {
            var data = (KbdLlHookStruct*)lParam;

            if (TypedCharacter(data->VkCode) is char typed && IsFileListFocused(out IntPtr window))
            {
                // Swallow the key: Glide opens with it instead of the file
                // list's own select-by-name.
                NativeMethods.PostMessageW(s_target, s_typeMessage, typed, window);
                return 1;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// A plain letter or digit with no Ctrl, Alt or Win held, as the lower-case
    /// character a search wants. Shift is allowed for letters only, because
    /// Shift+digit is a symbol on most layouts. Everything else, including
    /// F2, Delete and arrows, stays Explorer's.
    /// </summary>
    private static char? TypedCharacter(uint vk)
    {
        if (IsDown((int)VkControl) || IsDown(VkMenu) || IsDown(VkLWin) || IsDown(VkRWin))
        {
            return null;
        }

        if (vk is >= 'A' and <= 'Z')
        {
            return (char)(vk + 32);
        }

        if (IsDown(VkShift))
        {
            return null;
        }

        if (vk is >= '0' and <= '9')
        {
            return (char)vk;
        }

        if (vk is >= VkNumpad0 and <= VkNumpad9)
        {
            return (char)('0' + (vk - VkNumpad0));
        }

        return null;
    }

    /// <summary>
    /// True when the foreground window is File Explorer and keyboard focus is
    /// in its file list: not the address bar, the search box, a rename box or
    /// an open menu. Runs inside the hook, so it uses only cheap window calls.
    /// </summary>
    private static bool IsFileListFocused(out IntPtr explorer)
    {
        // File Explorer, or an Open/Save dialog: both host the same shell file
        // list, and Listary's hook treats them alike (WND_EXPLORER, WND_FD_*).
        explorer = NativeMethods.GetForegroundWindow();
        if (explorer == IntPtr.Zero)
        {
            return false;
        }

        bool isExplorer = HasClass(explorer, "CabinetWClass");
        bool isDialog = !isExplorer && HasClass(explorer, "#32770");

        if (!(isExplorer && s_explorerEnabled) && !(isDialog && s_dialogEnabled))
        {
            return false;
        }

        uint thread = NativeMethods.GetWindowThreadProcessId(explorer, out _);
        var info = new GuiThreadInfo { Size = sizeof(GuiThreadInfo) };

        if (!GetGUIThreadInfo(thread, &info) || (info.Flags & (GuiInMenuMode | GuiPopupMenuMode)) != 0)
        {
            return false;
        }

        return info.Focus != IntPtr.Zero &&
            HasClass(info.Focus, "DirectUIHWND") &&
            HasClass(GetParent(info.Focus), "SHELLDLL_DefView");
    }

    private static bool HasClass(IntPtr hwnd, string expected)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        char* buffer = stackalloc char[32];
        int length = GetClassNameW(hwnd, buffer, 32);
        return length == expected.Length && new ReadOnlySpan<char>(buffer, length).SequenceEqual(expected);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const uint VkNumpad0 = 0x60;
    private const uint VkNumpad9 = 0x69;
    private const uint GuiInMenuMode = 0x4;
    private const uint GuiPopupMenuMode = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeMethods.Rect CaretRect;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, GuiThreadInfo* info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, char* buffer, int capacity);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (eventType == EventSystemForeground && objectId == 0)
        {
            try
            {
                s_onForeground?.Invoke(hwnd);
            }
            catch (Exception ex)
            {
                // An exception must not unwind into user32.
                AppLog.Write($"foreground handler failed: {ex}");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int hookId, delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr> callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
