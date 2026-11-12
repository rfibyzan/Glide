using System.Runtime.InteropServices;

namespace Glide.App.Ui;

/// <summary>GDI, DWM and shell calls used only for drawing.</summary>
internal static unsafe class UiNative
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const int NullPen = 8;

    /// <summary>
    /// Windows 11 rounds the window and draws a hairline border in the theme's
    /// color. Older Windows ignores both attributes and keeps square corners.
    /// </summary>
    public static void StyleWindow(IntPtr hwnd, Palette palette)
    {
        int corner = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, &corner, sizeof(int));

        int border = Palette.ToColorRef(palette.Border);
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, &border, sizeof(int));

        int dark = palette.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, &dark, sizeof(int));
    }

    /// <summary>A filled rounded rectangle with no outline.</summary>
    public static void FillRounded(IntPtr dc, int left, int top, int right, int bottom, int radius, IntPtr brush)
    {
        if (right <= left || bottom <= top)
        {
            return;
        }

        IntPtr oldPen = SelectObject(dc, GetStockObject(NullPen));
        IntPtr oldBrush = SelectObject(dc, brush);

        // RoundRect excludes the right and bottom edge when no pen is drawn.
        RoundRect(dc, left, top, right + 1, bottom + 1, radius * 2, radius * 2);

        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
    }

    /// <summary>
    /// The icon font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on
    /// Windows 10. Both carry the glyphs Glide uses at the same code points.
    /// </summary>
    public static string IconFontFace =>
        Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>Segoe UI Variable on Windows 11, plain Segoe UI elsewhere.</summary>
    public static string TextFontFace(bool display) =>
        Environment.OSVersion.Version.Build >= 22000
            ? (display ? "Segoe UI Variable Display" : "Segoe UI Variable Text")
            : "Segoe UI";

    public const string GlyphSearch = "";
    public const string GlyphSettings = "";
    public const string GlyphGlobe = "";
    public const string GlyphCommand = "";
    public const string GlyphCalculator = "";

    /// <summary>
    /// The logo embedded as the exe's icon (<c>ApplicationIcon</c>; the C#
    /// compiler stores it as resource 32512), at <paramref name="size"/> pixels.
    /// Returns zero when the binary has no icon. The caller destroys it.
    /// </summary>
    public static IntPtr LoadAppIcon(int size)
    {
        const int IconResourceId = 32512;
        const uint ImageIcon = 1;
        return LoadImageW(Glide.App.Win32.NativeMethods.GetModuleHandleW(null), IconResourceId, ImageIcon, size, size, 0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImageW(IntPtr instance, nint name, uint type, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, void* value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, Margins* margins);

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("user32.dll")]
    public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr flickerBrush, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Glide.App.Win32.NativeMethods.Rect rect);
}
