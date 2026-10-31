using System.Text.RegularExpressions;

namespace Echo.Core.Apps;

/// <summary>Calling applications Echo can record.</summary>
public enum CallApp
{
    Teams,
    Discord,
}

public sealed record CallAppInfo(
    CallApp App,
    string DisplayName,
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> PackagePrefixes);

public static class CallApps
{
    public static IReadOnlyList<CallAppInfo> All { get; } =
    [
        new(CallApp.Teams, "Teams", ["ms-teams.exe", "teams.exe"], ["MSTeams_"]),
        new(CallApp.Discord, "Discord", ["discord.exe", "discordptb.exe", "discordcanary.exe"], ["Discord"]),
    ];

    public static CallAppInfo Get(CallApp app) => All.First(a => a.App == app);

    public static string Name(CallApp app) => Get(app).DisplayName;

    public static bool IsExecutableOf(CallApp app, string executable) =>
        Get(app).ExecutableNames.Contains(executable, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Matches a key of the Windows microphone "consent store": packaged apps use their
    /// package family name ("MSTeams_8wekyb3d8bbwe"), desktop apps their path with '#'
    /// separators ("C:#Users#me#AppData#Local#Discord#app-1.0.9#Discord.exe").
    /// </summary>
    public static bool MatchesConsentKey(CallApp app, string keyName, bool packaged)
    {
        var info = Get(app);
        return packaged
            ? info.PackagePrefixes.Any(p => keyName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            : info.ExecutableNames.Any(e => keyName.EndsWith("#" + e, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Reads the Discord voice controls (bottom-left user panel): the microphone button
/// ("Mute"/"Rendre muet"…) and the headphones button ("Deafen"/"Mettre en sourdine"…).
/// Discord exposes them as switches, whose on/off state is the mute state.
/// </summary>
public static partial class DiscordControls
{
    public static bool IsDeafenButton(string? name) => name is not null && DeafenRegex().IsMatch(name);

    public static bool IsMicButton(string? name) => name is not null && !IsDeafenButton(name) && MicRegex().IsMatch(name);

    /// <summary>
    /// Combines both controls. Toggle states (true = switched on) are preferred; labels are
    /// the fallback for builds that change the label instead ("Unmute", "Réactiver le micro").
    /// Being deafened in Discord also mutes the microphone.
    /// </summary>
    public static bool? IsMuted(bool? micToggle, string? micLabel, bool? deafenToggle, string? deafenLabel)
    {
        if (deafenToggle == true) return true;
        if (deafenToggle is null && deafenLabel is not null && UndoLabelRegex().IsMatch(deafenLabel)) return true;
        if (micToggle is { } toggled) return toggled;
        return MuteResolverLabel(micLabel);
    }

    private static bool? MuteResolverLabel(string? label) => Teams.MuteResolver.ParseMicButtonLabel(label);

    [GeneratedRegex(@"deafen|sourdine|\bson\b|\bcasque|ensordec|kopfhörer|ausgabe", RegexOptions.IgnoreCase)]
    private static partial Regex DeafenRegex();

    [GeneratedRegex(@"mute|muet|micro|\bmic\b|stumm|silenci|mikrofon", RegexOptions.IgnoreCase)]
    private static partial Regex MicRegex();

    [GeneratedRegex(@"undeafen|réactiver|reactiver|rétablir|retablir|aufheben", RegexOptions.IgnoreCase)]
    private static partial Regex UndoLabelRegex();
}
