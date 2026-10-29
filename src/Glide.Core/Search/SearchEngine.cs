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
    private const int ParallelThreshold = 16_384;
    private const int MaxWorkers = 8;

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

        List<SearchResult> ranked = entries.Count >= ParallelThreshold && Environment.ProcessorCount > 1
            ? SearchInParallel(entries, terms, limit)
            : Scan(entries, 0, entries.Count, terms, limit);

        for (int i = 0; i < ranked.Count; i++)
        {
            SearchResult result = ranked[i];
            if (result.NamePositions.Length != 0)
            {
                continue;
            }

            ReadOnlySpan<char> name = NameOf(result.Entry.Path);
            if (FuzzyMatcher.Score(terms[0], name) is not null)
            {
                ranked[i] = result with { NamePositions = FuzzyMatcher.MatchPositions(terms[0], name) };
            }
        }

        return ranked;
    }

    private static List<SearchResult> SearchInParallel(
        IReadOnlyList<FileEntry> entries,
        string[] terms,
        int limit)
    {
        int workers = Math.Min(MaxWorkers, Environment.ProcessorCount);
        int chunk = (entries.Count + workers - 1) / workers;
        var parts = new List<SearchResult>[workers];

        Parallel.For(0, workers, worker =>
        {
            int start = worker * chunk;
            int end = Math.Min(entries.Count, start + chunk);
            parts[worker] = Scan(entries, start, end, terms, limit);
        });

        var merged = new List<SearchResult>(limit);

        foreach (List<SearchResult> part in parts)
        {
            foreach (SearchResult candidate in part)
            {
                Insert(merged, candidate, limit);
            }
        }

        return merged;
    }

    private static List<SearchResult> Scan(
        IReadOnlyList<FileEntry> entries,
        int start,
        int end,
        string[] terms,
        int limit)
    {
        var top = new List<SearchResult>(Math.Min(limit, 64));

        for (int i = start; i < end; i++)
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
        ReadOnlySpan<char> name = NameOf(path);

        int total = 0;

        for (int t = 0; t < terms.Length; t++)
        {
            int? nameScore = FuzzyMatcher.Score(terms[t], name);

            if (nameScore is int matchedName)
            {
                total += NameTier + matchedName;
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

        result = new SearchResult(entry, total, Array.Empty<int>());
        return true;
    }

    private static int Insert(List<SearchResult> top, SearchResult candidate, int limit)
    {
        if (top.Count == limit && Compare(top[^1], candidate) <= 0)
        {
            return -1;
        }

        int index = 0;
        while (index < top.Count && Compare(top[index], candidate) <= 0)
        {
            index++;
        }

        top.Insert(index, candidate);

        if (top.Count > limit)
        {
            top.RemoveAt(top.Count - 1);
        }

        return index;
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

    private static ReadOnlySpan<char> NameOf(string path)
    {
        return NameOf(path.AsSpan());
    }

    private static ReadOnlySpan<char> NameOf(ReadOnlySpan<char> path)
    {
        int separator = path.LastIndexOfAny('\\', '/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static int NameLength(string path)
    {
        return NameOf(path).Length;
    }
}
