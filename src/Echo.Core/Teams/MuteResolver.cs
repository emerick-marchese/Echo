namespace Echo.Core.Teams;

public enum MuteSource
{
    /// <summary>Teams local API (exact, pushed instantly).</summary>
    TeamsApi,

    /// <summary>Microphone button read in the application window (Teams fallback, Discord).</summary>
    AppWindow,

    /// <summary>Connection to the Discord app (exact, pushed instantly, push-to-talk included).</summary>
    DiscordRpc,

    Unknown,
}

public readonly record struct MuteDecision(bool Muted, MuteSource Source);

/// <summary>
/// Decides whether the microphone must be silenced in the recording, i.e. whether the
/// user is muted <em>in the calling application</em> (Teams, Discord).
/// </summary>
public static class MuteResolver
{
    /// <param name="api">State from the Teams local API.</param>
    /// <param name="apiPaired">We hold a token Teams gave us after the user allowed Echo.</param>
    /// <param name="windowMuted">State read from the application window (UI Automation), if found.</param>
    /// <param name="recordWhenUnknown">What to do when nothing tells us the mute state.</param>
    /// <param name="discordMuted">State from the Discord connection, if connected and in a call.</param>
    public static MuteDecision Resolve(TeamsApiState api, bool apiPaired, bool? windowMuted, bool recordWhenUnknown, bool? discordMuted = null)
    {
        if (discordMuted is { } discord)
        {
            return new MuteDecision(discord, MuteSource.DiscordRpc);
        }

        if (apiPaired && api.Connected && api.HasMeetingState && api.IsInMeeting)
        {
            return new MuteDecision(api.IsMuted, MuteSource.TeamsApi);
        }

        if (windowMuted is { } muted)
        {
            return new MuteDecision(muted, MuteSource.AppWindow);
        }

        return new MuteDecision(!recordWhenUnknown, MuteSource.Unknown);
    }

    /// <summary>
    /// Interprets the label of the Teams microphone button. The label describes the
    /// action: "Unmute"/"Activer le micro" means the microphone is currently muted.
    /// Returns null when the label is not recognised.
    /// </summary>
    public static bool? ParseMicButtonLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        string l = label.ToLowerInvariant();

        // First match wins, so order matters: "unmute" contains "mute",
        // "désactiver" contains "activer".
        foreach (var (keyword, muted) in MicLabelKeywords)
        {
            if (l.Contains(keyword, StringComparison.Ordinal)) return muted;
        }
        return null;
    }

    private static readonly (string Keyword, bool Muted)[] MicLabelKeywords =
    [
        ("unmute", true),
        ("réactiver", true),
        ("reactiver", true),
        ("rétablir", true),
        ("stummschaltung aufheben", true),
        ("désactiver", false),
        ("desactiver", false),
        ("desactivar", false),
        ("couper", false),
        ("stummschalten", false),
        ("silenciar", false),
        ("mute", false),
        ("activer", true),
        ("activar", true),
    ];
}
