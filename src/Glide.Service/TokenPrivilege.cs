using System.Runtime.InteropServices;

namespace Glide.Service;

/// <summary>
/// Best-effort enablement of <c>SeBackupPrivilege</c> on the current process token.
///
/// The LocalSystem service account holds <c>SeBackupPrivilege</c>, but it is
/// disabled by default in the service token. Opening a volume's USN journal
/// (<c>\\.\C:</c>, <c>\\.\D:</c>) requires the privilege to be enabled, otherwise
/// <c>CreateFile</c> fails with Win32 error 5 (access denied). This is
/// best-effort: if we cannot enable it (for example running as a standard user),
/// the volume-open error message covers that case.
/// </summary>
internal static class TokenPrivilege
{
    private const string SeBackupName = "SeBackupPrivilege";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    public static void EnableBackupPrivilege()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            {
                return;
            }

            try
            {
                if (!LookupPrivilegeValue(null, SeBackupName, out Luid luid))
                {
                    return;
                }

                var state = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
                };

                AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    newState: ref state,
                    bufferLength: (uint)Marshal.SizeOf<TokenPrivileges>(),
                    previousState: IntPtr.Zero,
                    returnLength: IntPtr.Zero);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            // Best-effort only; a standard user cannot enable the privilege, and the
            // volume-open error message covers that case.
        }
    }
}
