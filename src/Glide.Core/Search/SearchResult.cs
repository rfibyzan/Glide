namespace Glide.Core.Search;

public enum SearchResultKind
{
    File,
    History,
    Bookmark
}

/// <summary>
/// A ranked candidate. <see cref="NamePositions"/> holds character offsets in
/// the file name, so the UI can highlight what the query matched.
/// </summary>
public readonly record struct SearchResult(
    FileEntry Entry,
    int Score,
    int[] NamePositions,
    SearchResultKind Kind = SearchResultKind.File);
