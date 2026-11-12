using System.Buffers.Binary;
using System.Text;
using Glide.Core.Index;
using Glide.Core.Index.Usn;
using Glide.Core.Search;

namespace Glide.Core.Tests;

public sealed class IndexDeltaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GlideDelta_" + Guid.NewGuid().ToString("N"));
    private readonly string _index;

    public IndexDeltaTests()
    {
        Directory.CreateDirectory(_directory);
        _index = Path.Combine(_directory, "index-c.gidx");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static FileEntry FileAt(string path) => new(path, false, 0, default);

    private void WriteIndex(params string[] paths) => IndexWriter.Write(_index, paths.Select(FileAt));

    [Fact]
    public void SaveAndParse_RoundTripsTheLastChangePerPath()
    {
        var delta = new IndexDelta();
        delta.Add(@"C:\a\new.txt", isDirectory: false);
        delta.Add(@"C:\a\folder", isDirectory: true);
        delta.Remove(@"C:\a\old.txt");
        delta.Add(@"C:\a\OLD.txt", isDirectory: false); // same path, other case: re-created

        string file = IndexDelta.PathFor(_index);
        delta.Save(file);
        IndexDelta read = IndexDelta.Parse(File.ReadAllText(file));

        Assert.Equal(3, read.Count);
        Assert.Equal(0, read.RemovedCount);
        Assert.Equal(
            [(@"C:\a\new.txt", false), (@"C:\a\folder", true), (@"C:\a\OLD.txt", false)],
            read.Added().ToArray());
    }

    [Fact]
    public void Save_OfAnEmptyDeltaDeletesTheFile()
    {
        string file = IndexDelta.PathFor(_index);
        File.WriteAllText(file, "glide-delta 1\n-\tC:\\x\n");

        new IndexDelta().Save(file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void SearchMany_ShowsAddedFilesAndHidesRemovedOnes()
    {
        WriteIndex(@"C:\Users\me\Downloads\download.jpeg", @"C:\Users\me\Documents\report.md");

        var delta = new IndexDelta();
        delta.Remove(@"C:\Users\me\Downloads\download.jpeg");
        delta.Add(@"C:\Users\me\Downloads\download.jpg", isDirectory: false);
        delta.Save(IndexDelta.PathFor(_index));

        string[] found = [.. IndexSearchSource.SearchMany([_index], "download", 10).Select(r => r.Entry.Path)];

        Assert.Equal([@"C:\Users\me\Downloads\download.jpg"], found);
    }

    [Fact]
    public void SearchMany_DoesNotListAPathTwiceWhenTheIndexAlreadyHasIt()
    {
        WriteIndex(@"C:\notes\plan.txt");

        var delta = new IndexDelta();
        delta.Add(@"C:\notes\plan.txt", isDirectory: false);
        delta.Save(IndexDelta.PathFor(_index));

        Assert.Single(IndexSearchSource.SearchMany([_index], "plan", 10));
    }

    [Fact]
    public void SearchMany_AppliesConstraintsToAddedPaths()
    {
        WriteIndex(@"C:\x\other.txt");

        var delta = new IndexDelta();
        delta.Add(@"C:\x\budget.xlsx", isDirectory: false);
        delta.Add(@"C:\x\budget", isDirectory: true);
        delta.Save(IndexDelta.PathFor(_index));

        var foldersOnly = new SearchConstraints(foldersOnly: true, filesOnly: false, extensions: null, scope: null);
        string[] found = [.. IndexSearchSource.SearchMany([_index], "budget", 10, foldersOnly).Select(r => r.Entry.Path)];

        Assert.Equal([@"C:\x\budget"], found);
    }

    [Fact]
    public void SearchMany_PicksUpADeltaWrittenAfterAnEarlierSearch()
    {
        WriteIndex(@"C:\x\alpha.txt");
        Assert.Empty(IndexSearchSource.SearchMany([_index], "brandnew", 10));

        var delta = new IndexDelta();
        delta.Add(@"C:\x\brandnew.txt", isDirectory: false);
        delta.Save(IndexDelta.PathFor(_index));

        Assert.Single(IndexSearchSource.SearchMany([_index], "brandnew", 10));
    }

    [Fact]
    public void Recorder_TurnsJournalRecordsIntoChanges()
    {
        var recorder = new IndexDeltaRecorder(id => id == 40 ? @"C:\Users\me\Downloads" : null);

        Assert.True(recorder.Apply(new UsnEntry(1, 40, 0, "a.crdownload", UsnEntry.ReasonFileCreate)));
        Assert.True(recorder.Apply(new UsnEntry(1, 40, 0, "a.crdownload", UsnEntry.ReasonRenameOldName)));
        Assert.True(recorder.Apply(new UsnEntry(1, 40, 0, "a.zip", UsnEntry.ReasonRenameNewName)));

        Assert.True(recorder.Delta.IsRemoved(@"C:\Users\me\Downloads\a.crdownload"));
        Assert.Equal([(@"C:\Users\me\Downloads\a.zip", false)], recorder.Delta.Added().ToArray());
        Assert.False(recorder.NeedsRebuild);
    }

    [Fact]
    public void Recorder_TreatsCreateAndDeleteInOneRecordAsGone()
    {
        var recorder = new IndexDeltaRecorder(_ => @"C:\t");

        recorder.Apply(new UsnEntry(1, 40, 0, "x.tmp", UsnEntry.ReasonFileCreate | UsnEntry.ReasonFileDelete));

        Assert.True(recorder.Delta.IsRemoved(@"C:\t\x.tmp"));
    }

    [Fact]
    public void Recorder_AsksForARebuildWhenAFolderMovesOrAParentIsGone()
    {
        var moved = new IndexDeltaRecorder(_ => @"C:\");
        moved.Apply(new UsnEntry(7, 5, UsnEntry.DirectoryAttribute, "Projects", UsnEntry.ReasonRenameOldName));
        Assert.True(moved.NeedsRebuild);
        Assert.True(moved.Delta.IsRemoved(@"C:\Projects"));

        var orphan = new IndexDeltaRecorder(_ => null);
        Assert.False(orphan.Apply(new UsnEntry(8, 99, 0, "lost.txt", UsnEntry.ReasonFileCreate)));
        Assert.True(orphan.NeedsRebuild);

        moved.Reset();
        Assert.False(moved.NeedsRebuild);
        Assert.Equal(0, moved.Delta.Count);
    }

    [Fact]
    public void Recorder_IgnoresRecordsThatChangeNoName()
    {
        var recorder = new IndexDeltaRecorder(_ => @"C:\");

        Assert.False(recorder.Apply(new UsnEntry(1, 5, 0, "data.db", Reason: 0x1 /* DATA_OVERWRITE */)));
        Assert.Equal(0, recorder.Delta.Count);
    }

    [Fact]
    public void Parser_ReadsTheReason()
    {
        byte[] encoded = Encoding.Unicode.GetBytes("a.txt");
        int length = (UsnRecordParser.V2HeaderSize + encoded.Length + 7) & ~7;
        byte[] record = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(record, length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 11);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), UsnEntry.ReasonRenameNewName);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)encoded.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), UsnRecordParser.V2HeaderSize);
        encoded.CopyTo(record, UsnRecordParser.V2HeaderSize);

        int offset = 0;
        Assert.True(UsnRecordParser.TryRead(record, ref offset, out UsnEntry entry));
        Assert.True(entry.IsAddition);
        Assert.False(entry.IsRemoval);
    }
}
