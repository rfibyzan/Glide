using Glide.Core.Search;
using Glide.Core.Snapshot;

namespace Glide.Core.Tests;

public class DirectoryScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"glide-scan-{Guid.NewGuid():N}");

    public DirectoryScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "readme.md"), "x");
        File.WriteAllText(Path.Combine(_root, "docs", "report.txt"), "xy");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FindsFilesAndDirectoriesRecursively()
    {
        EntryStore store = DirectoryScanner.Scan(_root);

        Assert.Equal(3, store.Count);
        Assert.Contains(store.Entries, e => e.Path.EndsWith("readme.md", StringComparison.Ordinal) && !e.IsDirectory);
        Assert.Contains(store.Entries, e => e.Path.EndsWith("report.txt", StringComparison.Ordinal) && !e.IsDirectory);
        Assert.Contains(store.Entries, e => e.Path.EndsWith("docs", StringComparison.Ordinal) && e.IsDirectory);
    }

    [Fact]
    public void RecordsFileSize()
    {
        EntryStore store = DirectoryScanner.Scan(_root);

        FileEntry report = store.Entries.Single(e => e.Path.EndsWith("report.txt", StringComparison.Ordinal));
        Assert.Equal(2, report.SizeBytes);
    }

    [Fact]
    public void RespectsMaxEntries()
    {
        Assert.Equal(1, DirectoryScanner.Scan(_root, maxEntries: 1).Count);
    }

    [Fact]
    public void ThrowsWhenRootIsMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryScanner.Scan(_root + "-missing"));
    }
}
