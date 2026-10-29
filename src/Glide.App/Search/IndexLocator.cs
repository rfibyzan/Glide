using Glide.Core.Index;

namespace Glide.App.Search;

/// <summary>
/// Finds the index files the service writes. The launcher never reads the USN
/// journal itself: without elevation it cannot open a volume, and keeping the
/// index out of this process is what keeps the launcher small.
/// </summary>
internal static class IndexLocator
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glide");

    /// <summary>Index files written by the service, one per volume, in a stable order.</summary>
    public static string[] Find()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            string[] files = System.IO.Directory.GetFiles(Directory, "index-*.gidx");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            return [.. files.Where(IndexFile.IsIndexFile)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Glide: cannot list index files: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Resolves an explicit <c>--index</c> value: a file, or a directory holding
    /// index files.
    /// </summary>
    public static string[] Resolve(string path)
    {
        if (System.IO.Directory.Exists(path))
        {
            string[] files = System.IO.Directory.GetFiles(path, "*.gidx");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return [.. files.Where(IndexFile.IsIndexFile)];
        }

        return IndexFile.IsIndexFile(path) ? [path] : [];
    }
}
