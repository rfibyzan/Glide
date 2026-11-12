using Glide.Core.Settings;

namespace Glide.Core.Search;

public enum QueryKind
{
    /// <summary>Search the index, possibly with constraints.</summary>
    Search,

    /// <summary>List the children of a folder typed as a path, filtered by the rest.</summary>
    Browse,

    /// <summary>Open a web search with the remaining terms.</summary>
    Web,
}

/// <summary>
/// What a raw query asks for. The launcher reads it to decide where results
/// come from; the index search only sees <see cref="Text"/> and
/// <see cref="Constraints"/>.
/// </summary>
public sealed record QueryIntent(
    QueryKind Kind,
    string Text,
    SearchConstraints? Constraints,
    string? FilterName,
    WebSearchSettings? Web,
    string? Folder)
{
    /// <summary>The URL to open for a web query.</summary>
    public string? WebUrl =>
        Web is null ? null : Web.Url.Replace("{query}", Uri.EscapeDataString(Text), StringComparison.Ordinal);
}

/// <summary>
/// Turns the typed query into a <see cref="QueryIntent"/>, in this order:
/// <list type="number">
/// <item>a web keyword followed by terms: <c>gg weather</c>;</item>
/// <item>a folder path: <c>D:\Projects\</c> browses it, <c>D:\Projects\ report</c>
/// searches under it;</item>
/// <item>filter tokens anywhere: <c>doc:</c>, <c>folder:</c>, <c>ext:pdf,docx</c>.</item>
/// </list>
/// </summary>
public static class QueryIntentParser
{
    private const int MaxScopeProbes = 16;

    public static QueryIntent Parse(
        string? raw,
        IReadOnlyList<SearchFilterSettings>? filters = null,
        IReadOnlyList<WebSearchSettings>? webSearches = null,
        Func<string, bool>? directoryExists = null)
    {
        string query = raw ?? string.Empty;
        directoryExists ??= Directory.Exists;

        if (TryWeb(query, webSearches, out QueryIntent? web))
        {
            return web;
        }

        string? scope = null;
        string rest = query;

        if (TryScope(query, directoryExists, out string? folder, out string remainder))
        {
            scope = folder;
            rest = remainder.Trim();

            // A path followed directly by a word is navigation: list the
            // children that match it, the way Explorer's address bar completes.
            // A space after the path means a search under that folder.
            if (!remainder.Contains(' ', StringComparison.Ordinal) && !remainder.Contains(':', StringComparison.Ordinal))
            {
                return new QueryIntent(QueryKind.Browse, rest, null, null, null, scope);
            }
        }

        bool foldersOnly = false;
        bool filesOnly = false;
        var extensions = new List<string>();
        string? filterName = null;
        var terms = new List<string>();

        foreach (string token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');

            if (colon > 0)
            {
                string keyword = token[..colon];
                string value = token[(colon + 1)..];

                if (keyword.Equals("ext", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    extensions.AddRange(value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries));
                    filterName ??= "*." + string.Join(", *.", extensions.Select(e => e.TrimStart('.')));
                    continue;
                }

                SearchFilterSettings? filter = FindFilter(filters, keyword);
                if (filter is not null)
                {
                    foldersOnly |= filter.FoldersOnly;
                    filesOnly |= filter.FilesOnly;
                    extensions.AddRange(filter.Extensions);
                    filterName = filterName is null ? filter.Name : filterName + " + " + filter.Name;

                    if (value.Length > 0)
                    {
                        terms.Add(value);
                    }

                    continue;
                }
            }

            terms.Add(token);
        }

        var constraints = new SearchConstraints(foldersOnly, filesOnly, extensions, scope);
        return new QueryIntent(
            QueryKind.Search,
            string.Join(' ', terms),
            constraints.IsEmpty ? null : constraints,
            filterName,
            null,
            scope);
    }

    private static bool TryWeb(
        string query,
        IReadOnlyList<WebSearchSettings>? webSearches,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out QueryIntent? intent)
    {
        intent = null;

        if (webSearches is null || webSearches.Count == 0)
        {
            return false;
        }

        int space = query.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        string keyword = query[..space];
        string terms = query[(space + 1)..].Trim();

        if (terms.Length == 0)
        {
            return false;
        }

        foreach (WebSearchSettings web in webSearches)
        {
            if (string.Equals(web.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            {
                intent = new QueryIntent(QueryKind.Web, terms, null, web.Name, web, null);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the longest prefix of the query that names an existing folder.
    /// Paths can hold spaces (<c>C:\Program Files\</c>), so the split point is a
    /// backslash rather than a space, and each candidate is checked on disk.
    /// </summary>
    private static bool TryScope(string query, Func<string, bool> directoryExists, out string? folder, out string rest)
    {
        folder = null;
        rest = query;

        bool driveRooted = query.Length >= 3 && char.IsAsciiLetter(query[0]) && query[1] == ':' && query[2] == '\\';
        bool uncRooted = query.StartsWith(@"\\", StringComparison.Ordinal) && query.Length > 2;

        if (!driveRooted && !uncRooted)
        {
            return false;
        }

        // The whole query is a folder without the trailing backslash.
        string trimmed = query.TrimEnd();
        if (!trimmed.EndsWith('\\') && directoryExists(trimmed))
        {
            folder = trimmed + "\\";
            rest = string.Empty;
            return true;
        }

        int probes = 0;

        for (int i = query.Length - 1; i >= 2 && probes < MaxScopeProbes; i--)
        {
            if (query[i] != '\\')
            {
                continue;
            }

            string candidate = query[..(i + 1)];
            probes++;

            if (directoryExists(candidate))
            {
                folder = candidate;
                rest = query[(i + 1)..];
                return true;
            }
        }

        return false;
    }

    private static SearchFilterSettings? FindFilter(IReadOnlyList<SearchFilterSettings>? filters, string keyword)
    {
        if (filters is null)
        {
            return null;
        }

        foreach (SearchFilterSettings filter in filters)
        {
            if (string.Equals(filter.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            {
                return filter;
            }
        }

        return null;
    }
}
