using System.Text.Json;

namespace ListenTeams.Core.Teams;

/// <summary>
/// What the Teams "third-party app API" (local WebSocket on port 8124) tells us.
/// Teams only sends the meeting state (mute, in meeting…) to a client the user has paired.
/// </summary>
public sealed record TeamsApiState
{
    public static TeamsApiState Disconnected { get; } = new();

    public bool Connected { get; init; }

    /// <summary>True when Teams sent a meetingState object (only happens once paired).</summary>
    public bool HasMeetingState { get; init; }

    public bool IsInMeeting { get; init; }

    public bool IsMuted { get; init; }

    public bool CanToggleMute { get; init; }

    /// <summary>Teams accepts a pairing request right now (the user is in a meeting).</summary>
    public bool CanPair { get; init; }

    /// <summary>A meeting seems to be in progress according to the API, paired or not.</summary>
    public bool SuggestsMeeting => Connected && (HasMeetingState ? IsInMeeting : CanToggleMute || CanPair);
}

/// <summary>One message received from the Teams local API.</summary>
public abstract record TeamsApiMessage
{
    public sealed record MeetingUpdate(
        bool HasMeetingState,
        bool IsInMeeting,
        bool IsMuted,
        bool CanToggleMute,
        bool CanPair) : TeamsApiMessage;

    public sealed record TokenRefresh(string Token) : TeamsApiMessage;

    public sealed record Response(int RequestId, string? Text, string? Error) : TeamsApiMessage;

    public sealed record Unknown(string Raw) : TeamsApiMessage;

    public static TeamsApiMessage Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new Unknown(json);

            if (root.TryGetProperty("tokenRefresh", out var token) && token.ValueKind == JsonValueKind.String)
            {
                return new TokenRefresh(token.GetString() ?? string.Empty);
            }

            if (root.TryGetProperty("meetingUpdate", out var update) && update.ValueKind == JsonValueKind.Object)
            {
                bool hasState = update.TryGetProperty("meetingState", out var state) && state.ValueKind == JsonValueKind.Object;
                update.TryGetProperty("meetingPermissions", out var permissions);
                return new MeetingUpdate(
                    hasState,
                    hasState && Bool(state, "isInMeeting"),
                    hasState && Bool(state, "isMuted"),
                    Bool(permissions, "canToggleMute"),
                    Bool(permissions, "canPair"));
            }

            if (root.TryGetProperty("requestId", out var id) && id.TryGetInt32(out int requestId))
            {
                return new Response(
                    requestId,
                    root.TryGetProperty("response", out var r) ? r.ToString() : null,
                    root.TryGetProperty("errorMsg", out var e) ? e.ToString() : null);
            }
        }
        catch (JsonException)
        {
        }

        return new Unknown(json);
    }

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;
}
