using System.Collections.Concurrent;
using System.Text;
using Glide.Core.Index.Usn;

namespace Glide.Core.Index;

/// <summary>
/// The changes a volume saw since its index file was last rebuilt: paths that
/// appeared and paths that went away. The service writes it beside the index
/// (<c>index-c.delta</c> next to <c>index-c.gidx</c>) within a second of a
/// change, and searches lay it over the index, so a new or renamed file shows
/// up without rewriting a file of a million entries. A rebuild folds the delta
/// into the index and deletes it.
///
/// The format is UTF-8 text, one change per line after a header:
/// <c>+&lt;TAB&gt;d|f&lt;TAB&gt;path</c> for a path that exists now, and
/// <c>-&lt;TAB&gt;path</c> for one that does not. The last line for a path wins.
/// </summary>
public sealed class IndexDelta
{
    public const string Extension = ".delta";
    private const string Header = "glide-delta 1";

    private static readonly ConcurrentDictionary<string, CachedDelta> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Insertion order is kept so a written file replays in the order it happened.
    private readonly Dictionary<string, Change> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public static IndexDelta Empty { get; } = new();

    public int Count => _changes.Count;

    public int RemovedCount { get; private set; }

    public static string PathFor(string indexPath) => Path.ChangeExtension(indexPath, Extension);

    /// <summary>Records that <paramref name="path"/> exists now.</summary>
    public void Add(string path, bool isDirectory) => Set(path, new Change(Removed: false, isDirectory));

    /// <summary>Records that <paramref name="path"/> no longer exists.</summary>
    public void Remove(string path) => Set(path, new Change(Removed: true, IsDirectory: false));

    public void Clear()
    {
        _changes.Clear();
        _order.Clear();
        RemovedCount = 0;
    }

    /// <summary>True when the delta says <paramref name="path"/> was added or removed; it then overrides the index.</summary>
    public bool Covers(string path) => _changes.ContainsKey(path);

    public bool IsRemoved(string path) => _changes.TryGetValue(path, out Change change) && change.Removed;

    /// <summary>The paths that exist now according to the delta, oldest change first.</summary>
    public IEnumerable<(string Path, bool IsDirectory)> Added()
    {
        foreach (string path in _order)
        {
            if (_changes.TryGetValue(path, out Change change) && !change.Removed)
            {
                yield return (path, change.IsDirectory);
            }
        }
    }

    /// <summary>
    /// Writes the delta, replacing the file in one rename so a reader never sees
    /// half of it. An empty delta deletes the file.
    /// </summary>
    public void Save(string deltaPath)
    {
        if (Count == 0)
        {
            Delete(deltaPath);
            return;
        }

        var text = new StringBuilder(Header.Length + 2 + Count * 64);
        text.Append(Header).Append('\n');

        foreach (string path in _order)
        {
            if (!_changes.TryGetValue(path, out Change change))
            {
                continue;
            }

            if (change.Removed)
            {
                text.Append("-\t").Append(path).Append('\n');
            }
            else
            {
                text.Append("+\t").Append(change.IsDirectory ? 'd' : 'f').Append('\t').Append(path).Append('\n');
            }
        }

        string temporary = deltaPath + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, deltaPath, overwrite: true);
    }

    public static void Delete(string deltaPath)
    {
        try
        {
            File.Delete(deltaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A reader holding it open; the next save replaces it.
        }
    }

    public static IndexDelta Parse(string text)
    {
        var delta = new IndexDelta();
        using var reader = new StringReader(text);

        if (reader.ReadLine() != Header)
        {
            return delta;
        }

        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("-\t", StringComparison.Ordinal) && line.Length > 2)
            {
                delta.Remove(line[2..]);
            }
            else if (line.Length > 4 && line[0] == '+' && line[1] == '\t' && line[3] == '\t')
            {
                delta.Add(line[4..], isDirectory: line[2] == 'd');
            }
        }

        return delta;
    }

    /// <summary>
    /// The delta beside <paramref name="indexPath"/>, read again only when the
    /// file changed. One stat per search; no delta file means an empty delta.
    /// </summary>
    public static IndexDelta LoadFor(string indexPath)
    {
        string deltaPath = PathFor(indexPath);

        try
        {
            var info = new FileInfo(deltaPath);
            if (!info.Exists)
            {
                Cache.TryRemove(deltaPath, out _);
                return Empty;
            }

            long length = info.Length;
            DateTime written = info.LastWriteTimeUtc;

            if (Cache.TryGetValue(deltaPath, out CachedDelta cached) && cached.Length == length && cached.Written == written)
            {
                return cached.Delta;
            }

            IndexDelta delta = Parse(File.ReadAllText(deltaPath, Encoding.UTF8));
            Cache[deltaPath] = new CachedDelta(length, written, delta);
            return delta;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Cache.TryGetValue(deltaPath, out CachedDelta cached) ? cached.Delta : Empty;
        }
    }

    private void Set(string path, Change change)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (_changes.TryGetValue(path, out Change previous))
        {
            if (previous.Removed)
            {
                RemovedCount--;
            }

            // Move it to the end: it is the latest change now. Paths compare
            // without case, like the dictionary.
            int index = _order.FindIndex(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _order.RemoveAt(index);
            }

            _changes.Remove(path);
        }

        _changes[path] = change;
        _order.Add(path);

        if (change.Removed)
        {
            RemovedCount++;
        }
    }

    private readonly record struct Change(bool Removed, bool IsDirectory);

    private readonly record struct CachedDelta(long Length, DateTime Written, IndexDelta Delta);
}

/// <summary>
/// Turns journal records into delta changes. A record carries only its parent
/// directory's id, so the full path comes from <c>resolveDirectory</c>, which
/// the service backs with <see cref="UsnVolume.TryGetPathById"/>. A renamed or
/// deleted directory changes the path of everything below it, which a delta
/// cannot express cheaply: those, and records whose parent cannot be found,
/// set <see cref="NeedsRebuild"/>.
/// </summary>
public sealed class IndexDeltaRecorder
{
    private const int MaxCachedDirectories = 4096;

    private readonly Func<ulong, string?> _resolveDirectory;
    private readonly Dictionary<ulong, string> _directories = [];

    public IndexDeltaRecorder(Func<ulong, string?> resolveDirectory)
    {
        _resolveDirectory = resolveDirectory ?? throw new ArgumentNullException(nameof(resolveDirectory));
    }

    public IndexDelta Delta { get; } = new();

    public bool NeedsRebuild { get; private set; }

    /// <summary>Applies one record. Returns true when the delta changed.</summary>
    public bool Apply(in UsnEntry entry)
    {
        bool removal = entry.IsRemoval;
        if (!removal && !entry.IsAddition)
        {
            return false;
        }

        if (entry.IsDirectory && removal)
        {
            // Everything under it moved or went away; paths cached below it are stale.
            NeedsRebuild = true;
            _directories.Clear();
        }

        string? parent = ResolveDirectory(entry.ParentId);
        if (parent is null)
        {
            NeedsRebuild = true;
            return false;
        }

        string path = parent.EndsWith('\\') ? parent + entry.Name : parent + "\\" + entry.Name;

        if (removal)
        {
            Delta.Remove(path);
        }
        else
        {
            Delta.Add(path, entry.IsDirectory);
        }

        return true;
    }

    /// <summary>After a rebuild: the index holds everything, start over.</summary>
    public void Reset()
    {
        Delta.Clear();
        _directories.Clear();
        NeedsRebuild = false;
    }

    private string? ResolveDirectory(ulong parentId)
    {
        if (_directories.TryGetValue(parentId, out string? cached))
        {
            return cached;
        }

        string? path = _resolveDirectory(parentId);
        if (path is null)
        {
            return null;
        }

        if (_directories.Count >= MaxCachedDirectories)
        {
            _directories.Clear();
        }

        _directories[parentId] = path;
        return path;
    }
}
