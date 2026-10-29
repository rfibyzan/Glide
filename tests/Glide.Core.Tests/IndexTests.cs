using Glide.Core.Index;
using Glide.Core.Search;

namespace Glide.Core.Tests;

public sealed class IndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"glide-index-{Guid.NewGuid():N}.gidx");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    private static FileEntry FileAt(string path) => new(path, false, 0, default);

    private static FileEntry Dir(string path) => new(path, true, 0, default);

    private static readonly FileEntry[] Entries =
    [
        FileAt(@"C:\Users\Asus\Documents\report-2024.md"),
        FileAt(@"C:\Users\Asus\Documents\report.md"),
        FileAt(@"C:\Users\Asus\Downloads\vacation\photo.png"),
        FileAt(@"D:\Projects\glide\src\Glide.Core\Search\SearchEngine.cs"),
        FileAt(@"D:\Projects\glide\README.md"),
        Dir(@"D:\Projects\glide\docs"),
        Dir(@"C:\Program Files"),
        FileAt(@"C:\Users\Asus\Documents\notes.txt"),
        FileAt(@"C:\Users\Asus\Documents\catatan-harian.txt"),
        FileAt(@"C:\Users\Asus\Documents\résumé-final.pdf"),
        FileAt(@"C:\Users\Asus\AppData\Local\Temp\glide-trace.log"),
        FileAt(@"C:\Windows\System32\drivers\etc\hosts"),
    ];

    public static TheoryData<string> Queries => new()
    {
        "report",
        "re",
        "docs",
        "glide",
        "search engine",
        "phot",
        "zzz",
        "txt",
        "catatan",
        "résumé",
        "users documents",
        "",
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void IndexSearchRanksLikeTheInMemoryEngine(string query)
    {
        IndexWriter.Write(_path, Entries);

        IReadOnlyList<SearchResult> expected = SearchEngine.Search(Entries, query, 5);
        IReadOnlyList<SearchResult> actual = IndexSearchSource.Search(_path, query, 5);

        Assert.Equal(expected.Select(r => r.Entry.Path), actual.Select(r => r.Entry.Path));
        Assert.Equal(expected.Select(r => r.Score), actual.Select(r => r.Score));
        Assert.Equal(expected.Select(r => r.Entry.IsDirectory), actual.Select(r => r.Entry.IsDirectory));
    }

    [Fact]
    public void RoundTripsCountAndDirectories()
    {
        int written = IndexWriter.Write(_path, Entries);

        Assert.Equal(Entries.Length, written);
        Assert.Equal(Entries.Length, IndexSearchSource.Count(_path));

        IReadOnlyList<SearchResult> docs = IndexSearchSource.Search(_path, "docs", 5);
        Assert.Contains(docs, r => r.Entry.IsDirectory && r.Entry.Path == @"D:\Projects\glide\docs");
    }

    [Fact]
    public void EmptyQueryReturnsFirstEntriesInFileOrder()
    {
        IndexWriter.Write(_path, Entries);

        IReadOnlyList<SearchResult> results = IndexSearchSource.Search(_path, "", 3);

        Assert.Equal(Entries.Take(3).Select(e => e.Path), results.Select(r => r.Entry.Path));
    }

    [Fact]
    public void RespectsLimitAndDropsNonMatches()
    {
        IndexWriter.Write(_path, Entries);

        Assert.Equal(2, IndexSearchSource.Search(_path, "report", 2).Count);
        Assert.Empty(IndexSearchSource.Search(_path, "nothing-matches-this", 10));
        Assert.Empty(IndexSearchSource.Search(_path, "report", 0));
    }

    [Fact]
    public void OverwritesAnExistingIndex()
    {
        IndexWriter.Write(_path, Entries);
        IndexWriter.Write(_path, [FileAt(@"C:\only.txt")]);

        Assert.Equal(1, IndexSearchSource.Count(_path));
        Assert.Single(IndexSearchSource.Search(_path, "only"));
    }

    [Fact]
    public void RejectsFilesThatAreNotIndexes()
    {
        File.WriteAllText(_path, "not an index");

        Assert.Throws<InvalidDataException>(() => IndexSearchSource.Search(_path, "x"));
        Assert.False(IndexFile.IsIndexFile(_path));
    }

    [Fact]
    public void RejectsTruncatedIndexFiles()
    {
        IndexWriter.Write(_path, Entries);

        byte[] bytes = File.ReadAllBytes(_path);
        File.WriteAllBytes(_path, bytes[..(IndexFile.HeaderSize + 2)]);

        Assert.Throws<InvalidDataException>(() => IndexSearchSource.Search(_path, "report"));
    }

    [Fact]
    public void HandlesPathsLongerThanTheReadBufferBoundary()
    {
        string deep = @"C:\" + string.Join('\\', Enumerable.Repeat("segment", 200)) + @"\target-file.txt";
        IndexWriter.Write(_path, [FileAt(deep)]);

        IReadOnlyList<SearchResult> results = IndexSearchSource.Search(_path, "target");

        Assert.Single(results);
        Assert.Equal(deep, results[0].Entry.Path);
    }

    [Fact]
    public void ServesQueriesWhileTheFileIsOpenForRewrite()
    {
        IndexWriter.Write(_path, Entries);

        using var writer = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(2, IndexSearchSource.Search(_path, "report", 5).Count);
    }
}
