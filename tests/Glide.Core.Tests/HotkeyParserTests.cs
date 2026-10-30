using Glide.Core.Settings;
using Xunit;

namespace Glide.Core.Tests;

public class HotkeyParserTests
{
    [Fact]
    public void TryParse_DefaultShortcut_IsCtrlShiftAltSlash()
    {
        var settings = new HotkeySettings();

        Assert.True(HotkeyParser.TryParse(settings, out uint modifiers, out uint virtualKey));
        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt,
            modifiers);
        Assert.Equal(0xBFu, virtualKey);
        Assert.Equal("Ctrl+Shift+Alt+/", HotkeyParser.ToSpec(modifiers, virtualKey));
    }

    [Fact]
    public void TryParse_SpaceShortcut_KeepsTheOldDefaultWorking()
    {
        var settings = new HotkeySettings { Modifiers = ["Ctrl", "Alt"], Key = "Space" };

        Assert.True(HotkeyParser.TryParse(settings, out uint modifiers, out uint virtualKey));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, modifiers);
        Assert.Equal(0x20u, virtualKey);
    }

    [Fact]
    public void TryParse_RejectsUnknownModifierAndKey()
    {
        Assert.False(HotkeyParser.TryParse(new HotkeySettings { Modifiers = ["Hyper"], Key = "/" }, out _, out _));
        Assert.False(HotkeyParser.TryParse(new HotkeySettings { Modifiers = ["Ctrl"], Key = "Blah" }, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsUnmodifiedLetter()
    {
        // A bare "g" would swallow typing across the whole desktop.
        Assert.False(HotkeyParser.TryParse(new HotkeySettings { Modifiers = [], Key = "g" }, out _, out _));
        Assert.True(HotkeyParser.TryParse(new HotkeySettings { Modifiers = [], Key = "F9" }, out uint modifiers, out uint virtualKey));
        Assert.Equal(0u, modifiers);
        Assert.Equal(0x78u, virtualKey);
    }

    [Fact]
    public void TryParseSpec_ReadsCommandLineForm()
    {
        Assert.True(HotkeyParser.TryParseSpec("Ctrl+Shift+Alt+/", out HotkeySettings settings));
        Assert.Equal(["Ctrl", "Shift", "Alt"], settings.Modifiers);
        Assert.Equal("/", settings.Key);

        Assert.False(HotkeyParser.TryParseSpec("Space", out _));
    }
}
