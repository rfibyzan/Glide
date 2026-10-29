using Glide.Core.Search;

namespace Glide.Core.Snapshot;

/// <summary>
/// Immutable set of entries loaded from a snapshot file. One path per line,
/// blank lines and lines starting with '#' are ignored, a trailing separator
/// marks a directory. This is the v0.2 bridge until the USN index lands.
/// </summary>
public sealed class EntryStore
{
    private readonly FileEntry[] _entries;

    public EntryStore(IEnumerable<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries as FileEntry[] ?? entries.ToArray();
    }

    public int Count => _entries.Length;

    public IReadOnlyList<FileEntry> Entries => _entries;

    public IReadOnlyList<SearchResult> Search(string? query, int limit = 50)
    {
        return SearchEngine.Search(_entries, query, limit);
    }

    public static EntryStore FromLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var parsed = new List<FileEntry>(capacity: 1024);

        foreach (string? line in lines)
        {
            if (line is null)
            {
                continue;
            }

            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
            {
                continue;
            }

            bool isDirectory = span[^1] is '\\' or '/';
            string path = span.ToString();

            if (isDirectory)
            {
                path = NormalizeDirectory(path);
            }

            parsed.Add(new FileEntry(path, isDirectory, 0, default));
        }

        return new EntryStore(parsed);
    }

    public static EntryStore FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromLines(File.ReadLines(path));
    }

    private static string NormalizeDirectory(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');

        // Keep drive roots as "C:\" instead of collapsing them to "C:".
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + "\\";
        }

        return trimmed;
    }
}
