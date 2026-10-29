using System.Buffers.Binary;
using System.Text;

namespace Glide.Core.Index.Usn;

/// <summary>
/// One record from the volume's USN journal: a file id, the id of its parent
/// directory, and the file name on its own. Full paths are reconstructed from
/// the parent links, which is why an enumeration has to be held in memory while
/// it is resolved.
/// </summary>
public readonly record struct UsnEntry(ulong FileId, ulong ParentId, uint Attributes, string Name)
{
    public const uint DirectoryAttribute = 0x10;

    public bool IsDirectory => (Attributes & DirectoryAttribute) != 0;
}

/// <summary>
/// Parses the USN_RECORD_V2 layout returned by FSCTL_ENUM_USN_DATA. Records are
/// packed back to back and each one starts with its own length, so a buffer can
/// be walked without knowing where the kernel split it.
/// </summary>
public static class UsnRecordParser
{
    public const int V2HeaderSize = 60;
    public const ushort V2MajorVersion = 2;

    public static bool TryRead(ReadOnlySpan<byte> buffer, ref int offset, out UsnEntry entry)
    {
        entry = default;

        if (offset < 0 || offset + 4 > buffer.Length)
        {
            return false;
        }

        int recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
        if (recordLength < V2HeaderSize || offset + recordLength > buffer.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> record = buffer.Slice(offset, recordLength);
        offset += recordLength;

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        if (major != V2MajorVersion)
        {
            // V3 and V4 records carry 128-bit ids. They are skipped rather than
            // misread as V2, which would produce two bogus file ids.
            return false;
        }

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);

        if (nameLength == 0 || nameOffset + nameLength > recordLength)
        {
            return false;
        }

        entry = new UsnEntry(
            BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[52..]),
            Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength)));

        return true;
    }
}
