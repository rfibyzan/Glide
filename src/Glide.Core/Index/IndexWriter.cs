using Glide.Core.Search;

namespace Glide.Core.Index;

/// <summary>
/// Writes an index file from a sequence of entries. The file is built next to
/// its destination and moved into place, so a reader never sees a half-written
/// index even while the service rebuilds it.
/// </summary>
public static class IndexWriter
{
    public static int Write(string path, IEnumerable<FileEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        using var writer = new IndexStreamWriter(path);
        foreach (FileEntry entry in entries)
        {
            writer.Write(entry.Path, entry.IsDirectory);
        }

        return writer.Finish();
    }
}
