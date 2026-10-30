using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GlideBookmarks_" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public BookmarkStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "bookmarks.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private BookmarkStore Store() => new(_file);

    [Fact]
    public void Add_PutsTheNewPathFirstAndDeduplicatesCaseInsensitively()
    {
        BookmarkStore store = Store();
        store.Add(@"C:\One");
        store.Add(@"C:\Two");
        store.Add(@"c:\one");

        Assert.Equal([@"c:\one", @"C:\Two"], store.Items);
    }

    [Fact]
    public void Add_TrimsToTheLimit_KeepingTheNewest()
    {
        BookmarkStore store = Store();

        for (int i = 0; i < BookmarkStore.MaxEntries + 10; i++)
        {
            store.Add($@"C:\path{i}");
        }

        Assert.Equal(BookmarkStore.MaxEntries, store.Items.Length);
        Assert.Equal($@"C:\path{BookmarkStore.MaxEntries + 9}", store.Items[0]);
    }

    [Fact]
    public void Remove_AndContains_UseCaseInsensitiveComparison()
    {
        BookmarkStore store = Store();
        store.Add(@"C:\Keep");
        store.Add(@"C:\Drop");

        Assert.True(store.Contains(@"c:\drop"));
        store.Remove(@"C:\DROP");

        Assert.False(store.Contains(@"C:\Drop"));
        Assert.Equal([@"C:\Keep"], store.Items);
    }

    [Fact]
    public void Changes_SurviveANewStoreInstance()
    {
        Store().Add(@"C:\Persisted");

        Assert.Equal([@"C:\Persisted"], Store().Items);
    }

    [Fact]
    public void CorruptFile_ReadsAsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(_file, "{ this is not json");

        Assert.Empty(Store().Items);
    }
}
