using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#confidence
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.initial
/// </summary>
public sealed class MeetingConfidenceScorerTests
{
    private readonly MeetingConfidenceScorer _scorer = new();

    [Fact]
    public void KnownApplicationIdentityAloneStaysIgnored()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")));

        Assert.Equal(MeetingDecisionBand.Ignored, result.Band);
        Assert.Equal(27, result.Score);
    }

    [Fact]
    public void ShortNotificationSoundNeverReachesAskBand()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "teams.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.NotificationLike, "audio.short-notification")));

        Assert.Equal(MeetingDecisionBand.Ignored, result.Band);
        Assert.True(result.Score < 40);
    }

    [Fact]
    public void DedicatedConversationalEvidenceReachesAskBand()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render", 0.9),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneInUse, "mic.in-use"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneSpeech, "vad.mic", 0.8),
            new MeetingEvidenceFact(MeetingEvidenceKind.ConversationalAlternation, "vad.turn-taking", 0.8),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")));

        Assert.Equal(MeetingDecisionBand.Ask, result.Band);
        Assert.True(result.Score >= 70);
        Assert.Contains(result.Contributions, static contribution =>
            contribution.Kind == MeetingEvidenceKind.ConversationalAlternation);
    }

    [Fact]
    public void BrowserPlaybackWithoutMeetingEvidenceCannotReachAskBand()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.BrowserService,
            new MeetingEvidenceFact(MeetingEvidenceKind.BrowserServiceIdentity, "browser.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneInUse, "mic.in-use"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneSpeech, "vad.mic"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ConversationalAlternation, "vad.turn-taking"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")));

        Assert.Equal(69, result.Score);
        Assert.Equal(MeetingDecisionBand.Suspected, result.Band);
        Assert.Contains("browser_requires_meeting_specific_evidence", result.DecisionReasons);
    }

    [Fact]
    public void BrowserMeetingWindowAndRemoteSpeechReachAskBand()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.BrowserService,
            new MeetingEvidenceFact(MeetingEvidenceKind.BrowserServiceIdentity, "google-meet.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "google-meet.meeting-window"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")));

        Assert.Equal(MeetingDecisionBand.Ask, result.Band);
        Assert.True(result.Score >= 70);
    }

    [Fact]
    public void WeakBrowserBrandWindowDoesNotLiftPlaybackCap()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.BrowserService,
            new MeetingEvidenceFact(MeetingEvidenceKind.BrowserServiceIdentity, "google-meet.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSessionActive, "audio.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.RenderSpeech, "vad.render"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneInUse, "mic.in-use"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneSpeech, "vad.mic"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ConversationalAlternation, "vad.turn-taking"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "google-meet.brand-window", 0.15),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable")));

        Assert.Equal(69, result.Score);
        Assert.Equal(MeetingDecisionBand.Suspected, result.Band);
        Assert.Contains("browser_requires_meeting_specific_evidence", result.DecisionReasons);
    }

    [Fact]
    public void RepeatedEvidenceKindContributesAtMostOneWeight()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "window.one", 0.8),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "window.two", 1)));

        Assert.Equal(26, result.Score);
        Assert.Single(result.Contributions);
    }

    [Fact]
    public void HardExclusionWinsOverPositiveEvidence()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "zoom.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingControls, "zoom.controls"),
            new MeetingEvidenceFact(MeetingEvidenceKind.HardExclusion, "system-process")));

        Assert.True(result.IsHardExcluded);
        Assert.Equal(0, result.Score);
        Assert.Equal(MeetingDecisionBand.Ignored, result.Band);
    }

    [Fact]
    public void RegistryContainsInitialCoverageAndAcceptsUserProfiles()
    {
        var custom = MeetingAppProfile.CreateUserDefined("my-call", "My Call", "my-call.exe");
        var registry = new MeetingProfileRegistry([custom]);

        Assert.Equal(
            ["generic-browser", "google-meet", "kontur-talk", "microsoft-teams", "user:my-call", "yandex-telemost", "zoom"],
            registry.Profiles.Select(static profile => profile.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("chrome.exe", registry.WatchedProcessNames);
        Assert.Contains("zen.exe", registry.WatchedProcessNames);
        Assert.Contains("opera.exe", registry.WatchedProcessNames);
        Assert.Contains("vivaldi.exe", registry.WatchedProcessNames);
        Assert.Contains("chromium.exe", registry.WatchedProcessNames);
        Assert.Contains("my-call.exe", registry.WatchedProcessNames);
        Assert.Contains("ms-teams.exe", registry.WatchedProcessNames);
        Assert.Contains("yandextelemost.exe", registry.WatchedProcessNames);
        Assert.Contains("ktalk.exe", registry.WatchedProcessNames);
        Assert.Contains("konturtalk.exe", registry.WatchedProcessNames);
        var fallback = registry.FindById("generic-browser")!;
        Assert.Equal(MeetingProfileRegistry.ProfileSchemaVersion, fallback.SchemaVersion);
        Assert.Equal(MeetingProcessTreeBehavior.RootAndDescendants, fallback.ProcessTreeBehavior);
        Assert.Equal(MeetingCaptureSourcePreference.ProcessOutputPreferred, fallback.CaptureSourcePreference);
        Assert.True(fallback.AllowControlOnlyBrowserMatch);
        Assert.True(fallback.IsFallbackProfile);
        Assert.NotEmpty(fallback.HardExclusionRules!);
    }

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.initial
    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("firefox")]
    [InlineData("ZEN.EXE")]
    [InlineData("vivaldi.exe")]
    public void RegistryIdentifiesKnownBrowserHostsWithoutReplacingServiceProfiles(string processName)
    {
        var registry = new MeetingProfileRegistry();

        Assert.True(registry.IsBrowserProcess(processName));
        Assert.False(registry.IsBrowserProcess("zoom.exe"));

        var zenProfiles = registry.MatchProcess("zen.exe");
        Assert.Contains(zenProfiles, static profile => profile.Id == "google-meet");
        Assert.Contains(zenProfiles, static profile => profile.Id == "microsoft-teams");
        Assert.Contains(zenProfiles, static profile => profile.Id == "yandex-telemost");
        Assert.Contains(zenProfiles, static profile => profile.Id == "kontur-talk");
        Assert.Contains(zenProfiles, static profile => profile.Id == "generic-browser");
    }

    [Fact]
    public void ControlRuleRequiresTwoIndependentCallControlGroups()
    {
        var zoom = new MeetingProfileRegistry().FindById("zoom")!;
        var rule = Assert.Single(zoom.ControlRules);

        Assert.Equal(0, rule.MatchStrength(["Mute"]));
        Assert.Equal(1, rule.MatchStrength(["Unmute", "Leave"]));
    }

    [Fact]
    public void IdleBrandedWindowPlusMicrophoneSpeechStaysBelowAsk()
    {
        var result = _scorer.Score(Frame(
            MeetingCandidateContext.DedicatedApplication,
            new MeetingEvidenceFact(MeetingEvidenceKind.KnownApplicationIdentity, "telemost.identity"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MeetingWindow, "telemost.brand-window", 0.25),
            new MeetingEvidenceFact(MeetingEvidenceKind.ForegroundWindow, "window.foreground"),
            new MeetingEvidenceFact(MeetingEvidenceKind.ProcessStable, "process.stable"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneInUse, "mic.active"),
            new MeetingEvidenceFact(MeetingEvidenceKind.MicrophoneSpeech, "mic.speech")));

        Assert.True(result.Score < MeetingScoringPolicy.Default.AskThreshold, result.ToString());
        Assert.NotEqual(MeetingDecisionBand.Ask, result.Band);
    }

    private static MeetingObservationFrame Frame(
        MeetingCandidateContext context,
        params MeetingEvidenceFact[] evidence) => new(
            DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
            CandidateId: "zoom:42",
            ProfileId: "zoom",
            DisplayName: "Zoom",
            context,
            RootProcessId: 42,
            ProcessName: "zoom.exe",
            evidence);
}
