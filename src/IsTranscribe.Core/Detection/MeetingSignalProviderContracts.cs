using IsTranscribe.Core.Audio;

namespace IsTranscribe.Core.Detection;

/// <summary>
/// Platform boundary for privacy-reduced window and UI Automation evidence.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public interface IMeetingWindowEvidenceProvider
{
    ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
        AudioPlatformSnapshot audioPlatform,
        MeetingProfileRegistry profiles,
        CancellationToken cancellationToken);
}

/// <summary>
/// Estimates speech from demand-gated, bounded in-memory audio and exposes only privacy-reduced summaries.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public interface IMeetingSpeechActivityProvider : IAsyncDisposable
{
    MeetingSpeechActivitySnapshot Snapshot { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
    ValueTask ApplyObservationDemandAsync(
        MeetingSpeechObservationDemand demand,
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask<MeetingSpeechActivitySnapshot> ObserveAsync(
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken);

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
    async ValueTask<MeetingSpeechCaptureHealth> ObserveMicrophoneCaptureHealthAsync(
        AudioPlatformSnapshot audioPlatform,
        CancellationToken cancellationToken)
    {
        var observation = await ObserveAsync(audioPlatform, cancellationToken).ConfigureAwait(false);
        return observation.MicrophoneCaptureHealth;
    }
}

/// <summary>
/// Declares the exact sample-bearing detection sources allowed for the current observation.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#audio-observation-lifecycle
/// </remarks>
public sealed record MeetingSpeechObservationDemand(
    IReadOnlyCollection<int> RenderRootProcessIds,
    bool ObserveMicrophone)
{
    public static MeetingSpeechObservationDemand Empty { get; } = new([], ObserveMicrophone: false);
}

public sealed record MeetingWindowEvidenceSnapshot(
    DateTimeOffset ObservedAtUtc,
    string CandidateId,
    string ProfileId,
    string DisplayName,
    MeetingCandidateContext Context,
    int RootProcessId,
    string ProcessName,
    IReadOnlyList<MeetingEvidenceFact> Evidence);

public sealed record MeetingSpeechActivitySummary(
    double SpeechProbability,
    bool IsSustainedSpeech,
    TimeSpan ObservedDuration,
    double ActiveRatio)
{
    public static MeetingSpeechActivitySummary Empty { get; } = new(0, false, TimeSpan.Zero, 0);
}

public sealed record MeetingSpeechActivitySnapshot(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyDictionary<int, MeetingSpeechActivitySummary> RenderByRootProcessId,
    MeetingSpeechActivitySummary Microphone,
    IReadOnlyDictionary<int, double> ConversationalAlternationByRootProcessId,
    IReadOnlyDictionary<int, bool>? RenderUsesDescendantProcessByRootProcessId = null)
{
    public MeetingSpeechCaptureHealth MicrophoneCaptureHealth { get; init; }

    public static MeetingSpeechActivitySnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        new Dictionary<int, MeetingSpeechActivitySummary>(),
        MeetingSpeechActivitySummary.Empty,
        new Dictionary<int, double>(),
        new Dictionary<int, bool>());
}

public enum MeetingSpeechCaptureHealth
{
    Unknown,
    Available,
    Unavailable
}
