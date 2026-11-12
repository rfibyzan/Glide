namespace Glide.Core.Search;

/// <summary>
/// Ranks what the user opened before above what they never did, the way
/// Listary learns from use. A recently opened entry that matches the query
/// gains <see cref="MaxBoost"/>, fading by <see cref="Step"/> per older
/// history slot down to <see cref="MinBoost"/>. Matching history entries that
/// the index scan did not return (it keeps a bounded top list) are added, so
/// a folder opened yesterday is found even when a thousand names tie with it.
/// </summary>
public static class RecentBoost
{
    public const int MaxBoost = 300;
    public const int MinBoost = 100;
    public const int Step = 5;

    public static SearchResult[] Apply(IReadOnlyList<SearchResult> results, IReadOnlyList<string> recent, string? query, int limit, SearchConstraints? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(recent);

        if (recent.Count == 0)
        {
            return [.. results];
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < recent.Count; i++)
        {
            rank.TryAdd(recent[i], i);
        }

        string[] terms = QueryParser.Parse(query);
        var boosted = new List<(SearchResult Result, int Order)>(results.Count + 8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < results.Count; i++)
        {
            SearchResult result = results[i];
            seen.Add(result.Entry.Path);

            if (result.Kind == SearchResultKind.File && rank.TryGetValue(result.Entry.Path, out int slot))
            {
                result = result with { Score = result.Score + BoostFor(slot) };
            }

            boosted.Add((result, i));
        }

        if (terms.Length > 0)
        {
            foreach ((string path, int slot) in rank)
            {
                if (seen.Contains(path) || !EntryScorer.TryScore(path.AsSpan(), terms, out int score))
                {
                    continue;
                }

                // No disk check for network paths: a disconnected share would
                // stall the search for its timeout.
                bool isDirectory = path.EndsWith('\\') ||
                    (!path.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(path));

                if (constraints is not null && !constraints.Accepts(path, isDirectory))
                {
                    continue;
                }

                var entry = new FileEntry(path, isDirectory, 0, default);
                var positions = FuzzyMatcher.MatchPositions(terms[0], EntryScorer.NameOf(path));
                boosted.Add((new SearchResult(entry, score + BoostFor(slot), positions), results.Count + slot));
            }
        }

        // Stable: equal scores keep the index's own order (shorter names first).
        return boosted
            .OrderByDescending(b => b.Result.Score)
            .ThenBy(b => b.Order)
            .Take(limit)
            .Select(b => b.Result)
            .ToArray();
    }

    private static int BoostFor(int slot) => Math.Max(MinBoost, MaxBoost - (slot * Step));
}
