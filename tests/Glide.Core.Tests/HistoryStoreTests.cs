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
