using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glide.Core.Index.Usn;

/// <summary>
/// Reads a volume's USN change journal. Enumerating the journal gives every file
/// on the volume with its parent id in one pass, which is far faster than walking
/// directories, and the same handle can then report later changes instead of
/// rescanning.
///
/// Opening <c>\\.\C:</c> needs administrator rights or a service account. When
/// the handle cannot be opened, <see cref="Open"/> throws an
/// <see cref="IOException"/> carrying the Win32 error, so callers can fall back
/// to the folder scan.
/// </summary>
public sealed partial class UsnVolume : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint SequentialOnly = 0x08000000;

    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;

    private const int EnumerateBufferSize = 1 << 20;
    private const int JournalDataSize = 56;
    private const int ReadJournalDataSize = 40;

    private readonly SafeFileHandle _handle;

    private UsnVolume(SafeFileHandle handle, string device, string root)
    {
        _handle = handle;
        Device = device;
        Root = root;
    }

    /// <summary>Device path, for example <c>\\.\C:</c>.</summary>
    public string Device { get; }

    /// <summary>Volume prefix that resolved paths start with, for example <c>C:\</c>.</summary>
    public string Root { get; }

    public static UsnVolume Open(string volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        string letter = volume.TrimEnd(':', '\\').ToUpperInvariant();
        string device = $@"\\.\{letter}:";
        string root = letter + @":\";

        SafeFileHandle handle = CreateFile(
            device,
            GenericRead,
            ShareAll,
            nint.Zero,
            OpenExisting,
            BackupSemantics | SequentialOnly,
            nint.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Cannot open {device} for USN reading (Win32 error {error}). " +
                "Enumerating a volume's journal requires administrator rights.",
                new Win32Exception(error));
        }

        return new UsnVolume(handle, device, root);
    }

    /// <summary>
    /// Streams every record in the journal, oldest file id first. Returns the
    /// number of records handed to <paramref name="onEntry"/>.
    /// </summary>
    public long Enumerate(Action<UsnEntry> onEntry)
    {
        ArgumentNullException.ThrowIfNull(onEntry);

        Span<byte> request = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(request, 0);
        BinaryPrimitives.WriteInt64LittleEndian(request[8..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(request[16..], long.MaxValue);

        byte[] output = new byte[EnumerateBufferSize];
        long count = 0;
        ulong start = 0;

        while (true)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(request, start);

            int returned = DeviceIoControl(FsctlEnumUsnData, request, output);
            if (returned <= 8)
            {
                return count;
            }

            start = BinaryPrimitives.ReadUInt64LittleEndian(output);
            int offset = 8;
            ReadOnlySpan<byte> span = output.AsSpan(0, returned);

            while (UsnRecordParser.TryRead(span, ref offset, out UsnEntry entry))
            {
                onEntry(entry);
                count++;
            }
        }
    }

    public UsnJournalState QueryJournal()
    {
        byte[] output = new byte[JournalDataSize];
        int returned = DeviceIoControl(FsctlQueryUsnJournal, default, output);

        if (returned < 24)
        {
            throw new IOException($"Unexpected USN journal data size {returned}.");
        }

        return new UsnJournalState(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)));
    }

    /// <summary>
    /// Returns the records written since <paramref name="since"/>. The returned
    /// state can be stored and passed in again; it never moves backwards.
    /// </summary>
    public UsnJournalState ReadChanges(UsnJournalState since, Action<UsnEntry>? onEntry = null)
    {
        return ReadChanges(since, uint.MaxValue, new byte[EnumerateBufferSize], onEntry);
    }

    /// <summary>Reasons that change a name or an existence: the only ones an index of names cares about.</summary>
    public const uint NameChangeReasons =
        0x0000_0100 /* FILE_CREATE */ |
        0x0000_0200 /* FILE_DELETE */ |
        0x0000_1000 /* RENAME_OLD_NAME */ |
        0x0000_2000 /* RENAME_NEW_NAME */;

    /// <summary>A read buffer sized for polling; change batches between polls are small.</summary>
    public const int ChangeBufferSize = 64 * 1024;

    /// <summary>
    /// Returns the records written since <paramref name="since"/> whose reason
    /// intersects <paramref name="reasonMask"/>. The kernel applies the mask, so
    /// a journal busy with data writes costs one small read per call, and the
    /// caller owns <paramref name="buffer"/> so a poll loop does not allocate.
    /// </summary>
    public UsnJournalState ReadChanges(UsnJournalState since, uint reasonMask, byte[] buffer, Action<UsnEntry>? onEntry)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Span<byte> request = stackalloc byte[ReadJournalDataSize];
        UsnJournalState state = since;

        // One call returns at most one buffer. Keep reading until the journal
        // has nothing past the current position, capped so a flood of changes
        // cannot pin the caller.
        for (int round = 0; round < 64; round++)
        {
            request.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(request, state.NextUsn);
            BinaryPrimitives.WriteUInt32LittleEndian(request[8..], reasonMask);
            BinaryPrimitives.WriteUInt64LittleEndian(request[32..], state.JournalId);

            int returned = DeviceIoControl(FsctlReadUsnJournal, request, buffer);
            if (returned < 8)
            {
                return state;
            }

            long next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            int offset = 8;
            ReadOnlySpan<byte> span = buffer.AsSpan(0, returned);

            while (UsnRecordParser.TryRead(span, ref offset, out UsnEntry entry))
            {
                onEntry?.Invoke(entry);
            }

            bool advanced = next > state.NextUsn;
            state = new UsnJournalState(state.JournalId, Math.Max(state.NextUsn, next));

            if (returned <= 8 || !advanced)
            {
                return state;
            }
        }

        return state;
    }

    /// <summary>
    /// The current full path of a file or directory, by its file reference
    /// number, for example <c>C:\Users\me\Downloads</c>. False when it no longer
    /// exists. Needs the same rights as reading the journal.
    /// </summary>
    public bool TryGetPathById(ulong fileId, out string path)
    {
        path = string.Empty;

        if (UsnPathResolver.IsRoot(fileId))
        {
            path = Root;
            return true;
        }

        const uint FileReadAttributes = 0x80;
        var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = 0, FileId = (long)fileId };

        using SafeFileHandle file = OpenFileById(_handle, ref descriptor, FileReadAttributes, ShareAll, nint.Zero, BackupSemantics);
        if (file.IsInvalid)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[1024];
        uint length;
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandle(file, pointer, (uint)buffer.Length, 0);
            }
        }

        if (length == 0 || length >= buffer.Length)
        {
            if (length < buffer.Length)
            {
                return false;
            }

            char[] large = new char[length + 1];
            unsafe
            {
                fixed (char* pointer = large)
                {
                    length = GetFinalPathNameByHandle(file, pointer, (uint)large.Length, 0);
                }
            }

            if (length == 0 || length >= large.Length)
            {
                return false;
            }

            path = StripDosPrefix(new string(large, 0, (int)length));
            return true;
        }

        path = StripDosPrefix(new string(buffer[..(int)length]));
        return true;
    }

    private static string StripDosPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public int Type;
        public long FileId;
        public long Padding;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint flagsAndAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    private int DeviceIoControl(uint code, ReadOnlySpan<byte> input, byte[] output)
    {
        unsafe
        {
            fixed (byte* inputPointer = input)
            fixed (byte* outputPointer = output)
            {
                bool ok = DeviceIoControlNative(
                    _handle,
                    code,
                    inputPointer,
                    input.Length,
                    outputPointer,
                    output.Length,
                    out int returned,
                    nint.Zero);

                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();
                    // ERROR_HANDLE_EOF (38) = USN journal is empty (no records yet).
                    // This is normal for a freshly created journal. Return 0 bytes instead of throwing.
                    if (error == 38 && code == FsctlEnumUsnData)
                    {
                        return 0;
                    }
                    throw new IOException(
                        $"DeviceIoControl 0x{code:X8} failed on {Device} (Win32 error {error}).",
                        new Win32Exception(error));
                }

                return returned;
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControlNative(
        SafeFileHandle device,
        uint controlCode,
        byte* inputBuffer,
        int inputSize,
        byte* outputBuffer,
        int outputSize,
        out int bytesReturned,
        nint overlapped);
}

public readonly record struct UsnJournalState(ulong JournalId, long NextUsn);
