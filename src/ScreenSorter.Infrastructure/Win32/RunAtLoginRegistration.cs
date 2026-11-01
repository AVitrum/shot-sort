using Microsoft.Win32;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// Start with Windows, through the current user's Run key. The registry is the only record of this setting, so it can
/// never disagree with what Windows does. It is changed only when the user asks; starting the app writes nothing.
/// </summary>
/// <param name="keyPath">Registry key under HKEY_CURRENT_USER; replaced in tests.</param>
/// <param name="valueName">Name of the registry value.</param>
public sealed class RunAtLoginRegistration(
    string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string valueName = "ScreenSorter")
{
    /// <summary>Whether the app is registered to start at login.</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    /// <summary>Registers or removes the app.</summary>
    /// <param name="enabled">Whether it should start at login.</param>
    /// <param name="exePath">Full path of the exe to start; quoted so paths with spaces work.</param>
    public void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled)
            key.SetValue(valueName, $"\"{exePath}\"");
        else
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
