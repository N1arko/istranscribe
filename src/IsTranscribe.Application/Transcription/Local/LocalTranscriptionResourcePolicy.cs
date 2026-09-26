using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Application.Transcription.Local;

/// <summary>
/// Platform-neutral resource evidence consumed before a worker process is started.
/// Nullable values mean that the platform adapter has not supplied that measurement.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// </remarks>
public sealed record LocalTranscriptionResourceState(
    bool IsLowPowerMode,
    long? AvailableMemoryBytes,
    long? AvailableDiskBytes,
    string? StableBlockCode = null)
{
    /// <summary>
    /// Total physical memory reported by the operating system. This remains separate from
    /// currently available memory so model suitability and transient pressure are both explicit.
    /// </summary>
    /// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
    public long? InstalledMemoryBytes { get; init; }
}

public sealed record LocalTranscriptionResourceRequest(
    string JobId,
    string SessionId,
    TranscriptionTriggerKind TriggerKind,
    long RequiredMemoryBytes,
    long RequiredDiskBytes,
    bool HasOneShotPowerOverride);

public enum LocalTranscriptionResourceDisposition
{
    Allowed,
    Deferred,
    AttentionRequired,
}

public sealed record LocalTranscriptionResourceAssessment(
    LocalTranscriptionResourceDisposition Disposition,
    LocalTranscriptionResourceState State,
    string? StableCode = null,
    string? SafeMessage = null,
    bool CanUseOneShotManualOverride = false);

/// <summary>
/// Additive platform seam for RAM, disk and energy-saver policy. Production adapters can provide
/// measured evidence; the neutral implementation remains available for deterministic tests and
/// platforms whose measurement adapter is still pending.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
public interface ILocalTranscriptionResourcePolicy
{
    LocalTranscriptionResourceState CurrentState { get; }

    ValueTask<LocalTranscriptionResourceAssessment> AssessAsync(
        LocalTranscriptionResourceRequest request,
        CancellationToken cancellationToken);
}

public sealed class DeterministicLocalTranscriptionResourcePolicy : ILocalTranscriptionResourcePolicy
{
    private readonly LocalTranscriptionResourceAssessment _assessment;

    public DeterministicLocalTranscriptionResourcePolicy(
        LocalTranscriptionResourceAssessment assessment)
    {
        _assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
    }

    public LocalTranscriptionResourceState CurrentState => _assessment.State;

    public ValueTask<LocalTranscriptionResourceAssessment> AssessAsync(
        LocalTranscriptionResourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_assessment);
    }
}

/// <summary>
/// Volatile one-run authorization. It is consumed by the matching manual job and is never written
/// to settings or reused by another session.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources</remarks>
internal sealed class LocalPowerOverrideStore
{
    private readonly object _sync = new();
    private readonly HashSet<string> _authorizedJobIds = new(StringComparer.Ordinal);

    public void Authorize(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_sync)
        {
            _authorizedJobIds.Add(jobId);
        }
    }

    public bool Consume(string jobId)
    {
        lock (_sync)
        {
            return _authorizedJobIds.Remove(jobId);
        }
    }

    public void Revoke(string jobId)
    {
        lock (_sync)
        {
            _authorizedJobIds.Remove(jobId);
        }
    }
}
