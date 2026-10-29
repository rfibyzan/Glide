using Glide.Core.Search;
using Glide.Core.Snapshot;

namespace Glide.Core.Tests;

public class EntryStoreTests
{
    [Fact]
    public void ParsesPlainFileLines()
    {
        EntryStore store = EntryStore.FromLines([@"C:\Users\Asus\notes.txt"]);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void TrimsWhitespaceAndSkipsBlanksAndComments()
    {
        EntryStore store = EntryStore.FromLines(
        [
            "  C:\\a.txt  ",
            "",
            "   ",
            "# a comment",
            @"C:\b.txt",
        ]);

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void TrailingSeparatorMarksDirectory()
    {
        EntryStore store = EntryStore.FromLines([@"C:\Users\Asus\Downloads\", @"C:\Users\Asus\notes.txt"]);

        IReadOnlyList<SearchResult> results = store.Search("down");

        SearchResult only = Assert.Single(results);
        Assert.True(only.Entry.IsDirectory);
        Assert.Equal(@"C:\Users\Asus\Downloads", only.Entry.Path);
    }

    [Fact]
    public void KeepsDriveRootIntact()
    {
        EntryStore store = EntryStore.FromLines([@"C:\", @"D:\"]);

        Assert.Equal(2, store.Count);
        Assert.All(store.Entries, entry => Assert.Equal(3, entry.Path.Length));
    }

    [Fact]
    public void SearchDelegatesToEngineAndRespectsLimit()
    {
        EntryStore store = EntryStore.FromLines(
        [
            @"C:\data\file1.txt",
            @"C:\data\file2.txt",
            @"C:\data\file3.txt",
        ]);

        Assert.Equal(2, store.Search("file", limit: 2).Count);
        Assert.Empty(store.Search("nothing"));
    }

    [Fact]
    public void EmptyQueryReturnsFirstEntriesInOrder()
    {
        EntryStore store = EntryStore.FromLines([@"C:\b.txt", @"C:\a.txt"]);

        Assert.Equal([@"C:\b.txt", @"C:\a.txt"], store.Search("").Select(r => r.Entry.Path));
    }

    [Fact]
    public void LoadsFromFileAndReportsMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glide-snapshot-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, [@"C:\one.txt", @"C:\two.txt"]);

        try
        {
            Assert.Equal(2, EntryStore.FromFile(path).Count);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<FileNotFoundException>(() => EntryStore.FromFile(path + ".missing"));
    }
}
