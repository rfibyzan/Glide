namespace Glide.Core.Index.Usn;

/// <summary>
/// Decides whether a journal record should make the service rebuild an index.
///
/// Most journal traffic on a system drive is noise for a name index: temp
/// files, browser caches, logs, and the service's own index writes. Without a
/// filter the service rewrites <c>index-c.gidx</c>, which is itself a change on
/// C:, which schedules the next rebuild, forever. The filter holds the ids of
/// directories whose churn should not trigger a rebuild. Their contents are
/// still indexed; they are only refreshed by the next rebuild something else
/// triggers.
/// </summary>
public sealed class ChangeFilter
{
    private static readonly string[] QuietFragments =
    [
        @"\Windows\Temp\",
        @"\Windows\Prefetch\",
        @"\Windows\Logs\",
        @"\Windows\SoftwareDistribution\",
        @"\Windows\ServiceProfiles\",
        @"\Windows\System32\LogFiles\",
        @"\Windows\System32\config\",
        @"\Windows\System32\sru\",
        @"\ProgramData\Microsoft\Windows\WER\",
        @"\ProgramData\Microsoft\Windows Defender\",
        @"\ProgramData\Microsoft\Search\",
        @"\AppData\Local\Temp\",
        @"\AppData\Local\Microsoft\Windows\INetCache\",
        @"\AppData\Local\Microsoft\Windows\Explorer\",
        @"\AppData\Local\Packages\",
        @"\AppData\Local\Google\Chrome\User Data\",
        @"\AppData\Local\Microsoft\Edge\User Data\",
        @"\AppData\Local\BraveSoftware\",
        @"\AppData\Local\Mozilla\Firefox\Profiles\",
        @"\AppData\Roaming\Mozilla\Firefox\Profiles\",
        @"\AppData\Roaming\zen\Profiles\",
        @"\AppData\Local\zen\Profiles\",
        @"\AppData\Roaming\discord\",
        @"\AppData\Roaming\Microsoft\Windows\Recent\CustomDestinations\",
        @"\AppData\Roaming\Code\",
        @"\AppData\Local\D3DSCache\",
        @"\AppData\Local\NVIDIA\",
    ];

    private readonly HashSet<ulong> _quiet;

    public ChangeFilter(HashSet<ulong> quietDirectories)
    {
        _quiet = quietDirectories ?? throw new ArgumentNullException(nameof(quietDirectories));
    }

    public static ChangeFilter Empty { get; } = new([]);

    public int QuietCount => _quiet.Count;

    /// <summary>
    /// True for a directory whose changes do not warrant a rebuild. The index
    /// folder is passed in because it differs between machines and tests.
    /// </summary>
    public static bool IsQuietPath(string directoryPath, string? indexDirectory)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        string withSlash = directoryPath.EndsWith('\\') ? directoryPath : directoryPath + "\\";

        if (!string.IsNullOrEmpty(indexDirectory))
        {
            string index = indexDirectory.EndsWith('\\') ? indexDirectory : indexDirectory + "\\";
            if (withSlash.StartsWith(index, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string fragment in QuietFragments)
        {
            if (withSlash.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the record changes something a search could return. A
    /// directory created inside a quiet directory becomes quiet too, so a temp
    /// folder that an installer fills after the last rebuild stays silent.
    /// </summary>
    public bool IsRelevant(in UsnEntry entry)
    {
        ulong parent = entry.ParentId & UsnPathResolver.MftRecordMask;
        ulong self = entry.FileId & UsnPathResolver.MftRecordMask;

        if (_quiet.Contains(parent))
        {
            if (entry.IsDirectory)
            {
                _quiet.Add(self);
            }

            return false;
        }

        return !_quiet.Contains(self);
    }
}
