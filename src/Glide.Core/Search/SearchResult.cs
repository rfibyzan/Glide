namespace Glide.Core.Search;

public enum SearchResultKind
{
    File,
    History,
    Bookmark,

    /// <summary>A web search; <see cref="FileEntry.Path"/> holds the URL.</summary>
    WebSearch,

    /// <summary>A built-in command; <see cref="FileEntry.Path"/> holds its target.</summary>
    Command,

    /// <summary>A folder open in a File Explorer window.</summary>
    OpenedFolder,
}

/// <summary>
/// A ranked candidate. <see cref="NamePositions"/> holds character offsets in
/// the file name, so the UI can highlight what the query matched.
/// <see cref="Title"/> replaces the file name on rows that are not files.
/// </summary>
public readonly record struct SearchResult(
    FileEntry Entry,
    int Score,
    int[] NamePositions,
    SearchResultKind Kind = SearchResultKind.File,
    string? Title = null);
