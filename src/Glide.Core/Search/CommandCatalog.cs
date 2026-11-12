namespace Glide.Core.Search;

/// <summary>A built-in command shown among results, like Listary's launcher commands.</summary>
/// <param name="Target">A <c>glide:</c> action handled by the launcher, or anything ShellExecute opens.</param>
public sealed record LauncherCommand(string Title, string Target, string[] Keywords, string Arguments = "");

public static class CommandCatalog
{
    public const string SettingsTarget = "glide:settings";
    public const string ExitTarget = "glide:exit";

    public static IReadOnlyList<LauncherCommand> BuiltIn { get; } =
    [
        new("Glide Settings", SettingsTarget, ["options", "preferences", "config"]),
        new("Exit Glide", ExitTarget, ["quit", "close"]),
        new("Recycle Bin", "shell:RecycleBinFolder", ["trash"]),
        new("Windows Settings", "ms-settings:", ["system", "preferences"]),
        new("Control Panel", "control.exe", []),
        new("This PC", "shell:MyComputerFolder", ["computer", "drives"]),
        new("Downloads", "shell:Downloads", []),
        new("Startup Folder", "shell:Startup", ["autostart"]),
    ];

    /// <summary>
    /// Commands whose title or keywords contain a word starting with every
    /// query term. Prefix matching keeps commands out of ordinary file queries:
    /// "set" finds Glide Settings, "report" finds nothing here.
    /// </summary>
    public static List<LauncherCommand> Match(string? query, IReadOnlyList<LauncherCommand>? commands = null)
    {
        commands ??= BuiltIn;
        string[] terms = QueryParser.Parse(query);
        var matches = new List<LauncherCommand>();

        if (terms.Length == 0)
        {
            return matches;
        }

        foreach (LauncherCommand command in commands)
        {
            bool all = true;

            foreach (string term in terms)
            {
                if (!HasWordStartingWith(command.Title, term) && !command.Keywords.Any(k => HasWordStartingWith(k, term)))
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                matches.Add(command);
            }
        }

        return matches;
    }

    private static bool HasWordStartingWith(string text, string term)
    {
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
