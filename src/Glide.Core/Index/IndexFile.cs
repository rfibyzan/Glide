using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Glide.Core.Index;

/// <summary>
/// On-disk index format. One variable-length record per entry:
/// a flags byte, the UTF-8 path, and a NUL terminator. Paths are not stored as
/// strings, so a whole-drive index stays a file the reader can stream instead of
/// a heap graph that has to fit in memory.
/// </summary>
public static class IndexFile
{
    public const int HeaderSize = 40;
    public const uint Version = 2;
    public const byte DirectoryFlag = 1;
    public const byte AsciiPathFlag = 2;

    private static readonly byte[] Magic = "GLIDX002"u8.ToArray();

    public static void WriteHeader(Stream stream, uint count, long recordsOffset, long recordsLength)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();

        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], count);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)recordsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)recordsLength);

        stream.Position = 0;
        stream.Write(header);
    }

    public static IndexHeader ReadHeader(SafeFileHandle handle)
    {
        Span<byte> header = stackalloc byte[HeaderSize];

        if (RandomAccess.Read(handle, header, 0) < HeaderSize)
        {
            throw new InvalidDataException("Index file is shorter than its header.");
        }

        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Glide index file.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported index version {version}.");
        }

        return new IndexHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(header[24..]),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]));
    }

    public static bool IsIndexFile(string path)
    {
        try
        {
            using SafeFileHandle handle = OpenHandle(path);
            ReadHeader(handle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SafeFileHandle OpenHandle(string path)
    {
        return File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
    }
}

public readonly record struct IndexHeader(uint Count, long RecordsOffset, long RecordsLength);
