using Microsoft.Win32;

namespace Crest.Platform;

/// <summary>Start with Windows: the "Crest" value in the current user's Run key, pointing at this exe.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Crest";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Crest: could not read start with Windows: {ex.Message}");
            return false;
        }
    }

    /// <summary>Adds or removes the Run value. On, it always points at the exe that is running now.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Crest: could not change start with Windows: {ex.Message}");
        }
    }
}
