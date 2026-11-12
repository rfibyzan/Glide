using Glide.Core.Index.Usn;
using Glide.Core.Search;

namespace Glide.Core.Index;

/// <summary>
/// Builds an index file from a volume's USN journal. Enumeration and resolution
/// are performed in two passes:
/// 1. Directories are collected and resolved into full paths (~5-10 MB heap).
/// 2. Records are streamed directly to disk without allocating file paths in memory.
/// </summary>
public static class IndexBuilder
{
    public static IndexBuildResult Build(
        string volume,
        string indexPath,
        Func<string, bool>? skip = null,
        Action<long>? onEnumerated = null,
        string? quietIndexDirectory = null)
    {
        using UsnVolume usn = UsnVolume.Open(volume);
        return Build(usn, indexPath, skip, onEnumerated, quietIndexDirectory);
    }

    public static IndexBuildResult Build(
        UsnVolume usn,
        string indexPath,
        Func<string, bool>? skip = null,
        Action<long>? onEnumerated = null,
        string? quietIndexDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(usn);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        // Pass 1: Collect directories only.
        var directories = new Dictionary<ulong, UsnEntry>(capacity: 1 << 16);
        long enumerated = 0;

        usn.Enumerate(entry =>
        {
            enumerated++;
            if (UsnPathResolver.IsRoot(entry.FileId))
            {
                return;
            }

            if (entry.IsDirectory)
            {
                directories[entry.FileId & UsnPathResolver.MftRecordMask] = entry;
            }

            onEnumerated?.Invoke(enumerated);
        });

        // Resolve directory paths. Skipped directories and the noisy ones the
        // change filter names are remembered by id, so the service can tell
        // which later journal records are worth a rebuild.
        var quiet = new HashSet<ulong>();
        Dictionary<ulong, string> resolvedDirs = UsnPathResolver.ResolveDirectoryPaths(directories, usn.Root, skip, quiet);

        foreach (var (key, dirPath) in resolvedDirs)
        {
            if (ChangeFilter.IsQuietPath(dirPath, quietIndexDirectory))
            {
                quiet.Add(key);
            }
        }

        // Free unneeded directory entries before streaming files
        directories.Clear();
        directories.TrimExcess();

        // Pass 2: Stream all records directly into the index file
        using var writer = new IndexStreamWriter(indexPath);

        // Write directories first
        foreach (var (_, dirPath) in resolvedDirs)
        {
            writer.Write(dirPath, isDirectory: true);
        }

        // Stream all non-directory files
        usn.Enumerate(entry =>
        {
            if (entry.IsDirectory || UsnPathResolver.IsRoot(entry.FileId))
            {
                return;
            }

            ulong parentKey = entry.ParentId & UsnPathResolver.MftRecordMask;
            if (resolvedDirs.TryGetValue(parentKey, out string? parentPath))
            {
                writer.WriteCombined(parentPath, entry.Name, isDirectory: false, skip);
            }
        });

        int written = writer.Finish();
        UsnJournalState journal = usn.QueryJournal();

        return new IndexBuildResult(written, enumerated, resolvedDirs.Count, usn.Root, journal, new ChangeFilter(quiet));
    }
}

public readonly record struct IndexBuildResult(
    int IndexedEntries,
    long EnumeratedRecords,
    int ResolvedCandidates,
    string Root,
    UsnJournalState Journal,
    ChangeFilter Changes);
