using System.Text.Json;
using System.Text.Json.Serialization;
using Echo.Core.Speakers;
using Echo.Core.Transcription;

namespace Echo.Core.Discord;

/// <summary>Someone of a recorded Discord call, with the moments Discord showed them speaking.</summary>
public sealed class DiscordCallMember
{
    /// <summary>Number used in the transcript (1, 2…; 0 for the user themself).</summary>
    public int Voice { get; set; }

    public string Id { get; set; } = string.Empty;

    /// <summary>Name shown in Discord (server nickname, else display name, else username).</summary>
    public string Name { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>[start, end] in seconds from the start of the recording.</summary>
    public List<double[]> Speaking { get; set; } = [];

    [JsonIgnore]
    public double TalkSeconds => Speaking.Sum(s => s[1] - s[0]);
}

/// <summary>What Discord told about a recorded call (saved as discord.json next to the audio).</summary>
public sealed class DiscordCallData
{
    public const string FileName = "discord.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DiscordCallMember? Me { get; set; }

    public string? Channel { get; set; }

    public string? Guild { get; set; }

    public List<DiscordCallMember> Participants { get; set; } = [];

    [JsonIgnore]
    public bool HasSpeech => Participants.Any(p => p.Speaking.Count > 0);

    public void Save(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static DiscordCallData? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DiscordCallData>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{path} illisible : {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Notes, during a recording, who is in the Discord call and when each person speaks
/// (Discord's own speaking indicator), on the recording's timeline. Thread-safe.
/// </summary>
public sealed class DiscordCallLog
{
    private sealed class Entry
    {
        public required DiscordCallMember Member { get; init; }

        public double? SpeakingSince { get; set; }

        public bool Present { get; set; }

        public bool Muted { get; set; }
    }

    private readonly long _start100ns;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private Entry? _me;
    private string? _channel;
    private string? _guild;
    private int _nextVoice = 1;

    /// <param name="start100ns">Start of the recording (<see cref="Audio.AudioClock"/> time of its first sample).</param>
    public DiscordCallLog(long start100ns, DiscordVoiceState current, long now100ns)
    {
        _start100ns = start100ns;
        Update(current);
        lock (_lock)
        {
            if (current.MeSpeaking && _me is not null) _me.SpeakingSince = Seconds(now100ns);
            foreach (var p in current.Participants.Where(p => p.Speaking))
            {
                if (_entries.TryGetValue(p.Id, out var entry)) entry.SpeakingSince = Seconds(now100ns);
            }
        }
    }

    /// <summary>Names, presence and channel from Discord.</summary>
    public void Update(DiscordVoiceState state)
    {
        lock (_lock)
        {
            if (state.Me is { } me)
            {
                _me ??= Get(me.Id, isMe: true);
                _me.Member.Name = me.DisplayName;
                _me.Member.Username = me.Username;
                _me.Present = true;
            }
            if (state.ChannelId is null) return;

            _channel = state.ChannelName;
            _guild = state.GuildName;
            var present = state.Participants.Select(p => p.Id).ToHashSet();
            foreach (var entry in _entries.Values.Where(e => e != _me)) entry.Present = present.Contains(entry.Member.Id);
            foreach (var p in state.Participants)
            {
                var entry = Get(p.Id, isMe: false);
                entry.Member.Name = p.Name;
                entry.Member.Username = p.Username;
                entry.Muted = p.Muted || p.Deafened;
                entry.Present = true;
            }
        }
    }

    /// <summary>Discord showed <paramref name="userId"/> starting or stopping to speak.</summary>
    public void Speaking(string userId, bool speaking, long at100ns)
    {
        lock (_lock)
        {
            bool isMe = _me?.Member.Id == userId;
            var entry = isMe ? _me! : Get(userId, isMe: false);
            double t = Seconds(at100ns);
            if (speaking)
            {
                entry.SpeakingSince ??= t;
            }
            else if (entry.SpeakingSince is { } since)
            {
                if (t > since) entry.Member.Speaking.Add([Math.Round(since, 2), Math.Round(t, 2)]);
                entry.SpeakingSince = null;
            }
        }
    }

    /// <summary>Who is speaking right now, for the recording screen.</summary>
    public LiveSpeakers Snapshot(long now100ns)
    {
        lock (_lock)
        {
            double now = Seconds(now100ns);
            double Talk(Entry e) => e.Member.TalkSeconds + (e.SpeakingSince is { } s ? Math.Max(0, now - s) : 0);

            var others = _entries.Values
                .Where(e => e != _me && (e.Present || e.Member.Speaking.Count > 0 || e.SpeakingSince is not null))
                .OrderBy(e => e.Member.Voice)
                .ToList();
            int? current = others.Where(e => e.SpeakingSince is not null)
                .OrderByDescending(e => e.SpeakingSince)
                .Select(e => (int?)e.Member.Voice)
                .FirstOrDefault();
            return new LiveSpeakers(
                MeSpeaking: _me?.SpeakingSince is not null,
                MeTalkSeconds: _me is null ? 0 : Talk(_me),
                CurrentSpeakerId: current,
                Speakers: others.Select(e => new LiveSpeaker(e.Member.Voice, e.Member.Name, e.SpeakingSince is not null, Talk(e), e.Muted, e.Present)).ToList(),
                Channel: ChannelLabel(_channel, _guild),
                ExactNames: true);
        }
    }

    /// <summary>Closes the open speaking turns and returns what to save with the recording.</summary>
    public DiscordCallData Finish(long end100ns)
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values.Where(e => e.SpeakingSince is not null).ToList())
            {
                Speaking(entry.Member.Id, false, end100ns);
            }
            return new DiscordCallData
            {
                Me = _me?.Member,
                Channel = _channel,
                Guild = _guild,
                Participants = _entries.Values.Where(e => e != _me).Select(e => e.Member).OrderBy(m => m.Voice).ToList(),
            };
        }
    }

    public static string? ChannelLabel(string? channel, string? guild) =>
        string.IsNullOrWhiteSpace(channel) ? null
        : string.IsNullOrWhiteSpace(guild) ? channel
        : $"{channel} · {guild}";

    private Entry Get(string id, bool isMe)
    {
        if (_entries.TryGetValue(id, out var entry)) return entry;
        entry = new Entry { Member = new DiscordCallMember { Id = id, Voice = isMe ? 0 : _nextVoice++, Name = "Participant" } };
        _entries[id] = entry;
        return entry;
    }

    private double Seconds(long at100ns) => Math.Max(0, (at100ns - _start100ns) / 10_000_000.0);
}

/// <summary>Gives each sentence of the transcript the Discord name of whoever said it.</summary>
public static class DiscordAttribution
{
    /// <summary>Margin around speaking turns: Discord's indicator and the audio are not perfectly in step.</summary>
    private const double Margin = 0.3;

    /// <summary>A sentence nobody was speaking during takes the nearest speaker within this delay.</summary>
    private const double NearestLimit = 2.0;

    public static (List<TranscriptSegment> Segments, Dictionary<int, string> Names) Apply(IReadOnlyList<TranscriptSegment> segments, DiscordCallData data)
    {
        var result = new List<TranscriptSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Speaker != Speaker.Others)
            {
                result.Add(segment);
                continue;
            }
            result.Add(segment with { Voice = Best(data.Participants, segment.Start.TotalSeconds, segment.End.TotalSeconds) });
        }

        var used = result.Where(s => s.Voice > 0).Select(s => s.Voice).ToHashSet();
        var names = data.Participants.Where(p => used.Contains(p.Voice)).ToDictionary(p => p.Voice, p => p.Name);
        return (result, names);
    }

    private static int Best(IEnumerable<DiscordCallMember> participants, double start, double end)
    {
        int best = 0;
        double bestOverlap = 0;
        double nearest = double.MaxValue;
        int nearestVoice = 0;
        foreach (var p in participants)
        {
            double overlap = 0;
            foreach (var turn in p.Speaking)
            {
                double from = Math.Max(start, turn[0] - Margin), to = Math.Min(end, turn[1] + Margin);
                if (to > from) overlap += to - from;
                double distance = turn[1] + Margin < start ? start - turn[1] - Margin
                    : turn[0] - Margin > end ? turn[0] - Margin - end
                    : 0;
                if (distance < nearest)
                {
                    nearest = distance;
                    nearestVoice = p.Voice;
                }
            }
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = p.Voice;
            }
        }
        if (best > 0) return best;
        return nearest <= NearestLimit ? nearestVoice : 0;
    }
}
