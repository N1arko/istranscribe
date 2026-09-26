using System.Security.Cryptography;
using System.Text.Json;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Application.Transcription;

public sealed class TranscriptionCommandException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Validates one explicit/manual or previously opted-in automatic request and freezes its
/// provider, model, consent and source identity before it enters the durable queue.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed class TranscriptionJobEnqueuer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TranscriptionJobRepository _repository;
    private readonly TranscriptionEngineRegistry _engines;
    private readonly ISecretVault _secretVault;
    private readonly string _workerRoot;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalTranscriptionExecutionLeaseProvider? _localExecutionLeases;

    public TranscriptionJobEnqueuer(
        TranscriptionJobRepository repository,
        TranscriptionEngineRegistry engines,
        ISecretVault secretVault,
        string workerRoot,
        TimeProvider? timeProvider = null,
        ILocalTranscriptionExecutionLeaseProvider? localExecutionLeases = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _workerRoot = string.IsNullOrWhiteSpace(workerRoot)
            ? throw new ArgumentException("A transcription worker root is required.", nameof(workerRoot))
            : Path.GetFullPath(workerRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localExecutionLeases = localExecutionLeases;
    }

    public async ValueTask<TranscriptionEnqueueResult> EnqueueAsync(
        MeetingSessionListItem session,
        TranscriptionPreferences preferences,
        IReadOnlyDictionary<string, IReadOnlyList<TranscriptionModelCapability>> discoveredModels,
        bool replaceExisting,
        CancellationToken cancellationToken,
        TranscriptionTriggerKind triggerKind = TranscriptionTriggerKind.Manual)
    {
        var request = await BuildRequestAsync(
                session,
                preferences,
                discoveredModels,
                replaceExisting,
                requireReplacementConfirmation: true,
                triggerKind,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.ExecutionKind != TranscriptionExecutionKind.Local)
        {
            return await _repository.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var localExecutionLeases = _localExecutionLeases
            ?? throw new TranscriptionCommandException(
                "local_runtime_unavailable",
                "The packaged local transcription runtime is unavailable.");
        await using var executionLease = await localExecutionLeases
            .AcquireExecutionLeaseAsync(
                request.ModelId,
                request.InputSha256,
                request.RequestedLanguage ?? "auto",
                cancellationToken)
            .ConfigureAwait(false);
        return await _repository.EnqueueAsync(
                request with { LocalExecution = executionLease.Identity },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds a failed provider configuration from current user settings while retaining the
    /// original job as an immutable diagnostic record.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration.resources
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    /// </remarks>
    public async ValueTask<TranscriptionEnqueueResult> SupersedeConfigurationAsync(
        string attentionJobId,
        MeetingSessionListItem session,
        TranscriptionPreferences preferences,
        IReadOnlyDictionary<string, IReadOnlyList<TranscriptionModelCapability>> discoveredModels,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionJobId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(discoveredModels);

        var attentionJob = await _repository.GetAsync(attentionJobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TranscriptionCommandException(
                "job_unavailable",
                "This recording has no transcription job to retry.");
        if (attentionJob.Status != TranscriptionJobStatus.AttentionRequired
            || !RequiresConfigurationSupersede(attentionJob.StableErrorCode))
        {
            throw new TranscriptionCommandException(
                "configuration_retry_unavailable",
                "This transcription job does not require a new execution configuration.");
        }

        if (!string.Equals(attentionJob.SessionId, session.Id, StringComparison.Ordinal))
        {
            throw new TranscriptionCommandException(
                "job_session_mismatch",
                "The transcription job belongs to a different recording.");
        }

        var canonical = preferences.Canonicalize();
        if (!string.Equals(canonical.SelectedEngineId, attentionJob.EngineId, StringComparison.Ordinal))
        {
            throw new TranscriptionCommandException(
                "provider_change_requires_new_run",
                "Start a new transcription when changing the provider.");
        }

        var replaceExisting = attentionJob.ReplaceExisting || HasPublishedTranscript(session);
        var replacement = await BuildRequestAsync(
                session,
                canonical,
                discoveredModels,
                replaceExisting,
                requireReplacementConfirmation: false,
                TranscriptionTriggerKind.Manual,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(attentionJob.ModelId, replacement.ModelId, StringComparison.Ordinal)
            && string.Equals(
                attentionJob.EngineOptionsJson,
                replacement.EngineOptionsJson,
                StringComparison.Ordinal))
        {
            throw new TranscriptionCommandException(
                "configuration_unchanged",
                "Choose another model or update the ZDR setting before retrying.");
        }

        if (replacement.ExecutionKind != TranscriptionExecutionKind.Local)
        {
            return await _repository.SupersedeConfigurationAsync(
                    attentionJob.Id,
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var localExecutionLeases = _localExecutionLeases
            ?? throw new TranscriptionCommandException(
                "local_runtime_unavailable",
                "The packaged local transcription runtime is unavailable.");
        await using var executionLease = await localExecutionLeases
            .AcquireExecutionLeaseAsync(
                replacement.ModelId,
                replacement.InputSha256,
                replacement.RequestedLanguage ?? "auto",
                cancellationToken)
            .ConfigureAwait(false);
        return await _repository.SupersedeConfigurationAsync(
                attentionJob.Id,
                replacement with { LocalExecution = executionLease.Identity },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool RequiresConfigurationSupersede(string? stableErrorCode) =>
        stableErrorCode is "invalid_engine_configuration"
            or "zdr_route_unavailable"
            or "native_crash"
            or "insufficient_memory";

    private async ValueTask<TranscriptionJobEnqueueRequest> BuildRequestAsync(
        MeetingSessionListItem session,
        TranscriptionPreferences preferences,
        IReadOnlyDictionary<string, IReadOnlyList<TranscriptionModelCapability>> discoveredModels,
        bool replaceExisting,
        bool requireReplacementConfirmation,
        TranscriptionTriggerKind triggerKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(discoveredModels);
        var canonical = preferences.Canonicalize();
        var engineId = canonical.SelectedEngineId
            ?? throw new TranscriptionCommandException(
                "engine_not_selected",
                "Choose a transcription engine first.");
        var engine = _engines.GetRequired(engineId);
        if (!_engines.GetSupportedCurrentPlatform().Any(candidate => string.Equals(
                candidate.Capabilities.EngineId,
                engineId,
                StringComparison.Ordinal)))
        {
            throw new TranscriptionCommandException(
                "engine_platform_unsupported",
                "The selected transcription engine is unavailable on this device.");
        }

        if (!Guid.TryParseExact(session.Id, "N", out _))
        {
            throw new TranscriptionCommandException(
                "session_invalid",
                "The recording identity is invalid.");
        }

        var recordingStatus = (session.RecordingStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (recordingStatus is not ("ready" or "saved")
            || string.IsNullOrWhiteSpace(session.PrimaryAudioPath)
            || !File.Exists(session.PrimaryAudioPath))
        {
            throw new TranscriptionCommandException(
                "recording_not_ready",
                "A readable saved recording is required for transcription.");
        }

        if (requireReplacementConfirmation && !replaceExisting && HasPublishedTranscript(session))
        {
            throw new TranscriptionCommandException(
                "replacement_confirmation_required",
                "Confirm replacement of the existing transcript before starting another run.");
        }

        var duration = session.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new TranscriptionCommandException(
                "duration_unavailable",
                "The recording duration is unavailable.");
        var model = ResolveModel(engine, canonical, discoveredModels);
        string? consentRevision = null;
        DateTimeOffset? consentAt = null;
        string? privacyPolicyJson = null;
        if (engine.Capabilities.ExecutionKind == TranscriptionExecutionKind.Remote)
        {
            consentRevision = RemoteTranscriptionDisclosureCatalog.GetRequiredRevision(engineId);
            if (!canonical.HasAcceptedDisclosure(engineId, consentRevision))
            {
                throw new TranscriptionCommandException(
                    "remote_consent_required",
                    "Accept the current remote audio-transfer disclosure before transcription.");
            }

            var credential = await _secretVault
                .ReadAsync(RemoteTranscriptionSecrets.ForEngine(engineId), cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
            {
                throw new TranscriptionCommandException(
                    "credential_required",
                    "Save an API key for the selected transcription provider.");
            }

            consentAt = _timeProvider.GetUtcNow();
            privacyPolicyJson = RemoteTranscriptionDisclosureCatalog.BuildFrozenPolicyJson(
                engineId,
                canonical.RequireZeroDataRetention);
        }

        var inputPath = Path.GetFullPath(session.PrimaryAudioPath);
        var input = await FingerprintAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (input.SizeBytes <= 0)
        {
            throw new TranscriptionCommandException(
                "recording_empty",
                "The saved recording is empty.");
        }

        var jobId = Guid.NewGuid().ToString("N");
        var jobRoot = Path.Combine(_workerRoot, jobId);
        var now = _timeProvider.GetUtcNow();
        var engineOptions = engineId == OpenRouterTranscriptionEngine.EngineId
            ? JsonSerializer.Serialize(
                new RemoteJobOptions(canonical.RequireZeroDataRetention),
                SerializerOptions)
            : null;
        return new TranscriptionJobEnqueueRequest(
            jobId,
            session.Id,
            engineId,
            engine.Capabilities.ExecutionKind,
            model.Id,
            inputPath,
            input.Sha256,
            input.SizeBytes,
            now,
            duration.TotalSeconds,
            engineOptions,
            canonical.Language,
            consentRevision,
            consentAt,
            privacyPolicyJson,
            AudioChunkPlanner.CurrentManifestVersion,
            Path.Combine(jobRoot, "manifest.json"),
            replaceExisting,
            triggerKind);
    }

    private static TranscriptionModelCapability ResolveModel(
        ITranscriptionEngine engine,
        TranscriptionPreferences preferences,
        IReadOnlyDictionary<string, IReadOnlyList<TranscriptionModelCapability>> discoveredModels)
    {
        var engineId = engine.Capabilities.EngineId;
        var available = discoveredModels.TryGetValue(engineId, out var discovered)
            ? discovered
            : engine.Capabilities.Models;
        var selectedModelId = preferences.GetModelId(engineId);
        if (selectedModelId is null)
        {
            var recommended = available.FirstOrDefault(static model => model.IsRecommended)
                ?? available.FirstOrDefault();
            return recommended ?? throw new TranscriptionCommandException(
                "model_discovery_required",
                "Refresh the provider model list and choose a speech-to-text model.");
        }

        return available.FirstOrDefault(model => string.Equals(
                   model.Id,
                   selectedModelId,
                   StringComparison.Ordinal))
               ?? throw new TranscriptionCommandException(
                   "model_unavailable",
                   "The selected speech-to-text model is unavailable. Refresh the model list.");
    }

    private static bool HasPublishedTranscript(MeetingSessionListItem session) =>
        session.CurrentTranscription?.Status == TranscriptionJobStatus.Completed
        || !string.IsNullOrWhiteSpace(session.TranscriptMarkdownPath)
        || !string.IsNullOrWhiteSpace(session.TranscriptJsonPath);

    private static async ValueTask<InputFingerprint> FingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return new InputFingerprint(hash, stream.Length);
    }

    private sealed record InputFingerprint(string Sha256, long SizeBytes);

    private sealed record RemoteJobOptions(bool RequireZeroDataRetention);
}
