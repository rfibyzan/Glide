using System.Runtime.InteropServices;
using Glide.App.Win32;

namespace Glide.App.Ui;

/// <summary>
/// Glide's notification-area icon. The icon is drawn once at startup (an
/// accent square with the search glyph), so the binary ships no image files.
/// Clicks arrive at the owner window as <see cref="CallbackMessage"/>, with
/// the mouse message in lParam.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifShowTip = 0x80;
    private const uint IconId = 1;

    private readonly IntPtr _owner;
    private readonly uint _callbackMessage;
    private readonly string _tip;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(IntPtr owner, uint callbackMessage, string tip, int accentRgb)
    {
        _owner = owner;
        _callbackMessage = callbackMessage;
        _tip = tip;
        IntPtr logo = UiNative.LoadAppIcon(Math.Max(16, GetSystemMetrics(SmCxSmIcon)));
        _icon = logo != IntPtr.Zero ? logo : CreateGlyphIcon(accentRgb);
    }

    public uint CallbackMessage => _callbackMessage;

    /// <summary>The message Explorer broadcasts when the taskbar is recreated.</summary>
    public static uint TaskbarCreatedMessage { get; } = RegisterWindowMessageW("TaskbarCreated");

    public IntPtr Icon => _icon;

    /// <summary>Adds the icon; call again after <see cref="TaskbarCreatedMessage"/>.</summary>
    public void Show()
    {
        NotifyIconData data = Data();
        _added = Shell_NotifyIconW(NimAdd, &data) || Shell_NotifyIconW(NimModify, &data);
    }

    public void Dispose()
    {
        if (_added)
        {
            NotifyIconData data = Data();
            Shell_NotifyIconW(NimDelete, &data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            UiNative.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private NotifyIconData Data()
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _owner,
            Id = IconId,
            Flags = NifMessage | NifIcon | NifTip | NifShowTip,
            CallbackMessage = _callbackMessage,
            Icon = _icon,
        };

        int length = Math.Min(_tip.Length, 127);
        for (int i = 0; i < length; i++)
        {
            data.Tip[i] = _tip[i];
        }

        return data;
    }

    /// <summary>
    /// Draws a rounded accent square with the search glyph into a 32-bit
    /// bitmap and turns it into an icon. GDI text leaves alpha at zero, so
    /// alpha is written afterwards from the rounded shape.
    /// </summary>
    private static IntPtr CreateGlyphIcon(int accentRgb)
    {
        int size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        int radius = Math.Max(3, size / 5);

        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);

        IntPtr bits;
        IntPtr color = CreateDIBSection(dc, &header, 0, &bits, IntPtr.Zero, 0);
        IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        IntPtr oldBitmap = SelectObject(dc, color);

        IntPtr accent = NativeMethods.CreateSolidBrush(Palette.ToColorRef(accentRgb));
        var full = new NativeMethods.Rect { Left = 0, Top = 0, Right = size, Bottom = size };
        NativeMethods.FillRect(dc, ref full, accent);
        NativeMethods.DeleteObject(accent);

        IntPtr font = NativeMethods.CreateFontW(-(size * 5 / 8), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, UiNative.IconFontFace);
        IntPtr oldFont = SelectObject(dc, font);
        NativeMethods.SetBkMode(dc, NativeMethods.Transparent);
        NativeMethods.SetTextColor(dc, Palette.ToColorRef(Palette.Dark.Background));
        string glyph = UiNative.GlyphSearch;
        NativeMethods.DrawTextW(dc, glyph, glyph.Length, ref full, NativeMethods.DtCenter | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix);
        SelectObject(dc, oldFont);
        NativeMethods.DeleteObject(font);

        GdiFlush();

        // Opaque inside the rounded square, transparent in the corners.
        uint* pixels = (uint*)bits;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = InsideRounded(x, y, size, radius);
                uint* pixel = pixels + (y * size) + x;
                *pixel = inside ? (*pixel & 0x00FFFFFF) | 0xFF000000 : 0;
            }
        }

        SelectObject(dc, oldBitmap);
        NativeMethods.DeleteDC(dc);

        var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
        IntPtr icon = CreateIconIndirect(&info);

        NativeMethods.DeleteObject(color);
        NativeMethods.DeleteObject(mask);
        return icon;
    }

    private static bool InsideRounded(int x, int y, int size, int radius)
    {
        float cx = x + 0.5f;
        float cy = y + 0.5f;
        float nearestX = Math.Clamp(cx, radius, size - radius);
        float nearestY = Math.Clamp(cy, radius, size - radius);
        float dx = cx - nearestX;
        float dy = cy - nearestY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    private const int SmCxSmIcon = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("shell32.dll")]
    private static extern bool Shell_NotifyIconW(uint message, NotifyIconData* data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(IconInfo* info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, BitmapInfoHeader* info, uint usage, IntPtr* bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
