using Glide.Core.Search;

namespace Glide.Core.Tests;

public class SearchEngineTests
{
    private static FileEntry File(string path) => new(path, IsDirectory: false, SizeBytes: 0, LastWriteUtc: default);

    private static FileEntry Dir(string path) => new(path, IsDirectory: true, SizeBytes: 0, LastWriteUtc: default);

    [Fact]
    public void FindsEntryByFileName()
    {
        FileEntry[] entries =
        [
            File(@"C:\Users\Asus\notes.txt"),
            File(@"C:\Users\Asus\report.docx"),
        ];

        IReadOnlyList<SearchResult> results = SearchEngine.Search(entries, "notes");

        SearchResult only = Assert.Single(results);
        Assert.Equal(@"C:\Users\Asus\notes.txt", only.Entry.Path);
    }

    [Fact]
    public void NameMatchRanksAbovePathOnlyMatch()
    {
        FileEntry[] entries =
        [
            File(@"C:\budget\salary.xlsx"),
            File(@"C:\docs\budget.xlsx"),
        ];

        IReadOnlyList<SearchResult> results = SearchEngine.Search(entries, "budget");

        Assert.Equal(2, results.Count);
        Assert.Equal(@"C:\docs\budget.xlsx", results[0].Entry.Path);
    }

    [Fact]
    public void EveryTermMustMatch()
    {
        FileEntry[] entries =
        [
            File(@"C:\work\annual-report.txt"),
            File(@"C:\work\annual-summary.txt"),
        ];

        IReadOnlyList<SearchResult> results = SearchEngine.Search(entries, "annual report");

        SearchResult only = Assert.Single(results);
        Assert.Equal(@"C:\work\annual-report.txt", only.Entry.Path);
    }

    [Fact]
    public void TermsMayMatchAcrossNameAndPath()
    {
        FileEntry[] entries = [File(@"C:\Users\Asus\Documents\report.pdf")];

        Assert.Single(SearchEngine.Search(entries, "doc rep"));
    }

    [Fact]
    public void SearchIsCaseInsensitive()
    {
        FileEntry[] entries = [File(@"C:\Users\Asus\README.md")];

        Assert.Single(SearchEngine.Search(entries, "ReAdMe"));
    }

    [Fact]
    public void RespectsLimit()
    {
        FileEntry[] entries = Enumerable.Range(1, 10)
            .Select(i => File($@"C:\data\file{i}.txt"))
            .ToArray();

        Assert.Equal(3, SearchEngine.Search(entries, "file", limit: 3).Count);
    }

    [Fact]
    public void EmptyQueryPreservesInputOrder()
    {
        FileEntry[] entries =
        [
            File(@"C:\c.txt"),
            File(@"C:\a.txt"),
            Dir(@"C:\b"),
        ];

        IReadOnlyList<SearchResult> results = SearchEngine.Search(entries, "   ");

        Assert.Equal(entries.Select(e => e.Path), results.Select(r => r.Entry.Path));
        Assert.All(results, r => Assert.Equal(0, r.Score));
    }

    [Fact]
    public void TiesAreOrderedDeterministically()
    {
        FileEntry[] entries =
        [
            File(@"C:\b\notes.txt"),
            File(@"C:\a\notes.txt"),
        ];

        IReadOnlyList<SearchResult> first = SearchEngine.Search(entries, "notes");
        IReadOnlyList<SearchResult> second = SearchEngine.Search(entries, "notes");

        Assert.Equal(first.Select(r => r.Entry.Path), second.Select(r => r.Entry.Path));
        Assert.Equal(@"C:\a\notes.txt", first[0].Entry.Path);
    }

    [Fact]
    public void KeepsDirectoryFlag()
    {
        FileEntry[] entries = [Dir(@"C:\Users\Asus\Downloads")];

        SearchResult only = Assert.Single(SearchEngine.Search(entries, "down"));
        Assert.True(only.Entry.IsDirectory);
    }

    [Fact]
    public void ReportsMatchPositionsForHighlighting()
    {
        FileEntry[] entries = [File(@"C:\Users\Asus\notes.txt")];

        SearchResult only = Assert.Single(SearchEngine.Search(entries, "notes"));

        Assert.Equal([0, 1, 2, 3, 4], only.NamePositions);
    }

    [Fact]
    public void NonMatchingQueryReturnsEmpty()
    {
        FileEntry[] entries = [File(@"C:\Users\Asus\notes.txt")];

        Assert.Empty(SearchEngine.Search(entries, "zzzz"));
    }
}
