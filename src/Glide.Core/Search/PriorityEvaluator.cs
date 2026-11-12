using System.Text;
using Glide.Core.Settings;

namespace Glide.Core.Search;

/// <summary>
/// Implements priority boosting and demoting rules (matching Listary Priorities).
/// Evaluates candidate file paths with zero string allocations during search.
/// </summary>
public static class PriorityEvaluator
{
    public const int HighPriorityBoost = 800;
    public const int NormalPriorityBoost = 500;
    public const int RarelyUsedPenalty = -1500;

    private static string[] s_highPaths = [];
    private static string[] s_normalPaths = [];
    private static string[] s_rarelyUsedPaths = [];
    private static string[] s_doNotIndexPaths = [];

    private static byte[][] s_highBytes = [];
    private static byte[][] s_normalBytes = [];
    private static byte[][] s_rarelyUsedBytes = [];
    private static byte[][] s_doNotIndexBytes = [];

    private static readonly byte[] NodeModulesBytes = "node_modules"u8.ToArray();
    private static readonly byte[] NodeModulesSlashBytes = "\\node_modules\\"u8.ToArray();

    static PriorityEvaluator()
    {
        Initialize(new FileSearchPriorities());
    }

    public static void Initialize(FileSearchPriorities priorities)
    {
        s_highPaths = ExpandAndNormalize(priorities.HighPriorityPaths);
        s_normalPaths = ExpandAndNormalize(priorities.NormalPriorityPaths);
        s_rarelyUsedPaths = ExpandAndNormalize(priorities.RarelyUsedPaths);
        s_doNotIndexPaths = ExpandAndNormalize(priorities.DoNotIndexPaths);

        s_highBytes = ToUtf8Bytes(s_highPaths);
        s_normalBytes = ToUtf8Bytes(s_normalPaths);
        s_rarelyUsedBytes = ToUtf8Bytes(s_rarelyUsedPaths);
        s_doNotIndexBytes = ToUtf8Bytes(s_doNotIndexPaths);
    }

    private static string[] ExpandAndNormalize(string[] paths)
    {
        var list = new List<string>(paths.Length);
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
            if (expanded.Length > 0)
            {
                list.Add(expanded);
            }
        }
        return list.ToArray();
    }

    private static byte[][] ToUtf8Bytes(string[] paths)
    {
        var bytesList = new List<byte[]>(paths.Length);
        foreach (string p in paths)
        {
            bytesList.Add(Encoding.UTF8.GetBytes(p));
        }
        return bytesList.ToArray();
    }

    /// <summary>
    /// Checks if a file path is completely excluded from indexing/search.
    /// </summary>
    public static bool IsExcluded(ReadOnlySpan<char> path)
    {
        foreach (string excluded in s_doNotIndexPaths)
        {
            if (path.IndexOf(excluded.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsExcludedBytes(ReadOnlySpan<byte> path)
    {
        foreach (byte[] excluded in s_doNotIndexBytes)
        {
            if (IndexOfBytes(path, excluded) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Computes the score adjustment for a candidate path.
    /// Returns positive boost for High/Normal priorities, negative penalty for rarely used files.
    /// </summary>
    public static int GetScoreAdjustment(ReadOnlySpan<char> path, ReadOnlySpan<char> name)
    {
        // 1. File name prefix patterns: dotfiles (^\.), temp files (^~), system files (^\$)
        if (name.Length > 0)
        {
            char first = name[0];
            if (first is '.' or '~' or '$')
            {
                return RarelyUsedPenalty;
            }
        }

        // node_modules, and anything under a folder named with a leading dot
        // (.git, .cargo, .vscode): tool state, not documents.
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOf(@"\node_modules\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf(@"\.", StringComparison.Ordinal) >= 0)
        {
            return RarelyUsedPenalty;
        }

        // 2. High priority paths (+800)
        foreach (string high in s_highPaths)
        {
            if (path.StartsWith(high.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return HighPriorityBoost;
            }
        }

        // 3. Normal priority paths (+500) - e.g. Start Menu shortcuts, Windows app shortcuts
        foreach (string normal in s_normalPaths)
        {
            if (path.StartsWith(normal.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return NormalPriorityBoost;
            }
        }

        // 4. Rarely used paths (-1500) - e.g. Windows.old, $WINDOWS.~BT, SystemRoot, AppData caches, Program Files
        foreach (string rarely in s_rarelyUsedPaths)
        {
            if (path.StartsWith(rarely.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return RarelyUsedPenalty;
            }
        }

        return 0;
    }

    /// <summary>
    /// Byte span overload for streaming index searching with zero allocations.
    /// </summary>
    public static int GetScoreAdjustmentBytes(ReadOnlySpan<byte> path, ReadOnlySpan<byte> name)
    {
        if (name.Length > 0)
        {
            byte first = name[0];
            if (first is (byte)'.' or (byte)'~' or (byte)'$')
            {
                return RarelyUsedPenalty;
            }
        }

        // A folder named with a leading dot (.git, .cargo, .vscode) holds tool
        // state, not documents, so everything under it is rarely wanted.
        if (EqualsIgnoreCase(name, NodeModulesBytes) || IndexOfBytes(path, NodeModulesSlashBytes) >= 0 || path.IndexOf(@"\."u8) >= 0)
        {
            return RarelyUsedPenalty;
        }

        foreach (byte[] high in s_highBytes)
        {
            if (StartsWithIgnoreCase(path, high))
            {
                return HighPriorityBoost;
            }
        }

        foreach (byte[] normal in s_normalBytes)
        {
            if (StartsWithIgnoreCase(path, normal))
            {
                return NormalPriorityBoost;
            }
        }

        foreach (byte[] rarely in s_rarelyUsedBytes)
        {
            if (StartsWithIgnoreCase(path, rarely))
            {
                return RarelyUsedPenalty;
            }
        }

        return 0;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> span, byte[] prefix)
    {
        if (span.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte a = span[i];
            byte b = prefix[i];
            if (a == b) continue;
            // Simple ASCII case fold
            if (a is >= (byte)'A' and <= (byte)'Z') a = (byte)(a + 32);
            if (b is >= (byte)'A' and <= (byte)'Z') b = (byte)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return StartsWithIgnoreCase(a, b);
    }

    private static int IndexOfBytes(ReadOnlySpan<byte> source, byte[] pattern)
    {
        if (pattern.Length == 0)
        {
            return 0;
        }

        // Jump between occurrences of the first byte (either case) with the
        // vectorized IndexOfAny, and compare the rest only there.
        byte first = pattern[0];
        byte lower = first is >= (byte)'A' and <= (byte)'Z' ? (byte)(first + 32) : first;
        byte upper = lower is >= (byte)'a' and <= (byte)'z' ? (byte)(lower - 32) : lower;
        int offset = 0;

        while (source.Length - offset >= pattern.Length)
        {
            int at = source[offset..(source.Length - pattern.Length + 1)].IndexOfAny(lower, upper);
            if (at < 0)
            {
                return -1;
            }

            int candidate = offset + at;
            if (StartsWithIgnoreCase(source[candidate..], pattern))
            {
                return candidate;
            }

            offset = candidate + 1;
        }

        return -1;
    }
}

