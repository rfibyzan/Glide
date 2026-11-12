namespace Glide.App.Ui;

/// <summary>
/// The two color sets every Glide window paints with, as 0xRRGGBB. Dark is
/// near-black rather than grey, the way Windows 11's own dark surfaces are;
/// elevation is shown by small steps in lightness, not by borders.
/// </summary>
internal readonly record struct Palette(
    bool IsDark,
    int Background,
    int Surface,
    int Raised,
    int Selection,
    int Border,
    int Ink,
    int Dim,
    int Placeholder,
    int Accent,
    int AccentSoft)
{
    public static Palette Dark { get; } = new(
        IsDark: true,
        Background: 0x0C0C0E,
        Surface: 0x111114,
        Raised: 0x19191D,
        Selection: 0x1E1E23,
        Border: 0x26262C,
        Ink: 0xF2F2F4,
        Dim: 0x8C8C95,
        Placeholder: 0x6B6B73,
        Accent: 0x2DD4BF,
        AccentSoft: 0x123A36);

    public static Palette Light { get; } = new(
        IsDark: false,
        Background: 0xFFFFFF,
        Surface: 0xF6F6F8,
        Raised: 0xEFEFF2,
        Selection: 0xEDF1F5,
        Border: 0xE2E2E6,
        Ink: 0x141416,
        Dim: 0x6B6B73,
        Placeholder: 0x8A8A92,
        Accent: 0x0D9488,
        AccentSoft: 0xD5F2EE);

    public static Palette For(bool dark) => dark ? Dark : Light;

    /// <summary>0xRRGGBB to a GDI COLORREF (0x00BBGGRR).</summary>
    public static int ToColorRef(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
}
