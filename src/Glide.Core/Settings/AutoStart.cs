using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace Glide.Core.Settings;

/// <summary>
/// "Start Glide when Windows starts" (Listary: EnableAutoStart). Stored where
/// Windows reads it, the current user's Run key, not in settings.json, so the
/// checkbox always shows what will really happen at the next sign-in.
/// </summary>
[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Glide runs on Windows only.")]
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glide";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Length > 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Registers or removes <paramref name="executable"/>; returns false when the registry refused.</summary>
    public static bool Set(bool enabled, string executable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
