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

    /// <summary>
    /// A deep copy, for an Options window that edits a draft and applies it
    /// only on OK or Apply.
    /// </summary>
    public static AppSettings Clone(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        return Normalize(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings());
    }

    /// <summary>Checks a settings object the way loading does, for values edited in the UI.</summary>
    public static AppSettings Validate(AppSettings settings) => Normalize(settings);

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

        settings.FileSearch ??= new FileSearchPriorities();
        settings.FileSearch.HighPriorityPaths ??= [];
        settings.FileSearch.NormalPriorityPaths ??=
        [
            @"%USERPROFILE%\AppData\Roaming\Microsoft\Internet Explorer",
            @"%APPDATA%\Microsoft\Windows\Start Menu",
            @"%ProgramData%\Microsoft\Windows\Start Menu"
        ];

        // Before v0.6.1 the default listed all of %APPDATA%\Microsoft\Windows,
        // which includes Recent: every recently opened file's shortcut was
        // boosted above the files themselves. Only the Start Menu was meant.
        settings.FileSearch.NormalPriorityPaths = settings.FileSearch.NormalPriorityPaths
            .Select(p => string.Equals(p, @"%USERPROFILE%\AppData\Roaming\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)
                ? @"%APPDATA%\Microsoft\Windows\Start Menu"
                : p)
            .ToArray();

        settings.FileSearch.RarelyUsedPaths ??=
        [
            @"%SystemDrive%\Windows.old",
            @"%SystemDrive%\$WINDOWS.~BT",
            @"%SystemDrive%\$Windows.~WS",
            @"%ProgramData%",
            @"%SystemRoot%",
            @"%USERPROFILE%\AppData",
            @"%ProgramW6432%",
            @"%ProgramFiles(x86)%"
        ];
        settings.FileSearch.RarelyUsedPatterns ??=
        [
            @"^\.",
            @"^~",
            @"^[\.]+$",
            @"^node_modules$",
            @"^\$"
        ];
        settings.FileSearch.DoNotIndexPaths ??=
        [
            @"\$Recycle.Bin",
            @"\System Volume Information"
        ];

        settings.Integration ??= new IntegrationSettings();
        settings.Integration.QuickSwitchHotkey ??= "Ctrl+G";

        // A file that predates a list gets the defaults; an explicit empty list
        // is the user turning the feature off and is kept.
        settings.SearchFilters = (settings.SearchFilters ?? SearchFilterSettings.Defaults())
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Keyword))
            .Select(f =>
            {
                f.Keyword = f.Keyword.Trim().TrimEnd(':');
                f.Name ??= f.Keyword;
                f.Extensions ??= [];
                return f;
            })
            .ToArray();

        settings.CustomCommands = (settings.CustomCommands ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Target))
            .Select(c =>
            {
                c.Arguments ??= string.Empty;
                return c;
            })
            .ToArray();

        settings.OpenFolderWith ??= new OpenFolderSettings();
        settings.OpenFolderWith.Path ??= string.Empty;
        settings.OpenFolderWith.Arguments = string.IsNullOrWhiteSpace(settings.OpenFolderWith.Arguments)
            ? "\"%1\""
            : settings.OpenFolderWith.Arguments;
        settings.MaxResults = Math.Clamp(settings.MaxResults, 1, 8);

        settings.WebSearches = (settings.WebSearches ?? WebSearchSettings.Defaults())
            .Where(w => w is not null &&
                        !string.IsNullOrWhiteSpace(w.Keyword) &&
                        !string.IsNullOrWhiteSpace(w.Url) &&
                        w.Url.Contains("{query}", StringComparison.Ordinal))
            .Select(w =>
            {
                w.Keyword = w.Keyword.Trim();
                w.Name = string.IsNullOrWhiteSpace(w.Name) ? w.Keyword : w.Name;
                return w;
            })
            .ToArray();

        return settings;
    }
}
