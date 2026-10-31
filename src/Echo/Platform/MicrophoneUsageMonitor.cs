using Echo.Core.Apps;
using Microsoft.Win32;

namespace Echo.Platform;

/// <summary>
/// Reads the Windows privacy "capability access" store, which tracks which applications
/// currently use the microphone. Teams and Discord keep the microphone open for the whole
/// call (even when muted), so this is a reliable call indicator that needs no configuration.
/// </summary>
internal static class MicrophoneUsageMonitor
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static bool IsUsingMicrophone(CallApp app)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (root is null) return false;

            // Packaged apps (new Teams): "MSTeams_8wekyb3d8bbwe".
            foreach (var name in root.GetSubKeyNames())
            {
                if (CallApps.MatchesConsentKey(app, name, packaged: true) && IsInUse(root, name)) return true;
            }

            // Desktop apps: "NonPackaged\C:#Users#…#Discord#app-1.0.9#Discord.exe".
            using var nonPackaged = root.OpenSubKey("NonPackaged");
            if (nonPackaged is null) return false;
            foreach (var name in nonPackaged.GetSubKeyNames())
            {
                if (CallApps.MatchesConsentKey(app, name, packaged: false) && IsInUse(nonPackaged, name)) return true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return false;
    }

    private static bool IsInUse(RegistryKey parent, string name)
    {
        using var key = parent.OpenSubKey(name);
        if (key is null) return false;
        long start = ToLong(key.GetValue("LastUsedTimeStart"));
        long stop = ToLong(key.GetValue("LastUsedTimeStop"));
        return start > 0 && stop == 0;
    }

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        _ => 0,
    };
}
