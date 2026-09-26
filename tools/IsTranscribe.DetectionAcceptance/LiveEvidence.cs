using System.Text.Json;
using System.Text.Json.Serialization;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Machine-readable evidence produced by one live FEAT-011 acceptance scenario.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed record LiveEvidence(
    string SchemaVersion,
    string RunId,
    string ScenarioId,
    DateTimeOffset GeneratedAtUtc,
    TargetEvidence Target,
    BuildEvidence Build,
    SubjectEvidence Subject,
    ObservationEvidence Observation,
    OutcomeEvidence Outcome);

public sealed record TargetEvidence(
    string OsBuild,
    string OsDescription,
    string Architecture);

public sealed record BuildEvidence(
    string InformationalVersion,
    string? RuntimeAssemblySha256,
    string? RunnerAssemblySha256,
    IReadOnlyDictionary<string, string> DependencySha256,
    SourceRevisionEvidence SourceRevision);

public sealed record SourceRevisionEvidence(
    ObservationValueStatus Status,
    string? Value,
    string? Source,
    string? Reason);

public sealed record SubjectEvidence(
    string ProfileId,
    string Surface,
    ClientEvidence Client,
    SourceRevisionEvidence ServiceVersion);

public sealed record ClientEvidence(
    int? ProcessId,
    string? ProcessName,
    SourceRevisionEvidence Version,
    string? ExecutableSha256);

public sealed record ObservationEvidence(
    ActivityReadyEvidence ActivityReady,
    AskEvidence Ask,
    PromptEvidence Prompt,
    PreconfirmationAudioEvidence PreconfirmationAudio,
    PrivacyEvidence Privacy);

public sealed record ActivityReadyEvidence(
    CheckStatus Status,
    string? Source,
    DateTimeOffset? AtUtc);

public sealed record AskEvidence(
    CheckStatus Status,
    long? ElapsedMilliseconds,
    long LimitMilliseconds);

public sealed record PromptEvidence(
    CheckStatus Status,
    bool? Published,
    string? CandidateId,
    string? ObservedProfileId,
    string? ActivityState,
    int? ConfidenceScore);

public sealed record PreconfirmationAudioEvidence(
    CheckStatus Status,
    int? CaptureStartCount,
    int? UnexpectedFileCount,
    int? MeetingSessionCount,
    IReadOnlyList<string> ScannedRoots);

public sealed record PrivacyEvidence(
    CheckStatus Status,
    bool? DetectionLogSchemaValid,
    int? RawWindowTitleMatchCount,
    int? AudioPayloadFieldCount,
    int? ManagedHttpRequestCount);

public sealed record OutcomeEvidence(
    RunOutcomeStatus Status,
    string? ReasonCode);

public enum CheckStatus
{
    Pass,
    Fail,
    NotObserved
}

public enum RunOutcomeStatus
{
    Passed,
    Failed,
    Blocked,
    NotRun
}

public enum ObservationValueStatus
{
    Observed,
    NotObserved,
    NotExposed,
    Unavailable
}

public static class EvidenceJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
