using Glide.Core.Search;

namespace Glide.Core.Index.Usn;

/// <summary>
/// Turns USN records into full paths. A record only carries its own name and the
/// id of its parent, so a path is rebuilt by walking parent links up to the
/// volume root. Directory paths are memoized: a tree with a million files has
/// only tens of thousands of directories, so the walk cost stays close to one
/// lookup per file.
/// </summary>
public static class UsnPathResolver
{
    /// <summary>NTFS gives the root directory of every volume file id 5.</summary>
    public const ulong RootFileId = 5;

    private const int MaxDepth = 512;
    private const int MaxCachedDirectories = 200_000;

    /// <summary>
    /// Skips NTFS metadata directories. They cannot be opened by normal users and
    /// would only add noise to the result list.
    /// </summary>
    public static bool IsMetadataPath(string path)
    {
        string normalized = path.EndsWith('\\') ? path : path + "\\";

        return normalized.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\System Volume Information\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\$Extend\", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<FileEntry> Resolve(
        IReadOnlyDictionary<ulong, UsnEntry> entries,
        string root,
        Func<string, bool>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var cache = new Dictionary<ulong, string?>(Math.Min(entries.Count, MaxCachedDirectories));
        string normalizedRoot = root.EndsWith('\\') ? root : root + "\\";

        foreach (UsnEntry entry in entries.Values)
        {
            if (entry.FileId == RootFileId)
            {
                continue;
            }

            string? parent = ResolveDirectory(entry.ParentId, entries, normalizedRoot, cache);
            if (parent is null)
            {
                continue;
            }

            string path = Append(parent, entry.Name);
            if ((skip is not null && skip(path)) || IsMetadataPath(path))
            {
                continue;
            }

            yield return new FileEntry(path, entry.IsDirectory, 0, default);
        }
    }

    private static string? ResolveDirectory(
        ulong id,
        IReadOnlyDictionary<ulong, UsnEntry> entries,
        string root,
        Dictionary<ulong, string?> cache)
    {
        if (id == RootFileId)
        {
            return root;
        }

        if (cache.TryGetValue(id, out string? known))
        {
            return known;
        }

        var chain = new List<UsnEntry>(8);
        ulong current = id;
        string? basePath;

        while (true)
        {
            if (current == RootFileId)
            {
                basePath = root;
                break;
            }

            if (cache.TryGetValue(current, out string? cached))
            {
                basePath = cached;
                break;
            }

            if (!entries.TryGetValue(current, out UsnEntry parent) || chain.Count >= MaxDepth)
            {
                basePath = null;
                break;
            }

            chain.Add(parent);
            current = parent.ParentId;
        }

        if (basePath is null)
        {
            Cache(cache, id, null);
            return null;
        }

        string path = basePath;

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path = Append(path, chain[i].Name);
            Cache(cache, chain[i].FileId, path);
        }

        return path;
    }

    private static void Cache(Dictionary<ulong, string?> cache, ulong id, string? path)
    {
        if (cache.Count < MaxCachedDirectories)
        {
            cache[id] = path;
        }
    }

    private static string Append(string parent, string name)
    {
        return parent.EndsWith('\\') ? parent + name : parent + "\\" + name;
    }
}
