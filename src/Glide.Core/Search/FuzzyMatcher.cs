namespace Glide.Core.Search;

/// <summary>
/// Scores a query against a single string. The matcher is a case-insensitive
/// subsequence check: every pattern character must appear in order, and the
/// score rewards the shapes users expect to rank first.
/// </summary>
public static class FuzzyMatcher
{
    private const int PerCharacter = 1;
    private const int ConsecutiveBonus = 25;
    private const int WordStartBonus = 20;
    private const int ExactCaseBonus = 1;
    private const int TailBonus = 10;
    private const int GapPenaltyPerCharacter = 2;
    private const int MaxPenalizedGap = 6;

    /// <summary>
    /// Returns a score when <paramref name="pattern"/> is a subsequence of
    /// <paramref name="candidate"/>, otherwise null. An empty pattern matches
    /// with score 0.
    /// </summary>
    public static int? Score(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate)
    {
        if (pattern.IsEmpty)
        {
            return 0;
        }

        if (candidate.IsEmpty || pattern.Length > candidate.Length)
        {
            return null;
        }

        int score = 0;
        int searchFrom = 0;
        int previous = -1;

        for (int p = 0; p < pattern.Length; p++)
        {
            char wanted = char.ToLowerInvariant(pattern[p]);
            int found = -1;

            for (int c = searchFrom; c < candidate.Length; c++)
            {
                if (char.ToLowerInvariant(candidate[c]) == wanted)
                {
                    found = c;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            int gap = found - previous - 1;
            if (gap > 0)
            {
                score -= Math.Min(gap, MaxPenalizedGap) * GapPenaltyPerCharacter;
            }

            score += PerCharacter;

            if (previous >= 0 && found == previous + 1)
            {
                score += ConsecutiveBonus;
            }
            else if (IsWordStart(candidate, found))
            {
                score += WordStartBonus;
            }

            if (candidate[found] == pattern[p])
            {
                score += ExactCaseBonus;
            }

            previous = found;
            searchFrom = found + 1;
        }

        if (previous == candidate.Length - 1)
        {
            score += TailBonus;
        }

        return score;
    }

    /// <summary>
    /// Character offsets of the match inside <paramref name="candidate"/>, or
    /// an empty array when there is no match. Used for highlighting.
    /// </summary>
    public static int[] MatchPositions(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate)
    {
        if (pattern.IsEmpty)
        {
            return Array.Empty<int>();
        }

        if (candidate.IsEmpty || pattern.Length > candidate.Length)
        {
            return Array.Empty<int>();
        }

        var positions = new int[pattern.Length];
        int searchFrom = 0;

        for (int p = 0; p < pattern.Length; p++)
        {
            char wanted = char.ToLowerInvariant(pattern[p]);
            int found = -1;

            for (int c = searchFrom; c < candidate.Length; c++)
            {
                if (char.ToLowerInvariant(candidate[c]) == wanted)
                {
                    found = c;
                    break;
                }
            }

            if (found < 0)
            {
                return Array.Empty<int>();
            }

            positions[p] = found;
            searchFrom = found + 1;
        }

        return positions;
    }

    private static bool IsWordStart(ReadOnlySpan<char> candidate, int index)
    {
        if (index == 0)
        {
            return true;
        }

        char previous = candidate[index - 1];
        if (!char.IsLetterOrDigit(previous))
        {
            return true;
        }

        return char.IsUpper(candidate[index]) && char.IsLower(previous);
    }
}
