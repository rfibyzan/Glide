using Glide.Core.Search;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

public sealed class CommandCatalogTests
{
    [Theory]
    [InlineData("set", "Glide Settings")]
    [InlineData("glide set", "Glide Settings")]
    [InlineData("options", "Glide Settings")]
    [InlineData("quit", "Exit Glide")]
    [InlineData("recycle", "Recycle Bin")]
    [InlineData("trash", "Recycle Bin")]
    public void PrefixesFindCommands(string query, string title)
    {
        Assert.Contains(CommandCatalog.Match(query), c => c.Title == title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("report")]
    [InlineData("ettings")]
    public void OrdinaryQueriesFindNoCommand(string query)
    {
        Assert.Empty(CommandCatalog.Match(query));
    }
}

public sealed class OptionsSettingsTests
{
    [Fact]
    public void CloneIsDeepSoADraftDoesNotTouchTheLiveSettings()
    {
        var live = new AppSettings();
        AppSettings draft = SettingsManager.Clone(live);

        draft.Hotkey.Key = "K";
        draft.WebSearches[0].Keyword = "changed";
        draft.OpenFolderWith.Path = "totalcmd.exe";

        Assert.Equal("/", live.Hotkey.Key);
        Assert.Equal("gg", live.WebSearches[0].Keyword);
        Assert.Equal(string.Empty, live.OpenFolderWith.Path);
    }

    [Fact]
    public void ValidateDropsIncompleteCommandsAndClampsResults()
    {
        var settings = new AppSettings
        {
            MaxResults = 40,
            CustomCommands =
            [
                new CustomCommandSettings { Title = "Notepad", Target = "notepad.exe" },
                new CustomCommandSettings { Title = "", Target = "x.exe" },
                new CustomCommandSettings { Title = "No target", Target = " " },
            ],
        };

        AppSettings valid = SettingsManager.Validate(settings);

        Assert.Equal(8, valid.MaxResults);
        Assert.Equal("Notepad", Assert.Single(valid.CustomCommands).Title);
    }

    [Fact]
    public void CustomCommandsMatchLikeBuiltInOnes()
    {
        var catalog = new List<LauncherCommand>(CommandCatalog.BuiltIn)
        {
            new("Visual Studio Code", "code.exe", [], "--new-window"),
        };

        LauncherCommand match = Assert.Single(CommandCatalog.Match("vis code", catalog));
        Assert.Equal("--new-window", match.Arguments);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Alt+/")]
    [InlineData("Ctrl+Space")]
    [InlineData("Alt+F9")]
    [InlineData("Ctrl+G")]
    [InlineData("Shift+Win+K")]
    public void ShortcutsSurviveASaveAndReload(string spec)
    {
        Assert.True(HotkeyParser.TryParseSpec(spec, out HotkeySettings parsed));
        Assert.True(HotkeyParser.TryParse(parsed, out uint mods, out uint vk));
        Assert.Equal(spec, HotkeyParser.ToSpec(mods, vk));
    }
}
