using Glide.Core.Index;
using Glide.Core.Index.Usn;
using Glide.Core.Search;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

public sealed class QueryIntentTests : IDisposable
{
    private static readonly SearchFilterSettings[] Filters = SearchFilterSettings.Defaults();
    private static readonly WebSearchSettings[] Webs = WebSearchSettings.Defaults();

    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\",
        @"C:\Users\",
        @"C:\Users",
        @"C:\Program Files\",
        @"C:\Program Files",
        @"D:\",
        @"D:\Projects\",
    };

    private readonly string _index = Path.Combine(Path.GetTempPath(), $"glide-intent-{Guid.NewGuid():N}.gidx");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"glide-browse-{Guid.NewGuid():N}");

    public void Dispose()
    {
        File.Delete(_index);
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static QueryIntent Parse(string query) =>
        QueryIntentParser.Parse(query, Filters, Webs, Folders.Contains);

    [Fact]
    public void PlainTextIsAnUnconstrainedSearch()
    {
        QueryIntent intent = Parse("budget 2024");

        Assert.Equal(QueryKind.Search, intent.Kind);
        Assert.Equal("budget 2024", intent.Text);
        Assert.Null(intent.Constraints);
    }

    [Fact]
    public void WebKeywordBuildsAnEscapedUrl()
    {
        QueryIntent intent = Parse("gg c# & .net");

        Assert.Equal(QueryKind.Web, intent.Kind);
        Assert.Equal("Google", intent.FilterName);
        Assert.Equal("https://www.google.com/search?q=c%23%20%26%20.net", intent.WebUrl);
    }

    [Fact]
    public void WebKeywordAloneIsStillAFileSearch()
    {
        Assert.Equal(QueryKind.Search, Parse("gg").Kind);
        Assert.Equal(QueryKind.Search, Parse("gg ").Kind);
    }

    [Fact]
    public void FilterKeywordConstrainsKindAndExtensions()
    {
        QueryIntent folders = Parse("folder: glide");
        Assert.Equal("glide", folders.Text);
        Assert.True(folders.Constraints!.FoldersOnly);
        Assert.Equal("Folders", folders.FilterName);

        QueryIntent docs = Parse("doc:report");
        Assert.Equal("report", docs.Text);
        Assert.True(docs.Constraints!.FilesOnly);
        Assert.Contains("pdf", docs.Constraints.Extensions);
    }

    [Fact]
    public void ExtTokenTakesAList()
    {
        QueryIntent intent = Parse("invoice ext:pdf,.DOCX");

        Assert.Equal("invoice", intent.Text);
        Assert.Equal(["pdf", "docx"], intent.Constraints!.Extensions);
    }

    [Fact]
    public void UnknownKeywordStaysATerm()
    {
        QueryIntent intent = Parse("http:server");

        Assert.Equal("http:server", intent.Text);
        Assert.Null(intent.Constraints);
    }

    [Fact]
    public void PathFollowedByWordBrowsesTheFolder()
    {
        QueryIntent intent = Parse(@"C:\Us");

        Assert.Equal(QueryKind.Browse, intent.Kind);
        Assert.Equal(@"C:\", intent.Folder);
        Assert.Equal("Us", intent.Text);
    }

    [Fact]
    public void FolderWithoutTrailingSlashBrowsesItself()
    {
        QueryIntent intent = Parse(@"C:\Users");

        Assert.Equal(QueryKind.Browse, intent.Kind);
        Assert.Equal(@"C:\Users\", intent.Folder);
        Assert.Equal(string.Empty, intent.Text);
    }

    [Fact]
    public void PathWithSpacesResolvesToTheLongestFolder()
    {
        QueryIntent intent = Parse(@"C:\Program Files\ report");

        Assert.Equal(QueryKind.Search, intent.Kind);
        Assert.Equal("report", intent.Text);
        Assert.Equal(@"C:\Program Files\", intent.Constraints!.Scope);
        Assert.Equal('C', intent.Constraints.ScopeDrive);
    }

    [Fact]
    public void ScopeAndFilterCombine()
    {
        QueryIntent intent = Parse(@"D:\Projects\ doc: spec");

        Assert.Equal("spec", intent.Text);
        Assert.Equal(@"D:\Projects\", intent.Constraints!.Scope);
        Assert.True(intent.Constraints.FilesOnly);
    }

    [Theory]
    [InlineData(@"D:\Projects\glide\README.md", false, true)]
    [InlineData(@"d:\projects\glide\readme.MD", false, true)]
    [InlineData(@"D:\Projects", true, false)]
    [InlineData(@"D:\ProjectsOld\a.md", false, false)]
    [InlineData(@"C:\Projects\a.md", false, false)]
    [InlineData(@"D:\Projects\glide\notes.txt", false, false)]
    [InlineData(@"D:\Projects\glide\docs.md", true, false)]
    public void ConstraintsAgreeOnCharsAndBytes(string path, bool isDirectory, bool expected)
    {
        var constraints = new SearchConstraints(false, false, ["md"], @"D:\Projects");

        Assert.Equal(expected, constraints.Accepts(path, isDirectory));
        Assert.Equal(expected, constraints.Accepts(System.Text.Encoding.UTF8.GetBytes(path), isDirectory));
    }

    [Fact]
    public void IndexSearchHonoursConstraints()
    {
        IndexWriter.Write(_index,
        [
            new FileEntry(@"C:\Users\Asus\Documents\report.md", false, 0, default),
            new FileEntry(@"C:\Users\Asus\Documents\report.pdf", false, 0, default),
            new FileEntry(@"C:\Users\Asus\Documents\reports", true, 0, default),
            new FileEntry(@"D:\Archive\report.pdf", false, 0, default),
        ]);

        var pdfOnC = new SearchConstraints(false, false, ["pdf"], @"C:\Users");
        IReadOnlyList<SearchResult> pdfs = IndexSearchSource.Search(_index, "report", 10, pdfOnC);
        Assert.Equal([@"C:\Users\Asus\Documents\report.pdf"], pdfs.Select(r => r.Entry.Path));

        var folders = new SearchConstraints(true, false, null, null);
        IReadOnlyList<SearchResult> dirs = IndexSearchSource.Search(_index, "report", 10, folders);
        Assert.Equal([@"C:\Users\Asus\Documents\reports"], dirs.Select(r => r.Entry.Path));

        // A filter with no terms lists matches instead of throwing.
        IReadOnlyList<SearchResult> allFolders = IndexSearchSource.Search(_index, "", 10, folders);
        Assert.Single(allFolders);
    }

    [Fact]
    public void ScopedSearchSkipsOtherVolumesIndexFiles()
    {
        var scope = new SearchConstraints(false, false, null, @"D:\Projects");

        Assert.False(IndexSearchSource.CoversScope(@"C:\ProgramData\Glide\index-c.gidx", scope));
        Assert.True(IndexSearchSource.CoversScope(@"C:\ProgramData\Glide\index-d.gidx", scope));
        Assert.True(IndexSearchSource.CoversScope(@"C:\bench\bench-100k.gidx", scope));
        Assert.True(IndexSearchSource.CoversScope(@"C:\ProgramData\Glide\index-c.gidx", null));
    }

    [Fact]
    public void FolderBrowserListsFoldersFirstAndFilters()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Users"));
        Directory.CreateDirectory(Path.Combine(_folder, "zeta"));
        File.WriteAllText(Path.Combine(_folder, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(_folder, "music.mp3"), "m");

        List<SearchResult> all = FolderBrowser.List(_folder, null, 10);
        Assert.Equal(["Users", "zeta", "alpha.txt", "music.mp3"], all.Select(r => Path.GetFileName(r.Entry.Path)));

        List<SearchResult> us = FolderBrowser.List(_folder, "us", 10);
        Assert.Equal("Users", Path.GetFileName(us[0].Entry.Path));
        Assert.Contains(us, r => Path.GetFileName(r.Entry.Path) == "music.mp3");
        Assert.DoesNotContain(us, r => Path.GetFileName(r.Entry.Path) == "alpha.txt");
    }

    [Fact]
    public void FolderBrowserTreatsAMissingFolderAsEmpty()
    {
        Assert.Empty(FolderBrowser.List(Path.Combine(_folder, "missing"), "x", 10));
    }
}

public sealed class ChangeFilterTests
{
    private const ulong Root = 5;

    [Theory]
    [InlineData(@"C:\ProgramData\Glide", true)]
    [InlineData(@"C:\ProgramData\Glide\sub", true)]
    [InlineData(@"C:\ProgramData\GlideOther", false)]
    [InlineData(@"C:\Users\Asus\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\Asus\AppData\Local\Temp\pip-123", true)]
    [InlineData(@"C:\Windows\Prefetch", true)]
    [InlineData(@"C:\Users\Asus\Documents", false)]
    [InlineData(@"D:\Kodingan\Temp", false)]
    public void QuietPathsCoverNoiseAndTheIndexFolder(string path, bool quiet)
    {
        Assert.Equal(quiet, ChangeFilter.IsQuietPath(path, @"C:\ProgramData\Glide"));
    }

    [Fact]
    public void DropsRecordsInQuietFoldersAndInheritsNewSubfolders()
    {
        const ulong temp = 40;
        var filter = new ChangeFilter([temp]);

        Assert.False(filter.IsRelevant(new UsnEntry(100, temp, 0, "x.tmp")));
        Assert.True(filter.IsRelevant(new UsnEntry(101, Root, 0, "report.md")));

        // A folder created inside Temp, and a file created inside that folder.
        Assert.False(filter.IsRelevant(new UsnEntry(200, temp, UsnEntry.DirectoryAttribute, "pip-build")));
        Assert.False(filter.IsRelevant(new UsnEntry(201, 200, 0, "setup.py")));
    }

    [Fact]
    public void SequenceBitsDoNotHideAQuietParent()
    {
        var filter = new ChangeFilter([40]);
        ulong parentWithSequence = (7UL << 48) | 40;

        Assert.False(filter.IsRelevant(new UsnEntry(100, parentWithSequence, 0, "x.tmp")));
    }

    [Fact]
    public void ResolverReportsSkippedDirectoriesAndReusesCachedPaths()
    {
        ulong rootId = (5UL << 48) | 5UL;
        var directories = new Dictionary<ulong, UsnEntry>
        {
            [10] = new(10, rootId, 0x10, "Users"),
            [11] = new(11, 10, 0x10, "Asus"),
            [12] = new(12, rootId, 0x10, "$Recycle.Bin"),
        };

        var skipped = new List<ulong>();
        Dictionary<ulong, string> resolved = UsnPathResolver.ResolveDirectoryPaths(directories, @"C:\", null, skipped);

        Assert.Equal(@"C:\Users\Asus", resolved[11]);
        Assert.Equal([12UL], skipped);
    }
}

public sealed class SearchSettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"glide-settings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void OldFilesGetDefaultFiltersAndWebSearches()
    {
        File.WriteAllText(_file, """{ "theme": "dark" }""");

        AppSettings settings = SettingsManager.Load(_file);

        Assert.Contains(settings.SearchFilters, f => f.Keyword == "doc");
        Assert.Contains(settings.WebSearches, w => w.Keyword == "gg");
        Assert.True(settings.Hotkey.PressAgainHides);
        Assert.True(settings.HideWhenLosingFocus);
        Assert.Empty(settings.CustomCommands);
        Assert.Equal("\"%1\"", settings.OpenFolderWith.Arguments);
        Assert.Equal("Ctrl+G", settings.Integration.QuickSwitchHotkey);
    }

    [Fact]
    public void InvalidEntriesAreDroppedAndEmptyListsKept()
    {
        File.WriteAllText(_file, """
            {
              "searchFilters": [],
              "webSearches": [
                { "keyword": "x", "name": "", "url": "https://example.com/?q={query}" },
                { "keyword": "bad", "name": "No placeholder", "url": "https://example.com/" }
              ]
            }
            """);

        AppSettings settings = SettingsManager.Load(_file);

        Assert.Empty(settings.SearchFilters);
        WebSearchSettings web = Assert.Single(settings.WebSearches);
        Assert.Equal("x", web.Name);
    }
}

public sealed class RankingRuleTests : IDisposable
{
    private readonly string _index = Path.Combine(Path.GetTempPath(), $"glide-rank-{Guid.NewGuid():N}.gidx");

    public void Dispose() => File.Delete(_index);

    [Fact]
    public void AStartMenuBoostDoesNotLiftAScatteredMatch()
    {
        string startMenu = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows\Start Menu\Programs");
        IndexWriter.Write(_index,
        [
            new FileEntry(startMenu + @"\ReelSmart Motion Blur for After Effects and Premiere Pro.lnk", false, 0, default),
            new FileEntry(@"D:\Macro Game", true, 0, default),
        ]);

        IReadOnlyList<SearchResult> results = IndexSearchSource.Search(_index, "macro", 10);

        Assert.Equal(@"D:\Macro Game", results[0].Entry.Path);
    }

    [Fact]
    public void AStartMenuBoostStillLiftsACleanMatch()
    {
        string startMenu = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows\Start Menu\Programs");
        IndexWriter.Write(_index,
        [
            new FileEntry(@"D:\Downloads\notepad-notes.txt", false, 0, default),
            new FileEntry(startMenu + @"\Notepad++.lnk", false, 0, default),
        ]);

        IReadOnlyList<SearchResult> results = IndexSearchSource.Search(_index, "notepad", 10);

        Assert.EndsWith("Notepad++.lnk", results[0].Entry.Path);
    }

    [Fact]
    public void FoldersUnderADotFolderRankLast()
    {
        IndexWriter.Write(_index,
        [
            new FileEntry(@"C:\Users\Asus\.cargo\registry\src\serde\tests\macros", true, 0, default),
            new FileEntry(@"D:\Projects\macros", true, 0, default),
        ]);

        IReadOnlyList<SearchResult> results = IndexSearchSource.Search(_index, "macros", 10);

        Assert.Equal(@"D:\Projects\macros", results[0].Entry.Path);
    }

    [Fact]
    public void RecentlyOpenedEntriesRiseAndMissingOnesAreAdded()
    {
        SearchResult[] fromIndex =
        [
            new(new FileEntry(@"D:\Tools\macro", true, 0, default), 1140, []),
            new(new FileEntry(@"D:\Old\Macro Game", true, 0, default), 1126, []),
        ];

        SearchResult[] ranked = RecentBoost.Apply(fromIndex, [@"D:\Macro Game", @"D:\Old\Macro Game"], "macro", 10);

        // Opened most recently, and found even though the index scan missed it.
        Assert.Equal(@"D:\Macro Game", ranked[0].Entry.Path);
        Assert.Equal(@"D:\Old\Macro Game", ranked[1].Entry.Path);
        Assert.Equal(@"D:\Tools\macro", ranked[2].Entry.Path);
    }

    [Fact]
    public void RecentBoostRespectsFilters()
    {
        var docsOnly = new SearchConstraints(false, false, ["pdf"], null);

        SearchResult[] ranked = RecentBoost.Apply([], [@"D:\Macro Game", @"D:\macro.pdf"], "macro", 10, docsOnly);

        Assert.Equal([@"D:\macro.pdf"], ranked.Select(r => r.Entry.Path));
    }
}
