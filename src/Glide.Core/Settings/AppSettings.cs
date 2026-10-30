using System.Text.Json.Serialization;

namespace Glide.Core.Settings;

public sealed class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public string Theme { get; set; } = "system";
    public int MaxResults { get; set; } = 8;
}

public sealed class HotkeySettings
{
    public string[] Modifiers { get; set; } = ["Ctrl", "Shift", "Alt"];
    public string Key { get; set; } = "/";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
public partial class SettingsJsonContext : JsonSerializerContext;
