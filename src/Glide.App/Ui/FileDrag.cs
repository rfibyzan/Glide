using System.Runtime.InteropServices;

namespace Glide.App.Ui;

/// <summary>
/// Drags a file or folder out of the launcher, the way Explorer does: the
/// shell builds the data object (CF_HDROP, shell ID list, file contents and
/// its own drag image) and runs the drag loop with its default drop source, so
/// Glide needs no COM classes of its own and nothing NativeAOT would trim.
/// </summary>
internal static unsafe class FileDrag
{
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint DropEffectLink = 4;
    private const int DragDropDropped = 0x00040100;
    private const int SlotRelease = 2;
    private const int SlotBindToHandler = 3;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;

    private static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid IidDataObject = new("0000010E-0000-0000-C000-000000000046");

    private static bool s_oleReady;

    /// <summary>
    /// True once the pointer has left the system drag rectangle around the
    /// point where the button went down.
    /// </summary>
    public static bool PassedThreshold(int startX, int startY, int x, int y)
    {
        int dx = Math.Max(GetSystemMetrics(SmCxDrag), 4) / 2;
        int dy = Math.Max(GetSystemMetrics(SmCyDrag), 4) / 2;
        return Math.Abs(x - startX) > dx || Math.Abs(y - startY) > dy;
    }

    /// <summary>
    /// Runs a modal drag of <paramref name="path"/> from <paramref name="owner"/>.
    /// Must be called on the window's (STA) thread, while the left button is down.
    /// Returns true when the item was dropped somewhere, false when the drag was
    /// cancelled or could not start.
    /// </summary>
    public static bool Start(IntPtr owner, string path)
    {
        if (!s_oleReady)
        {
            // Drag and drop needs OLE, not just COM. S_FALSE when already set up.
            int ole = OleInitialize(IntPtr.Zero);
            if (ole < 0)
            {
                AppLog.Write($"drag: OleInitialize failed 0x{ole:X8}");
                return false;
            }

            s_oleReady = true;
        }

        IntPtr pidl = IntPtr.Zero;
        IntPtr items = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;

        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (hr < 0 || pidl == IntPtr.Zero)
            {
                AppLog.Write($"drag: SHParseDisplayName failed 0x{hr:X8} for {path}");
                return false;
            }

            hr = SHCreateShellItemArrayFromIDLists(1, &pidl, out items);
            if (hr < 0 || items == IntPtr.Zero)
            {
                AppLog.Write($"drag: SHCreateShellItemArrayFromIDLists failed 0x{hr:X8}");
                return false;
            }

            Guid bhid = BhidDataObject;
            Guid iid = IidDataObject;
            var bindToHandler = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(items, SlotBindToHandler);
            hr = bindToHandler(items, IntPtr.Zero, &bhid, &iid, &data);
            if (hr < 0 || data == IntPtr.Zero)
            {
                AppLog.Write($"drag: BindToHandler(DataObject) failed 0x{hr:X8}");
                return false;
            }

            int result = SHDoDragDrop(owner, data, IntPtr.Zero, DropEffectCopy | DropEffectMove | DropEffectLink, out _);
            if (result < 0)
            {
                AppLog.Write($"drag: SHDoDragDrop failed 0x{result:X8}");
            }

            return result == DragDropDropped;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Write($"drag: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            Release(data);
            Release(items);
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static IntPtr Slot(IntPtr instance, int slot)
    {
        IntPtr* table = *(IntPtr**)instance;
        return table[slot];
    }

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, SlotRelease))(instance);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr* pidls, out IntPtr items);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr hwnd, IntPtr dataObject, IntPtr dropSource, uint okEffects, out uint effect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
