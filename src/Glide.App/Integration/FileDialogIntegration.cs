using System.Runtime.InteropServices;
using System.Text;
using Glide.App.Win32;

namespace Glide.App.Integration;

/// <summary>
/// Listary-style file dialog integration with documented window APIs only: no
/// hook DLL is injected into other processes.
///
/// <list type="bullet">
/// <item>When Glide opens over an Open or Save dialog, the chosen path is typed
/// into the dialog instead of being launched.</item>
/// <item>Quick Switch: a folder is applied by typing it into the file name box
/// and confirming, which makes the dialog navigate there; the name the user had
/// typed is put back afterwards.</item>
/// </list>
/// </summary>
internal static unsafe class FileDialogIntegration
{
    private const int FileNameComboId = 0x047C;
    private const int IdOk = 1;
    private const int RestoreDelayMs = 250;

    private sealed record CapturedDialog(IntPtr DialogHwnd, IntPtr EditHwnd);

    private static CapturedDialog? s_captured;
    private static PendingRestore? s_pendingRestore;

    private sealed record PendingRestore(IntPtr EditHwnd, string Text, UIntPtr TimerId);

    /// <summary>True while Glide is open on behalf of a file dialog.</summary>
    public static bool HasCapturedDialog => s_captured is not null && NativeMethods.IsWindow(s_captured.DialogHwnd);

    /// <summary>
    /// Captures the foreground window if it is an Open or Save dialog. Called
    /// right before Glide shows its window, while the dialog still has focus.
    /// </summary>
    public static void CaptureActiveDialog(IntPtr glideHwnd)
    {
        s_captured = null;

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == glideHwnd)
        {
            return;
        }

        if (TryGetFileNameEdit(foreground, out IntPtr edit))
        {
            s_captured = new CapturedDialog(foreground, edit);
        }
    }

    /// <summary>
    /// Diagnostic: every visible top-level dialog window, whether Glide takes
    /// it for a file dialog, and the window classes inside it. Reads only.
    /// </summary>
    public static void Probe(TextWriter output)
    {
        var dialogs = new List<IntPtr>();
        var handle = GCHandle.Alloc(dialogs);

        try
        {
            EnumWindows(&CollectTopLevel, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        foreach (IntPtr dialog in dialogs)
        {
            var title = new StringBuilder(256);
            GetWindowTextW(dialog, title, title.Capacity);
            bool isFileDialog = TryGetFileNameEdit(dialog, out IntPtr edit);

            var classes = new SortedSet<string>(StringComparer.Ordinal);
            var all = new ChildClasses { Names = classes };
            var classHandle = GCHandle.Alloc(all);
            try
            {
                EnumChildWindows(dialog, &CollectClass, GCHandle.ToIntPtr(classHandle));
            }
            finally
            {
                classHandle.Free();
            }

            output.WriteLine($"0x{dialog:X} \"{title}\" fileDialog={isFileDialog} edit=0x{edit:X}");
            output.WriteLine("   children: " + string.Join(", ", classes));
        }

        if (dialogs.Count == 0)
        {
            output.WriteLine("no visible #32770 windows");
        }
    }

    private sealed class ChildClasses
    {
        public required SortedSet<string> Names;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int CollectTopLevel(IntPtr hwnd, IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is List<IntPtr> list && IsWindowVisible(hwnd) && ClassOf(hwnd) == "#32770")
        {
            list.Add(hwnd);
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int CollectClass(IntPtr hwnd, IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is ChildClasses classes)
        {
            classes.Names.Add(ClassOf(hwnd) + (IsWindowVisible(hwnd) ? string.Empty : "(hidden)"));
        }

        return 1;
    }

    [DllImport("user32.dll")]
    private static extern int EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> callback, IntPtr state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int capacity);

    /// <summary>Captures <paramref name="dialog"/> directly, for the bar docked under it.</summary>
    public static bool Capture(IntPtr dialog)
    {
        if (TryGetFileNameEdit(dialog, out IntPtr edit))
        {
            s_captured = new CapturedDialog(dialog, edit);
            return true;
        }

        return false;
    }

    public static void ReleaseCapturedDialog()
    {
        s_captured = null;
    }

    /// <summary>
    /// Sends the chosen path to the captured dialog. A folder navigates the
    /// dialog there; a file is chosen as the dialog's result. Returns false when
    /// no dialog was captured, so the caller opens the path normally.
    /// </summary>
    public static bool TryInjectPath(string path)
    {
        CapturedDialog? captured = s_captured;
        s_captured = null;

        if (captured is null || !NativeMethods.IsWindow(captured.DialogHwnd) || !NativeMethods.IsWindow(captured.EditHwnd))
        {
            return false;
        }

        NativeMethods.SetForegroundWindow(captured.DialogHwnd);
        Apply(captured.DialogHwnd, captured.EditHwnd, path, Directory.Exists(path));
        return true;
    }

    /// <summary>
    /// Navigates <paramref name="dialog"/> to <paramref name="folder"/> if it is
    /// a file dialog. Used by the Quick Switch shortcut without opening Glide.
    /// </summary>
    public static bool TrySwitchTo(IntPtr dialog, string folder)
    {
        if (!TryGetFileNameEdit(dialog, out IntPtr edit) || !Directory.Exists(folder))
        {
            return false;
        }

        Apply(dialog, edit, folder, isFolder: true);
        return true;
    }

    /// <summary>
    /// True when <paramref name="hwnd"/> is a standard Open/Save/Browse dialog.
    /// A plain <c>#32770</c> is also every message box, so a dialog only counts
    /// when it hosts a shell view and has a text box to type a name into.
    /// </summary>
    public static bool IsFileDialog(IntPtr hwnd)
    {
        return TryGetFileNameEdit(hwnd, out _);
    }

    private static bool TryGetFileNameEdit(IntPtr dialog, out IntPtr edit)
    {
        edit = IntPtr.Zero;

        if (dialog == IntPtr.Zero || !NativeMethods.IsWindow(dialog) || ClassOf(dialog) != "#32770")
        {
            return false;
        }

        var search = new ChildSearch();
        var handle = GCHandle.Alloc(search);

        try
        {
            EnumChildWindows(dialog, &CollectChild, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        if (!search.HasShellView)
        {
            return false;
        }

        // The file name box: the combo with the documented id when present
        // (Open dialogs), otherwise the first visible edit in z-order, which is
        // the name box in Save dialogs too.
        IntPtr combo = NativeMethods.GetDlgItem(dialog, FileNameComboId);
        if (combo != IntPtr.Zero)
        {
            IntPtr inner = ClassOf(combo) == "Edit" ? combo : FindDescendantEdit(combo);
            if (inner != IntPtr.Zero)
            {
                edit = inner;
                return true;
            }
        }

        edit = search.FirstEdit;
        return edit != IntPtr.Zero;
    }

    private static void Apply(IntPtr dialog, IntPtr edit, string path, bool isFolder)
    {
        string original = GetText(edit);
        string text = isFolder && !path.EndsWith('\\') ? path + "\\" : path;

        NativeMethods.SendMessageW(edit, NativeMethods.WmSetText, IntPtr.Zero, text);
        NativeMethods.PostMessageW(dialog, NativeMethods.WmCommand, (IntPtr)IdOk, NativeMethods.GetDlgItem(dialog, IdOk));

        // Confirming a folder navigates and leaves the folder text behind. A
        // file name the user had typed in a Save dialog is put back once the
        // dialog has moved, so Quick Switch does not cost them the name.
        if (isFolder && original.Length > 0 && original.IndexOfAny([':', '\\', '/']) < 0)
        {
            ScheduleRestore(edit, original);
        }
    }

    private static void ScheduleRestore(IntPtr edit, string text)
    {
        if (s_pendingRestore is { } previous)
        {
            KillTimer(IntPtr.Zero, previous.TimerId);
        }

        UIntPtr id = SetTimer(IntPtr.Zero, UIntPtr.Zero, RestoreDelayMs, &RestoreTimer);
        s_pendingRestore = id == UIntPtr.Zero ? null : new PendingRestore(edit, text, id);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void RestoreTimer(IntPtr hwnd, uint message, UIntPtr id, uint time)
    {
        KillTimer(IntPtr.Zero, id);

        PendingRestore? pending = s_pendingRestore;
        s_pendingRestore = null;

        if (pending is not null && pending.TimerId == id && NativeMethods.IsWindow(pending.EditHwnd))
        {
            NativeMethods.SendMessageW(pending.EditHwnd, NativeMethods.WmSetText, IntPtr.Zero, pending.Text);
        }
    }

    private static string GetText(IntPtr hwnd)
    {
        const int Capacity = 1024;
        char* buffer = stackalloc char[Capacity];
        int length = (int)NativeMethods.SendMessageW(hwnd, NativeMethods.WmGetText, Capacity, (IntPtr)buffer);
        return length <= 0 ? string.Empty : new string(buffer, 0, Math.Min(length, Capacity - 1));
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return NativeMethods.GetClassNameW(hwnd, name, name.Capacity) == 0 ? string.Empty : name.ToString();
    }

    private static IntPtr FindDescendantEdit(IntPtr parent)
    {
        var search = new ChildSearch();
        var handle = GCHandle.Alloc(search);

        try
        {
            EnumChildWindows(parent, &CollectChild, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return search.FirstEdit;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int CollectChild(IntPtr hwnd, IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is not ChildSearch search)
        {
            return 0;
        }

        string name = ClassOf(hwnd);

        if (name is "SHELLDLL_DefView" or "DUIViewWndClassName")
        {
            search.HasShellView = true;
        }
        else if (name == "Edit" && search.FirstEdit == IntPtr.Zero && IsWindowVisible(hwnd))
        {
            search.FirstEdit = hwnd;
        }

        return 1;
    }

    private sealed class ChildSearch
    {
        public bool HasShellView;
        public IntPtr FirstEdit;
    }

    [DllImport("user32.dll")]
    private static extern int EnumChildWindows(IntPtr parent, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, uint, void> callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
}
