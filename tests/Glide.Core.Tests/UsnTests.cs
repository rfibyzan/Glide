using System.Buffers.Binary;
using System.Text;
using Glide.Core.Index.Usn;
using Glide.Core.Search;

namespace Glide.Core.Tests;

public class UsnTests
{
    private static byte[] Record(ulong fileId, ulong parentId, string name, uint attributes = 0, ushort major = 2)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(name);
        int length = (UsnRecordParser.V2HeaderSize + encoded.Length + 7) & ~7;
        byte[] record = new byte[length];

        BinaryPrimitives.WriteInt32LittleEndian(record, length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), major);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), parentId);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)encoded.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), UsnRecordParser.V2HeaderSize);
        encoded.CopyTo(record, UsnRecordParser.V2HeaderSize);

        return record;
    }

    [Fact]
    public void ReadsBackToBackRecords()
    {
        byte[] buffer = [.. Record(10, 5, "Users", 0x10), .. Record(11, 10, "report.txt")];
        int offset = 0;

        Assert.True(UsnRecordParser.TryRead(buffer, ref offset, out UsnEntry users));
        Assert.Equal("Users", users.Name);
        Assert.Equal(10UL, users.FileId);
        Assert.Equal(5UL, users.ParentId);
        Assert.True(users.IsDirectory);

        Assert.True(UsnRecordParser.TryRead(buffer, ref offset, out UsnEntry report));
        Assert.Equal("report.txt", report.Name);
        Assert.False(report.IsDirectory);
        Assert.Equal(buffer.Length, offset);

        Assert.False(UsnRecordParser.TryRead(buffer, ref offset, out _));
    }

    [Fact]
    public void SkipsRecordsThatAreNotVersionTwo()
    {
        byte[] buffer = [.. Record(10, 5, "Users", 0x10, major: 3), .. Record(11, 10, "report.txt")];
        int offset = 0;

        Assert.False(UsnRecordParser.TryRead(buffer, ref offset, out _));
        Assert.True(UsnRecordParser.TryRead(buffer, ref offset, out UsnEntry report));
        Assert.Equal("report.txt", report.Name);
    }

    [Fact]
    public void RejectsARecordThatRunsPastTheBuffer()
    {
        byte[] buffer = Record(10, 5, "Users", 0x10);
        int offset = 0;

        Assert.False(UsnRecordParser.TryRead(buffer.AsSpan(0, buffer.Length - 4), ref offset, out _));
        Assert.Equal(0, offset);
    }

    private static Dictionary<ulong, UsnEntry> Tree()
    {
        return new Dictionary<ulong, UsnEntry>
        {
            [UsnPathResolver.RootFileId] = new(UsnPathResolver.RootFileId, 0, 0x10, "."),
            [10] = new(10, 5, 0x10, "Users"),
            [11] = new(11, 10, 0x10, "Asus"),
            [12] = new(12, 11, 0, "report.txt"),
            [13] = new(13, 11, 0x10, "Documents"),
            [14] = new(14, 13, 0, "notes.md"),
            [15] = new(15, 999, 0, "orphan.txt"),
            [16] = new(16, 5, 0x10, "$Extend"),
            [17] = new(17, 16, 0, "$UsnJrnl"),
        };
    }

    [Fact]
    public void RebuildsPathsFromParentLinks()
    {
        List<FileEntry> entries = [.. UsnPathResolver.Resolve(Tree(), @"C:\")];

        Assert.Contains(entries, e => e.Path == @"C:\Users\Asus\report.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Path == @"C:\Users\Asus\Documents\notes.md" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Path == @"C:\Users\Asus\Documents" && e.IsDirectory);
        Assert.Contains(entries, e => e.Path == @"C:\Users");
    }

    [Fact]
    public void DropsFilesWhoseAncestorsAreMissing()
    {
        List<FileEntry> entries = [.. UsnPathResolver.Resolve(Tree(), @"C:\")];

        Assert.DoesNotContain(entries, e => e.Path.Contains("orphan", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Path == @"C:\");
    }

    [Fact]
    public void SkipsNtfsMetadataDirectories()
    {
        List<FileEntry> entries = [.. UsnPathResolver.Resolve(Tree(), @"C:\")];

        Assert.DoesNotContain(entries, e => e.Path.Contains("$Extend", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsARootWithoutATrailingSeparator()
    {
        List<FileEntry> entries = [.. UsnPathResolver.Resolve(Tree(), "C:")];

        Assert.Contains(entries, e => e.Path == @"C:\Users\Asus\report.txt");
    }

    [Fact]
    public void SurvivesAParentCycle()
    {
        var cycle = new Dictionary<ulong, UsnEntry>
        {
            [20] = new(20, 21, 0x10, "a"),
            [21] = new(21, 20, 0x10, "b"),
            [22] = new(22, 20, 0, "file.txt"),
        };

        Assert.Empty(UsnPathResolver.Resolve(cycle, @"C:\"));
    }

    [Fact]
    public void ResolvedPathsRankThroughTheNormalIndex()
    {
        var entries = UsnPathResolver.Resolve(Tree(), @"C:\").ToList();
        IReadOnlyList<SearchResult> results = SearchEngine.Search(entries, "notes", 5);

        Assert.Single(results);
        Assert.Equal(@"C:\Users\Asus\Documents\notes.md", results[0].Entry.Path);
    }
}
