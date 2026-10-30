using Glide.Core.Settings;
using Xunit;

namespace Glide.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GlideSettings_" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public SettingsTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "settings.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Defaults_MatchTheDocumentedShape()
    {
        var settings = new AppSettings();

        Assert.Equal("system", settings.Theme);
        Assert.Equal(8, settings.MaxResults);
        Assert.Equal(["Ctrl", "Shift", "Alt"], settings.Hotkey.Modifiers);
        Assert.Equal("/", settings.Hotkey.Key);
    }

    [Fact]
    public void LoadOrCreate_WritesTheDefaultFileOnce()
    {
        AppSettings created = SettingsManager.LoadOrCreate(_file);

        Assert.True(File.Exists(_file));
        Assert.Equal("system", created.Theme);

        string json = File.ReadAllText(_file);
        Assert.Contains("\"key\": \"/\"", json);
        Assert.Contains("\"theme\": \"system\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var original = new AppSettings
        {
            Theme = "dark",
            MaxResults = 6,
            Hotkey = new HotkeySettings
            {
                Modifiers = ["Ctrl", "Alt"],
                Key = "Space"
            }
        };

        SettingsManager.Save(original, _file);
        AppSettings loaded = SettingsManager.Load(_file);

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(6, loaded.MaxResults);
        Assert.Equal(["Ctrl", "Alt"], loaded.Hotkey.Modifiers);
        Assert.Equal("Space", loaded.Hotkey.Key);
    }

    [Fact]
    public void UnknownValues_FallBackInsteadOfFailing()
    {
        File.WriteAllText(
            _file,
            """{ "theme": "neon", "maxResults": -4, "hotkey": { "modifiers": [], "key": "g" } }""");

        AppSettings loaded = SettingsManager.Load(_file);

        Assert.Equal("system", loaded.Theme);
        Assert.Equal(8, loaded.MaxResults);
    }
}
