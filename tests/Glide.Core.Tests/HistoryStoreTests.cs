using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GlideHistory_" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public HistoryStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "history.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private HistoryStore Store() => new(_file);

    [Fact]
    public void Add_MovesAnExistingPathBackToTheFront()
    {
        HistoryStore store = Store();
        store.Add(@"C:\One");
        store.Add(@"C:\Two");
        store.Add(@"C:\One");

        Assert.Equal([@"C:\One", @"C:\Two"], store.Items);
    }

    [Fact]
    public void Remove_ForgetsThePathAndPersists()
    {
        HistoryStore store = Store();
        store.Add(@"C:\One");
        store.Add(@"C:\Two");

        store.Remove(@"c:\one");

        Assert.Equal([@"C:\Two"], store.Items);
        Assert.Equal([@"C:\Two"], new HistoryStore(_file).Items);
    }

    [Fact]
    public void IsKnownMissing_ChecksLocalPathsOnly()
    {
        string existing = Path.Combine(_directory, "here.txt");
        File.WriteAllText(existing, "x");

        Assert.False(HistoryStore.IsKnownMissing(existing));
        Assert.False(HistoryStore.IsKnownMissing(_directory));
        Assert.True(HistoryStore.IsKnownMissing(Path.Combine(_directory, "gone.txt")));
        Assert.False(HistoryStore.IsKnownMissing(@"\\server\share\gone.txt"));
        Assert.False(HistoryStore.IsKnownMissing("relative.txt"));
    }

    [Fact]
    public void Add_TrimsToTheLimit()
    {
        HistoryStore store = Store();

        for (int i = 0; i < HistoryStore.MaxEntries + 5; i++)
        {
            store.Add($@"C:\file{i}.txt");
        }

        Assert.Equal(HistoryStore.MaxEntries, store.Items.Length);
        Assert.Equal($@"C:\file{HistoryStore.MaxEntries + 4}.txt", store.Items[0]);
    }

    [Fact]
    public void History_SurvivesANewStoreInstance()
    {
        Store().Add(@"C:\Recent.txt");

        Assert.Equal([@"C:\Recent.txt"], Store().Items);
    }

    [Fact]
    public void MissingFile_ReadsAsEmpty()
    {
        Assert.Empty(Store().Items);
    }
}
