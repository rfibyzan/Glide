using System.Buffers;
using System.Text;
using Glide.Core.Search;

namespace Glide.Core.Index;

/// <summary>
/// Writes an index file from a sequence of entries. The file is built next to
/// its destination and moved into place, so a reader never sees a half-written
/// index even while the service rebuilds it.
/// </summary>
public static class IndexWriter
{
    public static int Write(string path, IEnumerable<FileEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        string temporary = path + ".tmp";
        int count = 0;

        using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1 << 20))
        {
            stream.Write(new byte[IndexFile.HeaderSize]);

            long start = stream.Position;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

            try
            {
                Span<byte> flags = stackalloc byte[1];
                Span<byte> terminator = stackalloc byte[1];

                foreach (FileEntry entry in entries)
                {
                    int length = Encoding.UTF8.GetBytes(entry.Path, buffer);

                    byte flagsByte = 0;
                    if (entry.IsDirectory)
                    {
                        flagsByte |= IndexFile.DirectoryFlag;
                    }

                    if (Ascii.IsValid(buffer.AsSpan(0, length)))
                    {
                        flagsByte |= IndexFile.AsciiPathFlag;
                    }

                    flags[0] = flagsByte;

                    stream.Write(flags);
                    stream.Write(buffer, 0, length);
                    stream.Write(terminator);

                    count++;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            IndexFile.WriteHeader(stream, (uint)count, start, stream.Position - start);
        }

        File.Move(temporary, path, overwrite: true);
        return count;
    }
}
