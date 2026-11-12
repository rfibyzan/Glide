namespace Glide.Core.Search;

/// <summary>
/// Scoring and ordering shared by the in-memory engine and the on-disk index.
/// Both backends must rank identically, so the rules live in one place and
/// operate on spans: the index path never has to allocate a string just to
/// compare a candidate against the current top list.
/// </summary>
internal static class EntryScorer
{
    internal const int NameTier = 1000;
    internal const int PathPenalty = 50;

    /// <summary>
    /// A name match worth at least this per query character is "strong": a
    /// mostly contiguous run (26 points a character) clears it, letters
    /// scattered across a long name (around zero) do not.
    /// </summary>
    internal const int StrongPerCharacter = 10;

    internal static bool TryScore(ReadOnlySpan<char> path, string[] terms, out int score)
    {
        ReadOnlySpan<char> name = NameOf(path);
        int total = 0;
        bool strong = true;

        for (int t = 0; t < terms.Length; t++)
        {
            int? nameScore = FuzzyMatcher.Score(terms[t], name);

            if (nameScore is int matchedName)
            {
                total += NameTier + matchedName;
                strong &= matchedName >= StrongPerCharacter * terms[t].Length;
                continue;
            }

            int? pathScore = FuzzyMatcher.Score(terms[t], path);
            if (pathScore is null)
            {
                score = 0;
                return false;
            }

            total += pathScore.Value - PathPenalty;
            strong = false;
        }

        // Exclusion is checked only for rows that matched every term: it is a
        // substring search, and running it on every row of a million-row
        // index cost more than the matching itself.
        if (PriorityEvaluator.IsExcluded(path))
        {
            score = 0;
            return false;
        }

        // A priority folder lifts a clean match, never a scattered one: the
        // Start Menu boost must not put "M...a...c...r...o" above "Macro".
        int adjustment = PriorityEvaluator.GetScoreAdjustment(path, name);
        total += adjustment > 0 && !strong ? 0 : adjustment;

        score = total;
        return true;
    }

    /// <summary>Same rules as the char overload, evaluated on ASCII bytes.</summary>
    internal static bool TryScore(ReadOnlySpan<byte> path, byte[][] terms, out int score)
    {
        ReadOnlySpan<byte> name = NameOfBytes(path);
        int total = 0;
        bool strong = true;

        foreach (byte[] term in terms)
        {
            int? nameScore = FuzzyMatcher.ScoreBytes(term, name);

            if (nameScore is int matchedName)
            {
                total += NameTier + matchedName;
                strong &= matchedName >= StrongPerCharacter * term.Length;
                continue;
            }

            int? pathScore = FuzzyMatcher.ScoreBytes(term, path);
            if (pathScore is null)
            {
                score = 0;
                return false;
            }

            total += pathScore.Value - PathPenalty;
            strong = false;
        }

        // Exclusion is checked only for rows that matched every term: it is a
        // substring search, and running it on every row of a million-row
        // index cost more than the matching itself.
        if (PriorityEvaluator.IsExcludedBytes(path))
        {
            score = 0;
            return false;
        }

        // A priority folder lifts a clean match, never a scattered one: the
        // Start Menu boost must not put "M...a...c...r...o" above "Macro".
        int adjustment = PriorityEvaluator.GetScoreAdjustmentBytes(path, name);
        total += adjustment > 0 && !strong ? 0 : adjustment;

        score = total;
        return true;
    }

    internal static ReadOnlySpan<byte> NameOfBytes(ReadOnlySpan<byte> path)
    {
        int separator = path.LastIndexOfAny((byte)'\\', (byte)'/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>
    /// Sorts by score, then by shorter file name, then by path. A positive
    /// result means the candidate belongs before <paramref name="other"/>.
    /// </summary>
    internal static int Compare(int score, ReadOnlySpan<char> path, in SearchResult other)
    {
        int byScore = score.CompareTo(other.Score);
        if (byScore != 0)
        {
            return byScore;
        }

        int byNameLength = NameOf(other.Entry.Path).Length.CompareTo(NameOf(path).Length);
        if (byNameLength != 0)
        {
            return byNameLength;
        }

        return other.Entry.Path.AsSpan().SequenceCompareTo(path);
    }

    /// <summary>
    /// Inserts a candidate, allocating a path string only when it survives into
    /// the top list. Returns the insert position, or -1 when it is dropped.
    /// </summary>
    internal static int Insert(List<SearchResult> top, int score, ReadOnlySpan<char> path, bool isDirectory, int limit)
    {
        if (top.Count == limit && Compare(score, path, top[^1]) <= 0)
        {
            return -1;
        }

        int index = 0;
        while (index < top.Count && Compare(score, path, top[index]) <= 0)
        {
            index++;
        }

        top.Insert(index, new SearchResult(new FileEntry(path.ToString(), isDirectory, 0, default), score, Array.Empty<int>()));

        if (top.Count > limit)
        {
            top.RemoveAt(top.Count - 1);
        }

        return index;
    }

    internal static ReadOnlySpan<char> NameOf(ReadOnlySpan<char> path)
    {
        int separator = path.LastIndexOfAny('\\', '/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>Recomputes highlight positions for the rows that made the cut.</summary>
    internal static void FillPositions(List<SearchResult> ranked, string[] terms)
    {
        if (terms.Length == 0)
        {
            return;
        }

        FillPositionsCore(ranked, terms);
    }

    private static void FillPositionsCore(List<SearchResult> ranked, string[] terms)
    {
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
    }
}
