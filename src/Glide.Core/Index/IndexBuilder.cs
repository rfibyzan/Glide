using Glide.Core.Index.Usn;
using Glide.Core.Search;

namespace Glide.Core.Index;

/// <summary>
/// Builds an index file from a volume's USN journal. Enumeration and resolution
/// are separate steps because the journal is not hierarchical: every record has
/// to be collected before parent links can be followed.
/// </summary>
public static class IndexBuilder
{
    public static IndexBuildResult Build(
        string volume,
        string indexPath,
        Func<string, bool>? skip = null,
        Action<long>? onEnumerated = null)
    {
        using UsnVolume usn = UsnVolume.Open(volume);
        return Build(usn, indexPath, skip, onEnumerated);
    }

    public static IndexBuildResult Build(
        UsnVolume usn,
        string indexPath,
        Func<string, bool>? skip = null,
        Action<long>? onEnumerated = null)
    {
        ArgumentNullException.ThrowIfNull(usn);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        var entries = new Dictionary<ulong, UsnEntry>(capacity: 1 << 18);
        long seen = 0;

        long enumerated = usn.Enumerate(entry =>
        {
            if (entry.FileId == UsnPathResolver.RootFileId)
            {
                return;
            }

            entries[entry.FileId] = entry;
            onEnumerated?.Invoke(++seen);
        });

        UsnJournalState journal = usn.QueryJournal();
        int written = IndexWriter.Write(indexPath, UsnPathResolver.Resolve(entries, usn.Root, skip));

        return new IndexBuildResult(written, enumerated, entries.Count, usn.Root, journal);
    }
}

public readonly record struct IndexBuildResult(
    int IndexedEntries,
    long EnumeratedRecords,
    int ResolvedCandidates,
    string Root,
    UsnJournalState Journal);
