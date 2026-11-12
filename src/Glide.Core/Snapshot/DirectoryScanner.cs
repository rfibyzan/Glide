using Glide.Core.Search;

namespace Glide.Core.Snapshot;

/// <summary>
/// Walks a directory tree and produces entries for the search store. This is
/// the v0.3 fallback until the USN index replaces it. Reparse points are
/// skipped so a junction cannot pull the walk into a loop.
/// </summary>
public static class DirectoryScanner
{
    public static IEnumerable<FileEntry> Enumerate(string root, int maxEntries = 200_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        int count = 0;
        foreach (FileSystemInfo info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            long size = !isDirectory && info is FileInfo file ? file.Length : 0;

            yield return new FileEntry(info.FullName, isDirectory, size, info.LastWriteTimeUtc);

            if (++count >= maxEntries)
            {
                break;
            }
        }
    }

    public static EntryStore Scan(string root, int maxEntries = 200_000)
    {
        return new EntryStore(Enumerate(root, maxEntries));
    }
}
