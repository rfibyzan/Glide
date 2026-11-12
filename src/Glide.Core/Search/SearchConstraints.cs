using System.Text;

namespace Glide.Core.Search;

/// <summary>
/// Restrictions a query places on candidates before they are scored: a kind
/// (folders or files), a set of extensions, and a folder the results must live
/// under. Checks run on the raw UTF-8 bytes of an index row, so a filtered
/// query over a million rows still allocates nothing per row.
/// </summary>
public sealed class SearchConstraints
{
    private readonly byte[][] _extensionBytes;
    private readonly byte[]? _scopeBytes;

    public SearchConstraints(bool foldersOnly, bool filesOnly, IEnumerable<string>? extensions, string? scope)
    {
        FoldersOnly = foldersOnly && !filesOnly;
        FilesOnly = filesOnly && !foldersOnly;

        Extensions = (extensions ?? [])
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // An extension filter only makes sense for files.
        if (Extensions.Length > 0)
        {
            FilesOnly = true;
            FoldersOnly = false;
        }

        _extensionBytes = Extensions.Select(e => Encoding.UTF8.GetBytes("." + e)).ToArray();

        if (!string.IsNullOrEmpty(scope))
        {
            Scope = scope.EndsWith('\\') ? scope : scope + "\\";
            _scopeBytes = Encoding.UTF8.GetBytes(Scope);
        }
    }

    public bool FoldersOnly { get; }

    public bool FilesOnly { get; }

    /// <summary>Lower-case extensions without the dot.</summary>
    public string[] Extensions { get; }

    /// <summary>Folder prefix ending in a backslash, or null for the whole index.</summary>
    public string? Scope { get; }

    public bool IsEmpty => !FoldersOnly && !FilesOnly && Extensions.Length == 0 && Scope is null;

    /// <summary>The drive letter the scope sits on, upper case, or null.</summary>
    public char? ScopeDrive =>
        Scope is { Length: >= 2 } s && s[1] == ':' && char.IsAsciiLetter(s[0]) ? char.ToUpperInvariant(s[0]) : null;

    public bool Accepts(ReadOnlySpan<char> path, bool isDirectory)
    {
        if ((FoldersOnly && !isDirectory) || (FilesOnly && isDirectory))
        {
            return false;
        }

        if (Scope is not null &&
            (path.Length <= Scope.Length || !path.StartsWith(Scope, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Extensions.Length == 0)
        {
            return true;
        }

        foreach (string extension in Extensions)
        {
            if (path.Length > extension.Length + 1 &&
                path[^(extension.Length + 1)] == '.' &&
                path[^extension.Length..].Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Same rules as <see cref="Accepts(ReadOnlySpan{char}, bool)"/> on UTF-8 bytes.</summary>
    public bool Accepts(ReadOnlySpan<byte> path, bool isDirectory)
    {
        if ((FoldersOnly && !isDirectory) || (FilesOnly && isDirectory))
        {
            return false;
        }

        if (_scopeBytes is not null &&
            (path.Length <= _scopeBytes.Length || !StartsWithAsciiIgnoreCase(path, _scopeBytes)))
        {
            return false;
        }

        if (_extensionBytes.Length == 0)
        {
            return true;
        }

        foreach (byte[] extension in _extensionBytes)
        {
            if (path.Length > extension.Length && StartsWithAsciiIgnoreCase(path[^extension.Length..], extension))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares ASCII letters without case and every other byte exactly. Folding
    /// only ASCII is what NTFS users type in practice and keeps the check cheap.
    /// </summary>
    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> span, byte[] prefix)
    {
        if (span.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            byte a = span[i];
            byte b = prefix[i];

            if (a == b)
            {
                continue;
            }

            if ((uint)((a | 0x20) - 'a') <= 'z' - 'a' && (a | 0x20) == (b | 0x20))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
