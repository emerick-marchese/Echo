using System.Windows.Automation;
using ListenTeams.Core;
using ListenTeams.Core.Teams;

namespace ListenTeams.Platform;

/// <summary>
/// Fallback when the Teams local API is not enabled: reads the label of the microphone
/// button in the Teams call window through UI Automation ("Mute"/"Unmute",
/// "Désactiver le micro"/"Activer le micro"…).
/// </summary>
internal sealed class TeamsWindowMuteReader
{
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(5);

    private AutomationElement? _button;
    private DateTime _lastSearch = DateTime.MinValue;

    /// <summary>True = muted, false = unmuted, null = unknown. Call from a background thread.</summary>
    public bool? ReadMuted(IReadOnlyCollection<int> teamsProcessIds)
    {
        if (_button is not null)
        {
            try
            {
                var state = MuteResolver.ParseMicButtonLabel(_button.Current.Name);
                if (state is not null) return state;
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            _button = null;
        }

        if (DateTime.UtcNow - _lastSearch < SearchInterval) return null;
        _lastSearch = DateTime.UtcNow;

        try
        {
            _button = FindMicrophoneButton(teamsProcessIds);
            return _button is null ? null : MuteResolver.ParseMicButtonLabel(_button.Current.Name);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Lecture de la fenêtre Teams impossible : {ex.Message}");
            _button = null;
            return null;
        }
    }

    public void Reset()
    {
        _button = null;
        _lastSearch = DateTime.MinValue;
    }

    private static AutomationElement? FindMicrophoneButton(IReadOnlyCollection<int> teamsProcessIds)
    {
        var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, "microphone-button");
        var buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

        foreach (int pid in teamsProcessIds)
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid));

            foreach (AutomationElement window in windows)
            {
                if (window.FindFirst(TreeScope.Descendants, byId) is { } button) return button;
            }

            foreach (AutomationElement window in windows)
            {
                foreach (AutomationElement candidate in window.FindAll(TreeScope.Descendants, buttons))
                {
                    string name = candidate.Current.Name ?? string.Empty;
                    if (name.Contains("mic", StringComparison.OrdinalIgnoreCase)
                        && MuteResolver.ParseMicButtonLabel(name) is not null)
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
