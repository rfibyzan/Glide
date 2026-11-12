using System.Runtime.InteropServices;

namespace Glide.App.Ui;

/// <summary>
/// Shell icons for the rows on screen. Most files share one icon per
/// extension, looked up from the extension alone without touching the disk;
/// only programs and shortcuts, whose icon lives inside the file, are read
/// from the file itself. The cache is emptied whenever it passes a few dozen
/// icons, so it never holds more than a screenful or two.
/// </summary>
internal sealed unsafe class IconCache : IDisposable
{
    private const int MaxIcons = 48;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;

    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr",
    };

    private readonly Dictionary<string, IntPtr> _icons = new(StringComparer.OrdinalIgnoreCase);

    public IntPtr Get(string path, bool isDirectory, bool small = false)
    {
        (string key, string lookup, bool fromFile) = KeyFor(path, isDirectory);
        key = small ? "s|" + key : key;

        if (_icons.TryGetValue(key, out IntPtr cached))
        {
            return cached;
        }

        if (_icons.Count >= MaxIcons)
        {
            Clear();
        }

        var info = default(ShFileInfo);
        uint flags = ShgfiIcon | (small ? ShgfiSmallIcon : ShgfiLargeIcon) | (fromFile ? 0 : ShgfiUseFileAttributes);
        uint attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;

        IntPtr icon = SHGetFileInfoW(lookup, attributes, &info, (uint)sizeof(ShFileInfo), flags) != IntPtr.Zero
            ? info.Icon
            : IntPtr.Zero;

        _icons[key] = icon;
        return icon;
    }

    public void Clear()
    {
        foreach (IntPtr icon in _icons.Values)
        {
            if (icon != IntPtr.Zero)
            {
                UiNative.DestroyIcon(icon);
            }
        }

        _icons.Clear();
    }

    public void Dispose() => Clear();

    private static (string Key, string Lookup, bool FromFile) KeyFor(string path, bool isDirectory)
    {
        bool network = path.StartsWith(@"\\", StringComparison.Ordinal);

        // Drive roots show the drive's own icon.
        if (path.Length <= 3 && path.Length >= 2 && path[1] == ':')
        {
            return (path, path.Length == 2 ? path + "\\" : path, true);
        }

        if (isDirectory)
        {
            return ("<folder>", "folder", false);
        }

        string extension = Path.GetExtension(path);

        if (!network && OwnIconExtensions.Contains(extension))
        {
            return (path, path, true);
        }

        string key = extension.Length == 0 ? "<file>" : extension;
        return (key, "file" + extension, false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ShFileInfo* info, uint size, uint flags);
}
