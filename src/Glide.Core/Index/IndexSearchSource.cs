using System.Buffers;
using System.Text;
using Glide.Core.Search;
using Microsoft.Win32.SafeHandles;

namespace Glide.Core.Index;

/// <summary>
/// Searches an index file by streaming it. Nothing is mapped or cached beyond a
/// per-worker read buffer, so query cost is a sequential read instead of a heap
/// that has to stay resident. ASCII paths are scored as bytes, so a million
/// entries never turn into a million strings.
/// </summary>
public static class IndexSearchSource
{
    private const int MaxPathChars = 32 * 1024;
    private const int ParallelThresholdBytes = 2 * 1024 * 1024;
    private const int MaxWorkers = 8;

    public static IReadOnlyList<SearchResult> Search(string indexPath, string? query, int limit = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        if (limit <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        string[] terms = QueryParser.Parse(query);

        using SafeFileHandle handle = IndexFile.OpenHandle(indexPath);
        IndexHeader header = IndexFile.ReadHeader(handle);

        if (terms.Length == 0)
        {
            return First(handle, header, limit);
        }

        byte[][]? termBytes = AsciiTerms(terms);
        int workers = WorkCount(header.RecordsLength);

        return workers == 1
            ? ScanRange(handle, header.RecordsOffset, header.RecordsOffset + header.RecordsLength, terms, termBytes, limit, alignStart: false)
            : ScanInParallel(handle, header, terms, termBytes, limit, workers);
    }

    public static int Count(string indexPath)
    {
        using SafeFileHandle handle = IndexFile.OpenHandle(indexPath);
        return (int)IndexFile.ReadHeader(handle).Count;
    }

    private static int WorkCount(long recordsLength)
    {
        int cpus = Environment.ProcessorCount;

        return cpus <= 1 || recordsLength < ParallelThresholdBytes
            ? 1
            : Math.Min(MaxWorkers, cpus);
    }

    private static List<SearchResult> ScanInParallel(
        SafeFileHandle handle,
        IndexHeader header,
        string[] terms,
        byte[][]? termBytes,
        int limit,
        int workers)
    {
        long chunk = (header.RecordsLength + workers - 1) / workers;
        var parts = new List<SearchResult>[workers];

        Parallel.For(0, workers, worker =>
        {
            long start = header.RecordsOffset + (worker * chunk);
            long end = Math.Min(header.RecordsOffset + header.RecordsLength, start + chunk);

            parts[worker] = start >= end
                ? new List<SearchResult>()
                : ScanRange(handle, start, end, terms, termBytes, limit, alignStart: worker > 0);
        });

        var merged = new List<SearchResult>(limit);

        foreach (List<SearchResult> part in parts)
        {
            foreach (SearchResult candidate in part)
            {
                EntryScorer.Insert(merged, candidate.Score, candidate.Entry.Path, candidate.Entry.IsDirectory, limit);
            }
        }

        EntryScorer.FillPositions(merged, terms);
        return merged;
    }

    private static List<SearchResult> First(SafeFileHandle handle, IndexHeader header, int limit)
    {
        var results = new List<SearchResult>(Math.Min(limit, 64));
        using var reader = new RecordReader(handle, header.RecordsOffset, header.RecordsOffset + header.RecordsLength, alignStart: false);

        while (results.Count < limit && reader.TryNext(out byte flags, out ReadOnlySpan<byte> path))
        {
            results.Add(new SearchResult(
                new FileEntry(Decode(path), IsDirectory(flags), 0, default),
                0,
                Array.Empty<int>()));
        }

        return results;
    }

    private static List<SearchResult> ScanRange(
        SafeFileHandle handle,
        long start,
        long end,
        string[] terms,
        byte[][]? termBytes,
        int limit,
        bool alignStart)
    {
        var top = new TopBuffer(limit);
        char[] pathBuffer = ArrayPool<char>.Shared.Rent(MaxPathChars);

        try
        {
            using var reader = new RecordReader(handle, start, end, alignStart);

            while (reader.TryNext(out byte flags, out ReadOnlySpan<byte> pathBytes))
            {
                bool isAscii = (flags & IndexFile.AsciiPathFlag) != 0;
                int score;

                if (termBytes is not null && isAscii)
                {
                    if (!EntryScorer.TryScore(pathBytes, termBytes, out score))
                    {
                        continue;
                    }
                }
                else
                {
                    if (pathBytes.Length > MaxPathChars * 3)
                    {
                        continue;
                    }

                    int pathLength = Decode(pathBytes, pathBuffer);
                    if (!EntryScorer.TryScore(pathBuffer.AsSpan(0, pathLength), terms, out score))
                    {
                        continue;
                    }
                }

                top.Offer(score, pathBytes, isAscii, IsDirectory(flags));
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(pathBuffer);
        }

        EntryScorer.FillPositions(top.Rows, terms);
        return top.Rows;
    }

    private static byte[][]? AsciiTerms(string[] terms)
    {
        var encoded = new byte[terms.Length][];

        for (int i = 0; i < terms.Length; i++)
        {
            foreach (char c in terms[i])
            {
                if (c > 127)
                {
                    return null;
                }
            }

            encoded[i] = Encoding.ASCII.GetBytes(terms[i]);
        }

        return encoded;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    private static int Decode(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        return Ascii.IsValid(bytes)
            ? Encoding.Latin1.GetChars(bytes, chars)
            : Encoding.UTF8.GetChars(bytes, chars);
    }

    private static bool IsDirectory(byte flags)
    {
        return (flags & IndexFile.DirectoryFlag) != 0;
    }
}

/// <summary>
/// The best few rows for one scanned range. Candidates are compared as bytes
/// until they can actually enter the list, so a query that ties on thousands of
/// entries does not decode and allocate a string for each tie. When the current
/// worst row is not ASCII, the byte comparison is skipped and the exact
/// character-based rules decide, because UTF-8 byte order is not guaranteed to
/// agree with UTF-16 code-unit order outside ASCII.
/// </summary>
internal sealed class TopBuffer
{
    private readonly int _limit;
    private readonly List<SearchResult> _rows;
    private byte[]? _worstPath;
    private int _worstNameLength;

    internal TopBuffer(int limit)
    {
        _limit = limit;
        _rows = new List<SearchResult>(Math.Min(limit, 64));
    }

    internal List<SearchResult> Rows => _rows;

    internal void Offer(int score, ReadOnlySpan<byte> path, bool isAscii, bool isDirectory)
    {
        if (_rows.Count == _limit && isAscii && !BeatsWorst(score, path))
        {
            return;
        }

        EntryScorer.Insert(_rows, score, Decode(path), isDirectory, _limit);
        RefreshWorst();
    }

    private bool BeatsWorst(int score, ReadOnlySpan<byte> path)
    {
        int worst = _rows[^1].Score;
        if (score != worst)
        {
            return score > worst;
        }

        if (_worstPath is null)
        {
            return true;
        }

        int byNameLength = NameLength(path).CompareTo(_worstNameLength);
        if (byNameLength != 0)
        {
            return byNameLength < 0;
        }

        return path.SequenceCompareTo(_worstPath) < 0;
    }

    private void RefreshWorst()
    {
        if (_rows.Count < _limit)
        {
            return;
        }

        string path = _rows[^1].Entry.Path;
        _worstPath = null;

        foreach (char c in path)
        {
            if (c > 127)
            {
                return;
            }
        }

        _worstNameLength = EntryScorer.NameOf(path.AsSpan()).Length;
        _worstPath = Encoding.UTF8.GetBytes(path);
    }

    private static ReadOnlySpan<byte> NameOfBytes(ReadOnlySpan<byte> path)
    {
        int separator = path.LastIndexOfAny((byte)'\\', (byte)'/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static int NameLength(ReadOnlySpan<byte> path)
    {
        return NameOfBytes(path).Length;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// Splits a byte range of the record area into flags and path bytes without
/// allocating per entry. A worker that does not start on a record boundary first
/// skips the partial record, which is covered by the previous worker.
/// </summary>
internal sealed class RecordReader : IDisposable
{
    private const int DefaultCapacity = 128 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly byte[] _buffer;
    private readonly long _end;
    private long _position;
    private int _start;
    private int _count;

    internal RecordReader(SafeFileHandle handle, long start, long end, bool alignStart)
    {
        _handle = handle;
        _end = end;
        _buffer = ArrayPool<byte>.Shared.Rent(DefaultCapacity);
        _position = alignStart ? AlignToRecord(start) : start;
    }

    internal bool TryNext(out byte flags, out ReadOnlySpan<byte> path)
    {
        while (true)
        {
            if (_start < _count)
            {
                int nul = _buffer.AsSpan(_start + 1, _count - _start - 1).IndexOf((byte)0);
                if (nul >= 0)
                {
                    flags = _buffer[_start];
                    path = _buffer.AsSpan(_start + 1, nul);
                    _start += nul + 2;
                    return true;
                }
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count - _start);
                _count -= _start;
                _start = 0;
            }

            if (_count == _buffer.Length)
            {
                throw new InvalidDataException("Index record is longer than the read buffer.");
            }

            if (_position >= _end)
            {
                flags = 0;
                path = default;
                return false;
            }

            int wanted = (int)Math.Min(_buffer.Length - _count, _end - _position);
            int read = RandomAccess.Read(_handle, _buffer.AsSpan(_count, wanted), _position);

            if (read <= 0)
            {
                throw new InvalidDataException("Index file ends inside the record area.");
            }

            _count += read;
            _position += read;
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    /// <summary>
    /// A record boundary is the byte after a NUL terminator. Paths cannot contain
    /// NUL, so scanning forward to the first one lands exactly there.
    /// </summary>
    private long AlignToRecord(long start)
    {
        Span<byte> probe = stackalloc byte[4096];
        long position = start;

        while (position < _end)
        {
            int wanted = (int)Math.Min(probe.Length, _end - position);
            int read = RandomAccess.Read(_handle, probe[..wanted], position);

            if (read <= 0)
            {
                break;
            }

            int nul = probe[..read].IndexOf((byte)0);
            if (nul >= 0)
            {
                return position + nul + 1;
            }

            position += read;
        }

        return _end;
    }
}
