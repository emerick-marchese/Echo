using ListenTeams.Core.Apps;
using ListenTeams.Core.Output;
using ListenTeams.Core.Settings;
using ListenTeams.Core.Summary;

namespace ListenTeams.Tests;

public class CallAppsTests
{
    [Theory]
    [InlineData(CallApp.Teams, "ms-teams.exe", true)]
    [InlineData(CallApp.Teams, "Teams.exe", true)]
    [InlineData(CallApp.Discord, "Discord.exe", true)]
    [InlineData(CallApp.Discord, "DiscordPTB.exe", true)]
    [InlineData(CallApp.Discord, "ms-teams.exe", false)]
    [InlineData(CallApp.Teams, "msedgewebview2.exe", false)]
    public void Executables_are_recognised(CallApp app, string executable, bool expected)
    {
        Assert.Equal(expected, CallApps.IsExecutableOf(app, executable));
    }

    [Fact]
    public void Microphone_consent_keys_are_matched_per_app()
    {
        const string discordKey = "C:#Users#alice#AppData#Local#Discord#app-1.0.9187#Discord.exe";
        Assert.True(CallApps.MatchesConsentKey(CallApp.Discord, discordKey, packaged: false));
        Assert.False(CallApps.MatchesConsentKey(CallApp.Teams, discordKey, packaged: false));

        Assert.True(CallApps.MatchesConsentKey(CallApp.Teams, "MSTeams_8wekyb3d8bbwe", packaged: true));
        Assert.True(CallApps.MatchesConsentKey(CallApp.Teams, "C:#Users#alice#AppData#Local#Microsoft#Teams#current#Teams.exe", packaged: false));
        Assert.False(CallApps.MatchesConsentKey(CallApp.Discord, "MSTeams_8wekyb3d8bbwe", packaged: true));
    }

    [Fact]
    public void Settings_enable_each_app_independently()
    {
        var settings = new AppSettings();
        Assert.True(settings.IsEnabled(CallApp.Teams));
        Assert.True(settings.IsEnabled(CallApp.Discord));

        settings.SetEnabled(CallApp.Teams, false);
        Assert.False(settings.RecordTeams);
        Assert.True(settings.IsEnabled(CallApp.Discord));
    }
}

public class DiscordControlsTests
{
    [Theory]
    [InlineData("Mute", true, false)]
    [InlineData("Unmute", true, false)]
    [InlineData("Rendre muet", true, false)]
    [InlineData("Désactiver le micro", true, false)]
    [InlineData("Deafen", false, true)]
    [InlineData("Undeafen", false, true)]
    [InlineData("Mettre en sourdine", false, true)]
    [InlineData("Désactiver le son", false, true)]
    [InlineData("Paramètres utilisateur", false, false)]
    [InlineData("Personnaliser", false, false)]
    public void Buttons_are_classified(string name, bool mic, bool deafen)
    {
        Assert.Equal(mic, DiscordControls.IsMicButton(name));
        Assert.Equal(deafen, DiscordControls.IsDeafenButton(name));
    }

    [Theory]
    // Switches (aria-checked): the toggle state is the truth.
    [InlineData(true, "Mute", false, "Deafen", true)]
    [InlineData(false, "Mute", false, "Deafen", false)]
    [InlineData(false, "Mute", true, "Deafen", true)]      // deafened ⇒ microphone muted too
    // No toggle pattern: fall back on the label.
    [InlineData(null, "Unmute", null, "Deafen", true)]
    [InlineData(null, "Mute", null, "Deafen", false)]
    [InlineData(null, "Mute", null, "Undeafen", true)]
    [InlineData(null, "Réactiver le micro", null, "Désactiver le son", true)]
    [InlineData(null, "Désactiver le micro", null, "Désactiver le son", false)]
    public void Mute_state_combines_microphone_and_deafen(bool? micToggle, string micLabel, bool? deafenToggle, string deafenLabel, bool expected)
    {
        Assert.Equal(expected, DiscordControls.IsMuted(micToggle, micLabel, deafenToggle, deafenLabel));
    }
}

public class AppMetadataTests
{
    [Fact]
    public void Default_title_depends_on_the_app()
    {
        var metadata = new CallMetadata { App = "Discord", Title = CallMetadata.DefaultTitleFor("Discord") };
        Assert.Equal("Appel Discord", metadata.Title);
        Assert.True(metadata.HasDefaultTitle);

        metadata.Title = "Raid du jeudi";
        Assert.False(metadata.HasDefaultTitle);
        Assert.True(new CallMetadata().HasDefaultTitle); // older files: Teams by default
    }

    [Fact]
    public void Summary_prompt_names_the_app()
    {
        var request = new SummaryRequest("Discord", "Appel Discord", DateTime.Now, TimeSpan.FromMinutes(5), "[00:00:01] Moi : salut", "Moi", "Participants");
        Assert.Contains("appels Discord", SummaryPrompt.System(request));
        Assert.DoesNotContain("Teams", SummaryPrompt.System(request));
    }
}
