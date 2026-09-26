using System.Globalization;
using IsTranscribe.Core.Runtime;
using IsTranscribe.Core.Transcription;
using Microsoft.Data.Sqlite;

namespace IsTranscribe.Host.Persistence;

public sealed class MeetingSessionRepository(SqliteConnection connection)
{
    private const int MaximumProviderRequestIdLength = 128;
    private readonly SqliteConnection _connection = connection;

    // @spec spec://common/PROP-004-meeting-session-and-data-model#entities.meeting-session
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#migration
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public async ValueTask UpsertAsync(MeetingSessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO meeting_session (
                id,
                created_at,
                started_at,
                ended_at,
                status,
                mode,
                source_type,
                source_app,
                source_process_id,
                output_device_id,
                microphone_device_id,
                audio_output_path,
                audio_mic_path,
                audio_mix_path,
                primary_audio_path,
                temp_session_path,
                source_manifest_path,
                staged_primary_path,
                artifact_progress,
                source_cleanup_pending,
                artifact_error_code,
                artifact_error_message,
                transcript_md_path,
                transcript_json_path,
                duration_seconds,
                transcription_status,
                queued_at,
                updated_at,
                retry_attempt_count,
                next_retry_at,
                last_retry_at,
                transcription_model,
                diarization_enabled,
                language,
                error_code,
                error_message,
                user_discarded)
            VALUES (
                $id,
                $created_at,
                $started_at,
                $ended_at,
                $status,
                $mode,
                $source_type,
                $source_app,
                $source_process_id,
                $output_device_id,
                $microphone_device_id,
                $audio_output_path,
                $audio_mic_path,
                $audio_mix_path,
                $primary_audio_path,
                $temp_session_path,
                $source_manifest_path,
                $staged_primary_path,
                $artifact_progress,
                $source_cleanup_pending,
                $artifact_error_code,
                $artifact_error_message,
                $transcript_md_path,
                $transcript_json_path,
                $duration_seconds,
                $transcription_status,
                $queued_at,
                $updated_at,
                $retry_attempt_count,
                $next_retry_at,
                $last_retry_at,
                $transcription_model,
                $diarization_enabled,
                $language,
                $error_code,
                $error_message,
                $user_discarded)
            ON CONFLICT(id) DO UPDATE SET
                created_at = excluded.created_at,
                started_at = excluded.started_at,
                ended_at = excluded.ended_at,
                status = excluded.status,
                mode = excluded.mode,
                source_type = excluded.source_type,
                source_app = excluded.source_app,
                source_process_id = excluded.source_process_id,
                output_device_id = excluded.output_device_id,
                microphone_device_id = excluded.microphone_device_id,
                audio_output_path = excluded.audio_output_path,
                audio_mic_path = excluded.audio_mic_path,
                audio_mix_path = excluded.audio_mix_path,
                primary_audio_path = excluded.primary_audio_path,
                temp_session_path = excluded.temp_session_path,
                source_manifest_path = excluded.source_manifest_path,
                staged_primary_path = excluded.staged_primary_path,
                artifact_progress = excluded.artifact_progress,
                source_cleanup_pending = excluded.source_cleanup_pending,
                artifact_error_code = excluded.artifact_error_code,
                artifact_error_message = excluded.artifact_error_message,
                transcript_md_path = excluded.transcript_md_path,
                transcript_json_path = excluded.transcript_json_path,
                duration_seconds = excluded.duration_seconds,
                transcription_status = excluded.transcription_status,
                queued_at = excluded.queued_at,
                updated_at = excluded.updated_at,
                retry_attempt_count = excluded.retry_attempt_count,
                next_retry_at = excluded.next_retry_at,
                last_retry_at = excluded.last_retry_at,
                transcription_model = excluded.transcription_model,
                diarization_enabled = excluded.diarization_enabled,
                language = excluded.language,
                error_code = excluded.error_code,
                error_message = excluded.error_message,
                user_discarded = excluded.user_discarded;
            """;

        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$created_at", session.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$started_at", ToDbValue(session.StartedAtUtc));
        command.Parameters.AddWithValue("$ended_at", ToDbValue(session.EndedAtUtc));
        command.Parameters.AddWithValue("$status", session.Status);
        command.Parameters.AddWithValue("$mode", session.Mode);
        command.Parameters.AddWithValue("$source_type", session.SourceType);
        command.Parameters.AddWithValue("$source_app", ToDbValue(session.SourceApp));
        command.Parameters.AddWithValue("$source_process_id", session.SourceProcessId.HasValue ? session.SourceProcessId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$output_device_id", ToDbValue(session.OutputDeviceId));
        command.Parameters.AddWithValue("$microphone_device_id", ToDbValue(session.MicrophoneDeviceId));
        command.Parameters.AddWithValue("$audio_output_path", ToDbValue(session.AudioOutputPath));
        command.Parameters.AddWithValue("$audio_mic_path", ToDbValue(session.AudioMicPath));
        command.Parameters.AddWithValue("$audio_mix_path", ToDbValue(session.AudioMixPath));
        command.Parameters.AddWithValue("$primary_audio_path", ToDbValue(session.PrimaryAudioPath));
        command.Parameters.AddWithValue("$temp_session_path", ToDbValue(session.TempSessionPath));
        command.Parameters.AddWithValue("$source_manifest_path", ToDbValue(session.SourceManifestPath));
        command.Parameters.AddWithValue("$staged_primary_path", ToDbValue(session.StagedPrimaryPath));
        command.Parameters.AddWithValue("$artifact_progress", NormalizeArtifactProgress(session.ArtifactProgress));
        command.Parameters.AddWithValue("$source_cleanup_pending", session.SourceCleanupPending ? 1 : 0);
        command.Parameters.AddWithValue("$artifact_error_code", ToDbValue(session.ArtifactErrorCode));
        command.Parameters.AddWithValue("$artifact_error_message", ToDbValue(session.ArtifactErrorMessage));
        command.Parameters.AddWithValue("$transcript_md_path", ToDbValue(session.TranscriptMarkdownPath));
        command.Parameters.AddWithValue("$transcript_json_path", ToDbValue(session.TranscriptJsonPath));
        command.Parameters.AddWithValue("$duration_seconds", session.DurationSeconds.HasValue ? session.DurationSeconds.Value : DBNull.Value);
        command.Parameters.AddWithValue("$transcription_status", session.TranscriptionStatus);
        command.Parameters.AddWithValue("$queued_at", ToDbValue(session.QueuedAtUtc));
        command.Parameters.AddWithValue("$updated_at", ToDbValue(session.UpdatedAtUtc));
        command.Parameters.AddWithValue("$retry_attempt_count", session.RetryAttemptCount);
        command.Parameters.AddWithValue("$next_retry_at", ToDbValue(session.NextRetryAtUtc));
        command.Parameters.AddWithValue("$last_retry_at", ToDbValue(session.LastRetryAtUtc));
        command.Parameters.AddWithValue("$transcription_model", ToDbValue(session.TranscriptionModel));
        command.Parameters.AddWithValue("$diarization_enabled", session.DiarizationEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$language", ToDbValue(session.Language));
        command.Parameters.AddWithValue("$error_code", ToDbValue(session.ErrorCode));
        command.Parameters.AddWithValue("$error_message", ToDbValue(session.ErrorMessage));
        command.Parameters.AddWithValue("$user_discarded", session.UserDiscarded ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#screen-model.data-source
    // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#screen-model.ordering
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    public IReadOnlyList<MeetingSessionListItem> ListRecent(int limit, int offset)
    {
        var normalizedLimit = Math.Max(1, limit);
        var normalizedOffset = Math.Max(0, offset);
        var sessions = new List<MeetingSessionListItem>(normalizedLimit);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                session.id,
                session.source_app,
                session.started_at,
                session.ended_at,
                session.duration_seconds,
                session.status,
                session.transcription_status,
                session.audio_output_path,
                session.audio_mic_path,
                session.audio_mix_path,
                session.primary_audio_path,
                session.temp_session_path,
                session.source_manifest_path,
                session.staged_primary_path,
                session.artifact_progress,
                session.source_cleanup_pending,
                session.artifact_error_code,
                session.artifact_error_message,
                session.transcript_md_path,
                session.transcript_json_path,
                session.error_code,
                session.error_message,
                session.display_title,
                current_job.id,
                current_job.engine_id,
                current_job.execution_kind,
                current_job.model_id,
                current_job.status,
                current_job.progress,
                current_job.current_chunk_index,
                current_job.next_attempt_at,
                current_job.stable_error_code,
                current_job.error_message,
                current_job.artifact_publication_state,
                current_job.transcript_md_path,
                current_job.transcript_json_path,
                current_job.usage_json,
                (SELECT COUNT(*)
                 FROM transcription_chunk AS counted_chunk
                 WHERE counted_chunk.job_id = current_job.id
                   AND counted_chunk.status <> 'split'),
                (SELECT request_chunk.engine_request_id
                 FROM transcription_chunk AS request_chunk
                 WHERE request_chunk.job_id = current_job.id
                   AND request_chunk.engine_request_id IS NOT NULL
                 ORDER BY
                   request_chunk.updated_at DESC,
                   request_chunk.sequence_index DESC
                 LIMIT 1),
                current_local.requested_backend,
                current_local.resolved_backend,
                current_local.thread_count,
                current_local.runtime_version,
                current_local.native_bundle_manifest_sha256,
                current_local.model_sha256,
                current_local.processing_duration_milliseconds
            FROM meeting_session AS session
            LEFT JOIN transcription_job AS current_job
              ON current_job.id = session.current_transcription_job_id
             AND current_job.session_id = session.id
            LEFT JOIN transcription_local_job AS current_local
              ON current_local.job_id = current_job.id
            WHERE session.status NOT IN ('prebuffering', 'awaiting_confirmation', 'history_removed')
            ORDER BY session.started_at DESC, session.id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", normalizedLimit);
        command.Parameters.AddWithValue("$offset", normalizedOffset);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var currentTranscription = ReadCurrentTranscription(reader);
            var currentTranscriptPublished = currentTranscription is
            {
                Status: TranscriptionJobStatus.Completed,
                ArtifactPublicationState: TranscriptionArtifactPublicationState.Promoted
            };
            var transcriptMarkdownPath = currentTranscriptPublished
                ? currentTranscription!.TranscriptMarkdownPath
                : reader.IsDBNull(18) ? null : reader.GetString(18);
            var transcriptJsonPath = currentTranscriptPublished
                ? currentTranscription!.TranscriptJsonPath
                : reader.IsDBNull(19) ? null : reader.GetString(19);

            sessions.Add(new MeetingSessionListItem(
                Id: reader.GetString(0),
                SourceApp: reader.IsDBNull(1) ? null : reader.GetString(1),
                StartedAtUtc: reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                EndedAtUtc: reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DurationSeconds: reader.IsDBNull(4) ? null : reader.GetDouble(4),
                RecordingStatus: reader.GetString(5),
                TranscriptionStatus: currentTranscription is null
                    ? reader.GetString(6)
                    : ToDatabaseStatus(currentTranscription.Status),
                AudioOutputPath: reader.IsDBNull(7) ? null : reader.GetString(7),
                AudioMicPath: reader.IsDBNull(8) ? null : reader.GetString(8),
                AudioMixPath: reader.IsDBNull(9) ? null : reader.GetString(9),
                TranscriptMarkdownPath: transcriptMarkdownPath,
                ErrorCode: reader.IsDBNull(20) ? null : reader.GetString(20),
                ErrorMessage: reader.IsDBNull(21) ? null : reader.GetString(21),
                PrimaryAudioPath: reader.IsDBNull(10) ? null : reader.GetString(10),
                TempSessionPath: reader.IsDBNull(11) ? null : reader.GetString(11),
                SourceManifestPath: reader.IsDBNull(12) ? null : reader.GetString(12),
                StagedPrimaryPath: reader.IsDBNull(13) ? null : reader.GetString(13),
                ArtifactProgress: reader.GetDouble(14),
                SourceCleanupPending: reader.GetInt64(15) != 0,
                ArtifactErrorCode: reader.IsDBNull(16) ? null : reader.GetString(16),
                ArtifactErrorMessage: reader.IsDBNull(17) ? null : reader.GetString(17),
                TranscriptJsonPath: transcriptJsonPath,
                DisplayTitle: reader.IsDBNull(22) ? null : reader.GetString(22),
                CurrentTranscription: currentTranscription));
        }

        return sessions;
    }

    private static CurrentTranscriptionJobListItem? ReadCurrentTranscription(SqliteDataReader reader)
    {
        if (reader.IsDBNull(23))
        {
            return null;
        }

        var status = ParseTranscriptionJobStatus(reader.GetString(27));
        var publicationState = ParseTranscriptionPublicationState(reader.GetString(33));
        var isPublished = status == TranscriptionJobStatus.Completed
            && publicationState == TranscriptionArtifactPublicationState.Promoted;

        return new CurrentTranscriptionJobListItem(
            JobId: reader.GetString(23),
            EngineId: reader.GetString(24),
            ExecutionKind: ParseTranscriptionExecutionKind(reader.GetString(25)),
            ModelId: reader.GetString(26),
            Status: status,
            Progress: reader.GetDouble(28),
            CurrentChunkIndex: reader.IsDBNull(29) ? null : reader.GetInt32(29),
            NextAttemptAtUtc: reader.IsDBNull(30)
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(30),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            StableErrorCode: reader.IsDBNull(31) ? null : reader.GetString(31),
            ErrorMessage: reader.IsDBNull(32) ? null : reader.GetString(32),
            ArtifactPublicationState: publicationState,
            TranscriptMarkdownPath: isPublished && !reader.IsDBNull(34) ? reader.GetString(34) : null,
            TranscriptJsonPath: isPublished && !reader.IsDBNull(35) ? reader.GetString(35) : null,
            UsageJson: reader.IsDBNull(36) ? null : reader.GetString(36),
            ChunkCount: (int)Math.Min(reader.GetInt64(37), int.MaxValue),
            ProviderRequestId: reader.IsDBNull(38)
                ? null
                : SanitizeProviderRequestId(reader.GetString(38)),
            LocalDiagnostics: reader.IsDBNull(39)
                ? null
                : new CurrentLocalTranscriptionDiagnosticsListItem(
                    RequestedBackend: reader.GetString(39),
                    ResolvedBackend: reader.IsDBNull(40) ? null : reader.GetString(40),
                    ThreadCount: reader.GetInt32(41),
                    RuntimeVersion: reader.GetString(42),
                    NativeBundleManifestSha256: reader.GetString(43),
                    ModelSha256: reader.GetString(44),
                    ProcessingDurationMilliseconds: reader.IsDBNull(45)
                        ? null
                        : reader.GetInt64(45)));
    }

    private static string? SanitizeProviderRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var normalized = requestId.Trim();
        return normalized.Length <= MaximumProviderRequestIdLength
               && !LooksSensitiveProviderRequestId(normalized)
               && normalized.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '-' or '_' or '.' or ':')
            ? normalized
            : null;
    }

    private static bool LooksSensitiveProviderRequestId(string value) =>
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || value.Contains("gsk_", StringComparison.OrdinalIgnoreCase)
        || value.Contains("sk-or-v1-", StringComparison.OrdinalIgnoreCase);

    private static TranscriptionExecutionKind ParseTranscriptionExecutionKind(string value) =>
        value switch
        {
            "local" => TranscriptionExecutionKind.Local,
            "remote" => TranscriptionExecutionKind.Remote,
            _ => throw new InvalidDataException($"Unknown transcription execution kind '{value}'.")
        };

    private static TranscriptionJobStatus ParseTranscriptionJobStatus(string value) =>
        value switch
        {
            "queued" => TranscriptionJobStatus.Queued,
            "preparing" => TranscriptionJobStatus.Preparing,
            "uploading" => TranscriptionJobStatus.Uploading,
            "processing" => TranscriptionJobStatus.Processing,
            "finalizing" => TranscriptionJobStatus.Finalizing,
            "retry_scheduled" => TranscriptionJobStatus.RetryScheduled,
            "attention_required" => TranscriptionJobStatus.AttentionRequired,
            "completed" => TranscriptionJobStatus.Completed,
            "cancelled" => TranscriptionJobStatus.Cancelled,
            "failed" => TranscriptionJobStatus.Failed,
            _ => throw new InvalidDataException($"Unknown transcription job status '{value}'.")
        };

    private static TranscriptionArtifactPublicationState ParseTranscriptionPublicationState(string value) =>
        value switch
        {
            "none" => TranscriptionArtifactPublicationState.None,
            "staged" => TranscriptionArtifactPublicationState.Staged,
            "promoted" => TranscriptionArtifactPublicationState.Promoted,
            _ => throw new InvalidDataException($"Unknown transcription artifact publication state '{value}'.")
        };

    private static string ToDatabaseStatus(TranscriptionJobStatus status) =>
        status switch
        {
            TranscriptionJobStatus.Queued => "queued",
            TranscriptionJobStatus.Preparing => "preparing",
            TranscriptionJobStatus.Uploading => "uploading",
            TranscriptionJobStatus.Processing => "processing",
            TranscriptionJobStatus.Finalizing => "finalizing",
            TranscriptionJobStatus.RetryScheduled => "retry_scheduled",
            TranscriptionJobStatus.AttentionRequired => "attention_required",
            TranscriptionJobStatus.Completed => "completed",
            TranscriptionJobStatus.Cancelled => "cancelled",
            TranscriptionJobStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown transcription job status.")
        };

    // The user-owned title is deliberately separate from source_app and artifact paths: later
    // capture/recovery upserts can refresh technical session metadata without losing the rename.
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    public bool TryRenameRecentRecording(
        string sessionId,
        string displayTitle,
        DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);
        var normalizedTitle = RecentRecordingTitle.Normalize(displayTitle);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                display_title = $display_title,
                updated_at = $updated_at
            WHERE id = $id
              AND status NOT IN (
                    'prebuffering',
                    'awaiting_confirmation',
                    'recording',
                    'paused',
                    'stopping',
                    'processing',
                    'verifying',
                    'promoting',
                    'discarded',
                    'history_removed',
                    'file_deletion_pending'
              );
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$display_title", normalizedTitle);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryBeginActiveDiscard(
        string sessionId,
        DateTimeOffset endedAtUtc,
        double durationSeconds)
    {
        ValidateSessionId(sessionId);
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "Recording duration must be finite and non-negative.");
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                status = 'discarding',
                ended_at = $ended_at,
                duration_seconds = $duration_seconds,
                user_discarded = 1,
                updated_at = $updated_at
            WHERE id = $id
              AND status IN ('recording', 'paused');
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$ended_at", endedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$duration_seconds", durationSeconds);
        command.Parameters.AddWithValue("$updated_at", endedAtUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    public bool TryCompleteActiveDiscard(string sessionId, DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                status = 'discarded',
                audio_output_path = NULL,
                audio_mic_path = NULL,
                audio_mix_path = NULL,
                primary_audio_path = NULL,
                temp_session_path = NULL,
                source_manifest_path = NULL,
                staged_primary_path = NULL,
                source_cleanup_pending = 0,
                artifact_error_code = NULL,
                artifact_error_message = NULL,
                updated_at = $updated_at
            WHERE id = $id
              AND status = 'discarding';
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryMarkActiveDiscardAttention(
        string sessionId,
        string errorMessage,
        DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Discard error message is required.", nameof(errorMessage));
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                status = 'attention_required',
                source_cleanup_pending = 0,
                artifact_error_code = 'recording_discard_failed',
                artifact_error_message = $artifact_error_message,
                updated_at = $updated_at
            WHERE id = $id
              AND status = 'discarding';
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$artifact_error_message", errorMessage.Trim());
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // The state transition happens before filesystem mutation. A pending row retains every
    // recorded path so a failed or interrupted delete can be retried without scanning folders.
    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public RecentRecordingRemovalWorkItem? TryBeginRecentRecordingRemoval(
        string sessionId,
        bool deleteAudioFile,
        DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);

        using var transaction = _connection.BeginTransaction();
        using var readCommand = _connection.CreateCommand();
        readCommand.Transaction = transaction;
        readCommand.CommandText =
            """
            SELECT
                id,
                created_at,
                status,
                audio_output_path,
                audio_mic_path,
                audio_mix_path,
                primary_audio_path,
                temp_session_path,
                source_manifest_path,
                staged_primary_path
            FROM meeting_session
            WHERE id = $id;
            """;
        readCommand.Parameters.AddWithValue("$id", sessionId);

        RecentRecordingRemovalWorkItem? workItem;
        using (var reader = readCommand.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            workItem = new RecentRecordingRemovalWorkItem(
                Id: reader.GetString(0),
                CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                OriginalStatus: reader.GetString(2),
                AudioOutputPath: reader.IsDBNull(3) ? null : reader.GetString(3),
                AudioMicPath: reader.IsDBNull(4) ? null : reader.GetString(4),
                AudioMixPath: reader.IsDBNull(5) ? null : reader.GetString(5),
                PrimaryAudioPath: reader.IsDBNull(6) ? null : reader.GetString(6),
                TempSessionPath: reader.IsDBNull(7) ? null : reader.GetString(7),
                SourceManifestPath: reader.IsDBNull(8) ? null : reader.GetString(8),
                StagedPrimaryPath: reader.IsDBNull(9) ? null : reader.GetString(9));
        }

        if (!CanRemoveRecentStatus(workItem.OriginalStatus, deleteAudioFile))
        {
            return null;
        }

        var nextStatus = deleteAudioFile ? "file_deletion_pending" : "history_removed";
        using var updateCommand = _connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText =
            """
            UPDATE meeting_session
            SET
                status = $next_status,
                artifact_error_code = CASE
                    WHEN $next_status = 'file_deletion_pending' THEN NULL
                    ELSE artifact_error_code
                END,
                artifact_error_message = CASE
                    WHEN $next_status = 'file_deletion_pending' THEN NULL
                    ELSE artifact_error_message
                END,
                updated_at = $updated_at
            WHERE id = $id
              AND status = $original_status;
            """;
        updateCommand.Parameters.AddWithValue("$id", sessionId);
        updateCommand.Parameters.AddWithValue("$original_status", workItem.OriginalStatus);
        updateCommand.Parameters.AddWithValue("$next_status", nextStatus);
        updateCommand.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        if (updateCommand.ExecuteNonQuery() == 0)
        {
            return null;
        }

        transaction.Commit();
        return workItem;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    public bool TryCompleteRecentRecordingRemoval(string sessionId, DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                status = 'history_removed',
                audio_output_path = NULL,
                audio_mic_path = NULL,
                audio_mix_path = NULL,
                primary_audio_path = NULL,
                temp_session_path = NULL,
                source_manifest_path = NULL,
                staged_primary_path = NULL,
                source_cleanup_pending = 0,
                artifact_error_code = NULL,
                artifact_error_message = NULL,
                updated_at = $updated_at
            WHERE id = $id
              AND status = 'file_deletion_pending';
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryMarkRecentRecordingRemovalFailed(
        string sessionId,
        string errorMessage,
        DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Removal error message is required.", nameof(errorMessage));
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                artifact_error_code = 'recording_removal_failed',
                artifact_error_message = $artifact_error_message,
                updated_at = $updated_at
            WHERE id = $id
              AND status = 'file_deletion_pending';
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$artifact_error_message", errorMessage.Trim());
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryTransitionArtifactStage(
        string sessionId,
        IReadOnlyCollection<string> expectedStatuses,
        string nextStatus,
        double artifactProgress,
        DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);
        ValidateStatus(nextStatus, nameof(nextStatus));

        using var command = _connection.CreateCommand();
        var expectedStatusClause = AddExpectedStatusParameters(command, expectedStatuses);
        command.CommandText =
            $"""
            UPDATE meeting_session
            SET
                status = $next_status,
                artifact_progress = $artifact_progress,
                updated_at = $updated_at
            WHERE id = $id
              AND status IN ({expectedStatusClause});
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$next_status", nextStatus.Trim());
        command.Parameters.AddWithValue("$artifact_progress", NormalizeArtifactProgress(artifactProgress));
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryCheckpointArtifact(
        string sessionId,
        IReadOnlyCollection<string> expectedStatuses,
        MeetingSessionArtifactCheckpoint checkpoint,
        DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateStatus(checkpoint.Status, nameof(checkpoint));

        using var command = _connection.CreateCommand();
        var expectedStatusClause = AddExpectedStatusParameters(command, expectedStatuses);
        command.CommandText =
            $"""
            UPDATE meeting_session
            SET
                status = $next_status,
                temp_session_path = COALESCE($temp_session_path, temp_session_path),
                source_manifest_path = COALESCE($source_manifest_path, source_manifest_path),
                staged_primary_path = COALESCE($staged_primary_path, staged_primary_path),
                audio_output_path = COALESCE($audio_output_path, audio_output_path),
                audio_mic_path = COALESCE($audio_mic_path, audio_mic_path),
                audio_mix_path = COALESCE($audio_mix_path, audio_mix_path),
                duration_seconds = COALESCE($duration_seconds, duration_seconds),
                artifact_progress = $artifact_progress,
                artifact_error_code = NULL,
                artifact_error_message = NULL,
                updated_at = $updated_at
            WHERE id = $id
              AND status IN ({expectedStatusClause});
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$next_status", checkpoint.Status.Trim());
        command.Parameters.AddWithValue("$temp_session_path", ToDbValue(checkpoint.TempSessionPath));
        command.Parameters.AddWithValue("$source_manifest_path", ToDbValue(checkpoint.SourceManifestPath));
        command.Parameters.AddWithValue("$staged_primary_path", ToDbValue(checkpoint.StagedPrimaryPath));
        command.Parameters.AddWithValue("$audio_output_path", ToDbValue(checkpoint.AudioOutputPath));
        command.Parameters.AddWithValue("$audio_mic_path", ToDbValue(checkpoint.AudioMicPath));
        command.Parameters.AddWithValue("$audio_mix_path", ToDbValue(checkpoint.AudioMixPath));
        command.Parameters.AddWithValue(
            "$duration_seconds",
            checkpoint.DurationSeconds.HasValue ? checkpoint.DurationSeconds.Value : DBNull.Value);
        command.Parameters.AddWithValue("$artifact_progress", NormalizeArtifactProgress(checkpoint.ArtifactProgress));
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryMarkArtifactReady(
        string sessionId,
        IReadOnlyCollection<string> expectedStatuses,
        string primaryAudioPath,
        DateTimeOffset nowUtc,
        bool sourceCleanupPending = true)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(primaryAudioPath))
        {
            throw new ArgumentException("Primary audio path is required.", nameof(primaryAudioPath));
        }

        using var command = _connection.CreateCommand();
        var expectedStatusClause = AddExpectedStatusParameters(command, expectedStatuses);
        command.CommandText =
            $"""
            UPDATE meeting_session
            SET
                status = 'ready',
                primary_audio_path = $primary_audio_path,
                staged_primary_path = NULL,
                artifact_progress = 1,
                source_cleanup_pending = $source_cleanup_pending,
                artifact_error_code = NULL,
                artifact_error_message = NULL,
                updated_at = $updated_at
            WHERE id = $id
              AND status IN ({expectedStatusClause});
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$primary_audio_path", primaryAudioPath.Trim());
        command.Parameters.AddWithValue("$source_cleanup_pending", sourceCleanupPending ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryMarkArtifactAttentionRequired(
        string sessionId,
        IReadOnlyCollection<string> expectedStatuses,
        string errorCode,
        string errorMessage,
        DateTimeOffset nowUtc,
        double? artifactProgress = null)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Artifact error code is required.", nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Artifact error message is required.", nameof(errorMessage));
        }

        using var command = _connection.CreateCommand();
        var expectedStatusClause = AddExpectedStatusParameters(command, expectedStatuses);
        command.CommandText =
            $"""
            UPDATE meeting_session
            SET
                status = 'attention_required',
                artifact_progress = COALESCE($artifact_progress, artifact_progress),
                source_cleanup_pending = 0,
                artifact_error_code = $artifact_error_code,
                artifact_error_message = $artifact_error_message,
                updated_at = $updated_at
            WHERE id = $id
              AND status IN ({expectedStatusClause});
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue(
            "$artifact_progress",
            artifactProgress.HasValue ? NormalizeArtifactProgress(artifactProgress.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$artifact_error_code", errorCode.Trim());
        command.Parameters.AddWithValue("$artifact_error_message", errorMessage.Trim());
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public bool TryCompleteSourceCleanup(string sessionId, DateTimeOffset nowUtc)
    {
        ValidateSessionId(sessionId);

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                temp_session_path = NULL,
                source_manifest_path = NULL,
                staged_primary_path = NULL,
                audio_output_path = NULL,
                audio_mic_path = NULL,
                audio_mix_path = NULL,
                source_cleanup_pending = 0,
                updated_at = $updated_at
            WHERE id = $id
              AND status = 'ready'
              AND source_cleanup_pending = 1;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    public IReadOnlyList<MeetingSessionArtifactWorkItem> ListRecoverableArtifactWork()
    {
        var items = new List<MeetingSessionArtifactWorkItem>();
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                created_at,
                started_at,
                ended_at,
                status,
                mode,
                source_type,
                source_app,
                audio_output_path,
                audio_mic_path,
                audio_mix_path,
                primary_audio_path,
                temp_session_path,
                source_manifest_path,
                staged_primary_path,
                duration_seconds,
                artifact_progress,
                source_cleanup_pending,
                artifact_error_code,
                artifact_error_message
            FROM meeting_session
            WHERE status IN ('recording', 'paused', 'stopping', 'processing', 'verifying', 'promoting')
               OR (status = 'ready' AND source_cleanup_pending = 1)
            ORDER BY created_at ASC, id ASC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new MeetingSessionArtifactWorkItem(
                Id: reader.GetString(0),
                CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                StartedAtUtc: reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                EndedAtUtc: reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                Status: reader.GetString(4),
                Mode: reader.GetString(5),
                SourceType: reader.GetString(6),
                SourceApp: reader.IsDBNull(7) ? null : reader.GetString(7),
                AudioOutputPath: reader.IsDBNull(8) ? null : reader.GetString(8),
                AudioMicPath: reader.IsDBNull(9) ? null : reader.GetString(9),
                AudioMixPath: reader.IsDBNull(10) ? null : reader.GetString(10),
                PrimaryAudioPath: reader.IsDBNull(11) ? null : reader.GetString(11),
                TempSessionPath: reader.IsDBNull(12) ? null : reader.GetString(12),
                SourceManifestPath: reader.IsDBNull(13) ? null : reader.GetString(13),
                StagedPrimaryPath: reader.IsDBNull(14) ? null : reader.GetString(14),
                DurationSeconds: reader.IsDBNull(15) ? null : reader.GetDouble(15),
                ArtifactProgress: reader.GetDouble(16),
                SourceCleanupPending: reader.GetInt64(17) != 0,
                ArtifactErrorCode: reader.IsDBNull(18) ? null : reader.GetString(18),
                ArtifactErrorMessage: reader.IsDBNull(19) ? null : reader.GetString(19)));
        }

        return items;
    }

    // @spec spec://modules/app/FEAT-004-recordings-home-and-artifact-access#actions.retry
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    public bool TryQueueTranscriptionRetry(string sessionId, DateTimeOffset nowUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                transcription_status = 'queued',
                queued_at = $queued_at,
                updated_at = $updated_at,
                retry_attempt_count = 0,
                next_retry_at = NULL,
                error_code = NULL,
                error_message = NULL
            WHERE id = $id
              AND transcription_status = 'failed'
              AND (
                    COALESCE(audio_mix_path, '') <> ''
                 OR COALESCE(audio_output_path, '') <> ''
                 OR COALESCE(audio_mic_path, '') <> ''
              );
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$queued_at", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#queue-management.ordering
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    public MeetingSessionTranscriptionWorkItem? TryClaimNextQueuedTranscription(DateTimeOffset nowUtc)
    {
        string? sessionId;
        using (var select = _connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT id
                FROM meeting_session
                WHERE transcription_status = 'queued'
                ORDER BY COALESCE(queued_at, created_at) ASC, id ASC
                LIMIT 1;
                """;
            var raw = select.ExecuteScalar();
            sessionId = raw is null || raw is DBNull ? null : Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        using (var update = _connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE meeting_session
                SET
                    transcription_status = 'uploading',
                    updated_at = $updated_at
                WHERE id = $id
                  AND transcription_status = 'queued';
                """;
            update.Parameters.AddWithValue("$id", sessionId);
            update.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
            if (update.ExecuteNonQuery() <= 0)
            {
                return null;
            }
        }

        return GetTranscriptionWorkItem(sessionId);
    }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    public int PromoteDueScheduledRetries(DateTimeOffset nowUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                transcription_status = 'queued',
                queued_at = $queued_at,
                updated_at = $updated_at,
                next_retry_at = NULL
            WHERE transcription_status = 'retry_scheduled'
              AND next_retry_at IS NOT NULL
              AND next_retry_at <= $now_utc;
            """;
        command.Parameters.AddWithValue("$queued_at", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        return command.ExecuteNonQuery();
    }

    public void MarkTranscriptionProcessing(string sessionId, DateTimeOffset nowUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                transcription_status = 'processing',
                updated_at = $updated_at
            WHERE id = $id
              AND transcription_status = 'uploading';
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#artifact-materialization.paths
    public void MarkTranscriptionCompleted(string sessionId, string markdownPath, string jsonPath, DateTimeOffset nowUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                transcription_status = 'completed',
                transcript_md_path = $markdown_path,
                transcript_json_path = $json_path,
                updated_at = $updated_at,
                error_code = NULL,
                error_message = NULL,
                next_retry_at = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$markdown_path", markdownPath);
        command.Parameters.AddWithValue("$json_path", jsonPath);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#retry-lifecycle.classification
    public void MarkTranscriptionFailed(
        string sessionId,
        string errorCode,
        string errorMessage,
        DateTimeOffset nowUtc,
        bool scheduleRetry,
        DateTimeOffset? nextRetryAtUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                transcription_status = $transcription_status,
                error_code = $error_code,
                error_message = $error_message,
                updated_at = $updated_at,
                retry_attempt_count = retry_attempt_count + 1,
                last_retry_at = $last_retry_at,
                next_retry_at = $next_retry_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$transcription_status", scheduleRetry ? "retry_scheduled" : "failed");
        command.Parameters.AddWithValue("$error_code", errorCode);
        command.Parameters.AddWithValue("$error_message", errorMessage);
        command.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$last_retry_at", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$next_retry_at", scheduleRetry && nextRetryAtUtc.HasValue
            ? nextRetryAtUtc.Value.ToString("O")
            : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private MeetingSessionTranscriptionWorkItem? GetTranscriptionWorkItem(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                created_at,
                source_app,
                mode,
                audio_output_path,
                audio_mic_path,
                audio_mix_path,
                transcription_model,
                diarization_enabled,
                language,
                retry_attempt_count,
                started_at,
                ended_at,
                duration_seconds,
                output_device_id,
                microphone_device_id
            FROM meeting_session
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MeetingSessionTranscriptionWorkItem(
            Id: reader.GetString(0),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            StartedAtUtc: reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            EndedAtUtc: reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            DurationSeconds: reader.IsDBNull(13) ? null : reader.GetDouble(13),
            SourceApp: reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
            Mode: reader.IsDBNull(3) ? "unknown" : reader.GetString(3),
            OutputDeviceId: reader.IsDBNull(14) ? null : reader.GetString(14),
            MicrophoneDeviceId: reader.IsDBNull(15) ? null : reader.GetString(15),
            AudioOutputPath: reader.IsDBNull(4) ? null : reader.GetString(4),
            AudioMicPath: reader.IsDBNull(5) ? null : reader.GetString(5),
            AudioMixPath: reader.IsDBNull(6) ? null : reader.GetString(6),
            TranscriptionModel: reader.IsDBNull(7) ? null : reader.GetString(7),
            DiarizationEnabled: !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
            Language: reader.IsDBNull(9) ? null : reader.GetString(9),
            RetryAttemptCount: reader.IsDBNull(10) ? 0 : reader.GetInt32(10));
    }

    private static string AddExpectedStatusParameters(
        SqliteCommand command,
        IReadOnlyCollection<string> expectedStatuses)
    {
        ArgumentNullException.ThrowIfNull(expectedStatuses);
        var statuses = expectedStatuses
            .Where(static status => !string.IsNullOrWhiteSpace(status))
            .Select(static status => status.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (statuses.Length == 0)
        {
            throw new ArgumentException("At least one expected status is required.", nameof(expectedStatuses));
        }

        var parameterNames = new string[statuses.Length];
        for (var index = 0; index < statuses.Length; index++)
        {
            parameterNames[index] = $"$expected_status_{index}";
            command.Parameters.AddWithValue(parameterNames[index], statuses[index]);
        }

        return string.Join(", ", parameterNames);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }
    }

    private static bool CanRemoveRecentStatus(string status, bool deleteAudioFile)
    {
        if (string.Equals(status, "history_removed", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(status, "file_deletion_pending", StringComparison.Ordinal))
        {
            return deleteAudioFile;
        }

        return status is not ("prebuffering"
            or "awaiting_confirmation"
            or "recording"
            or "paused"
            or "stopping"
            or "processing"
            or "verifying"
            or "promoting");
    }

    private static void ValidateStatus(string status, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Artifact status is required.", parameterName);
        }
    }

    private static double NormalizeArtifactProgress(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Artifact progress must be finite.");
        }

        return Math.Clamp(value, 0, 1);
    }

    private static object ToDbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object ToDbValue(DateTimeOffset? value) => value.HasValue ? value.Value.ToString("O") : DBNull.Value;
}

/// <summary>
/// Durable session state shared by capture, artifact finalization and legacy readers.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#migration
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
public sealed record MeetingSessionRecord(
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string Status,
    string Mode,
    string SourceType,
    string? SourceApp,
    int? SourceProcessId,
    string? OutputDeviceId,
    string? MicrophoneDeviceId,
    string? AudioOutputPath,
    string? AudioMicPath,
    string? AudioMixPath,
    string? TranscriptMarkdownPath,
    string? TranscriptJsonPath,
    double? DurationSeconds,
    string TranscriptionStatus,
    DateTimeOffset? QueuedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    int RetryAttemptCount,
    DateTimeOffset? NextRetryAtUtc,
    DateTimeOffset? LastRetryAtUtc,
    string? TranscriptionModel,
    bool DiarizationEnabled,
    string? Language,
    string? ErrorCode,
    string? ErrorMessage,
    bool UserDiscarded,
    string? PrimaryAudioPath = null,
    string? TempSessionPath = null,
    string? SourceManifestPath = null,
    string? StagedPrimaryPath = null,
    double ArtifactProgress = 0,
    bool SourceCleanupPending = false,
    string? ArtifactErrorCode = null,
    string? ArtifactErrorMessage = null)
{
    public static MeetingSessionRecord Create(
        Guid id,
        DateTimeOffset createdAtUtc,
        string mode,
        string sourceType,
        string? outputDeviceId,
        string? microphoneDeviceId) =>
        new(
            Id: id.ToString("N"),
            CreatedAtUtc: createdAtUtc,
            StartedAtUtc: createdAtUtc,
            EndedAtUtc: null,
            Status: "recording",
            Mode: mode,
            SourceType: sourceType,
            SourceApp: null,
            SourceProcessId: null,
            OutputDeviceId: outputDeviceId,
            MicrophoneDeviceId: microphoneDeviceId,
            AudioOutputPath: null,
            AudioMicPath: null,
            AudioMixPath: null,
            TranscriptMarkdownPath: null,
            TranscriptJsonPath: null,
            DurationSeconds: null,
            TranscriptionStatus: "not_started",
            QueuedAtUtc: null,
            UpdatedAtUtc: createdAtUtc,
            RetryAttemptCount: 0,
            NextRetryAtUtc: null,
            LastRetryAtUtc: null,
            TranscriptionModel: null,
            DiarizationEnabled: true,
            Language: null,
            ErrorCode: null,
            ErrorMessage: null,
            UserDiscarded: false);
}

/// <summary>
/// Recent-session projection with a canonical primary artifact and recovery state.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// </remarks>
public sealed record MeetingSessionListItem(
    string Id,
    string? SourceApp,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    double? DurationSeconds,
    string RecordingStatus,
    string TranscriptionStatus,
    string? AudioOutputPath,
    string? AudioMicPath,
    string? AudioMixPath,
    string? TranscriptMarkdownPath,
    string? ErrorCode,
    string? ErrorMessage,
    string? PrimaryAudioPath = null,
    string? TempSessionPath = null,
    string? SourceManifestPath = null,
    string? StagedPrimaryPath = null,
    double ArtifactProgress = 0,
    bool SourceCleanupPending = false,
    string? ArtifactErrorCode = null,
    string? ArtifactErrorMessage = null,
    string? TranscriptJsonPath = null,
    string? DisplayTitle = null,
    CurrentTranscriptionJobListItem? CurrentTranscription = null)
{
    public string? SessionDirectoryPath =>
        FirstDirectory(PrimaryAudioPath)
        ?? FirstDirectory(TranscriptMarkdownPath)
        ?? FirstDirectory(TranscriptJsonPath)
        ?? FirstDirectory(StagedPrimaryPath)
        ?? FirstDirectory(AudioMixPath)
        ?? FirstDirectory(AudioOutputPath)
        ?? FirstDirectory(AudioMicPath)
        ?? (string.IsNullOrWhiteSpace(TempSessionPath) ? null : TempSessionPath);

    private static string? FirstDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return Path.GetDirectoryName(filePath);
    }
}

/// <summary>
/// Provider-neutral projection of the job selected by a meeting session.
/// Final paths become visible only after the staged artifacts were promoted successfully.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed record CurrentTranscriptionJobListItem(
    string JobId,
    string EngineId,
    TranscriptionExecutionKind ExecutionKind,
    string ModelId,
    TranscriptionJobStatus Status,
    double Progress,
    int? CurrentChunkIndex,
    DateTimeOffset? NextAttemptAtUtc,
    string? StableErrorCode,
    string? ErrorMessage,
    TranscriptionArtifactPublicationState ArtifactPublicationState,
    string? TranscriptMarkdownPath,
    string? TranscriptJsonPath,
    string? UsageJson = null,
    int ChunkCount = 0,
    string? ProviderRequestId = null,
    CurrentLocalTranscriptionDiagnosticsListItem? LocalDiagnostics = null);

/// <summary>
/// Bounded, content-free local runtime evidence persisted with the selected job.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed record CurrentLocalTranscriptionDiagnosticsListItem(
    string RequestedBackend,
    string? ResolvedBackend,
    int ThreadCount,
    string RuntimeVersion,
    string NativeBundleManifestSha256,
    string ModelSha256,
    long? ProcessingDurationMilliseconds);

/// <summary>
/// Durable artifact paths captured before a recent-recording removal mutates the filesystem.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
public sealed record RecentRecordingRemovalWorkItem(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string OriginalStatus,
    string? AudioOutputPath,
    string? AudioMicPath,
    string? AudioMixPath,
    string? PrimaryAudioPath,
    string? TempSessionPath,
    string? SourceManifestPath,
    string? StagedPrimaryPath);

public sealed record MeetingSessionTranscriptionWorkItem(
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    double? DurationSeconds,
    string SourceApp,
    string Mode,
    string? OutputDeviceId,
    string? MicrophoneDeviceId,
    string? AudioOutputPath,
    string? AudioMicPath,
    string? AudioMixPath,
    string? TranscriptionModel,
    bool DiarizationEnabled,
    string? Language,
    int RetryAttemptCount);

/// <summary>
/// Recoverable file/repository checkpoint for one artifact pipeline transition.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.sources
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
public sealed record MeetingSessionArtifactCheckpoint(
    string Status,
    string? TempSessionPath,
    string? SourceManifestPath,
    string? StagedPrimaryPath,
    string? AudioOutputPath,
    string? AudioMicPath,
    string? AudioMixPath,
    double? DurationSeconds,
    double ArtifactProgress);

/// <summary>
/// Repository projection used by startup recovery without scanning user folders.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
public sealed record MeetingSessionArtifactWorkItem(
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string Status,
    string Mode,
    string SourceType,
    string? SourceApp,
    string? AudioOutputPath,
    string? AudioMicPath,
    string? AudioMixPath,
    string? PrimaryAudioPath,
    string? TempSessionPath,
    string? SourceManifestPath,
    string? StagedPrimaryPath,
    double? DurationSeconds,
    double ArtifactProgress,
    bool SourceCleanupPending,
    string? ArtifactErrorCode,
    string? ArtifactErrorMessage);
