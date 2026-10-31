namespace Echo.Core.Teams;

public enum CallEvent
{
    None,
    Started,
    Ended,
}

/// <summary>
/// Decides when a call starts and ends in one calling application, from several signals:
/// <list type="bullet">
/// <item>the Teams local API (authoritative when available, Teams only);</item>
/// <item>Windows reporting that the application is using the microphone (Teams and Discord
/// keep it open for the whole call, even when muted), which needs no configuration.</item>
/// </list>
/// Signals are debounced so a short glitch neither starts nor splits a recording.
/// </summary>
public sealed class CallDetector
{
    private readonly TimeSpan _micOnlyStartDelay;
    private readonly TimeSpan _endDelay;

    private bool _appRunning;
    private TeamsApiState _api = TeamsApiState.Disconnected;
    private bool _micInUse;
    private DateTime? _rawSince;
    private bool _rawInCall;
    private bool _manualRecording;
    private bool _suppressedUntilCallEnds;

    public CallDetector(TimeSpan? micOnlyStartDelay = null, TimeSpan? endDelay = null)
    {
        _micOnlyStartDelay = micOnlyStartDelay ?? TimeSpan.FromSeconds(2);
        _endDelay = endDelay ?? TimeSpan.FromSeconds(4);
    }

    /// <summary>True while a recording should be running.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>True when the current recording was started by hand.</summary>
    public bool IsManual => IsRecording && _manualRecording;

    public bool AppRunning => _appRunning;

    public bool CallInProgress => _rawInCall;

    public void SetAppRunning(bool running) => _appRunning = running;

    public void SetApiState(TeamsApiState state) => _api = state;

    public void SetMicInUse(bool inUse) => _micInUse = inUse;

    /// <summary>Starts recording now, even if no call is detected.</summary>
    public CallEvent StartManually()
    {
        _suppressedUntilCallEnds = false;
        if (IsRecording) return CallEvent.None;
        _manualRecording = true;
        IsRecording = true;
        return CallEvent.Started;
    }

    /// <summary>Stops recording; the current call will not be recorded again automatically.</summary>
    public CallEvent StopManually()
    {
        if (!IsRecording) return CallEvent.None;
        _suppressedUntilCallEnds = _rawInCall;
        _manualRecording = false;
        IsRecording = false;
        return CallEvent.Ended;
    }

    /// <summary>Evaluates the signals; call regularly (about once per second).</summary>
    public CallEvent Tick(DateTime now)
    {
        bool apiSaysCall = _api.SuggestsMeeting;
        bool raw = _appRunning && (apiSaysCall || _micInUse);
        if (raw != _rawInCall)
        {
            _rawInCall = raw;
            _rawSince = now;
        }
        var stableFor = now - (_rawSince ?? now);

        if (!_rawInCall && _suppressedUntilCallEnds && stableFor >= _endDelay)
        {
            _suppressedUntilCallEnds = false;
        }

        if (_manualRecording) return CallEvent.None;

        if (!IsRecording && _rawInCall && !_suppressedUntilCallEnds)
        {
            var delay = apiSaysCall ? TimeSpan.Zero : _micOnlyStartDelay;
            if (stableFor >= delay)
            {
                IsRecording = true;
                return CallEvent.Started;
            }
        }
        else if (IsRecording && !_rawInCall && stableFor >= _endDelay)
        {
            IsRecording = false;
            return CallEvent.Ended;
        }

        return CallEvent.None;
    }
}
