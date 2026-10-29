namespace Glide.Core.Search;

/// <summary>
/// One candidate shown in the result list. Paths are absolute and use the
/// platform separator; <see cref="LastWriteUtc"/> is used to break ties later.
/// </summary>
public readonly record struct FileEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastWriteUtc);
