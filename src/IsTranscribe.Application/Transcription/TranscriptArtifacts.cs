using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Application.Transcription;

public sealed record TranscriptDocumentContext(
    string JobId,
    Guid SessionId,
    string DisplayTitle,
    DateTimeOffset MeetingStartedAt,
    TimeSpan? MeetingDuration,
    string EngineId,
    string ModelId,
    string? RequestedLanguage,
    AudioSourceFingerprint Source,
    NormalizedTranscriptLocalExecution? LocalExecution = null,
    bool SpeakerAware = false,
    string UiLanguage = "en",
    IReadOnlyList<TranscriptionSourceTrack>? SourceTracks = null,
    IReadOnlyList<NormalizedSourceRecognition>? SourceRecognitions = null);

/// <summary>
/// Versioned provider-neutral transcript artifact persisted as JSON.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record NormalizedTranscriptDocument(
    int Version,
    string JobId,
    Guid SessionId,
    string DisplayTitle,
    DateTimeOffset MeetingStartedAt,
    long? MeetingDurationMilliseconds,
    string EngineId,
    string ModelId,
    string? RequestedLanguage,
    string? DetectedLanguage,
    NormalizedTranscriptSource Source,
    string Text,
    IReadOnlyList<NormalizedTranscriptSegment> Segments,
    IReadOnlyList<NormalizedTranscriptChunk> Chunks,
    TranscriptionUsage? Usage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NormalizedTranscriptLocalExecution? LocalExecution = null,
    string? Schema = null,
    string? Mode = null,
    string UiLanguage = "en",
    IReadOnlyList<NormalizedSpeaker>? Speakers = null,
    IReadOnlyList<NormalizedSpeakerTurn>? Turns = null,
    NormalizedDiarization? Diarization = null,
    IReadOnlyList<NormalizedSpeakerSource>? SourceInputs = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<NormalizedSourceRecognition>? SourceRecognitions = null)
{
    public const int CurrentVersion = 1;

    public static NormalizedTranscriptDocument Create(
        TranscriptDocumentContext context,
        MergedTranscript merged)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(merged);
        if (string.IsNullOrWhiteSpace(context.JobId)
            || context.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(context.EngineId)
            || string.IsNullOrWhiteSpace(context.ModelId))
        {
            throw new ArgumentException("Transcript artifact identity is incomplete.", nameof(context));
        }

        var source = context.Source.Validate();
        return new NormalizedTranscriptDocument(
            CurrentVersion,
            context.JobId,
            context.SessionId,
            NormalizeTitle(context.DisplayTitle, context.MeetingStartedAt),
            context.MeetingStartedAt,
            ToMilliseconds(context.MeetingDuration),
            context.EngineId,
            merged.ResolvedModelId ?? context.ModelId,
            context.RequestedLanguage,
            merged.DetectedLanguage,
            new NormalizedTranscriptSource(
                source.Sha256,
                source.SizeBytes,
                checked((long)Math.Round(source.Duration.TotalMilliseconds, MidpointRounding.AwayFromZero)),
                source.Format),
            merged.Text,
            merged.Segments.Select(ToNormalizedSegment).ToArray(),
            merged.Chunks.Select(static chunk => new NormalizedTranscriptChunk(
                chunk.Id,
                chunk.SequenceIndex,
                ToMilliseconds(chunk.Start)!.Value,
                ToMilliseconds(chunk.End)!.Value,
                chunk.Text,
                chunk.EngineRequestId,
                chunk.Usage)).ToArray(),
            merged.Usage,
            context.LocalExecution,
            Schema: context.SpeakerAware ? "speaker-transcript/v1" : null,
            Mode: context.SpeakerAware ? context.EngineId == "local.whisper" ? "local" : "online" : null,
            UiLanguage: context.UiLanguage,
            Speakers: context.SpeakerAware ? merged.Segments.Select(static segment => NormalizedSpeaker.FromId(segment.SpeakerLabel))
                .DistinctBy(static speaker => speaker.Id).ToArray() : null,
            Turns: context.SpeakerAware ? merged.Segments.Select(segment => new NormalizedSpeakerTurn(
                NormalizedSpeaker.FromId(segment.SpeakerLabel).Id,
                ToMilliseconds(segment.Start) ?? throw new InvalidDataException("speaker_turn_time_missing"),
                ToMilliseconds(segment.End) ?? throw new InvalidDataException("speaker_turn_time_missing"),
                segment.Text, segment.SpeakerLabel == "speaker_unresolved"
                    ? context.SourceTracks?.Any(static track => track.Role == "mixed") == true ? "mixed" : "system_output"
                    : NormalizedSpeaker.FromId(segment.SpeakerLabel).SourceKind,
                segment.SpeakerLabel == "speaker_unresolved" ? ["speaker_unresolved"] : [])).ToArray() : null,
            Diarization: context.SpeakerAware ? new NormalizedDiarization(
                IsTranscribe.Transcription.Local.Models.DiarizationAssets.RuntimeVersion,
                IsTranscribe.Transcription.Local.Models.DiarizationAssets.ParametersVersion,
                IsTranscribe.Transcription.Local.Models.DiarizationAssets.Manifest.Select(static asset => asset.Sha256).ToArray()) : null,
            SourceInputs: context.SourceTracks?.Select(static track => new NormalizedSpeakerSource(track.Role, track.Sha256,
                track.SizeBytes, track.DurationSeconds, track.OffsetSeconds)).ToArray(),
            Warnings: context.SpeakerAware ? new[] {
                context.SourceTracks?.Any(static track => track.Role == "mixed") == true ? "source_identity_unavailable" : null,
                merged.Segments.Any(static segment => segment.SpeakerLabel == "speaker_unresolved") ? "speaker_unresolved" : null
            }.OfType<string>().ToArray() : null,
            SourceRecognitions: context.SourceRecognitions);
    }

    private static NormalizedTranscriptSegment ToNormalizedSegment(TranscriptionSegment segment) => new(
        segment.Text,
        ToMilliseconds(segment.Start),
        ToMilliseconds(segment.End),
        segment.Words.Select(static word => new NormalizedTranscriptWord(
            word.Text,
            ToMilliseconds(word.Start)!.Value,
            ToMilliseconds(word.End)!.Value,
            word.Confidence)).ToArray(), segment.SpeakerLabel);

    private static long? ToMilliseconds(TimeSpan? value) => value is null
        ? null
        : checked((long)Math.Round(value.Value.TotalMilliseconds, MidpointRounding.AwayFromZero));

    private static string NormalizeTitle(string? title, DateTimeOffset startedAt)
    {
        var normalized = string.Join(' ', (title ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized)
            ? $"Meeting {startedAt:yyyy-MM-dd HH:mm}"
            : normalized;
    }
}

/// <summary>
/// Bounded reproducibility evidence for a local transcript. Filesystem paths and raw native
/// diagnostics are intentionally excluded from the portable artifact.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts</remarks>
public sealed record NormalizedTranscriptLocalExecution(
    string RuntimeVersion,
    string ModelSha256,
    string RequestedBackend,
    string? ResolvedBackend,
    int ThreadCount,
    string NativeBundleManifestSha256,
    long? ProcessingDurationMilliseconds);

public sealed record NormalizedTranscriptSource(
    string Sha256,
    long SizeBytes,
    long DurationMilliseconds,
    string Format);

public sealed record NormalizedTranscriptSegment(
    string Text,
    long? StartMilliseconds,
    long? EndMilliseconds,
    IReadOnlyList<NormalizedTranscriptWord> Words,
    string? SpeakerId = null);

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#artifacts
public sealed record NormalizedSpeaker(string Id, string Role, int? Ordinal, string SourceKind)
{
    public static NormalizedSpeaker FromId(string? id)
    {
        if (id == "self") return new(id, "self", null, "microphone");
        if (id == "speaker_unresolved") return new(id, "unresolved", null, "unresolved");
        var parts = id?.Split(':');
        if (parts is { Length: 2 } && parts[0] is "remote" or "unknown" && int.TryParse(parts[1], out var ordinal) && ordinal is > 0 and <= 64)
            return new(id!, parts[0], ordinal, parts[0] == "remote" ? "system_output" : "mixed");
        throw new InvalidDataException("speaker_identity_invalid");
    }
}
public sealed record NormalizedSpeakerTurn(string SpeakerId, long StartMilliseconds, long EndMilliseconds, string Text, string SourceKind, IReadOnlyList<string> ConfidenceFlags);
public sealed record NormalizedDiarization(string RuntimeVersion, string ParametersVersion, IReadOnlyList<string> ModelHashes);
public sealed record NormalizedSpeakerSource(string Role, string Sha256, long SizeBytes, double DurationSeconds, double OffsetSeconds);
public sealed record NormalizedSourceRecognition(string ChunkId, int SourceIndex, string Role, string InputSha256,
    string? DetectedLanguage, string? ResolvedModelId, string? EngineRequestId, TranscriptionUsage? Usage);

public sealed record NormalizedTranscriptWord(
    string Text,
    long StartMilliseconds,
    long EndMilliseconds,
    double? Confidence);

public sealed record NormalizedTranscriptChunk(
    string Id,
    int SequenceIndex,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    string? EngineRequestId,
    TranscriptionUsage? Usage);

public static class TranscriptMarkdownRenderer
{
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public static string Render(NormalizedTranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var duration = document.MeetingDurationMilliseconds is { } durationMilliseconds
            ? TimeSpan.FromMilliseconds(durationMilliseconds).ToString("c")
            : "unknown";
        var language = document.DetectedLanguage
            ?? document.RequestedLanguage
            ?? "auto";
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(document.DisplayTitle);
        builder.AppendLine();
        builder.Append("- Date: ").AppendLine(document.MeetingStartedAt.ToString("yyyy-MM-dd HH:mm zzz"));
        builder.Append("- Duration: ").AppendLine(duration);
        builder.Append("- Engine: ").AppendLine(document.EngineId);
        builder.Append("- Model: ").AppendLine(document.ModelId);
        builder.Append("- Language: ").AppendLine(language);
        builder.AppendLine();
        builder.AppendLine("## Transcript");
        builder.AppendLine();
        // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#artifacts
        if (document.Schema == "speaker-transcript/v1" && document.Turns is { } turns)
        {
            var russian = document.UiLanguage == "ru";
            foreach (var turn in turns)
            {
                var speaker = NormalizedSpeaker.FromId(turn.SpeakerId);
                var label = speaker.Role switch
                {
                    "self" => russian ? "Я" : "Me",
                    "remote" => (russian ? "Собеседник " : "Speaker ") + speaker.Ordinal,
                    "unknown" => (russian ? "Участник " : "Participant ") + speaker.Ordinal,
                    _ => russian ? "Голос не определён" : "Unresolved speaker"
                };
                var at = TimeSpan.FromMilliseconds(turn.StartMilliseconds);
                var timestamp = at.TotalHours >= 1 ? at.ToString(@"hh\:mm\:ss") : at.ToString(@"mm\:ss");
                builder.Append("**").Append(label).Append(" · ").Append(timestamp).AppendLine("**");
                builder.AppendLine(turn.Text).AppendLine();
            }
        }
        else builder.AppendLine(document.Text);
        return builder.ToString();
    }
}

public sealed record StagedTranscriptArtifacts(
    string JobId,
    string FinalMarkdownPath,
    string FinalJsonPath,
    string StagedMarkdownPath,
    string StagedJsonPath,
    string MarkdownSha256,
    string JsonSha256);

public sealed record PublishedTranscriptArtifacts(
    string JobId,
    string FinalMarkdownPath,
    string FinalJsonPath,
    string MarkdownSha256,
    string JsonSha256);

public enum TranscriptArtifactRecoveryAction
{
    RollBack,
    Complete
}

public enum TranscriptArtifactPublicationPhase
{
    Preparing,
    Prepared,
    Promoted,
    DatabaseCommitted
}

public sealed record PendingTranscriptArtifactPublication(
    string JournalPath,
    string JobId,
    TranscriptArtifactPublicationPhase Phase,
    string FinalMarkdownPath,
    string FinalJsonPath,
    string MarkdownSha256,
    string JsonSha256)
{
    public PublishedTranscriptArtifacts Artifacts => new(
        JobId,
        FinalMarkdownPath,
        FinalJsonPath,
        MarkdownSha256,
        JsonSha256);
}

public sealed record QuarantinedTranscriptArtifact(
    string OriginalPath,
    string QuarantinePath,
    string ReasonCode);

public sealed class TranscriptArtifactRecoveryRequiredException(
    string journalPath,
    string message,
    Exception? innerException = null)
    : IOException(message, innerException)
{
    public string JournalPath { get; } = journalPath;
}

/// <summary>
/// Stages, validates and recoverably promotes the Markdown/JSON artifact pair.
/// Database state transitions remain owned by the transcription repository.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed class TranscriptArtifactMaterializer(ArtifactPathResolver pathResolver)
{
    private const int PublicationJournalVersion = 1;
    private const long MaximumPublicationJournalBytes = 256L * 1024;
    private const string PublicationJournalSuffix = ".publication-journal";
    private const string PublicationBackupSuffix = ".publication-backup";
    private const string QuarantineDirectoryName = ".transcription-quarantine";
    private static readonly StringComparer PathComparison = StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublicationGates = new(
        PathComparison);
    private static readonly JsonSerializerOptions ArtifactSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions JournalSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private readonly ArtifactPathResolver _pathResolver = pathResolver;

    internal string GetTempSessionPath(ApplicationSettings settings, Guid sessionId) =>
        _pathResolver.GetTempSessionDirectoryPath(settings, sessionId);

    public async ValueTask<StagedTranscriptArtifacts> StageAsync(
        ApplicationSettings settings,
        NormalizedTranscriptDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(document);
        var finalMarkdownPath = _pathResolver.GetCanonicalTranscriptFilePath(
            settings,
            document.SessionId,
            document.MeetingStartedAt,
            "md");
        var finalJsonPath = _pathResolver.GetCanonicalTranscriptFilePath(
            settings,
            document.SessionId,
            document.MeetingStartedAt,
            "json");
        var stagedMarkdownPath = _pathResolver.GetStagedTranscriptFilePath(
            settings,
            document.SessionId,
            document.MeetingStartedAt,
            document.JobId,
            "md");
        var stagedJsonPath = _pathResolver.GetStagedTranscriptFilePath(
            settings,
            document.SessionId,
            document.MeetingStartedAt,
            document.JobId,
            "json");
        var markdown = Encoding.UTF8.GetBytes(TranscriptMarkdownRenderer.Render(document));
        var json = JsonSerializer.SerializeToUtf8Bytes(document, ArtifactSerializerOptions);

        await WriteAtomicAsync(stagedMarkdownPath, markdown, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(stagedJsonPath, json, cancellationToken).ConfigureAwait(false);
            await ValidateStagedAsync(stagedMarkdownPath, stagedJsonPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(stagedMarkdownPath);
            TryDelete(stagedJsonPath);
            throw;
        }

        return new StagedTranscriptArtifacts(
            document.JobId,
            finalMarkdownPath,
            finalJsonPath,
            stagedMarkdownPath,
            stagedJsonPath,
            Sha256(markdown),
            Sha256(json));
    }

    /// <summary>
    /// Promotes both files, publishes their paths in the caller-owned database transaction,
    /// and retains a durable rollback journal until that transaction succeeds.
    /// </summary>
    /// <remarks>
    /// The callback must return only after its database transaction commits. If it throws,
    /// including cancellation, the previous artifact pair is restored before the exception escapes.
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// </remarks>
    public async ValueTask<PublishedTranscriptArtifacts> PublishAsync(
        StagedTranscriptArtifacts artifacts,
        Func<PublishedTranscriptArtifacts, CancellationToken, ValueTask> publishDatabaseAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(publishDatabaseAsync);
        var publication = await BeginPublicationAsync(artifacts, cancellationToken).ConfigureAwait(false);
        try
        {
            await publishDatabaseAsync(publication.Artifacts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception publicationException)
        {
            try
            {
                await RecoverPublicationAsync(
                        publication.JournalPath,
                        TranscriptArtifactRecoveryAction.RollBack,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception recoveryException)
            {
                throw new TranscriptArtifactRecoveryRequiredException(
                    publication.JournalPath,
                    $"Transcript publication failed and requires rollback recovery from '{publication.JournalPath}'.",
                    new AggregateException(publicationException, recoveryException));
            }

            throw;
        }

        try
        {
            await MarkDatabaseCommittedAsync(publication.JournalPath).ConfigureAwait(false);
            await RecoverPublicationAsync(
                    publication.JournalPath,
                    TranscriptArtifactRecoveryAction.Complete,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return publication.Artifacts;
        }
        catch (Exception exception) when (exception is not TranscriptArtifactRecoveryRequiredException)
        {
            throw new TranscriptArtifactRecoveryRequiredException(
                publication.JournalPath,
                $"The database accepted the transcript, but publication cleanup requires recovery from '{publication.JournalPath}'.",
                exception);
        }
    }

    /// <summary>
    /// Finds durable publication journals that startup recovery must reconcile with database state.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.idempotency
    /// </remarks>
    public IReadOnlyList<string> FindPendingPublicationJournals(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = _pathResolver.GetRecordingsRoot(settings);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, $"*{PublicationJournalSuffix}", SearchOption.AllDirectories)
            .Order(PathComparison)
            .ToArray();
    }

    /// <summary>
    /// Reads durable publication identity without changing files, so startup recovery can compare
    /// the journal with the caller-owned database before choosing completion or rollback.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.algorithm
    /// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.idempotency
    /// </remarks>
    public async ValueTask<PendingTranscriptArtifactPublication> GetPendingPublicationAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentException("A publication journal path is required.", nameof(journalPath));
        }

        var normalizedJournalPath = Path.GetFullPath(journalPath);
        var gate = PublicationGates.GetOrAdd(normalizedJournalPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await ReadJournalAsync(normalizedJournalPath, cancellationToken).ConfigureAwait(false);
            ValidateJournal(journal, normalizedJournalPath);
            return ToPendingPublication(journal, normalizedJournalPath);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Moves an unreadable publication journal out of the active recovery scan while preserving
    /// the exact payload for explicit inspection. Related staged/final artifacts remain untouched.
    /// </summary>
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.idempotency
    public async ValueTask<QuarantinedTranscriptArtifact?> QuarantinePublicationJournalAsync(
        string journalPath,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentException("A publication journal path is required.", nameof(journalPath));
        }

        var normalizedJournalPath = Path.GetFullPath(journalPath);
        var gate = PublicationGates.GetOrAdd(normalizedJournalPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(normalizedJournalPath))
            {
                return null;
            }

            var normalizedReason = NormalizeQuarantineReason(reasonCode);
            var quarantinePath = CreateQuarantinePath(normalizedJournalPath, normalizedReason);
            File.Move(normalizedJournalPath, quarantinePath);
            return new QuarantinedTranscriptArtifact(
                normalizedJournalPath,
                quarantinePath,
                normalizedReason);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Copies damaged staged payloads into explicit quarantine before the owning job leaves the
    /// automatic recovery set. Canonical final artifacts are never copied or changed here.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public IReadOnlyList<QuarantinedTranscriptArtifact> QuarantineStagedArtifacts(
        StagedTranscriptArtifacts artifacts,
        string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var normalizedReason = NormalizeQuarantineReason(reasonCode);
        var quarantined = new List<QuarantinedTranscriptArtifact>(2);
        CopyStagedToQuarantine(
            artifacts.StagedMarkdownPath,
            artifacts.FinalMarkdownPath,
            normalizedReason,
            quarantined);
        CopyStagedToQuarantine(
            artifacts.StagedJsonPath,
            artifacts.FinalJsonPath,
            normalizedReason,
            quarantined);
        return quarantined;
    }

    /// <summary>
    /// Removes only byte-identical staged payloads after durable local cancellation. Canonical
    /// transcript paths and publication journals remain outside this operation.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
    public bool DiscardStagedArtifacts(StagedTranscriptArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var validated = ValidateStagedDiscard(artifacts);
        if (File.Exists(GetJournalPath(validated.FinalJsonPath))
            || !IsMissingOrExpected(validated.StagedMarkdownPath, artifacts.MarkdownSha256)
            || !IsMissingOrExpected(validated.StagedJsonPath, artifacts.JsonSha256))
        {
            return false;
        }

        TryDeleteExpected(validated.StagedMarkdownPath, artifacts.MarkdownSha256);
        TryDeleteExpected(validated.StagedJsonPath, artifacts.JsonSha256);
        return !File.Exists(validated.StagedMarkdownPath)
            && !File.Exists(validated.StagedJsonPath);
    }

    /// <summary>
    /// Idempotently restores the previous pair or completes cleanup after the caller reconciles
    /// the durable journal with database state.
    /// </summary>
    /// <remarks>
    /// A journal marked as database-committed can only be completed.
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    /// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.idempotency
    /// </remarks>
    public async ValueTask<PublishedTranscriptArtifacts> RecoverPublicationAsync(
        string journalPath,
        TranscriptArtifactRecoveryAction action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentException("A publication journal path is required.", nameof(journalPath));
        }

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        var normalizedJournalPath = Path.GetFullPath(journalPath);
        var gate = PublicationGates.GetOrAdd(normalizedJournalPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await ReadJournalAsync(normalizedJournalPath, cancellationToken).ConfigureAwait(false);
            ValidateJournal(journal, normalizedJournalPath);
            if (action == TranscriptArtifactRecoveryAction.RollBack)
            {
                if (journal.Phase == TranscriptArtifactPublicationPhase.DatabaseCommitted)
                {
                    throw new InvalidOperationException(
                        "A database-committed transcript publication cannot be rolled back.");
                }

                await RollBackAsync(journal, normalizedJournalPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CompleteAsync(journal, normalizedJournalPath, cancellationToken).ConfigureAwait(false);
            }

            return ToPublishedArtifacts(journal);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<PendingTranscriptArtifactPublication> BeginPublicationAsync(
        StagedTranscriptArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var journalPath = GetJournalPath(artifacts.FinalJsonPath);
        var gate = PublicationGates.GetOrAdd(journalPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = CreateJournal(artifacts);
            await VerifyExpectedFileAsync(
                    journal.Markdown.StagedPath,
                    journal.Markdown.FinalPath,
                    journal.Markdown.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyExpectedFileAsync(
                    journal.Json.StagedPath,
                    journal.Json.FinalPath,
                    journal.Json.ExpectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);

            await ReserveJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            try
            {
                await CreateBackupAsync(journal.Markdown, cancellationToken).ConfigureAwait(false);
                await CreateBackupAsync(journal.Json, cancellationToken).ConfigureAwait(false);
                journal = journal with { Phase = TranscriptArtifactPublicationPhase.Prepared };
                await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                PromoteOne(journal.Markdown);
                cancellationToken.ThrowIfCancellationRequested();
                PromoteOne(journal.Json);
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyHashAsync(
                        journal.Markdown.FinalPath,
                        journal.Markdown.ExpectedSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
                await VerifyHashAsync(
                        journal.Json.FinalPath,
                        journal.Json.ExpectedSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
                journal = journal with { Phase = TranscriptArtifactPublicationPhase.Promoted };
                await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception promotionException)
            {
                try
                {
                    await RollBackAsync(journal, journalPath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception recoveryException)
                {
                    throw new TranscriptArtifactRecoveryRequiredException(
                        journalPath,
                        $"Transcript promotion requires rollback recovery from '{journalPath}'.",
                        new AggregateException(promotionException, recoveryException));
                }

                throw;
            }

            return ToPendingPublication(journal, journalPath);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async ValueTask ValidateStagedAsync(
        string markdownPath,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        var markdown = await File.ReadAllTextAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(markdown) || !markdown.Contains("## Transcript", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staged Markdown transcript is invalid.");
        }

        await using var json = File.OpenRead(jsonPath);
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The staged JSON transcript is invalid.");
        }
    }

    private static async ValueTask VerifyExpectedFileAsync(
        string stagedPath,
        string finalPath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(stagedPath))
        {
            await VerifyHashAsync(stagedPath, expectedHash, cancellationToken).ConfigureAwait(false);
            return;
        }

        await VerifyHashAsync(finalPath, expectedHash, cancellationToken).ConfigureAwait(false);
    }

    private static void PromoteOne(PublicationFile journalFile)
    {
        if (File.Exists(journalFile.FinalPath)
            && string.Equals(
                Sha256(journalFile.FinalPath),
                journalFile.ExpectedSha256,
                StringComparison.Ordinal))
        {
            TryDeleteExpected(journalFile.StagedPath, journalFile.ExpectedSha256);
            return;
        }

        if (!File.Exists(journalFile.StagedPath))
        {
            throw new FileNotFoundException(
                "The staged transcript artifact is unavailable.",
                journalFile.StagedPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(journalFile.FinalPath)!);
        File.Move(journalFile.StagedPath, journalFile.FinalPath, overwrite: true);
    }

    private static async ValueTask CreateBackupAsync(
        PublicationFile journalFile,
        CancellationToken cancellationToken)
    {
        if (!journalFile.HadOriginal)
        {
            return;
        }

        if (journalFile.OriginalSha256 is null)
        {
            throw new InvalidDataException("The publication journal is missing an original artifact hash.");
        }

        if (File.Exists(journalFile.BackupPath))
        {
            await VerifyHashAsync(
                    journalFile.BackupPath,
                    journalFile.OriginalSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var temporaryPath = $"{journalFile.BackupPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = new FileStream(
                             journalFile.FinalPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = CreateDurableWriteStream(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await FlushDurablyAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            await VerifyHashAsync(temporaryPath, journalFile.OriginalSha256, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, journalFile.BackupPath);
            await VerifyHashAsync(
                    journalFile.FinalPath,
                    journalFile.OriginalSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async ValueTask RollBackAsync(
        PublicationJournal journal,
        string journalPath,
        CancellationToken cancellationToken)
    {
        await VerifyRollbackIsPossibleAsync(journal.Markdown, cancellationToken).ConfigureAwait(false);
        await VerifyRollbackIsPossibleAsync(journal.Json, cancellationToken).ConfigureAwait(false);
        RestoreOne(journal.Markdown, cancellationToken);
        RestoreOne(journal.Json, cancellationToken);
        await VerifyRestoredAsync(journal.Markdown, cancellationToken).ConfigureAwait(false);
        await VerifyRestoredAsync(journal.Json, cancellationToken).ConfigureAwait(false);
        DeletePublicationScratch(journal.Markdown);
        DeletePublicationScratch(journal.Json);
        TryDeleteTemporarySiblings(journalPath);
        TryDelete(journalPath);
    }

    private static async ValueTask VerifyRollbackIsPossibleAsync(
        PublicationFile journalFile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!journalFile.HadOriginal)
        {
            return;
        }

        if (journalFile.OriginalSha256 is null)
        {
            throw new InvalidDataException("The publication journal is missing an original artifact hash.");
        }

        if (File.Exists(journalFile.BackupPath))
        {
            await VerifyHashAsync(
                    journalFile.BackupPath,
                    journalFile.OriginalSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await VerifyHashAsync(journalFile.FinalPath, journalFile.OriginalSha256, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void RestoreOne(PublicationFile journalFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (journalFile.HadOriginal)
        {
            if (File.Exists(journalFile.BackupPath))
            {
                File.Move(journalFile.BackupPath, journalFile.FinalPath, overwrite: true);
            }

            return;
        }

        if (!File.Exists(journalFile.FinalPath))
        {
            return;
        }

        if (!string.Equals(
                Sha256(journalFile.FinalPath),
                journalFile.ExpectedSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A transcript artifact changed after promotion and cannot be deleted during rollback.");
        }

        File.Delete(journalFile.FinalPath);
    }

    private static async ValueTask VerifyRestoredAsync(
        PublicationFile journalFile,
        CancellationToken cancellationToken)
    {
        if (journalFile.HadOriginal)
        {
            await VerifyHashAsync(
                    journalFile.FinalPath,
                    journalFile.OriginalSha256!,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (File.Exists(journalFile.FinalPath))
        {
            throw new InvalidDataException("A newly promoted transcript artifact survived rollback.");
        }
    }

    private static async ValueTask CompleteAsync(
        PublicationJournal journal,
        string journalPath,
        CancellationToken cancellationToken)
    {
        await VerifyHashAsync(
                journal.Markdown.FinalPath,
                journal.Markdown.ExpectedSha256,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyHashAsync(
                journal.Json.FinalPath,
                journal.Json.ExpectedSha256,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DeletePublicationScratch(journal.Markdown);
        DeletePublicationScratch(journal.Json);
        TryDeleteTemporarySiblings(journalPath);
        TryDelete(journalPath);
    }

    private static void DeletePublicationScratch(PublicationFile journalFile)
    {
        TryDeleteExpected(journalFile.StagedPath, journalFile.ExpectedSha256);
        TryDeleteTemporarySiblings(journalFile.BackupPath);
        TryDelete(journalFile.BackupPath);
    }

    private static void TryDeleteTemporarySiblings(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(
                     directory,
                     $"{Path.GetFileName(path)}.*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDeleteExpected(string path, string expectedHash)
    {
        if (File.Exists(path)
            && string.Equals(Sha256(path), expectedHash, StringComparison.Ordinal))
        {
            File.Delete(path);
        }
    }

    private static bool IsMissingOrExpected(string path, string expectedHash) =>
        !File.Exists(path)
        || string.Equals(Sha256(path), expectedHash, StringComparison.Ordinal);

    private static ValidatedStagedDiscard ValidateStagedDiscard(StagedTranscriptArtifacts artifacts)
    {
        ValidateHash(artifacts.MarkdownSha256, nameof(artifacts));
        ValidateHash(artifacts.JsonSha256, nameof(artifacts));
        var jobId = artifacts.JobId.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(jobId)
            || jobId.Length > 128
            || jobId.Any(static character =>
                !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
        {
            throw new ArgumentException("The transcription job id is invalid.", nameof(artifacts));
        }

        var finalMarkdownPath = Path.GetFullPath(artifacts.FinalMarkdownPath);
        var finalJsonPath = Path.GetFullPath(artifacts.FinalJsonPath);
        var stagedMarkdownPath = Path.GetFullPath(artifacts.StagedMarkdownPath);
        var stagedJsonPath = Path.GetFullPath(artifacts.StagedJsonPath);
        var finalDirectory = Path.GetDirectoryName(finalMarkdownPath);
        if (!PathComparison.Equals(finalDirectory, Path.GetDirectoryName(finalJsonPath))
            || !PathComparison.Equals(finalDirectory, Path.GetDirectoryName(stagedMarkdownPath))
            || !PathComparison.Equals(finalDirectory, Path.GetDirectoryName(stagedJsonPath))
            || !PathComparison.Equals(
                stagedMarkdownPath,
                Path.Combine(finalDirectory!, $".{Path.GetFileName(finalMarkdownPath)}.{jobId}.partial"))
            || !PathComparison.Equals(
                stagedJsonPath,
                Path.Combine(finalDirectory!, $".{Path.GetFileName(finalJsonPath)}.{jobId}.partial")))
        {
            throw new InvalidDataException("The persisted staged transcript paths are invalid.");
        }

        const string markdownSuffix = ".transcript.md";
        const string jsonSuffix = ".transcript.json";
        var markdownName = Path.GetFileName(finalMarkdownPath);
        var jsonName = Path.GetFileName(finalJsonPath);
        if (!markdownName.EndsWith(markdownSuffix, StringComparison.Ordinal)
            || !jsonName.EndsWith(jsonSuffix, StringComparison.Ordinal)
            || !string.Equals(
                markdownName[..^markdownSuffix.Length],
                jsonName[..^jsonSuffix.Length],
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The persisted final transcript paths are invalid.");
        }

        return new ValidatedStagedDiscard(
            finalJsonPath,
            stagedMarkdownPath,
            stagedJsonPath);
    }

    private static async ValueTask WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = CreateDurableWriteStream(temporaryPath))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async ValueTask VerifyHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The transcript artifact is unavailable.", path);
        }

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The transcript artifact hash did not match its publication record.");
        }
    }

    private static string Sha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static FileStream CreateDurableWriteStream(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    private static async ValueTask FlushDurablyAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static PublicationJournal CreateJournal(StagedTranscriptArtifacts artifacts)
    {
        ValidateHash(artifacts.MarkdownSha256, nameof(artifacts));
        ValidateHash(artifacts.JsonSha256, nameof(artifacts));
        var jobId = artifacts.JobId.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(jobId)
            || jobId.Length > 128
            || jobId.Any(static character =>
                !(character is >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
        {
            throw new ArgumentException("The transcription job id is invalid.", nameof(artifacts));
        }

        var markdown = CreateJournalFile(
            artifacts.FinalMarkdownPath,
            artifacts.StagedMarkdownPath,
            artifacts.MarkdownSha256);
        var json = CreateJournalFile(
            artifacts.FinalJsonPath,
            artifacts.StagedJsonPath,
            artifacts.JsonSha256);
        if (!PathComparison.Equals(
                Path.GetDirectoryName(markdown.FinalPath),
                Path.GetDirectoryName(json.FinalPath)))
        {
            throw new ArgumentException("Transcript artifact pairs must share a directory.", nameof(artifacts));
        }

        return new PublicationJournal(
            PublicationJournalVersion,
            jobId,
            TranscriptArtifactPublicationPhase.Preparing,
            markdown,
            json);
    }

    private static PublicationFile CreateJournalFile(
        string finalPath,
        string stagedPath,
        string expectedSha256)
    {
        var normalizedFinalPath = Path.GetFullPath(finalPath);
        var normalizedStagedPath = Path.GetFullPath(stagedPath);
        if (!PathComparison.Equals(
                Path.GetDirectoryName(normalizedFinalPath),
                Path.GetDirectoryName(normalizedStagedPath)))
        {
            throw new ArgumentException("A staged transcript must share its final artifact directory.");
        }

        var hadOriginal = File.Exists(normalizedFinalPath);
        var originalSha256 = hadOriginal ? Sha256(normalizedFinalPath) : null;
        return new PublicationFile(
            normalizedFinalPath,
            normalizedStagedPath,
            $"{normalizedFinalPath}{PublicationBackupSuffix}",
            expectedSha256,
            hadOriginal,
            originalSha256);
    }

    private static async ValueTask ReserveJournalAsync(
        string journalPath,
        PublicationJournal journal,
        CancellationToken cancellationToken)
    {
        if (File.Exists(journalPath))
        {
            throw new TranscriptArtifactRecoveryRequiredException(
                journalPath,
                $"A pending transcript publication requires recovery from '{journalPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var temporaryPath = $"{journalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(journal, JournalSerializerOptions);
            await using (var stream = CreateDurableWriteStream(temporaryPath))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(temporaryPath, journalPath);
            }
            catch (IOException exception) when (File.Exists(journalPath))
            {
                throw new TranscriptArtifactRecoveryRequiredException(
                    journalPath,
                    $"A pending transcript publication requires recovery from '{journalPath}'.",
                    exception);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async ValueTask WriteJournalAsync(
        string journalPath,
        PublicationJournal journal,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(journal, JournalSerializerOptions);
        await WriteAtomicAsync(journalPath, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PublicationJournal> ReadJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumPublicationJournalBytes)
        {
            throw new InvalidDataException("The transcript publication journal is too large.");
        }

        return await JsonSerializer.DeserializeAsync<PublicationJournal>(
                   stream,
                   JournalSerializerOptions,
                   cancellationToken)
               .ConfigureAwait(false)
            ?? throw new InvalidDataException("The transcript publication journal is empty.");
    }

    private static void CopyStagedToQuarantine(
        string stagedPath,
        string finalPath,
        string reasonCode,
        ICollection<QuarantinedTranscriptArtifact> quarantined)
    {
        var normalizedStagedPath = Path.GetFullPath(stagedPath);
        var normalizedFinalPath = Path.GetFullPath(finalPath);
        var stagedFileName = Path.GetFileName(normalizedStagedPath);
        if (!PathComparison.Equals(
                Path.GetDirectoryName(normalizedStagedPath),
                Path.GetDirectoryName(normalizedFinalPath))
            || !stagedFileName.StartsWith(".", StringComparison.Ordinal)
            || !stagedFileName.EndsWith(".partial", StringComparison.Ordinal)
            || !File.Exists(normalizedStagedPath))
        {
            return;
        }

        var quarantinePath = CreateQuarantinePath(normalizedStagedPath, reasonCode);
        File.Copy(normalizedStagedPath, quarantinePath);
        quarantined.Add(new QuarantinedTranscriptArtifact(
            normalizedStagedPath,
            quarantinePath,
            reasonCode));
    }

    private static string CreateQuarantinePath(string sourcePath, string reasonCode)
    {
        var quarantineDirectory = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            QuarantineDirectoryName);
        Directory.CreateDirectory(quarantineDirectory);
        return Path.Combine(
            quarantineDirectory,
            $"{Path.GetFileName(sourcePath)}.{reasonCode}.{Guid.NewGuid():N}.quarantined");
    }

    private static string NormalizeQuarantineReason(string reasonCode)
    {
        var normalized = new string((reasonCode ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(static character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-')
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? "recovery-failed" : normalized;
    }

    private static async ValueTask MarkDatabaseCommittedAsync(string journalPath)
    {
        var normalizedJournalPath = Path.GetFullPath(journalPath);
        var gate = PublicationGates.GetOrAdd(normalizedJournalPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var journal = await ReadJournalAsync(normalizedJournalPath, CancellationToken.None)
                .ConfigureAwait(false);
            ValidateJournal(journal, normalizedJournalPath);
            journal = journal with { Phase = TranscriptArtifactPublicationPhase.DatabaseCommitted };
            await WriteJournalAsync(normalizedJournalPath, journal, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateJournal(PublicationJournal journal, string journalPath)
    {
        if (journal.Version != PublicationJournalVersion || string.IsNullOrWhiteSpace(journal.JobId))
        {
            throw new InvalidDataException("The transcript publication journal version or identity is invalid.");
        }

        ValidateJournalFile(journal.Markdown);
        ValidateJournalFile(journal.Json);
        if (!PathComparison.Equals(journalPath, GetJournalPath(journal.Json.FinalPath))
            || !PathComparison.Equals(
                Path.GetDirectoryName(journal.Markdown.FinalPath),
                Path.GetDirectoryName(journal.Json.FinalPath)))
        {
            throw new InvalidDataException("The transcript publication journal paths are invalid.");
        }
    }

    private static void ValidateJournalFile(PublicationFile journalFile)
    {
        ValidateHash(journalFile.ExpectedSha256, nameof(journalFile));
        if (journalFile.HadOriginal)
        {
            ValidateHash(journalFile.OriginalSha256, nameof(journalFile));
        }
        else if (journalFile.OriginalSha256 is not null)
        {
            throw new InvalidDataException("A transcript publication journal has an unexpected original hash.");
        }

        var finalPath = Path.GetFullPath(journalFile.FinalPath);
        var stagedPath = Path.GetFullPath(journalFile.StagedPath);
        var backupPath = Path.GetFullPath(journalFile.BackupPath);
        if (!PathComparison.Equals(finalPath, journalFile.FinalPath)
            || !PathComparison.Equals(stagedPath, journalFile.StagedPath)
            || !PathComparison.Equals(backupPath, journalFile.BackupPath)
            || !PathComparison.Equals(Path.GetDirectoryName(finalPath), Path.GetDirectoryName(stagedPath))
            || !PathComparison.Equals(backupPath, $"{finalPath}{PublicationBackupSuffix}"))
        {
            throw new InvalidDataException("A transcript publication journal file path is invalid.");
        }
    }

    private static void ValidateHash(string? hash, string parameterName)
    {
        if (hash is null
            || hash.Length != 64
            || hash.Any(static character =>
                !(character is >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 hash is required.", parameterName);
        }
    }

    private static PublishedTranscriptArtifacts ToPublishedArtifacts(PublicationJournal journal) => new(
        journal.JobId,
        journal.Markdown.FinalPath,
        journal.Json.FinalPath,
        journal.Markdown.ExpectedSha256,
        journal.Json.ExpectedSha256);

    private static PendingTranscriptArtifactPublication ToPendingPublication(
        PublicationJournal journal,
        string journalPath) => new(
        journalPath,
        journal.JobId,
        journal.Phase,
        journal.Markdown.FinalPath,
        journal.Json.FinalPath,
        journal.Markdown.ExpectedSha256,
        journal.Json.ExpectedSha256);

    private static string GetJournalPath(string finalJsonPath) =>
        $"{Path.GetFullPath(finalJsonPath)}{PublicationJournalSuffix}";

    private sealed record PublicationJournal(
        int Version,
        string JobId,
        TranscriptArtifactPublicationPhase Phase,
        PublicationFile Markdown,
        PublicationFile Json);

    private sealed record PublicationFile(
        string FinalPath,
        string StagedPath,
        string BackupPath,
        string ExpectedSha256,
        bool HadOriginal,
        string? OriginalSha256);

    private sealed record ValidatedStagedDiscard(
        string FinalJsonPath,
        string StagedMarkdownPath,
        string StagedJsonPath);

}
