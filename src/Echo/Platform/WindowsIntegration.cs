using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Echo.Core.Settings;
using Microsoft.Win32;

namespace Echo.Platform;

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

/// <summary>Start Echo with the Windows session, quietly in the notification area.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Echo";

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

/// <summary>
/// One-time import from ListenTeams, Echo's former name: settings (API key included), and the
/// default recordings folder when it was never changed.
/// </summary>
internal static class LegacyMigration
{
    public static void Run()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string oldSettings = Path.Combine(appData, "ListenTeams", "settings.json");
            if (File.Exists(AppPaths.SettingsFile) || !File.Exists(oldSettings)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
            File.Copy(oldSettings, AppPaths.SettingsFile);
            var store = new SettingsStore(AppPaths.SettingsFile, new DpapiProtector());
            string oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ListenTeams");
            store.Update(s =>
            {
                // Teams pairs per application name: Echo must be allowed again.
                s.TeamsApiToken = null;
                s.PairingRequestedOnce = false;
                if (string.Equals(s.OutputFolder, oldDefault, StringComparison.OrdinalIgnoreCase) && !HasContent(oldDefault))
                {
                    s.OutputFolder = AppSettings.DefaultOutputFolder;
                }
            });
            Core.Log.Info("Paramètres de ListenTeams importés.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.Log.Warn($"Import des paramètres de ListenTeams impossible : {ex.Message}");
        }
    }

    private static bool HasContent(string folder) =>
        Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
}

internal static class AppPaths
{
    public static string AppFolder => AppContext.BaseDirectory;

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Echo");

    public static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Echo", "settings.json");

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
