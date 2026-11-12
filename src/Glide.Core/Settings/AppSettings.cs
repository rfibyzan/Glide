using System.Text.Json.Serialization;

namespace Glide.Core.Settings;

public sealed class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public string Theme { get; set; } = "system";

    /// <summary>Show Glide in the notification area: click to search, right-click for a menu.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Hide the launcher when another window is activated (Listary: HideWhenLosingFocus).</summary>
    public bool HideWhenLosingFocus { get; set; } = true;

    public int MaxResults { get; set; } = 8;
    public FileSearchPriorities FileSearch { get; set; } = new();
    public IntegrationSettings Integration { get; set; } = new();
    public SearchFilterSettings[] SearchFilters { get; set; } = SearchFilterSettings.Defaults();
    public WebSearchSettings[] WebSearches { get; set; } = WebSearchSettings.Defaults();

    /// <summary>User commands offered among results (Listary: CustomCommand).</summary>
    public CustomCommandSettings[] CustomCommands { get; set; } = [];

    /// <summary>
    /// A file manager to open folders with instead of Explorer (Listary:
    /// OpenFolderPath / OpenFolderParam). Empty means Explorer.
    /// </summary>
    public OpenFolderSettings OpenFolderWith { get; set; } = new();
}

public sealed class HotkeySettings
{
    public string[] Modifiers { get; set; } = ["Ctrl", "Shift", "Alt"];
    public string Key { get; set; } = "/";

    /// <summary>Pressing the shortcut while Glide is open hides it; otherwise it stays open.</summary>
    public bool PressAgainHides { get; set; } = true;

    /// <summary>Ignore the shortcut while a full-screen game or video is in front.</summary>
    public bool DisableInFullScreen { get; set; } = true;
}

public sealed class CustomCommandSettings
{
    public string Title { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

public sealed class OpenFolderSettings
{
    /// <summary>Executable of the file manager; empty for Explorer.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Arguments; <c>%1</c> is replaced by the folder.</summary>
    public string Arguments { get; set; } = "\"%1\"";
}

/// <summary>
/// A named filter typed as <c>keyword:</c> in the query, for example
/// <c>doc: budget</c>. Mirrors the filters Listary offers.
/// </summary>
public sealed class SearchFilterSettings
{
    public string Keyword { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string[] Extensions { get; set; } = [];
    public bool FoldersOnly { get; set; }
    public bool FilesOnly { get; set; }

    public static SearchFilterSettings[] Defaults() =>
    [
        new() { Keyword = "folder", Name = "Folders", FoldersOnly = true },
        new() { Keyword = "file", Name = "Files", FilesOnly = true },
        new() { Keyword = "doc", Name = "Documents", Extensions = ["doc", "docx", "pdf", "txt", "rtf", "odt", "md", "xls", "xlsx", "csv", "ods", "ppt", "pptx", "odp", "epub"] },
        new() { Keyword = "pic", Name = "Pictures", Extensions = ["jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff", "heic", "psd", "raw"] },
        new() { Keyword = "audio", Name = "Audio", Extensions = ["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus"] },
        new() { Keyword = "video", Name = "Video", Extensions = ["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg"] },
        new() { Keyword = "zip", Name = "Archives", Extensions = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab"] },
        new() { Keyword = "app", Name = "Programs", Extensions = ["exe", "lnk", "msi", "bat", "cmd", "ps1", "appref-ms", "url"] },
        new() { Keyword = "code", Name = "Source code", Extensions = ["cs", "js", "ts", "tsx", "jsx", "py", "java", "kt", "c", "cpp", "h", "hpp", "go", "rs", "rb", "php", "json", "xml", "yml", "yaml", "html", "css", "sql", "sh", "ps1"] },
    ];
}

/// <summary>
/// A web search typed as <c>keyword terms</c>, for example <c>gg weather</c>.
/// <see cref="Url"/> holds <c>{query}</c> where the escaped terms go.
/// </summary>
public sealed class WebSearchSettings
{
    public string Keyword { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public static WebSearchSettings[] Defaults() =>
    [
        new() { Keyword = "gg", Name = "Google", Url = "https://www.google.com/search?q={query}" },
        new() { Keyword = "bing", Name = "Bing", Url = "https://www.bing.com/search?q={query}" },
        new() { Keyword = "ddg", Name = "DuckDuckGo", Url = "https://duckduckgo.com/?q={query}" },
        new() { Keyword = "yt", Name = "YouTube", Url = "https://www.youtube.com/results?search_query={query}" },
        new() { Keyword = "wiki", Name = "Wikipedia", Url = "https://en.wikipedia.org/w/index.php?search={query}" },
        new() { Keyword = "gh", Name = "GitHub", Url = "https://github.com/search?q={query}" },
        new() { Keyword = "maps", Name = "Google Maps", Url = "https://www.google.com/maps/search/{query}" },
    ];
}

public sealed class FileSearchPriorities
{
    public bool HideSystemAndHidden { get; set; } = true;

    public string[] HighPriorityPaths { get; set; } = [];

    public string[] NormalPriorityPaths { get; set; } =
    [
        @"%USERPROFILE%\AppData\Roaming\Microsoft\Internet Explorer",
        @"%APPDATA%\Microsoft\Windows\Start Menu",
        @"%ProgramData%\Microsoft\Windows\Start Menu"
    ];

    public string[] RarelyUsedPaths { get; set; } =
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

    public string[] RarelyUsedPatterns { get; set; } =
    [
        @"^\.",
        @"^~",
        @"^[\.]+$",
        @"^node_modules$",
        @"^\$"
    ];

    public string[] DoNotIndexPaths { get; set; } =
    [
        @"\$Recycle.Bin",
        @"\System Volume Information"
    ];
}

public sealed class IntegrationSettings
{
    public bool EnableFileDialogIntegration { get; set; } = true;
    public bool EnableQuickSwitch { get; set; } = true;

    /// <summary>
    /// Pressed inside an Open or Save dialog, jumps the dialog to the folder open
    /// in the most recent Explorer window. Registered only while a file dialog
    /// is in the foreground, so the key keeps its meaning everywhere else.
    /// </summary>
    public string QuickSwitchHotkey { get; set; } = "Ctrl+G";

    /// <summary>
    /// Typing a letter in a File Explorer file list opens Glide with it, and a
    /// folder chosen there opens in that same Explorer window, as in Listary.
    /// </summary>
    public bool ExplorerTypeToSearch { get; set; } = true;

    /// <summary>Typing a letter in an Open/Save dialog's file list opens the bar under the dialog.</summary>
    public bool DialogTypeToSearch { get; set; } = true;

    /// <summary>
    /// Keep a search bar under every Open/Save dialog for as long as it is open,
    /// ready to click or type into.
    /// </summary>
    public bool DialogSearchBar { get; set; } = true;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(HotkeySettings))]
[JsonSerializable(typeof(FileSearchPriorities))]
[JsonSerializable(typeof(IntegrationSettings))]
[JsonSerializable(typeof(SearchFilterSettings[]))]
[JsonSerializable(typeof(WebSearchSettings[]))]
[JsonSerializable(typeof(CustomCommandSettings[]))]
[JsonSerializable(typeof(OpenFolderSettings))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
public partial class SettingsJsonContext : JsonSerializerContext;
