using System.Windows.Automation;
using Echo.Core;
using Echo.Core.Apps;
using Echo.Core.Teams;

namespace Echo.Platform;

/// <summary>
/// Reads the microphone state shown in the call application's window through UI
/// Automation (the API screen readers use):
/// <list type="bullet">
/// <item>Teams: label of the microphone button of the call window ("Mute"/"Unmute",
/// "Désactiver le micro"/"Activer le micro"…) — fallback when the Teams API is not enabled;</item>
/// <item>Discord: the "Mute" and "Deafen" switches of the user panel — the only source,
/// Discord has no local API for this.</item>
/// </list>
/// </summary>
internal sealed class CallWindowMuteReader
{
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(5);
    private static readonly Condition Buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

    private readonly CallApp _app;
    private AutomationElement? _mic;
    private AutomationElement? _deafen;
    private DateTime _lastSearch = DateTime.MinValue;

    public CallWindowMuteReader(CallApp app)
    {
        _app = app;
    }

    /// <summary>True = muted, false = unmuted, null = unknown. Call from a background thread.</summary>
    public bool? ReadMuted(IReadOnlyCollection<int> processIds)
    {
        if (_mic is not null)
        {
            try
            {
                var state = ReadState();
                if (state is not null) return state;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
            _mic = _deafen = null;
        }

        if (DateTime.UtcNow - _lastSearch < SearchInterval) return null;
        _lastSearch = DateTime.UtcNow;

        try
        {
            if (_app == CallApp.Discord) FindDiscordControls(processIds);
            else _mic = FindTeamsMicrophoneButton(processIds);
            return _mic is null ? null : ReadState();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Lecture de la fenêtre {CallApps.Name(_app)} impossible : {ex.Message}");
            _mic = _deafen = null;
            return null;
        }
    }

    public void Reset()
    {
        _mic = _deafen = null;
        _lastSearch = DateTime.MinValue;
    }

    private bool? ReadState()
    {
        if (_app == CallApp.Discord)
        {
            return DiscordControls.IsMuted(
                ToggleState(_mic!), _mic!.Current.Name,
                _deafen is null ? null : ToggleState(_deafen), _deafen?.Current.Name);
        }
        return MuteResolver.ParseMicButtonLabel(_mic!.Current.Name);
    }

    private static bool? ToggleState(AutomationElement element) =>
        element.TryGetCurrentPattern(TogglePattern.Pattern, out object pattern)
            ? ((TogglePattern)pattern).Current.ToggleState == System.Windows.Automation.ToggleState.On
            : null;

    private static IEnumerable<AutomationElement> Windows(IReadOnlyCollection<int> processIds)
    {
        foreach (int pid in processIds)
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            foreach (AutomationElement window in windows) yield return window;
        }
    }

    private static AutomationElement? FindTeamsMicrophoneButton(IReadOnlyCollection<int> processIds)
    {
        var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, "microphone-button");
        var windows = Windows(processIds).ToList();

        foreach (var window in windows)
        {
            if (window.FindFirst(TreeScope.Descendants, byId) is { } button) return button;
        }

        foreach (var window in windows)
        {
            foreach (AutomationElement candidate in window.FindAll(TreeScope.Descendants, Buttons))
            {
                string name = candidate.Current.Name ?? string.Empty;
                if (name.Contains("mic", StringComparison.OrdinalIgnoreCase)
                    && MuteResolver.ParseMicButtonLabel(name) is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The user panel holds the microphone and headphones switches side by side: requiring
    /// both in the same container avoids other "mute" buttons (channels, members…).
    /// </summary>
    private void FindDiscordControls(IReadOnlyCollection<int> processIds)
    {
        _mic = _deafen = null;
        var walker = TreeWalker.ControlViewWalker;
        foreach (var window in Windows(processIds))
        {
            foreach (AutomationElement candidate in window.FindAll(TreeScope.Descendants, Buttons))
            {
                if (!DiscordControls.IsMicButton(candidate.Current.Name)) continue;

                var parent = walker.GetParent(candidate);
                for (int level = 0; level < 2 && parent is not null; level++)
                {
                    foreach (AutomationElement sibling in parent.FindAll(TreeScope.Descendants, Buttons))
                    {
                        if (DiscordControls.IsDeafenButton(sibling.Current.Name))
                        {
                            _mic = candidate;
                            _deafen = sibling;
                            return;
                        }
                    }
                    parent = walker.GetParent(parent);
                }
            }
        }
    }
}
