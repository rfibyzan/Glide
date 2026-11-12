# The index

One file per volume, written by the index service (`glide.exe --service`), streamed by the launcher.

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

The service opens `\\.\C:` and calls `FSCTL_ENUM_USN_DATA`. That returns one
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

After the initial enumeration the service remembers the journal's `NextUsn`
(taken before the enumeration, so changes made during a build are read again)
and polls every second.

### The delta (v0.7.0)

Rebuilding C: takes 6–12 s on a real drive with 1.3M entries, so a rebuild per
change meant new files showed up 10–15 s late and kept a core busy. Now each
relevant record goes into the volume's delta file right away:

- `index-c.delta` sits next to `index-c.gidx`: a header line, then
  `+<TAB>f|d<TAB>path` for a path that exists now and `-<TAB>path` for one that
  does not, the last line for a path winning. It is replaced in one rename.
- A record names only its parent's file id. `UsnVolume.TryGetPathById`
  (`OpenFileById` + `GetFinalPathNameByHandle`) turns that into a path, with a
  small cache of directory ids (`IndexDeltaRecorder`).
- The launcher stats the delta on each search and rereads it only when it
  changed. `IndexSearchSource.SearchMany` drops index rows the delta names,
  then scores the delta's added paths with the same scorer and constraints, so
  a delta row ranks exactly like an index row.

A new, deleted or renamed file is therefore searchable about a second after the
journal records it. A full rebuild still happens, debounced as before (three
quiet seconds, at most fifteen, at least ten between rebuilds), but only when
the delta cannot say what changed: a folder renamed or deleted moves every path
below it, a parent that can no longer be opened, or a delta past 5,000 paths.
The rebuild folds the delta in and deletes the file.

### Which records count (v0.6.0)

Before v0.6.0 any movement of `NextUsn` scheduled a rebuild. On a system drive
the journal never stops moving, and the rebuild itself wrote `index-c.gidx` on
C:, which moved the journal again: the service rebuilt C: every ten to fifteen
seconds forever, and each rebuild allocated the whole directory table.

Now:

1. `FSCTL_READ_USN_JOURNAL` is called with a reason mask of `FILE_CREATE`,
   `FILE_DELETE`, `RENAME_OLD_NAME` and `RENAME_NEW_NAME`. The kernel filters,
   so data writes to logs and databases never reach the service.
2. `ChangeFilter` drops records whose parent is a quiet directory: the index
   folder itself, `%TEMP%` folders, browser profiles, `Windows\Prefetch`,
   `Windows\Logs` and similar caches (the list is in
   `src/Glide.Core/Index/Usn/ChangeFilter.cs`). Directories skipped by the build,
   such as `$Recycle.Bin`, are quiet too, and a folder created inside a quiet
   folder inherits it. Quiet folders are still indexed; they are refreshed by
   the next rebuild something else triggers.
3. A failed journal read (journal recreated, position purged) triggers a
   rebuild after a retry delay, instead of failing on every poll forever.
4. The 64 KB read buffer is allocated once per volume; polling allocates
   nothing.

## Known limits

- One path per file id. A hard link has several names and only the last record
  seen in the enumeration is kept.
- Volume mount points other than a drive letter are not resolved; such a volume
  is skipped because only single letters are enumerated.
- A rebuild is a full rebuild. While it runs, queries keep using the previous
  file, so the only cost is the enumeration's memory inside the service.

## What it costs

- Index file: 50 bytes per entry, so 50 MB for a million entries.
- Launcher: 18.4 MB working set with a 100k index loaded and 50 rows on screen;
  19.5 MB in v0.5.0 with themes and settings loaded.
- Service: the enumeration holds one dictionary of directory records while it
  builds, which is the peak; a directory path found during the parent walk is
  reused instead of being built twice (v0.6.0). Between rebuilds it reads at
  most one 64 KB buffer of filtered records per poll.

## Testing without administrator rights

`FSCTL_ENUM_USN_DATA` needs an elevated process or a service account, so the
enumeration itself is not exercised by the test suite on a normal account. What
is tested is everything downstream: parsing synthetic `USN_RECORD_V2` buffers,
path reconstruction from parent links, cycle and orphan handling, and the index
round trip. The volume call itself is verified by running
`glide --service-build` from an elevated prompt.
