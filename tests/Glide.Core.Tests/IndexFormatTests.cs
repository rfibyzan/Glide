using Glide.Core.Index;
using Glide.Core.Search;

namespace Glide.Core.Tests;

public sealed class IndexFormatTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"glide-format-{Guid.NewGuid():N}.gidx");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    [Fact]
    public void FlagsRecordDirectoryAndAsciiPaths()
    {
        IndexWriter.Write(_path, [
            new FileEntry(@"C:\plain\file.txt", false, 0, default),
            new FileEntry(@"C:\plain\folder", true, 0, default),
            new FileEntry(@"C:\Users\Asus\résumé-final.pdf", false, 0, default),
        ]);

        (byte Flags, string Path)[] records = ReadRecords(_path);

        Assert.Equal(3, records.Length);
        Assert.Equal(IndexFile.AsciiPathFlag, records[0].Flags);
        Assert.Equal(IndexFile.AsciiPathFlag | IndexFile.DirectoryFlag, records[1].Flags);
        Assert.Equal(0, records[2].Flags);
    }

    [Fact]
    public void RejectsThePreviousFormatVersion()
    {
        IndexWriter.Write(_path, [new FileEntry(@"C:\only.txt", false, 0, default)]);

        byte[] bytes = File.ReadAllBytes(_path);
        "GLIDX001"u8.CopyTo(bytes);
        File.WriteAllBytes(_path, bytes);

        Assert.Throws<InvalidDataException>(() => IndexSearchSource.Count(_path));
    }

    private static (byte Flags, string Path)[] ReadRecords(string path)
    {
        using var handle = IndexFile.OpenHandle(path);
        IndexHeader header = IndexFile.ReadHeader(handle);
        byte[] raw = new byte[header.RecordsLength];
        RandomAccess.Read(handle, raw, header.RecordsOffset);

        var records = new List<(byte, string)>();
        int start = 0;

        while (start < raw.Length)
        {
            int nul = Array.IndexOf(raw, (byte)0, start + 1);
            Assert.True(nul > start, "record is not terminated");

            records.Add((raw[start], System.Text.Encoding.UTF8.GetString(raw, start + 1, nul - start - 1)));
            start = nul + 1;
        }

        return records.ToArray();
    }
}
