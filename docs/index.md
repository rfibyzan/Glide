# The index

One file per volume, written by `glide-service`, streamed by the launcher.

## Why not scan folders

Walking a whole drive allocates a path string per entry. On this machine a
profile scan costs about 390 bytes per entry on the heap, and 200,000 entries
already reach 88 MB. A launcher that needs the drive indexed has to keep the
entries somewhere other than the managed heap.

## Format

```
header (40 bytes)
  "GLIDX002"
  version            u32  = 2
  entry count        u32
  reserved           8 bytes
  records offset     u64
  records length     u64
records
  flags byte, UTF-8 path, 0x00, repeated
```

Flags: `0x01` directory, `0x02` the path is ASCII only. The ASCII bit exists
because the reader picks a decoder per entry; storing the answer once turns a
per-query pass over every path into a test of one bit.

Paths are variable length and NUL-terminated, so there is no per-entry offset
table to keep in sync and no alignment padding. A 50-byte path costs 52 bytes:
50 of path, the flag, and the terminator.

The file is written to `<name>.tmp` and moved into place, so a query that races
a rebuild sees either the old file or the new one. The reader opens the index
with `FileShare.ReadWrite | FileShare.Delete`, which is what makes that move
safe.

## Reading

`IndexSearchSource.Search` maps the record area onto ranges, one per worker, and
each worker opens its own handle. Two details that were measured rather than
guessed:

- A worker that does not start on a record boundary scans forward to the first
  NUL, which is exactly the next record start, because paths cannot contain NUL.
- Reads on a single synchronous handle are serialized by the file object, so
  concurrency needs a handle per worker. Opening the handle with
  `FILE_FLAG_OVERLAPPED` instead made `RandomAccess` fault under NativeAOT, so
  that is not an option.

Only rows that can still enter the top list are decoded to strings. Ties are
settled in byte space before decoding; when the current worst row is not ASCII,
the byte comparison is skipped and the character rules decide, because UTF-8
byte order and UTF-16 code-unit order are not the same outside ASCII.

## Filling it

`glide-service` opens `\\.\C:` and calls `FSCTL_ENUM_USN_DATA`. That returns one
record per file with its name and the id of its parent, in file-id order. Paths
are rebuilt by following parent links up to the volume root (file id 5), with
directory paths memoized: a million files live in tens of thousands of
directories.

Records whose parent is missing are dropped. That happens for files deleted
between the enumeration and the resolution, and for files that live on a volume
that is not mounted where the journal says it is.

`$Extend`, `$Recycle.Bin`, and `System Volume Information` are skipped: normal
users cannot open them, and they would only add noise.

## Keeping it current

v0.4.0 does not apply change records one by one. After the initial enumeration
the service remembers the journal's `NextUsn` and polls it every three seconds.
When new records appear it waits for five quiet seconds, then rebuilds the whole
file: an enumeration plus a sequential write, 83 ms for a million entries. A
rebuild cannot drift, and it is simpler than tracking renames, hard links, and
the journal being wrapped by its own size limit.

Incremental updates are the obvious next step, and the journal state needed for
them is already kept per volume.

## Known limits

- One path per file id. A hard link has several names and only the last record
  seen in the enumeration is kept.
- Volume mount points other than a drive letter are not resolved; such a volume
  is skipped because only single letters are enumerated.
- A rebuild is a full rebuild. While it runs, queries keep using the previous
  file, so the only cost is the enumeration's memory inside the service.

## What it costs

- Index file: 50 bytes per entry, so 50 MB for a million entries.
- Launcher: 18.4 MB working set with a 100k index loaded and 50 rows on screen.
- Service: the enumeration holds one dictionary of records while it builds, which
  is the peak; between rebuilds it only polls the journal.

## Testing without administrator rights

`FSCTL_ENUM_USN_DATA` needs an elevated process or a service account, so the
enumeration itself is not exercised by the test suite on a normal account. What
is tested is everything downstream: parsing synthetic `USN_RECORD_V2` buffers,
path reconstruction from parent links, cycle and orphan handling, and the index
round trip. The volume call itself is verified by running
`glide-service --build` from an elevated prompt.
