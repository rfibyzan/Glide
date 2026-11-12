namespace Glide.Core.Search;

/// <summary>
/// Lists one folder's children for a query typed as a path, such as
/// <c>C:\Us</c>. This is navigation rather than search: it reads the folder
/// from disk, so it works on drives the service does not index (network
/// shares, USB sticks) and never holds more than one folder's names.
/// </summary>
public static class FolderBrowser
{
    private const int PrefixBonus = 2000;
    private const int MaxScanned = 20_000;

    public static List<SearchResult> List(string folder, string? filter, int limit, bool showHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var results = new List<SearchResult>(Math.Min(limit, 64));
        if (limit <= 0)
        {
            return results;
        }

        string pattern = (filter ?? string.Empty).Trim().ToLowerInvariant();
        string[] terms = pattern.Length == 0 ? [] : [pattern];

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };

        int scanned = 0;

        try
        {
            var enumeration = new System.IO.Enumeration.FileSystemEnumerable<(string Path, bool IsDirectory)>(
                folder,
                (ref System.IO.Enumeration.FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
                options);

            foreach ((string path, bool isDirectory) in enumeration)
            {
                if (++scanned > MaxScanned)
                {
                    break;
                }

                ReadOnlySpan<char> name = EntryScorer.NameOf(path);
                int score;

                if (pattern.Length == 0)
                {
                    score = 0;
                }
                else if (FuzzyMatcher.Score(pattern, name) is int matched)
                {
                    score = matched + (name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ? PrefixBonus : 0);
                }
                else
                {
                    continue;
                }

                // Folders first, the way a file manager lists them.
                if (isDirectory)
                {
                    score += 1;
                }

                EntryScorer.Insert(results, score, path, isDirectory, limit);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable folder lists as empty; the window says "No matches".
        }

        if (pattern.Length == 0)
        {
            // Without a filter every row ties; order folders first, then by name.
            results.Sort(static (a, b) =>
            {
                int kind = b.Entry.IsDirectory.CompareTo(a.Entry.IsDirectory);
                return kind != 0
                    ? kind
                    : string.Compare(Path.GetFileName(a.Entry.Path), Path.GetFileName(b.Entry.Path), StringComparison.OrdinalIgnoreCase);
            });
        }

        EntryScorer.FillPositions(results, terms);
        return results;
    }
}
