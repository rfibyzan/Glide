namespace Glide.Core.Search;

public static class QueryParser
{
    /// <summary>
    /// Splits a raw query into lower-case terms. Repeated terms are dropped
    /// because scoring each one twice would only inflate the score.
    /// </summary>
    public static string[] Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var terms = new List<string>(4);
        ReadOnlySpan<char> remaining = query.AsSpan();

        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny(" \t\r\n".AsSpan());
            ReadOnlySpan<char> token = separator < 0 ? remaining : remaining[..separator];
            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];

            if (token.IsEmpty)
            {
                continue;
            }

            string term = token.ToString().ToLowerInvariant();
            if (!terms.Contains(term))
            {
                terms.Add(term);
            }
        }

        return terms.ToArray();
    }
}
