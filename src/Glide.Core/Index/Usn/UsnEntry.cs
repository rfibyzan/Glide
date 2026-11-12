using System.Buffers.Binary;
using System.Text;

namespace Glide.Core.Index.Usn;

/// <summary>
/// One record from the volume's USN journal: a file id, the id of its parent
/// directory, and the file name on its own. Full paths are reconstructed from
/// the parent links, which is why an enumeration has to be held in memory while
/// it is resolved. <see cref="Reason"/> is the USN_REASON mask of a journal
/// read; an enumeration of the MFT leaves it zero.
/// </summary>
public readonly record struct UsnEntry(ulong FileId, ulong ParentId, uint Attributes, string Name, uint Reason = 0)
{
    public const uint DirectoryAttribute = 0x10;

    public const uint ReasonFileCreate = 0x0000_0100;
    public const uint ReasonFileDelete = 0x0000_0200;
    public const uint ReasonRenameOldName = 0x0000_1000;
    public const uint ReasonRenameNewName = 0x0000_2000;

    public bool IsDirectory => (Attributes & DirectoryAttribute) != 0;

    /// <summary>The name stopped existing at this place: deleted, or renamed away.</summary>
    public bool IsRemoval => (Reason & (ReasonFileDelete | ReasonRenameOldName)) != 0;

    /// <summary>The name started existing at this place: created, or renamed to.</summary>
    public bool IsAddition => !IsRemoval && (Reason & (ReasonFileCreate | ReasonRenameNewName)) != 0;
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
            Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength)),
            BinaryPrimitives.ReadUInt32LittleEndian(record[40..]));

        return true;
    }
}
