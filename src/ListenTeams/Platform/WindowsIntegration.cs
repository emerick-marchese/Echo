using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ListenTeams.Core.Settings;
using Microsoft.Win32;

namespace ListenTeams.Platform;

/// <summary>Encrypts secrets with DPAPI: only the current Windows user can decrypt them.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ListenTeams.v1");

    public string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Start ListenTeams with the Windows session, quietly in the notification area.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ListenTeams";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>Choices made in the installer (applications to record), applied once by the app.</summary>
internal static class InstallerChoices
{
    private const string Key = @"Software\ListenTeams";

    public static void Apply(SettingsStore settings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (key?.GetValue("Apps") is not string apps) return;

            var chosen = apps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (chosen.Count > 0)
            {
                settings.Update(s =>
                {
                    s.RecordTeams = chosen.Contains("Teams");
                    s.RecordDiscord = chosen.Contains("Discord");
                });
                Core.Log.Info($"Applications choisies à l'installation : {apps}");
            }
            key.DeleteValue("Apps", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Core.Log.Warn($"Choix de l'installateur illisibles : {ex.Message}");
        }
    }
}

internal static class AppPaths
{
    public static string AppFolder => AppContext.BaseDirectory;

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ListenTeams");

    public static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ListenTeams", "settings.json");

    public static string LogsFolder => Path.Combine(DataFolder, "logs");

    public static string DownloadedModelsFolder => Path.Combine(DataFolder, "models");

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Impossible d'ouvrir {path} : {ex.Message}");
        }
    }
}
