namespace Glide.Core.Search;

/// <summary>
/// Ranks entries against a query. Every term must match somewhere, either in
/// the file name or in the directory path; a file name match always outranks a
/// path-only match because users think in names first.
/// </summary>
public static class SearchEngine
{
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

        EntryScorer.FillPositions(ranked, terms);
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
                EntryScorer.Insert(merged, candidate.Score, candidate.Entry.Path, candidate.Entry.IsDirectory, limit);
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
            FileEntry entry = entries[i];

            if (EntryScorer.TryScore(entry.Path.AsSpan(), terms, out int score))
            {
                EntryScorer.Insert(top, score, entry.Path, entry.IsDirectory, limit);
            }
        }

        return top;
    }
}
