using System.Text.Json;

namespace Glide.Core.Settings;

public static class SettingsManager
{
    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glide");

    public static string SettingsFilePath { get; } =
        Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// Loads the settings and writes the default file when there is none, so the
    /// shortcut and theme are editable without reading the source.
    /// </summary>
    public static AppSettings LoadOrCreate(string? path = null)
    {
        path ??= SettingsFilePath;
        AppSettings settings = Load(path);

        if (!File.Exists(path))
        {
            Save(settings, path);
        }

        return settings;
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsFilePath;

        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            AppSettings? settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            return settings is null ? new AppSettings() : Normalize(settings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: error loading settings, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= SettingsFilePath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: error saving settings: {ex.Message}");
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Theme = settings.Theme switch
        {
            "dark" => "dark",
            "light" => "light",
            _ => "system"
        };

        settings.Hotkey ??= new HotkeySettings();
        settings.Hotkey.Modifiers ??= ["Ctrl", "Shift", "Alt"];
        settings.Hotkey.Key ??= "/";

        if (settings.MaxResults <= 0)
        {
            settings.MaxResults = 8;
        }

        return settings;
    }
}
