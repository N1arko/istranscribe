namespace IsTranscribe.Core.Detection;

/// <summary>
/// Privacy-safe facts produced by detection signal providers.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public enum MeetingEvidenceKind
{
    KnownApplicationIdentity,
    UserDefinedApplicationIdentity,
    BrowserServiceIdentity,
    RenderSessionActive,
    RenderSpeech,
    MicrophoneInUse,
    MicrophoneSpeech,
    ConversationalAlternation,
    MeetingWindow,
    MeetingControls,
    ForegroundWindow,
    ProcessStable,
    MediaPlayback,
    NotificationLike,
    HardExclusion
}

public sealed record MeetingEvidenceFact(
    MeetingEvidenceKind Kind,
    string RuleId,
    double Strength = 1,
    string ProviderId = "core")
{
    public double NormalizedStrength => Math.Clamp(Strength, 0, 1);
}

public sealed record MeetingScoreContribution(
    MeetingEvidenceKind Kind,
    string RuleId,
    string ProviderId,
    double Strength,
    int Weight,
    int ScoreDelta);
