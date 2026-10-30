using System.Runtime.InteropServices;

namespace Glide.App.Win32;

public static class NativeMethods
{
    public const int WsPopup = unchecked((int)0x80000000);
    public const int WsExTopmost = 0x00000008;
    public const int CsDropShadow = 0x00020000;

    public const int SwHide = 0;
    public const int SwShow = 5;

    public const uint WmDestroy = 0x0002;
    public const uint WmSize = 0x0005;
    public const uint WmActivate = 0x0006;
    public const uint WmPaint = 0x000F;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmKeyDown = 0x0100;
    public const uint WmChar = 0x0102;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonDblClk = 0x0203;
    public const uint WmHotkey = 0x0312;
    public const uint WmTimer = 0x0113;
    public const uint WmApp = 0x8000;

    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkPrior = 0x21; // Page Up
    public const int VkNext = 0x22;  // Page Down
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkInsert = 0x2D;
    public const int VkDelete = 0x2E;
    public const int VkControl = 0x11;
    public const int VkShift = 0x10;
    public const int VkMenu = 0x12; // Alt
    public const int VkV = 0x56;
    public const int VkA = 0x41;
    public const int VkC = 0x43;
    public const int VkF1 = 0x70;
    public const int VkF2 = 0x71;
    public const int VkF3 = 0x72;
    public const int VkF4 = 0x73;
    public const int VkF5 = 0x74;
    public const int VkF6 = 0x75;
    public const int VkF7 = 0x76;
    public const int VkF8 = 0x77;
    public const int VkF9 = 0x78;
    public const int VkF10 = 0x79;
    public const int VkF11 = 0x7A;
    public const int VkF12 = 0x7B;
    public const int VkApps = 0x5D; // Context Menu key

    public const int VkNumpad0 = 0x60;
    public const int VkNumpad1 = 0x61;
    public const int VkNumpad2 = 0x62;
    public const int VkNumpad3 = 0x63;
    public const int VkNumpad4 = 0x64;
    public const int VkNumpad5 = 0x65;
    public const int VkNumpad6 = 0x66;
    public const int VkNumpad7 = 0x67;
    public const int VkNumpad8 = 0x68;
    public const int VkNumpad9 = 0x69;
    public const int VkMultiply = 0x6A;
    public const int VkAdd = 0x6B;
    public const int VkSeparator = 0x6C;
    public const int VkSubtract = 0x6D;
    public const int VkDecimal = 0x6E;
    public const int VkDivide = 0x6F;

    public const int VkOem1 = 0xBA;     // ;:
    public const int VkOemPlus = 0xBB;  // =+
    public const int VkOemComma = 0xBC; // ,<
    public const int VkOemMinus = 0xBD; // -_
    public const int VkOemPeriod = 0xBE;// .>
    public const int VkOem2 = 0xBF;     // /?
    public const int VkOem3 = 0xC0;     // `~
    public const int VkOem4 = 0xDB;     // [{
    public const int VkOem5 = 0xDC;     // \|
    public const int VkOem6 = 0xDD;     // ]}
    public const int VkOem7 = 0xDE;     // '"

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public const int Transparent = 1;
    public const uint DtLeft = 0x0000;
    public const uint DtVCenter = 0x0004;
    public const uint DtSingleLine = 0x0020;
    public const uint DtNoPrefix = 0x0800;
    public const uint DtEndEllipsis = 0x8000;

    public const int FwNormal = 400;
    public const int FwSemiBold = 600;

    public const int SwpNoZOrder = 0x0004;
    public const int SwpShowWindow = 0x0040;

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public const int IdcArrow = 32512;
    public const uint CfUnicodeText = 13;
    public const uint GmemMoveable = 0x0002;
    public const uint Srccopy = 0x00CC0020;
    public const uint MonitorDefaultToNearest = 2;

    public const uint WmRButtonUp = 0x0205;
    public const uint WmLButtonUp = 0x0202;

    // Menu constants
    public const uint MfString = 0x00000000;
    public const uint MfSeparator = 0x00000800;
    public const uint MfChecked = 0x00000008;
    public const uint MfUnchecked = 0x00000000;
    public const uint MfPopup = 0x00000010;
    public const uint MfRightJustify = 0x00004000;

    public const uint TpmLeftButton = 0x0000;
    public const uint TpmRightButton = 0x0002;
    public const uint TpmLeftAlign = 0x0000;
    public const uint TpmCenterAlign = 0x0004;
    public const uint TpmRightAlign = 0x0008;
    public const uint TpmTopAlign = 0x0000;
    public const uint TpmVcenterAlign = 0x0010;
    public const uint TpmBottomAlign = 0x0020;
    public const uint TpmHorizontal = 0x0000;
    public const uint TpmVertical = 0x0040;
    public const uint TpmNonotify = 0x0080;
    public const uint TpmRetcmd = 0x0100;

    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, uint dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
        public long Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessageW(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessageW(ref Msg message);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    public static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(
        IntPtr hWnd,
        string operation,
        string file,
        string? parameters,
        string? directory,
        int showCommand);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(
        IntPtr destination,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    public static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string text, int length, ref Rect rect, uint format);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out Size size);

    public static int Rgb(int red, int green, int blue)
    {
        return red | (green << 8) | (blue << 16);
    }
}
