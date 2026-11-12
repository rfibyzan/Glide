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

    /// <summary>Mask for the 48-bit MFT record segment index.</summary>
    public const ulong MftRecordMask = 0x0000_FFFF_FFFF_FFFFUL;

    private const int MaxDepth = 512;
    private const int MaxCachedDirectories = 200_000;

    /// <summary>
    /// Checks whether the given 64-bit file reference number refers to the NTFS volume root.
    /// In NTFS, record segment 5 is $Root. Upper 16 bits hold the sequence number.
    /// </summary>
    public static bool IsRoot(ulong id) =>
        id == RootFileId || (id & MftRecordMask) == RootFileId;

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

    /// <summary>
    /// Resolves all directory records into their full volume paths.
    /// </summary>
    public static Dictionary<ulong, string> ResolveDirectoryPaths(
        IReadOnlyDictionary<ulong, UsnEntry> directories,
        string root,
        Func<string, bool>? skip = null,
        ICollection<ulong>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string normalizedRoot = root.EndsWith('\\') ? root : root + "\\";
        var cache = new Dictionary<ulong, string?>(Math.Min(directories.Count, MaxCachedDirectories));
        var resolved = new Dictionary<ulong, string>(directories.Count);

        foreach (var (key, entry) in directories)
        {
            string fullPath;

            // A directory resolved earlier as some other directory's ancestor is
            // already cached; reusing that string keeps one copy per path.
            if (cache.TryGetValue(key, out string? cachedSelf) && cachedSelf is not null)
            {
                fullPath = cachedSelf;
            }
            else
            {
                string? dirPath = ResolveDirectory(entry.ParentId, directories, normalizedRoot, cache, resolved);
                if (dirPath is null)
                {
                    continue;
                }

                fullPath = Append(dirPath, entry.Name);
            }

            if ((skip is not null && skip(fullPath)) || IsMetadataPath(fullPath))
            {
                skipped?.Add(key);
                continue;
            }

            resolved[key] = fullPath;
        }

        return resolved;
    }

    public static IEnumerable<FileEntry> Resolve(
        IReadOnlyDictionary<ulong, UsnEntry> entries,
        string root,
        Func<string, bool>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var normalizedEntries = new Dictionary<ulong, UsnEntry>(entries.Count);
        foreach (var (k, v) in entries)
        {
            normalizedEntries[k & MftRecordMask] = v;
        }

        var cache = new Dictionary<ulong, string?>(Math.Min(entries.Count, MaxCachedDirectories));
        string normalizedRoot = root.EndsWith('\\') ? root : root + "\\";

        foreach (UsnEntry entry in entries.Values)
        {
            if (IsRoot(entry.FileId))
            {
                continue;
            }

            string? parent = ResolveDirectory(entry.ParentId, normalizedEntries, normalizedRoot, cache, null);
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
        Dictionary<ulong, string?> cache,
        Dictionary<ulong, string>? resolved)
    {
        if (IsRoot(id))
        {
            return root;
        }

        ulong maskedId = id & MftRecordMask;
        if (maskedId == RootFileId)
        {
            return root;
        }

        if (resolved is not null && resolved.TryGetValue(maskedId, out string? knownResolved))
        {
            return knownResolved;
        }

        if (cache.TryGetValue(maskedId, out string? known))
        {
            return known;
        }

        var chain = new List<UsnEntry>(8);
        ulong current = maskedId;
        string? basePath;

        while (true)
        {
            if (current == RootFileId)
            {
                basePath = root;
                break;
            }

            if (resolved is not null && resolved.TryGetValue(current, out string? preResolved))
            {
                basePath = preResolved;
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

            ulong parentMasked = parent.ParentId & MftRecordMask;
            if (parentMasked == current)
            {
                basePath = current == RootFileId ? root : null;
                break;
            }

            chain.Add(parent);
            current = parentMasked;
        }

        if (basePath is null)
        {
            Cache(cache, maskedId, null);
            return null;
        }

        string path = basePath;

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path = Append(path, chain[i].Name);
            Cache(cache, chain[i].FileId & MftRecordMask, path);
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
