namespace Glide.Core.Search;

/// <summary>
/// Ranks entries against a query. Every term must match somewhere, either in
/// the file name or in the directory path; a file name match always outranks a
/// path-only match because users think in names first.
/// </summary>
public static class SearchEngine
{
    private const int NameTier = 1000;
    private const int PathPenalty = 50;

    public static IReadOnlyList<SearchResult> Search(
        IReadOnlyList<FileEntry> entries,
        string? query,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (limit <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        string[] terms = QueryParser.Parse(query);

        if (terms.Length == 0)
        {
            int count = Math.Min(limit, entries.Count);
            var everything = new SearchResult[count];
            for (int i = 0; i < count; i++)
            {
                everything[i] = new SearchResult(entries[i], 0, Array.Empty<int>());
            }

            return everything;
        }

        var top = new List<SearchResult>(Math.Min(limit, 64));

        for (int i = 0; i < entries.Count; i++)
        {
            if (TryScore(entries[i], terms, out SearchResult result))
            {
                Insert(top, result, limit);
            }
        }

        return top;
    }

    private static bool TryScore(FileEntry entry, string[] terms, out SearchResult result)
    {
        ReadOnlySpan<char> path = entry.Path.AsSpan();
        int separator = path.LastIndexOfAny('\\', '/');
        ReadOnlySpan<char> name = separator < 0 ? path : path[(separator + 1)..];

        int total = 0;
        int[] positions = Array.Empty<int>();

        for (int t = 0; t < terms.Length; t++)
        {
            int? nameScore = FuzzyMatcher.Score(terms[t], name);

            if (nameScore is int matchedName)
            {
                total += NameTier + matchedName;

                if (t == 0)
                {
                    positions = FuzzyMatcher.MatchPositions(terms[0], name);
                }

                continue;
            }

            int? pathScore = FuzzyMatcher.Score(terms[t], path);
            if (pathScore is null)
            {
                result = default;
                return false;
            }

            total += pathScore.Value - PathPenalty;
        }

        result = new SearchResult(entry, total, positions);
        return true;
    }

    private static void Insert(List<SearchResult> top, SearchResult candidate, int limit)
    {
        int index = 0;
        while (index < top.Count && Compare(top[index], candidate) <= 0)
        {
            index++;
        }

        if (index >= limit)
        {
            return;
        }

        top.Insert(index, candidate);

        if (top.Count > limit)
        {
            top.RemoveAt(top.Count - 1);
        }
    }

    private static int Compare(in SearchResult a, in SearchResult b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0)
        {
            return byScore;
        }

        int byNameLength = NameLength(a.Entry.Path).CompareTo(NameLength(b.Entry.Path));
        if (byNameLength != 0)
        {
            return byNameLength;
        }

        return string.CompareOrdinal(a.Entry.Path, b.Entry.Path);
    }

    private static int NameLength(string path)
    {
        int separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path.Length : path.Length - separator - 1;
    }
}
