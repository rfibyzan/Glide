using System.Buffers;
using System.Text;

namespace Glide.Core.Index;

/// <summary>
/// Streams entries into an index file directly without keeping the entire
/// list of paths in memory. Entries are written to a temporary file and moved
/// atomically on finish.
/// </summary>
public sealed class IndexStreamWriter : IDisposable
{
    private readonly string _path;
    private readonly string _temporary;
    private readonly FileStream _stream;
    private readonly byte[] _buffer;
    private readonly long _start;
    private int _count;
    private bool _finished;

    public IndexStreamWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _temporary = path + ".tmp";
        _stream = new FileStream(
            _temporary,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1 << 20);

        _stream.Write(new byte[IndexFile.HeaderSize]);
        _start = _stream.Position;
        _buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
    }

    public int Count => _count;

    public void Write(string path, bool isDirectory)
    {
        int length = Encoding.UTF8.GetBytes(path, _buffer);
        WriteEncoded(_buffer.AsSpan(0, length), isDirectory);
    }

    public void WriteCombined(string parent, string name, bool isDirectory, Func<string, bool>? skip = null)
    {
        int parentLen = Encoding.UTF8.GetBytes(parent, _buffer);
        if (!parent.EndsWith('\\'))
        {
            _buffer[parentLen++] = (byte)'\\';
        }

        int nameLen = Encoding.UTF8.GetBytes(name, _buffer.AsSpan(parentLen));
        int totalLen = parentLen + nameLen;

        if (skip is not null)
        {
            string fullPath = Encoding.UTF8.GetString(_buffer.AsSpan(0, totalLen));
            if (skip(fullPath))
            {
                return;
            }
        }

        WriteEncoded(_buffer.AsSpan(0, totalLen), isDirectory);
    }

    private void WriteEncoded(ReadOnlySpan<byte> pathBytes, bool isDirectory)
    {
        byte flagsByte = 0;
        if (isDirectory)
        {
            flagsByte |= IndexFile.DirectoryFlag;
        }

        if (Ascii.IsValid(pathBytes))
        {
            flagsByte |= IndexFile.AsciiPathFlag;
        }

        Span<byte> flags = stackalloc byte[1] { flagsByte };
        Span<byte> terminator = stackalloc byte[1];

        _stream.Write(flags);
        _stream.Write(pathBytes);
        _stream.Write(terminator);

        _count++;
    }

    public int Finish()
    {
        if (_finished)
        {
            return _count;
        }

        IndexFile.WriteHeader(_stream, (uint)_count, _start, _stream.Position - _start);
        _stream.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
        File.Move(_temporary, _path, overwrite: true);
        _finished = true;
        return _count;
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _stream.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer);
            if (File.Exists(_temporary))
            {
                try
                {
                    File.Delete(_temporary);
                }
                catch
                {
                    // Ignore cleanup failure
                }
            }
        }
    }
}

