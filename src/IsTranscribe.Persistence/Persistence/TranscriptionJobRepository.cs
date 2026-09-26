using System.Data;
using System.Globalization;
using System.Text.Json;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;

namespace IsTranscribe.Host.Persistence;

/// <summary>
/// Owns the durable, engine-neutral transcription queue and chunk checkpoints.
/// Every operation opens an independent SQLite connection so the worker does not share the
/// release runtime's long-lived connection.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed class TranscriptionJobRepository
{
    private const int BusyTimeoutMilliseconds = 5_000;

    private const int MaxLocalAttemptIndex = 1_000_000;

    private const int MaxLocalAttemptIdentifierLength = 256;

    private const int MaxStableFailureCategoryLength = 128;

    private const int MaxBackendHistoryEntries = 32;

    private const int MaxBackendHistoryJsonLength = 512;

    private const int DefaultLocalAttemptReadCount = 256;

    private const int MaxLocalAttemptReadCount = 1_024;

    private const string JobColumns =
        """
        id,
        session_id,
        engine_id,
        execution_kind,
        model_id,
        engine_options_json,
        requested_language,
        detected_language,
        input_audio_path,
        input_sha256,
        input_size_bytes,
        input_duration_seconds,
        status,
        progress,
        current_chunk_index,
        current_chunk_id,
        queued_at,
        created_at,
        updated_at,
        attempt_count,
        next_attempt_at,
        last_attempt_at,
        stable_error_code,
        error_message,
        transcript_md_path,
        transcript_json_path,
        usage_json,
        remote_consent_revision,
        remote_consent_at,
        privacy_policy_json,
        manifest_version,
        manifest_path,
        artifact_publication_state,
        staged_transcript_md_path,
        staged_transcript_json_path,
        staged_transcript_md_sha256,
        staged_transcript_json_sha256,
        replace_existing,
        cancellation_requested,
        completed_at,
        cancelled_at,
        trigger_kind,
        supersedes_job_id
        """;

    private const string ChunkColumns =
        """
        id,
        job_id,
        sequence_index,
        parent_chunk_id,
        split_depth,
        start_milliseconds,
        end_milliseconds,
        overlap_milliseconds,
        artifact_path,
        artifact_format,
        artifact_sha256,
        artifact_size_bytes,
        status,
        attempt_count,
        created_at,
        updated_at,
        engine_request_id,
        result_path,
        result_sha256,
        result_metadata_json,
        usage_json,
        stable_error_code,
        error_message
        """;

    private const string LocalAttemptColumns =
        """
        job_id,
        chunk_id,
        attempt_index,
        requested_backend,
        resolved_backend,
        worker_started_at,
        worker_ended_at,
        exit_category,
        decode_duration_milliseconds,
        inference_duration_milliseconds,
        peak_working_set_bytes,
        stable_failure_category,
        created_at
        """;

    private const string LocalJobColumns =
        """
        job_id,
        model_catalog_version,
        model_catalog_revision,
        model_format,
        model_path,
        model_size_bytes,
        model_sha256,
        runtime_version,
        runtime_commit,
        runtime_source_archive_sha256,
        native_bundle_manifest_sha256,
        bridge_abi_version,
        worker_protocol_version,
        requested_backend,
        resolved_backend,
        backend_history_json,
        thread_count,
        inference_parameters_json,
        chunk_profile_version,
        run_identity_sha256,
        policy_defer_reason,
        native_crash_count,
        last_crash_backend,
        processing_duration_milliseconds,
        peak_working_set_bytes,
        created_at,
        updated_at
        """;

    private readonly string _connectionString;

    public TranscriptionJobRepository(LocalAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<TranscriptionEnqueueResult> EnqueueAsync(
        TranscriptionJobEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $$"""
                INSERT INTO transcription_job (
                    id,
                    session_id,
                    engine_id,
                    execution_kind,
                    model_id,
                    engine_options_json,
                    requested_language,
                    input_audio_path,
                    input_sha256,
                    input_size_bytes,
                    input_duration_seconds,
                    status,
                    progress,
                    queued_at,
                    created_at,
                    updated_at,
                    remote_consent_revision,
                    remote_consent_at,
                    privacy_policy_json,
                    manifest_version,
                    manifest_path,
                    replace_existing,
                    trigger_kind,
                    supersedes_job_id)
                SELECT
                    $id,
                    $session_id,
                    $engine_id,
                    $execution_kind,
                    $model_id,
                    $engine_options_json,
                    $requested_language,
                    $input_audio_path,
                    $input_sha256,
                    $input_size_bytes,
                    $input_duration_seconds,
                    'queued',
                    0,
                    $queued_at,
                    $created_at,
                    $updated_at,
                    $remote_consent_revision,
                    $remote_consent_at,
                    $privacy_policy_json,
                    $manifest_version,
                    $manifest_path,
                    $replace_existing,
                    $trigger_kind,
                    $supersedes_job_id
                FROM meeting_session
                WHERE id = $session_id
                  AND status IN ('ready', 'saved')
                  AND user_discarded = 0
                  AND primary_audio_path = $input_audio_path
                ON CONFLICT DO NOTHING
                RETURNING {{JobColumns}};
                """;
            AddEnqueueParameters(insert, normalized);

            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var created = ReadJob(reader);
                await reader.DisposeAsync().ConfigureAwait(false);
                await InsertLocalExecutionAsync(
                        connection,
                        transaction,
                        created,
                        normalized.LocalExecution,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SetCurrentJobPointerAsync(
                        connection,
                        transaction,
                        created.SessionId,
                        created.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new TranscriptionEnqueueResult(created, Created: true);
            }
        }

        var activeJob = await ReadActiveJobForSessionAsync(
                connection,
                transaction,
                normalized.SessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeJob is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TranscriptionEnqueueResult(activeJob, Created: false);
        }

        var collidingJob = await ReadJobAsync(
                connection,
                transaction,
                normalized.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (collidingJob is not null)
        {
            throw new InvalidOperationException(
                $"Transcription job id '{normalized.JobId}' is already in use.");
        }

        throw new InvalidOperationException(
            "The transcription job could not be enqueued and no active job was found for the session.");
    }

    public async ValueTask<TranscriptionJobRecord?> GetAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(connection, transaction: null, normalizedJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the immutable model/runtime identity for a local job without probing the model or
    /// loading native code.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    /// </remarks>
    public async ValueTask<LocalTranscriptionJobRecord?> GetLocalExecutionAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLocalJobAsync(
                connection,
                transaction: null,
                normalizedJobId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether a verified model payload is still frozen into a resumable local job.
    /// Terminal jobs release the payload; queued, interrupted and attention-required jobs retain it.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// </remarks>
    public async ValueTask<bool> IsLocalModelRetainedAsync(
        string modelSha256,
        CancellationToken cancellationToken)
    {
        var normalizedSha256 = NormalizeSha256(modelSha256, nameof(modelSha256));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM transcription_local_job AS local_job
                INNER JOIN transcription_job AS job ON job.id = local_job.job_id
                WHERE local_job.model_sha256 = $model_sha256
                  AND job.status NOT IN ('completed', 'cancelled', 'failed'));
            """;
        command.Parameters.AddWithValue("$model_sha256", normalizedSha256);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Atomically opens the next deterministic worker attempt for an existing local job chunk.
    /// Repeating the exact start is idempotent; a reused index with different identity is rejected.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// </remarks>
    public async ValueTask<LocalTranscriptionChunkAttemptStartResult> StartLocalChunkAttemptAsync(
        LocalTranscriptionChunkAttemptStart start,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(start);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var existing = await ReadLocalAttemptAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ChunkId,
                normalized.AttemptIndex,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSameAttemptStart(existing, normalized);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LocalTranscriptionChunkAttemptStartResult(existing, Created: false);
        }

        var sequenceState = await ReadLocalAttemptSequenceStateAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ChunkId,
                cancellationToken)
            .ConfigureAwait(false);
        if (sequenceState.HasOpenAttempt)
        {
            throw new InvalidOperationException(
                $"Chunk '{normalized.ChunkId}' already has an open local worker attempt.");
        }

        if (normalized.AttemptIndex != sequenceState.NextAttemptIndex)
        {
            throw new InvalidOperationException(
                $"Local worker attempt index {normalized.AttemptIndex} is out of sequence; "
                + $"expected {sequenceState.NextAttemptIndex}.");
        }

        var startedAt = ToDatabaseTimestamp(normalized.WorkerStartedAtUtc);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO transcription_local_chunk_attempt (
                    job_id,
                    chunk_id,
                    attempt_index,
                    requested_backend,
                    worker_started_at,
                    created_at)
                SELECT
                    $job_id,
                    $chunk_id,
                    $attempt_index,
                    $requested_backend,
                    $worker_started_at,
                    $created_at
                FROM transcription_local_job AS local_job
                INNER JOIN transcription_job AS job
                    ON job.id = local_job.job_id
                INNER JOIN transcription_chunk AS chunk
                    ON chunk.job_id = local_job.job_id
                   AND chunk.id = $chunk_id
                WHERE local_job.job_id = $job_id
                  AND job.execution_kind = 'local'
                  AND job.status IN ('preparing', 'uploading', 'processing')
                  AND job.cancellation_requested = 0
                  AND chunk.status NOT IN ('completed', 'split', 'cancelled');
                """;
            insert.Parameters.AddWithValue("$job_id", normalized.JobId);
            insert.Parameters.AddWithValue("$chunk_id", normalized.ChunkId);
            insert.Parameters.AddWithValue("$attempt_index", normalized.AttemptIndex);
            insert.Parameters.AddWithValue(
                "$requested_backend",
                ToDatabaseLocalBackend(normalized.RequestedBackend));
            insert.Parameters.AddWithValue("$worker_started_at", startedAt);
            insert.Parameters.AddWithValue("$created_at", startedAt);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Local worker attempt requires an active local job and executable chunk; "
                    + $"'{normalized.JobId}/{normalized.ChunkId}' is not eligible.");
            }
        }

        await using (var updateChunkAttemptCount = connection.CreateCommand())
        {
            updateChunkAttemptCount.Transaction = transaction;
            updateChunkAttemptCount.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    attempt_count = CASE
                        WHEN attempt_count < $attempt_count THEN $attempt_count
                        ELSE attempt_count
                    END,
                    updated_at = $updated_at
                WHERE job_id = $job_id
                  AND id = $chunk_id;
                """;
            updateChunkAttemptCount.Parameters.AddWithValue(
                "$attempt_count",
                normalized.AttemptIndex + 1);
            updateChunkAttemptCount.Parameters.AddWithValue("$updated_at", startedAt);
            updateChunkAttemptCount.Parameters.AddWithValue("$job_id", normalized.JobId);
            updateChunkAttemptCount.Parameters.AddWithValue("$chunk_id", normalized.ChunkId);
            if (await updateChunkAttemptCount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Chunk '{normalized.ChunkId}' disappeared while its local attempt was starting.");
            }
        }

        var created = await ReadLocalAttemptAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ChunkId,
                normalized.AttemptIndex,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The local worker attempt was not persisted.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LocalTranscriptionChunkAttemptStartResult(created, Created: true);
    }

    /// <summary>
    /// Completes only the exact open attempt and updates bounded aggregate runtime telemetry in the
    /// same transaction. A byte-identical repeated completion is idempotent.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    /// </remarks>
    public async ValueTask<bool> TryCompleteLocalChunkAttemptAsync(
        LocalTranscriptionChunkAttemptCompletion completion,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(completion);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var existing = await ReadLocalAttemptAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ChunkId,
                normalized.AttemptIndex,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null
            || existing.WorkerStartedAtUtc != normalized.WorkerStartedAtUtc)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (existing.Status != LocalTranscriptionAttemptStatus.Running)
        {
            EnsureSameAttemptCompletion(existing, normalized);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using (var completeAttempt = connection.CreateCommand())
        {
            completeAttempt.Transaction = transaction;
            completeAttempt.CommandText =
                """
                UPDATE transcription_local_chunk_attempt
                SET
                    resolved_backend = $resolved_backend,
                    worker_ended_at = $worker_ended_at,
                    exit_category = $exit_category,
                    decode_duration_milliseconds = $decode_duration_milliseconds,
                    inference_duration_milliseconds = $inference_duration_milliseconds,
                    peak_working_set_bytes = $peak_working_set_bytes,
                    stable_failure_category = $stable_failure_category
                WHERE job_id = $job_id
                  AND chunk_id = $chunk_id
                  AND attempt_index = $attempt_index
                  AND worker_started_at = $worker_started_at
                  AND worker_ended_at IS NULL
                  AND exit_category IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM transcription_local_chunk_attempt AS later
                      WHERE later.job_id = $job_id
                        AND later.chunk_id = $chunk_id
                        AND later.attempt_index > $attempt_index);
                """;
            AddLocalAttemptCompletionParameters(completeAttempt, normalized);
            if (await completeAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var localJob = await ReadLocalJobAsync(
                connection,
                transaction,
                normalized.JobId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Local execution identity for job '{normalized.JobId}' was not found.");
        var attemptProcessingDuration = SumOptionalDurations(
            normalized.DecodeDurationMilliseconds,
            normalized.InferenceDurationMilliseconds);
        var processingDuration = AddOptionalDuration(
            localJob.ProcessingDurationMilliseconds,
            attemptProcessingDuration);
        var peakWorkingSet = MaxOptional(
            localJob.PeakWorkingSetBytes,
            normalized.PeakWorkingSetBytes);
        var isCrash = normalized.Status == LocalTranscriptionAttemptStatus.Crashed;
        var crashCount = isCrash
            ? checked(localJob.NativeCrashCount + 1)
            : localJob.NativeCrashCount;
        var crashBackend = isCrash
            ? normalized.ResolvedBackend ??
                (existing.RequestedBackend == LocalTranscriptionBackend.Auto
                    ? null
                    : existing.RequestedBackend)
            : localJob.LastCrashBackend;
        var backendHistory = normalized.ResolvedBackend is { } resolvedBackend
            ? AppendBackendHistory(localJob.Execution.BackendHistoryJson, resolvedBackend)
            : localJob.Execution.BackendHistoryJson;

        await using (var updateAggregate = connection.CreateCommand())
        {
            updateAggregate.Transaction = transaction;
            updateAggregate.CommandText =
                """
                UPDATE transcription_local_job
                SET
                    resolved_backend = $resolved_backend,
                    backend_history_json = $backend_history_json,
                    native_crash_count = $native_crash_count,
                    last_crash_backend = $last_crash_backend,
                    processing_duration_milliseconds = $processing_duration_milliseconds,
                    peak_working_set_bytes = $peak_working_set_bytes,
                    updated_at = $updated_at
                WHERE job_id = $job_id;
                """;
            var aggregateResolvedBackend = normalized.ResolvedBackend
                ?? localJob.Execution.ResolvedBackend;
            updateAggregate.Parameters.AddWithValue(
                "$resolved_backend",
                aggregateResolvedBackend is null
                    ? DBNull.Value
                    : ToDatabaseLocalBackend(aggregateResolvedBackend.Value));
            updateAggregate.Parameters.AddWithValue(
                "$backend_history_json",
                ToDbValue(backendHistory));
            updateAggregate.Parameters.AddWithValue("$native_crash_count", crashCount);
            updateAggregate.Parameters.AddWithValue(
                "$last_crash_backend",
                crashBackend is null
                    ? DBNull.Value
                    : ToDatabaseLocalBackend(crashBackend.Value));
            updateAggregate.Parameters.AddWithValue(
                "$processing_duration_milliseconds",
                processingDuration ?? (object)DBNull.Value);
            updateAggregate.Parameters.AddWithValue(
                "$peak_working_set_bytes",
                peakWorkingSet ?? (object)DBNull.Value);
            updateAggregate.Parameters.AddWithValue(
                "$updated_at",
                ToDatabaseTimestamp(normalized.WorkerEndedAtUtc));
            updateAggregate.Parameters.AddWithValue("$job_id", normalized.JobId);
            if (await updateAggregate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Local aggregate telemetry for job '{normalized.JobId}' was not updated.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Lists bounded technical attempt records in deterministic start order for diagnostics and
    /// restart recovery. No transcript or audio content is selected.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
    /// </remarks>
    public async ValueTask<IReadOnlyList<LocalTranscriptionChunkAttemptRecord>>
        ListLocalChunkAttemptsAsync(
            string jobId,
            CancellationToken cancellationToken,
            int maximumCount = DefaultLocalAttemptReadCount)
    {
        var normalizedJobId = RequireBoundedValue(
            jobId,
            nameof(jobId),
            MaxLocalAttemptIdentifierLength);
        if (maximumCount is <= 0 or > MaxLocalAttemptReadCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                $"Attempt read count must be between one and {MaxLocalAttemptReadCount}.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            SELECT {{LocalAttemptColumns}}
            FROM (
                SELECT {{LocalAttemptColumns}}
                FROM transcription_local_chunk_attempt
                WHERE job_id = $job_id
                ORDER BY worker_started_at DESC, chunk_id DESC, attempt_index DESC
                LIMIT $maximum_count)
            ORDER BY worker_started_at ASC, chunk_id ASC, attempt_index ASC;
            """;
        command.Parameters.AddWithValue("$job_id", normalizedJobId);
        command.Parameters.AddWithValue("$maximum_count", maximumCount);

        var attempts = new List<LocalTranscriptionChunkAttemptRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(ReadLocalAttempt(reader));
        }

        return attempts;
    }

    /// <summary>
    /// Atomically retires an attention-required job whose frozen execution configuration is no
    /// longer usable, inserts its explicitly reconfigured successor, and switches the session
    /// pointer. Published session artifacts are intentionally left untouched until the successor
    /// completes its own publication transaction.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    /// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
    /// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
    /// </remarks>
    public async ValueTask<TranscriptionEnqueueResult> SupersedeConfigurationAsync(
        string attentionJobId,
        TranscriptionJobEnqueueRequest replacementRequest,
        CancellationToken cancellationToken)
    {
        var normalizedAttentionJobId = RequireValue(attentionJobId, nameof(attentionJobId));
        var normalized = Normalize(replacementRequest) with
        {
            SupersedesJobId = normalizedAttentionJobId
        };
        if (string.Equals(normalizedAttentionJobId, normalized.JobId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A reconfigured transcription job requires a new identity.",
                nameof(replacementRequest));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var attentionJob = await ReadJobAsync(
                connection,
                transaction,
                normalizedAttentionJobId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Transcription job '{normalizedAttentionJobId}' was not found.");
        if (!string.Equals(attentionJob.SessionId, normalized.SessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A replacement transcription job must belong to the same recording session.",
                nameof(replacementRequest));
        }

        var existingReplacement = await ReadJobAsync(
                connection,
                transaction,
                normalized.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingReplacement is not null)
        {
            var currentJobId = await ReadCurrentJobPointerAsync(
                    connection,
                    transaction,
                    normalized.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (attentionJob.Status == TranscriptionJobStatus.Cancelled
                && attentionJob.CancellationRequested
                && string.Equals(attentionJob.StableErrorCode, "superseded", StringComparison.Ordinal)
                && string.Equals(currentJobId, existingReplacement.Id, StringComparison.Ordinal)
                && HasSameFrozenIdentity(existingReplacement, normalized))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new TranscriptionEnqueueResult(existingReplacement, Created: false);
            }

            throw new InvalidOperationException(
                $"Transcription job id '{normalized.JobId}' is already in use.");
        }

        if (attentionJob.Status != TranscriptionJobStatus.AttentionRequired
            || attentionJob.CancellationRequested
            || !IsConfigurationRemediationCode(attentionJob.StableErrorCode))
        {
            throw new InvalidOperationException(
                $"Transcription job '{attentionJob.Id}' is not awaiting a configuration change.");
        }

        if (!string.Equals(attentionJob.EngineId, normalized.EngineId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Configuration retry cannot change the transcription provider.",
                nameof(replacementRequest));
        }

        if (string.Equals(attentionJob.ModelId, normalized.ModelId, StringComparison.Ordinal)
            && string.Equals(
                attentionJob.EngineOptionsJson,
                normalized.EngineOptionsJson,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The replacement model or engine privacy options must differ from the superseded job.",
                nameof(replacementRequest));
        }

        var now = ToDatabaseTimestamp(normalized.QueuedAtUtc);
        await using (var retireJob = connection.CreateCommand())
        {
            retireJob.Transaction = transaction;
            retireJob.CommandText =
                """
                UPDATE transcription_job
                SET
                    status = 'cancelled',
                    cancellation_requested = 1,
                    cancelled_at = $cancelled_at,
                    updated_at = $updated_at,
                    next_attempt_at = NULL,
                    current_chunk_index = NULL,
                    current_chunk_id = NULL,
                    stable_error_code = 'superseded',
                    error_message = NULL
                WHERE id = $job_id
                  AND status = 'attention_required'
                  AND cancellation_requested = 0
                  AND stable_error_code IN (
                      'invalid_engine_configuration',
                      'zdr_route_unavailable',
                      'native_crash',
                      'insufficient_memory')
                  AND EXISTS (
                      SELECT 1
                      FROM meeting_session
                      WHERE id = $session_id
                        AND current_transcription_job_id = $job_id);
                """;
            retireJob.Parameters.AddWithValue("$cancelled_at", now);
            retireJob.Parameters.AddWithValue("$updated_at", now);
            retireJob.Parameters.AddWithValue("$job_id", normalizedAttentionJobId);
            retireJob.Parameters.AddWithValue("$session_id", normalized.SessionId);
            if (await retireJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Transcription job '{attentionJob.Id}' changed before it could be superseded.");
            }
        }

        await using (var retireChunks = connection.CreateCommand())
        {
            retireChunks.Transaction = transaction;
            retireChunks.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = 'cancelled',
                    updated_at = $updated_at,
                    stable_error_code = 'superseded',
                    error_message = NULL
                WHERE job_id = $job_id
                  AND status <> 'cancelled';
                """;
            retireChunks.Parameters.AddWithValue("$updated_at", now);
            retireChunks.Parameters.AddWithValue("$job_id", normalizedAttentionJobId);
            await retireChunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        TranscriptionJobRecord created;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $$"""
                INSERT INTO transcription_job (
                    id,
                    session_id,
                    engine_id,
                    execution_kind,
                    model_id,
                    engine_options_json,
                    requested_language,
                    input_audio_path,
                    input_sha256,
                    input_size_bytes,
                    input_duration_seconds,
                    status,
                    progress,
                    queued_at,
                    created_at,
                    updated_at,
                    remote_consent_revision,
                    remote_consent_at,
                    privacy_policy_json,
                    manifest_version,
                    manifest_path,
                    replace_existing,
                    trigger_kind,
                    supersedes_job_id)
                SELECT
                    $id,
                    $session_id,
                    $engine_id,
                    $execution_kind,
                    $model_id,
                    $engine_options_json,
                    $requested_language,
                    $input_audio_path,
                    $input_sha256,
                    $input_size_bytes,
                    $input_duration_seconds,
                    'queued',
                    0,
                    $queued_at,
                    $created_at,
                    $updated_at,
                    $remote_consent_revision,
                    $remote_consent_at,
                    $privacy_policy_json,
                    $manifest_version,
                    $manifest_path,
                    $replace_existing,
                    $trigger_kind,
                    $supersedes_job_id
                FROM meeting_session
                WHERE id = $session_id
                  AND status IN ('ready', 'saved')
                  AND user_discarded = 0
                  AND primary_audio_path = $input_audio_path
                  AND current_transcription_job_id = $superseded_job_id
                RETURNING {{JobColumns}};
                """;
            AddEnqueueParameters(insert, normalized);
            insert.Parameters.AddWithValue("$superseded_job_id", normalizedAttentionJobId);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The reconfigured transcription job could not be inserted for the current recording.");
            }

            created = ReadJob(reader);
        }

        await InsertLocalExecutionAsync(
                connection,
                transaction,
                created,
                normalized.LocalExecution,
                cancellationToken)
            .ConfigureAwait(false);
        await SetCurrentJobPointerAsync(
                connection,
                transaction,
                created.SessionId,
                created.Id,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TranscriptionEnqueueResult(created, Created: true);
    }

    /// <summary>
    /// Claims at most one due FIFO job across every engine. The UPDATE statement is the claim,
    /// so concurrent workers cannot observe and return the same queued row.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<TranscriptionJobRecord?> ClaimNextDueAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var now = ToDatabaseTimestamp(nowUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            WITH next_job AS (
                SELECT candidate.id
                FROM transcription_job AS candidate
                WHERE candidate.cancellation_requested = 0
                  AND (
                        (candidate.status = 'queued'
                         AND (candidate.next_attempt_at IS NULL OR candidate.next_attempt_at <= $now))
                     OR (candidate.status = 'retry_scheduled'
                         AND candidate.next_attempt_at IS NOT NULL
                         AND candidate.next_attempt_at <= $now)
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM transcription_job AS active
                      WHERE active.status IN ('preparing', 'uploading', 'processing', 'finalizing')
                        AND active.cancellation_requested = 0)
                ORDER BY candidate.queued_at ASC, candidate.id ASC
                LIMIT 1)
            UPDATE transcription_job
            SET
                status = 'preparing',
                updated_at = $now,
                attempt_count = attempt_count + 1,
                last_attempt_at = $now,
                next_attempt_at = NULL,
                stable_error_code = NULL,
                error_message = NULL,
                current_chunk_index = NULL,
                current_chunk_id = NULL
            WHERE id = (SELECT id FROM next_job)
              AND status IN ('queued', 'retry_scheduled')
              AND cancellation_requested = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM transcription_job AS active
                  WHERE active.id <> transcription_job.id
                    AND active.status IN ('preparing', 'uploading', 'processing', 'finalizing')
                    AND active.cancellation_requested = 0)
            RETURNING {{JobColumns}};
            """;
        command.Parameters.AddWithValue("$now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
    public async ValueTask<bool> TrySetExecutionStateAsync(
        string jobId,
        TranscriptionJobStatus status,
        double progress,
        string? currentChunkId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        if (status is not (TranscriptionJobStatus.Preparing
            or TranscriptionJobStatus.Uploading
            or TranscriptionJobStatus.Processing))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Only an executing transcription state can be set through this operation.");
        }

        ValidateProgress(progress);
        var normalizedChunkId = OptionalValue(currentChunkId);
        if (status != TranscriptionJobStatus.Preparing && normalizedChunkId is null)
        {
            throw new ArgumentException(
                "Uploading and processing states require a current chunk id.",
                nameof(currentChunkId));
        }

        var databaseStatus = ToDatabaseStatus(status);
        var now = ToDatabaseTimestamp(nowUtc);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        if (normalizedChunkId is not null)
        {
            await using var chunk = connection.CreateCommand();
            chunk.Transaction = transaction;
            chunk.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = $status,
                    attempt_count = CASE
                        WHEN $status IN ('uploading', 'processing')
                         AND status IN ('pending', 'preparing', 'failed')
                         AND NOT EXISTS (
                             SELECT 1
                             FROM transcription_local_chunk_attempt AS local_attempt
                             WHERE local_attempt.job_id = $job_id
                               AND local_attempt.chunk_id = $chunk_id)
                        THEN attempt_count + 1
                        ELSE attempt_count
                    END,
                    updated_at = $updated_at,
                    stable_error_code = NULL,
                    error_message = NULL
                WHERE id = $chunk_id
                  AND job_id = $job_id
                  AND status NOT IN ('completed', 'cancelled', 'split')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM transcription_chunk AS other
                      WHERE other.job_id = $job_id
                        AND other.id <> $chunk_id
                        AND other.status IN ('preparing', 'uploading', 'processing'));
                """;
            chunk.Parameters.AddWithValue("$status", databaseStatus);
            chunk.Parameters.AddWithValue("$updated_at", now);
            chunk.Parameters.AddWithValue("$chunk_id", normalizedChunkId);
            chunk.Parameters.AddWithValue("$job_id", normalizedJobId);
            if (await chunk.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using var job = connection.CreateCommand();
        job.Transaction = transaction;
        job.CommandText =
            """
            UPDATE transcription_job
            SET
                status = $status,
                progress = $progress,
                current_chunk_id = $chunk_id,
                current_chunk_index = (
                    SELECT sequence_index
                    FROM transcription_chunk
                    WHERE id = $chunk_id
                      AND job_id = $job_id),
                updated_at = $updated_at,
                stable_error_code = NULL,
                error_message = NULL
            WHERE id = $job_id
              AND status IN ('preparing', 'uploading', 'processing')
              AND cancellation_requested = 0
              AND (
                  $chunk_id IS NOT NULL
                  OR NOT EXISTS (
                      SELECT 1
                      FROM transcription_chunk
                      WHERE job_id = $job_id
                        AND status IN ('preparing', 'uploading', 'processing')));
            """;
        job.Parameters.AddWithValue("$status", databaseStatus);
        job.Parameters.AddWithValue("$progress", progress);
        job.Parameters.AddWithValue("$chunk_id", ToDbValue(normalizedChunkId));
        job.Parameters.AddWithValue("$job_id", normalizedJobId);
        job.Parameters.AddWithValue("$updated_at", now);

        if (await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Replaces the deterministic manifest while retaining byte-identical completed checkpoints.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
    public async ValueTask ReplaceChunksAsync(
        string jobId,
        IReadOnlyList<TranscriptionChunkDefinition> chunks,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        var normalizedChunks = NormalizeChunks(chunks, requireAllParentsInManifest: false);
        var byId = normalizedChunks.ToDictionary(static chunk => chunk.Id, StringComparer.Ordinal);
        var now = ToDatabaseTimestamp(nowUtc);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var job = await ReadJobAsync(connection, transaction, normalizedJobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Transcription job '{normalizedJobId}' was not found.");
        if (job.CancellationRequested
            || job.Status is not (TranscriptionJobStatus.Queued or TranscriptionJobStatus.Preparing)
            || job.CurrentChunkId is not null)
        {
            throw new InvalidOperationException(
                $"The chunk manifest cannot be replaced while job '{normalizedJobId}' is {job.Status}.");
        }

        var existingChunks = await ReadChunksAsync(
                connection,
                transaction,
                normalizedJobId,
                onlyCompleted: false,
                cancellationToken)
            .ConfigureAwait(false);
        var existingCompleted = existingChunks
            .Where(static chunk => chunk.Status == TranscriptionChunkStatus.Completed)
            .ToDictionary(static chunk => chunk.Id, StringComparer.Ordinal);
        var existingSplit = existingChunks
            .Where(static chunk => chunk.Status == TranscriptionChunkStatus.Split)
            .ToDictionary(static chunk => chunk.Id, StringComparer.Ordinal);
        if (existingChunks.Any(static chunk => chunk.Status is
            TranscriptionChunkStatus.Preparing
            or TranscriptionChunkStatus.Uploading
            or TranscriptionChunkStatus.Processing))
        {
            throw new InvalidOperationException(
                $"The chunk manifest cannot be replaced while job '{normalizedJobId}' contains an active chunk.");
        }

        foreach (var completed in existingCompleted.Values)
        {
            if (!byId.TryGetValue(completed.Id, out var replacement))
            {
                throw new InvalidOperationException(
                    $"Completed chunk '{completed.Id}' cannot be removed from a resumed manifest.");
            }

            EnsureSameDefinition(completed, replacement);
        }

        foreach (var split in existingSplit.Values)
        {
            if (!byId.TryGetValue(split.Id, out var replacement))
            {
                continue;
            }

            EnsureSameDefinition(split, replacement);
            if (replacement.InitialStatus != TranscriptionChunkStatus.Split)
            {
                throw new InvalidOperationException(
                    $"Split parent '{split.Id}' cannot return to an executable state.");
            }
        }

        foreach (var split in existingSplit.Values)
        {
            var persistedChildren = existingChunks
                .Where(chunk => string.Equals(
                    chunk.ParentChunkId,
                    split.Id,
                    StringComparison.Ordinal))
                .ToArray();
            if (persistedChildren.Length != 2)
            {
                throw new InvalidOperationException(
                    $"Split parent '{split.Id}' must retain exactly two direct children.");
            }

            var persistedChildIds = persistedChildren
                .Select(static child => child.Id)
                .ToHashSet(StringComparer.Ordinal);
            var unexpectedChild = normalizedChunks.FirstOrDefault(chunk =>
                string.Equals(chunk.ParentChunkId, split.Id, StringComparison.Ordinal)
                && !persistedChildIds.Contains(chunk.Id));
            if (unexpectedChild is not null)
            {
                throw new InvalidOperationException(
                    $"Split parent '{split.Id}' cannot replace child '{unexpectedChild.Id}'.");
            }

            foreach (var child in persistedChildren)
            {
                if (byId.TryGetValue(child.Id, out var replacement))
                {
                    EnsureSameDefinition(child, replacement);
                    continue;
                }

                if (child.Status != TranscriptionChunkStatus.Split)
                {
                    throw new InvalidOperationException(
                        $"Split child '{child.Id}' cannot be removed from a resumed manifest.");
                }
            }
        }

        var persistedParentIds = existingCompleted.Keys.Concat(existingSplit.Keys).ToArray();
        _ = OrderForInsert(normalizedChunks, persistedParentIds);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                """
                DELETE FROM transcription_chunk
                WHERE job_id = $job_id
                  AND status NOT IN ('completed', 'split');
                """;
            delete.Parameters.AddWithValue("$job_id", normalizedJobId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in OrderForInsert(normalizedChunks, persistedParentIds))
        {
            if (existingCompleted.ContainsKey(chunk.Id) || existingSplit.ContainsKey(chunk.Id))
            {
                continue;
            }

            await InsertChunkAsync(
                    connection,
                    transaction,
                    normalizedJobId,
                    chunk,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var completed in existingCompleted.Values)
        {
            var definition = byId[completed.Id];
            await using var restoreParent = connection.CreateCommand();
            restoreParent.Transaction = transaction;
            restoreParent.CommandText =
                """
                UPDATE transcription_chunk
                SET parent_chunk_id = $parent_chunk_id
                WHERE id = $id
                  AND job_id = $job_id
                  AND status = 'completed';
                """;
            restoreParent.Parameters.AddWithValue("$parent_chunk_id", ToDbValue(definition.ParentChunkId));
            restoreParent.Parameters.AddWithValue("$id", definition.Id);
            restoreParent.Parameters.AddWithValue("$job_id", normalizedJobId);
            await restoreParent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var executableChunkCount = normalizedChunks.Count(
            static chunk => chunk.InitialStatus != TranscriptionChunkStatus.Split);
        var completedCount = existingCompleted.Keys.Count(
            chunkId => byId[chunkId].InitialStatus != TranscriptionChunkStatus.Split);
        await using (var updateJob = connection.CreateCommand())
        {
            updateJob.Transaction = transaction;
            updateJob.CommandText =
                """
                UPDATE transcription_job
                SET
                    progress = $progress,
                    current_chunk_index = NULL,
                    current_chunk_id = NULL,
                    updated_at = $updated_at
                WHERE id = $job_id;
                """;
            updateJob.Parameters.AddWithValue(
                "$progress",
                executableChunkCount == 0 ? 0d : (double)completedCount / executableChunkCount);
            updateJob.Parameters.AddWithValue("$updated_at", now);
            updateJob.Parameters.AddWithValue("$job_id", normalizedJobId);
            await updateJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically retires one provider-rejected chunk and inserts its two deterministic children.
    /// The parent row remains as a durable split checkpoint so restart recovery cannot recreate
    /// or upload it.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<IReadOnlyList<TranscriptionChunkRecord>> SplitChunkAsync(
        TranscriptionChunkSplitRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        var now = ToDatabaseTimestamp(normalized.SplitAtUtc);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var job = await ReadJobAsync(
                connection,
                transaction,
                normalized.JobId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Transcription job '{normalized.JobId}' was not found.");
        var parent = await ReadChunkAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ParentChunkId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Transcription chunk '{normalized.ParentChunkId}' was not found for job '{normalized.JobId}'.");

        if (parent.Status == TranscriptionChunkStatus.Split)
        {
            var persistedChildren = await ReadDirectChildrenAsync(
                    connection,
                    transaction,
                    normalized.JobId,
                    normalized.ParentChunkId,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureSameSplitChildren(persistedChildren, normalized.Children);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return persistedChildren;
        }

        if (job.CancellationRequested
            || job.Status is not (TranscriptionJobStatus.Preparing
                or TranscriptionJobStatus.Uploading
                or TranscriptionJobStatus.Processing)
            || !string.Equals(job.CurrentChunkId, parent.Id, StringComparison.Ordinal)
            || parent.Status is not (TranscriptionChunkStatus.Uploading or TranscriptionChunkStatus.Processing))
        {
            throw new InvalidOperationException(
                $"Chunk '{parent.Id}' is not the active chunk of job '{job.Id}'.");
        }

        ValidateSplitGeometry(parent, normalized.Children);
        var existing = await ReadChunksAsync(
                connection,
                transaction,
                normalized.JobId,
                onlyCompleted: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing.Any(chunk =>
                !string.Equals(chunk.Id, parent.Id, StringComparison.Ordinal)
                && chunk.Status is TranscriptionChunkStatus.Preparing
                    or TranscriptionChunkStatus.Uploading
                    or TranscriptionChunkStatus.Processing))
        {
            throw new InvalidOperationException(
                $"Job '{job.Id}' contains another active chunk and cannot split '{parent.Id}'.");
        }

        var existingIds = existing.Select(static chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var child in normalized.Children)
        {
            if (existingIds.Contains(child.Id))
            {
                throw new InvalidOperationException(
                    $"Split child '{child.Id}' already exists in job '{job.Id}'.");
            }
        }

        var executable = existing
            .Where(chunk => chunk.Status != TranscriptionChunkStatus.Split
                && !string.Equals(chunk.Id, parent.Id, StringComparison.Ordinal))
            .Select(static chunk => new ChunkSequenceCandidate(
                chunk.Id,
                chunk.StartMilliseconds,
                chunk.EndMilliseconds,
                chunk.SplitDepth))
            .Concat(normalized.Children.Select(static child => new ChunkSequenceCandidate(
                child.Id,
                child.StartMilliseconds,
                child.EndMilliseconds,
                child.SplitDepth)))
            .OrderBy(static chunk => chunk.StartMilliseconds)
            .ThenBy(static chunk => chunk.EndMilliseconds)
            .ThenBy(static chunk => chunk.SplitDepth)
            .ThenBy(static chunk => chunk.Id, StringComparer.Ordinal)
            .ToArray();
        var executableSequence = executable
            .Select((chunk, index) => (chunk.Id, SequenceIndex: index))
            .ToDictionary(static item => item.Id, static item => item.SequenceIndex, StringComparer.Ordinal);
        foreach (var child in normalized.Children)
        {
            if (child.SequenceIndex != executableSequence[child.Id])
            {
                throw new ArgumentException(
                    $"Split child '{child.Id}' has sequence {child.SequenceIndex}; deterministic order requires {executableSequence[child.Id]}.",
                    nameof(request));
            }
        }

        var splitParents = existing
            .Where(chunk => chunk.Status == TranscriptionChunkStatus.Split)
            .Append(parent)
            .OrderBy(static chunk => chunk.StartMilliseconds)
            .ThenBy(static chunk => chunk.SplitDepth)
            .ThenBy(static chunk => chunk.Id, StringComparer.Ordinal)
            .ToArray();
        var splitSequence = splitParents
            .Select((chunk, index) => (chunk.Id, SequenceIndex: executable.Length + index))
            .ToDictionary(static item => item.Id, static item => item.SequenceIndex, StringComparer.Ordinal);

        var highestSequence = existing.Max(static chunk => chunk.SequenceIndex);
        var temporaryOffset = checked(highestSequence + existing.Count + normalized.Children.Count + 1_024);
        await using (var moveExisting = connection.CreateCommand())
        {
            moveExisting.Transaction = transaction;
            moveExisting.CommandText =
                """
                UPDATE transcription_chunk
                SET sequence_index = sequence_index + $temporary_offset
                WHERE job_id = $job_id;
                """;
            moveExisting.Parameters.AddWithValue("$temporary_offset", temporaryOffset);
            moveExisting.Parameters.AddWithValue("$job_id", normalized.JobId);
            await moveExisting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in existing)
        {
            var finalSequence = string.Equals(chunk.Id, parent.Id, StringComparison.Ordinal)
                || chunk.Status == TranscriptionChunkStatus.Split
                ? splitSequence[chunk.Id]
                : executableSequence[chunk.Id];
            await SetChunkSequenceAsync(
                    connection,
                    transaction,
                    normalized.JobId,
                    chunk.Id,
                    finalSequence,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var retireParent = connection.CreateCommand())
        {
            retireParent.Transaction = transaction;
            retireParent.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = 'split',
                    updated_at = $updated_at,
                    stable_error_code = NULL,
                    error_message = NULL
                WHERE job_id = $job_id
                  AND id = $parent_chunk_id
                  AND status IN ('uploading', 'processing');
                """;
            retireParent.Parameters.AddWithValue("$updated_at", now);
            retireParent.Parameters.AddWithValue("$job_id", normalized.JobId);
            retireParent.Parameters.AddWithValue("$parent_chunk_id", normalized.ParentChunkId);
            if (await retireParent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Chunk '{parent.Id}' changed state before its split could be persisted.");
            }
        }

        foreach (var child in normalized.Children)
        {
            await InsertChunkAsync(
                    connection,
                    transaction,
                    normalized.JobId,
                    child with { SequenceIndex = executableSequence[child.Id] },
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var completedCount = existing.Count(static chunk =>
            chunk.Status == TranscriptionChunkStatus.Completed);
        await using (var updateJob = connection.CreateCommand())
        {
            updateJob.Transaction = transaction;
            updateJob.CommandText =
                """
                UPDATE transcription_job
                SET
                    status = 'preparing',
                    progress = $progress,
                    current_chunk_index = NULL,
                    current_chunk_id = NULL,
                    updated_at = $updated_at,
                    stable_error_code = NULL,
                    error_message = NULL
                WHERE id = $job_id
                  AND cancellation_requested = 0;
                """;
            updateJob.Parameters.AddWithValue(
                "$progress",
                executable.Length == 0 ? 0d : (double)completedCount / executable.Length);
            updateJob.Parameters.AddWithValue("$updated_at", now);
            updateJob.Parameters.AddWithValue("$job_id", normalized.JobId);
            if (await updateJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Job '{job.Id}' changed state before chunk '{parent.Id}' could be split.");
            }
        }

        var children = await ReadDirectChildrenAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ParentChunkId,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSameSplitChildren(children, normalized.Children);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return children;
    }

    public async ValueTask<IReadOnlyList<TranscriptionChunkRecord>> ListChunksAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadChunksAsync(
                connection,
                transaction: null,
                normalizedJobId,
                onlyCompleted: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
    public async ValueTask<bool> TryMarkChunkCompletedAsync(
        TranscriptionChunkCompletion completion,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(completion);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var existing = await ReadChunkAsync(
                connection,
                transaction,
                normalized.JobId,
                normalized.ChunkId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Transcription chunk '{normalized.ChunkId}' was not found for job '{normalized.JobId}'.");

        if (existing.Status == TranscriptionChunkStatus.Completed)
        {
            EnsureSameCompletion(existing, normalized);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = 'completed',
                    updated_at = $updated_at,
                    engine_request_id = $engine_request_id,
                    result_path = $result_path,
                    result_sha256 = $result_sha256,
                    result_metadata_json = $result_metadata_json,
                    usage_json = $usage_json,
                    stable_error_code = NULL,
                    error_message = NULL
                WHERE id = $chunk_id
                  AND job_id = $job_id
                  AND status IN ('uploading', 'processing')
                  AND EXISTS (
                      SELECT 1
                      FROM transcription_job
                      WHERE id = $job_id
                        AND status IN ('preparing', 'uploading', 'processing')
                        AND cancellation_requested = 0);
                """;
            checkpoint.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(normalized.CompletedAtUtc));
            checkpoint.Parameters.AddWithValue("$engine_request_id", ToDbValue(normalized.EngineRequestId));
            checkpoint.Parameters.AddWithValue("$result_path", normalized.ResultPath);
            checkpoint.Parameters.AddWithValue("$result_sha256", normalized.ResultSha256);
            checkpoint.Parameters.AddWithValue("$result_metadata_json", ToDbValue(normalized.ResultMetadataJson));
            checkpoint.Parameters.AddWithValue("$usage_json", ToDbValue(normalized.UsageJson));
            checkpoint.Parameters.AddWithValue("$chunk_id", normalized.ChunkId);
            checkpoint.Parameters.AddWithValue("$job_id", normalized.JobId);
            if (await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var updateProgress = connection.CreateCommand())
        {
            updateProgress.Transaction = transaction;
            updateProgress.CommandText =
                """
                UPDATE transcription_job
                SET
                    progress = COALESCE(
                        (SELECT 1.0 * SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END)
                                      / NULLIF(SUM(CASE WHEN status <> 'split' THEN 1 ELSE 0 END), 0)
                         FROM transcription_chunk
                         WHERE job_id = $job_id),
                        0),
                    current_chunk_index = CASE
                        WHEN current_chunk_id = $completed_chunk_id THEN NULL
                        ELSE current_chunk_index
                    END,
                    current_chunk_id = CASE
                        WHEN current_chunk_id = $completed_chunk_id THEN NULL
                        ELSE current_chunk_id
                    END,
                    updated_at = $updated_at
                WHERE id = $job_id;
                """;
            updateProgress.Parameters.AddWithValue("$job_id", normalized.JobId);
            updateProgress.Parameters.AddWithValue("$completed_chunk_id", normalized.ChunkId);
            updateProgress.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(normalized.CompletedAtUtc));
            await updateProgress.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<bool> ScheduleRetryAsync(
        string jobId,
        DateTimeOffset nextAttemptAtUtc,
        string stableErrorCode,
        string? errorMessage,
        string? currentChunkId,
        string? engineRequestId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (nextAttemptAtUtc < nowUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAtUtc),
                nextAttemptAtUtc,
                "The next attempt cannot be scheduled in the past.");
        }

        return await SetPausedStateAsync(
                jobId,
                TranscriptionJobStatus.RetryScheduled,
                stableErrorCode,
                errorMessage,
                currentChunkId,
                engineRequestId,
                nextAttemptAtUtc,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<bool> RequireAttentionAsync(
        string jobId,
        string stableErrorCode,
        string? errorMessage,
        string? currentChunkId,
        string? engineRequestId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        await SetPausedStateAsync(
                jobId,
                TranscriptionJobStatus.AttentionRequired,
                stableErrorCode,
                errorMessage,
                currentChunkId,
                engineRequestId,
                nextAttemptAtUtc: null,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<bool> RequeueAsync(
        string jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        var now = ToDatabaseTimestamp(nowUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE transcription_job
            SET
                status = 'queued',
                queued_at = $queued_at,
                updated_at = $updated_at,
                attempt_count = 0,
                next_attempt_at = NULL,
                stable_error_code = NULL,
                error_message = NULL,
                current_chunk_index = NULL,
                current_chunk_id = NULL
            WHERE id = $job_id
              AND status IN ('retry_scheduled', 'attention_required', 'failed')
              AND cancellation_requested = 0;
            """;
        command.Parameters.AddWithValue("$queued_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        command.Parameters.AddWithValue("$job_id", normalizedJobId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<bool> CancelAsync(
        string jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        var now = ToDatabaseTimestamp(nowUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        await using (var cancelJob = connection.CreateCommand())
        {
            cancelJob.Transaction = transaction;
            cancelJob.CommandText =
                """
                UPDATE transcription_job
                SET
                    status = 'cancelled',
                    cancellation_requested = 1,
                    cancelled_at = $cancelled_at,
                    updated_at = $updated_at,
                    next_attempt_at = NULL,
                    current_chunk_index = NULL,
                    current_chunk_id = NULL
                WHERE id = $job_id
                  AND status NOT IN ('completed', 'cancelled');
                """;
            cancelJob.Parameters.AddWithValue("$cancelled_at", now);
            cancelJob.Parameters.AddWithValue("$updated_at", now);
            cancelJob.Parameters.AddWithValue("$job_id", normalizedJobId);
            if (await cancelJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var cancelChunks = connection.CreateCommand())
        {
            cancelChunks.Transaction = transaction;
            cancelChunks.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = 'cancelled',
                    updated_at = $updated_at
                WHERE job_id = $job_id
                  AND status NOT IN ('completed', 'split', 'cancelled');
                """;
            cancelChunks.Parameters.AddWithValue("$updated_at", now);
            cancelChunks.Parameters.AddWithValue("$job_id", normalizedJobId);
            await cancelChunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Lists terminal jobs whose app-owned worker workspace still has durable pointers.
    /// The caller removes only the deterministic job directory before clearing these pointers.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#retention
    // The write lock excludes enqueue/supersede while source files are released. A crash
    // rolls back pointers and leaves the handoff receipt for an idempotent cleanup retry.
    public async ValueTask<string?> ReleaseSpeakerSourcesAsync(string jobId,
        Func<string, string, string?, bool> releaseFiles, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        string? root = null;
        string? primary = null;
        string? sessionId = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT meeting.id, meeting.temp_session_path, meeting.primary_audio_path
                FROM meeting_session AS meeting JOIN transcription_job AS job ON job.session_id = meeting.id
                WHERE job.id = $job AND job.status IN ('completed', 'cancelled')
                  AND COALESCE(job.stable_error_code, '') != 'superseded'
                  AND meeting.status IN ('ready', 'saved') AND meeting.user_discarded = 0
                  AND meeting.source_cleanup_pending = 1 AND meeting.temp_session_path IS NOT NULL
                  AND (meeting.current_transcription_job_id = job.id OR meeting.current_transcription_job_id IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM transcription_job AS active
                      WHERE active.session_id = meeting.id AND active.status NOT IN ('completed', 'cancelled', 'failed'));
                """;
            command.Parameters.AddWithValue("$job", jobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessionId = reader.GetString(0); root = reader.GetString(1);
                primary = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }
        if (root is null || sessionId is null || !releaseFiles(sessionId, root, primary)) return null;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE meeting_session SET audio_output_path = NULL, audio_mic_path = NULL,
                    audio_mix_path = NULL, temp_session_path = NULL, source_manifest_path = NULL,
                    source_cleanup_pending = 0 WHERE id = $session;
                """;
            update.Parameters.AddWithValue("$session", sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return root;
    }

    public async ValueTask<IReadOnlyList<string>> ListTerminalWorkspaceCleanupJobIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job.id
            FROM transcription_job AS job
            WHERE job.status IN ('completed', 'cancelled')
              AND (
                    job.manifest_path IS NOT NULL
                 OR EXISTS (
                        SELECT 1
                        FROM transcription_chunk AS chunk
                        WHERE chunk.job_id = job.id
                          AND (
                                chunk.artifact_path IS NOT NULL
                             OR chunk.result_path IS NOT NULL)))
            ORDER BY job.updated_at ASC, job.id ASC;
            """;

        var jobIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobIds.Add(reader.GetString(0));
        }

        return jobIds;
    }

    /// <summary>
    /// Clears file pointers only after the terminal worker workspace has been removed. Provider
    /// request ids and bounded result metadata remain available for diagnostics.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#chunking
    public async ValueTask<bool> CompleteTerminalWorkspaceCleanupAsync(
        string jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        var now = ToDatabaseTimestamp(nowUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        await using (var clearChunks = connection.CreateCommand())
        {
            clearChunks.Transaction = transaction;
            clearChunks.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    artifact_path = NULL,
                    artifact_format = NULL,
                    artifact_sha256 = NULL,
                    artifact_size_bytes = NULL,
                    result_path = NULL,
                    result_sha256 = NULL,
                    updated_at = $updated_at
                WHERE job_id = $job_id
                  AND EXISTS (
                      SELECT 1
                      FROM transcription_job
                      WHERE id = $job_id
                        AND status IN ('completed', 'cancelled'));
                """;
            clearChunks.Parameters.AddWithValue("$updated_at", now);
            clearChunks.Parameters.AddWithValue("$job_id", normalizedJobId);
            await clearChunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var clearJob = connection.CreateCommand();
        clearJob.Transaction = transaction;
        clearJob.CommandText =
            """
            UPDATE transcription_job
            SET
                manifest_path = NULL,
                updated_at = $updated_at
            WHERE id = $job_id
              AND status IN ('completed', 'cancelled');
            """;
        clearJob.Parameters.AddWithValue("$updated_at", now);
        clearJob.Parameters.AddWithValue("$job_id", normalizedJobId);
        if (await clearJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Returns interrupted execution to the FIFO queue and resets only transient chunk states.
    /// Completed checkpoints remain untouched.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
    public async ValueTask<TranscriptionStartupRecoveryResult> RecoverInterruptedAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var now = ToDatabaseTimestamp(nowUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        int resetChunkCount;
        await using (var resetChunks = connection.CreateCommand())
        {
            resetChunks.Transaction = transaction;
            resetChunks.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = 'pending',
                    updated_at = $updated_at
                WHERE status IN ('preparing', 'uploading', 'processing')
                  AND job_id IN (
                      SELECT id
                      FROM transcription_job
                      WHERE status IN ('preparing', 'uploading', 'processing'));
                """;
            resetChunks.Parameters.AddWithValue("$updated_at", now);
            resetChunkCount = await resetChunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int requeuedJobCount;
        await using (var requeueJobs = connection.CreateCommand())
        {
            requeueJobs.Transaction = transaction;
            requeueJobs.CommandText =
                """
                UPDATE transcription_job
                SET
                    status = 'queued',
                    updated_at = $updated_at,
                    next_attempt_at = NULL,
                    current_chunk_index = NULL,
                    current_chunk_id = NULL
                WHERE status IN ('preparing', 'uploading', 'processing')
                  AND cancellation_requested = 0;
                """;
            requeueJobs.Parameters.AddWithValue("$updated_at", now);
            requeuedJobCount = await requeueJobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TranscriptionStartupRecoveryResult(requeuedJobCount, resetChunkCount);
    }

    /// <summary>
    /// Records verified staged files. Canonical session paths remain unchanged until publication.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public async ValueTask<bool> StageCompletionAsync(
        TranscriptionArtifactStage stage,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(stage);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE transcription_job
            SET
                status = 'finalizing',
                progress = 1,
                artifact_publication_state = 'staged',
                staged_transcript_md_path = $staged_md_path,
                staged_transcript_json_path = $staged_json_path,
                staged_transcript_md_sha256 = $staged_md_sha256,
                staged_transcript_json_sha256 = $staged_json_sha256,
                transcript_md_path = $final_md_path,
                transcript_json_path = $final_json_path,
                updated_at = $updated_at,
                stable_error_code = NULL,
                error_message = NULL
            WHERE id = $job_id
              AND status IN ('preparing', 'uploading', 'processing')
              AND cancellation_requested = 0
              AND EXISTS (
                  SELECT 1
                  FROM transcription_chunk
                  WHERE job_id = $job_id
                    AND status <> 'split')
              AND NOT EXISTS (
                  SELECT 1
                  FROM transcription_chunk
                  WHERE job_id = $job_id
                    AND status NOT IN ('completed', 'split'));
            """;
        command.Parameters.AddWithValue("$staged_md_path", normalized.StagedMarkdownPath);
        command.Parameters.AddWithValue("$staged_json_path", normalized.StagedJsonPath);
        command.Parameters.AddWithValue("$staged_md_sha256", normalized.StagedMarkdownSha256);
        command.Parameters.AddWithValue("$staged_json_sha256", normalized.StagedJsonSha256);
        command.Parameters.AddWithValue("$final_md_path", normalized.FinalMarkdownPath);
        command.Parameters.AddWithValue("$final_json_path", normalized.FinalJsonPath);
        command.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(normalized.StagedAtUtc));
        command.Parameters.AddWithValue("$job_id", normalized.JobId);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var existing = await ReadJobAsync(
                connection,
                transaction,
                normalized.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null
            && existing.Status == TranscriptionJobStatus.Finalizing
            && existing.ArtifactPublicationState == TranscriptionArtifactPublicationState.Staged)
        {
            EnsureSameStage(existing, normalized);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async ValueTask<IReadOnlyList<TranscriptionJobRecord>> ListStagedJobsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            SELECT {{JobColumns}}
            FROM transcription_job
            WHERE status = 'finalizing'
              AND artifact_publication_state = 'staged'
              AND cancellation_requested = 0
            ORDER BY updated_at ASC, id ASC;
            """;

        var jobs = new List<TranscriptionJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    /// <summary>
    /// Lists cancelled jobs whose persisted staged artifact checkpoint still requires cleanup.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public async ValueTask<IReadOnlyList<TranscriptionJobRecord>> ListCancelledStagedJobsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            SELECT {{JobColumns}}
            FROM transcription_job
            WHERE status = 'cancelled'
              AND cancellation_requested = 1
              AND artifact_publication_state = 'staged'
            ORDER BY updated_at ASC, id ASC;
            """;

        var jobs = new List<TranscriptionJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    /// <summary>
    /// Clears a cancelled publication checkpoint only when every persisted path and hash still
    /// matches the staged files that the caller has removed.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public async ValueTask<bool> CompleteCancelledStagedArtifactCleanupAsync(
        TranscriptionArtifactStage checkpoint,
        DateTimeOffset cleanedAtUtc,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(checkpoint);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE transcription_job
            SET
                artifact_publication_state = 'none',
                staged_transcript_md_path = NULL,
                staged_transcript_json_path = NULL,
                staged_transcript_md_sha256 = NULL,
                staged_transcript_json_sha256 = NULL,
                transcript_md_path = NULL,
                transcript_json_path = NULL,
                updated_at = $updated_at
            WHERE id = $job_id
              AND status = 'cancelled'
              AND cancellation_requested = 1
              AND artifact_publication_state = 'staged'
              AND staged_transcript_md_path = $staged_md_path
              AND staged_transcript_json_path = $staged_json_path
              AND staged_transcript_md_sha256 = $staged_md_sha256
              AND staged_transcript_json_sha256 = $staged_json_sha256
              AND transcript_md_path = $final_md_path
              AND transcript_json_path = $final_json_path;
            """;
        command.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(cleanedAtUtc));
        command.Parameters.AddWithValue("$job_id", normalized.JobId);
        command.Parameters.AddWithValue("$staged_md_path", normalized.StagedMarkdownPath);
        command.Parameters.AddWithValue("$staged_json_path", normalized.StagedJsonPath);
        command.Parameters.AddWithValue("$staged_md_sha256", normalized.StagedMarkdownSha256);
        command.Parameters.AddWithValue("$staged_json_sha256", normalized.StagedJsonSha256);
        command.Parameters.AddWithValue("$final_md_path", normalized.FinalMarkdownPath);
        command.Parameters.AddWithValue("$final_json_path", normalized.FinalJsonPath);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Publishes final paths and the current-job pointer in one SQLite transaction after the
    /// staged files have been promoted by the artifact materializer.
    /// </summary>
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
    public async ValueTask<bool> PublishCompletionAsync(
        TranscriptionArtifactPublication publication,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(publication);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var existing = await ReadJobAsync(
                connection,
                transaction,
                normalized.JobId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Transcription job '{normalized.JobId}' was not found.");

        if (existing.Status == TranscriptionJobStatus.Completed
            && existing.ArtifactPublicationState == TranscriptionArtifactPublicationState.Promoted)
        {
            if (!string.Equals(existing.TranscriptMarkdownPath, normalized.FinalMarkdownPath, StringComparison.Ordinal)
                || !string.Equals(existing.TranscriptJsonPath, normalized.FinalJsonPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Completed job '{normalized.JobId}' cannot be published to different paths.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (existing.Status != TranscriptionJobStatus.Finalizing
            || existing.ArtifactPublicationState != TranscriptionArtifactPublicationState.Staged
            || existing.CancellationRequested)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!string.Equals(existing.TranscriptMarkdownPath, normalized.FinalMarkdownPath, StringComparison.Ordinal)
            || !string.Equals(existing.TranscriptJsonPath, normalized.FinalJsonPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication paths for staged job '{normalized.JobId}' do not match its frozen targets.");
        }

        await using (var completeJob = connection.CreateCommand())
        {
            completeJob.Transaction = transaction;
            completeJob.CommandText =
                """
                UPDATE transcription_job
                SET
                    status = 'completed',
                    progress = 1,
                    detected_language = $detected_language,
                    transcript_md_path = $transcript_md_path,
                    transcript_json_path = $transcript_json_path,
                    usage_json = $usage_json,
                    artifact_publication_state = 'promoted',
                    current_chunk_index = NULL,
                    current_chunk_id = NULL,
                    updated_at = $updated_at,
                    completed_at = $completed_at,
                    stable_error_code = NULL,
                    error_message = NULL
                WHERE id = $job_id
                  AND session_id = $session_id
                  AND status = 'finalizing'
                  AND artifact_publication_state = 'staged'
                  AND cancellation_requested = 0;
                """;
            completeJob.Parameters.AddWithValue("$detected_language", ToDbValue(normalized.DetectedLanguage));
            completeJob.Parameters.AddWithValue("$transcript_md_path", normalized.FinalMarkdownPath);
            completeJob.Parameters.AddWithValue("$transcript_json_path", normalized.FinalJsonPath);
            completeJob.Parameters.AddWithValue("$usage_json", ToDbValue(normalized.UsageJson));
            completeJob.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(normalized.PublishedAtUtc));
            completeJob.Parameters.AddWithValue("$completed_at", ToDatabaseTimestamp(normalized.PublishedAtUtc));
            completeJob.Parameters.AddWithValue("$job_id", normalized.JobId);
            completeJob.Parameters.AddWithValue("$session_id", existing.SessionId);
            if (await completeJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var publishSession = connection.CreateCommand())
        {
            publishSession.Transaction = transaction;
            publishSession.CommandText =
                """
                UPDATE meeting_session
                SET
                    current_transcription_job_id = $job_id,
                    transcript_md_path = $transcript_md_path,
                    transcript_json_path = $transcript_json_path,
                    transcription_status = 'completed',
                    transcription_model = $transcription_model,
                    language = COALESCE($detected_language, $requested_language, language),
                    error_code = NULL,
                    error_message = NULL,
                    updated_at = $updated_at
                WHERE id = $session_id
                  AND current_transcription_job_id = $job_id
                  AND EXISTS (
                      SELECT 1
                      FROM transcription_job
                      WHERE id = $job_id
                        AND session_id = $session_id
                        AND status = 'completed');
                """;
            publishSession.Parameters.AddWithValue("$job_id", normalized.JobId);
            publishSession.Parameters.AddWithValue("$transcript_md_path", normalized.FinalMarkdownPath);
            publishSession.Parameters.AddWithValue("$transcript_json_path", normalized.FinalJsonPath);
            publishSession.Parameters.AddWithValue("$transcription_model", existing.ModelId);
            publishSession.Parameters.AddWithValue("$detected_language", ToDbValue(normalized.DetectedLanguage));
            publishSession.Parameters.AddWithValue("$requested_language", ToDbValue(existing.RequestedLanguage));
            publishSession.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(normalized.PublishedAtUtc));
            publishSession.Parameters.AddWithValue("$session_id", existing.SessionId);
            if (await publishSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Meeting session '{existing.SessionId}' disappeared while publishing transcription job '{normalized.JobId}'.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask SetCurrentJobPointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE meeting_session
            SET current_transcription_job_id = $job_id
            WHERE id = $session_id
              AND EXISTS (
                  SELECT 1
                  FROM transcription_job
                  WHERE id = $job_id
                    AND session_id = $session_id);
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Meeting session '{sessionId}' disappeared while enqueuing transcription job '{jobId}'.");
        }
    }

    private static async ValueTask<string?> ReadCurrentJobPointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT current_transcription_job_id
            FROM meeting_session
            WHERE id = $session_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    private async ValueTask<bool> SetPausedStateAsync(
        string jobId,
        TranscriptionJobStatus status,
        string stableErrorCode,
        string? errorMessage,
        string? currentChunkId,
        string? engineRequestId,
        DateTimeOffset? nextAttemptAtUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedJobId = RequireValue(jobId, nameof(jobId));
        var normalizedCode = RequireValue(stableErrorCode, nameof(stableErrorCode));
        var expectedChunkId = OptionalValue(currentChunkId);
        var normalizedRequestId = OptionalValue(engineRequestId);
        var normalizedErrorMessage = OptionalValue(errorMessage);
        var now = ToDatabaseTimestamp(nowUtc);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var job = await ReadJobAsync(connection, transaction, normalizedJobId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null
            || job.Status is not (TranscriptionJobStatus.Preparing
                or TranscriptionJobStatus.Uploading
                or TranscriptionJobStatus.Processing
                or TranscriptionJobStatus.Finalizing)
            || job.CancellationRequested
            || (expectedChunkId is not null
                && !string.Equals(expectedChunkId, job.CurrentChunkId, StringComparison.Ordinal)))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var effectiveChunkId = job.CurrentChunkId;
        if (effectiveChunkId is not null)
        {
            await using var updateChunk = connection.CreateCommand();
            updateChunk.Transaction = transaction;
            updateChunk.CommandText =
                """
                UPDATE transcription_chunk
                SET
                    status = 'pending',
                    updated_at = $updated_at,
                    engine_request_id = COALESCE($engine_request_id, engine_request_id),
                    stable_error_code = $stable_error_code,
                    error_message = $error_message
                WHERE id = $chunk_id
                  AND job_id = $job_id
                  AND status IN ('preparing', 'uploading', 'processing');
                """;
            updateChunk.Parameters.AddWithValue("$updated_at", now);
            updateChunk.Parameters.AddWithValue("$engine_request_id", ToDbValue(normalizedRequestId));
            updateChunk.Parameters.AddWithValue("$stable_error_code", normalizedCode);
            updateChunk.Parameters.AddWithValue("$error_message", ToDbValue(normalizedErrorMessage));
            updateChunk.Parameters.AddWithValue("$chunk_id", effectiveChunkId);
            updateChunk.Parameters.AddWithValue("$job_id", normalizedJobId);
            if (await updateChunk.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var updateJob = connection.CreateCommand())
        {
            updateJob.Transaction = transaction;
            updateJob.CommandText =
                """
                UPDATE transcription_job
                SET
                    status = $status,
                    updated_at = $updated_at,
                    next_attempt_at = $next_attempt_at,
                    stable_error_code = $stable_error_code,
                    error_message = $error_message,
                    artifact_publication_state = CASE
                        WHEN $reset_publication = 1 THEN 'none'
                        ELSE artifact_publication_state
                    END,
                    current_chunk_index = NULL,
                    current_chunk_id = NULL
                WHERE id = $job_id
                  AND status IN ('preparing', 'uploading', 'processing', 'finalizing')
                  AND cancellation_requested = 0
                  AND (
                      ($current_chunk_id IS NULL AND current_chunk_id IS NULL)
                      OR current_chunk_id = $current_chunk_id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM transcription_chunk
                      WHERE job_id = $job_id
                        AND status IN ('preparing', 'uploading', 'processing'));
                """;
            updateJob.Parameters.AddWithValue("$status", ToDatabaseStatus(status));
            updateJob.Parameters.AddWithValue("$updated_at", now);
            updateJob.Parameters.AddWithValue(
                "$next_attempt_at",
                nextAttemptAtUtc is null ? DBNull.Value : ToDatabaseTimestamp(nextAttemptAtUtc.Value));
            updateJob.Parameters.AddWithValue("$stable_error_code", normalizedCode);
            updateJob.Parameters.AddWithValue("$error_message", ToDbValue(normalizedErrorMessage));
            updateJob.Parameters.AddWithValue(
                "$reset_publication",
                job.Status == TranscriptionJobStatus.Finalizing ? 1 : 0);
            updateJob.Parameters.AddWithValue("$current_chunk_id", ToDbValue(effectiveChunkId));
            updateJob.Parameters.AddWithValue("$job_id", normalizedJobId);
            if (await updateJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout={BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<TranscriptionJobRecord?> ReadActiveJobForSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{JobColumns}}
            FROM transcription_job
            WHERE session_id = $session_id
              AND status IN (
                  'preparing',
                  'queued',
                  'uploading',
                  'processing',
                  'finalizing',
                  'retry_scheduled',
                  'attention_required')
            ORDER BY queued_at ASC, id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    private static async ValueTask<TranscriptionJobRecord?> ReadJobAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{JobColumns}}
            FROM transcription_job
            WHERE id = $job_id;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
    }

    private static async ValueTask<LocalTranscriptionJobRecord?> ReadLocalJobAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{LocalJobColumns}}
            FROM transcription_local_job
            WHERE job_id = $job_id;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLocalJob(reader)
            : null;
    }

    private static async ValueTask<LocalTranscriptionChunkAttemptRecord?> ReadLocalAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobId,
        string chunkId,
        int attemptIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{LocalAttemptColumns}}
            FROM transcription_local_chunk_attempt
            WHERE job_id = $job_id
              AND chunk_id = $chunk_id
              AND attempt_index = $attempt_index;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        command.Parameters.AddWithValue("$attempt_index", attemptIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLocalAttempt(reader)
            : null;
    }

    private static async ValueTask<LocalAttemptSequenceState> ReadLocalAttemptSequenceStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string chunkId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                COALESCE(MAX(attempt_index), -1),
                COALESCE(SUM(
                    CASE
                        WHEN worker_ended_at IS NULL AND exit_category IS NULL THEN 1
                        ELSE 0
                    END), 0)
            FROM transcription_local_chunk_attempt
            WHERE job_id = $job_id
              AND chunk_id = $chunk_id;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Local attempt sequence state was not available.");
        }

        var maximumAttemptIndex = reader.GetInt32(0);
        if (maximumAttemptIndex >= MaxLocalAttemptIndex)
        {
            throw new InvalidOperationException(
                $"Local worker attempt limit {MaxLocalAttemptIndex} was reached for chunk '{chunkId}'.");
        }

        return new LocalAttemptSequenceState(
            NextAttemptIndex: maximumAttemptIndex + 1,
            HasOpenAttempt: reader.GetInt64(1) != 0);
    }

    private static async ValueTask<IReadOnlyList<TranscriptionChunkRecord>> ReadChunksAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobId,
        bool onlyCompleted,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{ChunkColumns}}
            FROM transcription_chunk
            WHERE job_id = $job_id
              {{(onlyCompleted ? "AND status = 'completed'" : string.Empty)}}
            ORDER BY sequence_index ASC, start_milliseconds ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);

        var chunks = new List<TranscriptionChunkRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(ReadChunk(reader));
        }

        return chunks;
    }

    private static async ValueTask<TranscriptionChunkRecord?> ReadChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string chunkId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{ChunkColumns}}
            FROM transcription_chunk
            WHERE id = $chunk_id
              AND job_id = $job_id;
            """;
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        command.Parameters.AddWithValue("$job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadChunk(reader)
            : null;
    }

    private static async ValueTask<IReadOnlyList<TranscriptionChunkRecord>> ReadDirectChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string parentChunkId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT {{ChunkColumns}}
            FROM transcription_chunk
            WHERE job_id = $job_id
              AND parent_chunk_id = $parent_chunk_id
            ORDER BY start_milliseconds ASC, end_milliseconds ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$parent_chunk_id", parentChunkId);

        var children = new List<TranscriptionChunkRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            children.Add(ReadChunk(reader));
        }

        return children;
    }

    private static async ValueTask SetChunkSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string chunkId,
        int sequenceIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE transcription_chunk
            SET sequence_index = $sequence_index
            WHERE job_id = $job_id
              AND id = $chunk_id;
            """;
        command.Parameters.AddWithValue("$sequence_index", sequenceIndex);
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Chunk '{chunkId}' disappeared while resequencing job '{jobId}'.");
        }
    }

    private static async ValueTask InsertChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        TranscriptionChunkDefinition chunk,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO transcription_chunk (
                id,
                job_id,
                sequence_index,
                parent_chunk_id,
                split_depth,
                start_milliseconds,
                end_milliseconds,
                overlap_milliseconds,
                artifact_path,
                artifact_format,
                artifact_sha256,
                artifact_size_bytes,
                status,
                attempt_count,
                created_at,
                updated_at)
            VALUES (
                $id,
                $job_id,
                $sequence_index,
                $parent_chunk_id,
                $split_depth,
                $start_milliseconds,
                $end_milliseconds,
                $overlap_milliseconds,
                $artifact_path,
                $artifact_format,
                $artifact_sha256,
                $artifact_size_bytes,
                $status,
                0,
                $created_at,
                $updated_at);
            """;
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$sequence_index", chunk.SequenceIndex);
        command.Parameters.AddWithValue("$parent_chunk_id", ToDbValue(chunk.ParentChunkId));
        command.Parameters.AddWithValue("$split_depth", chunk.SplitDepth);
        command.Parameters.AddWithValue("$start_milliseconds", chunk.StartMilliseconds);
        command.Parameters.AddWithValue("$end_milliseconds", chunk.EndMilliseconds);
        command.Parameters.AddWithValue("$overlap_milliseconds", chunk.OverlapMilliseconds);
        command.Parameters.AddWithValue("$artifact_path", ToDbValue(chunk.ArtifactPath));
        command.Parameters.AddWithValue("$artifact_format", ToDbValue(chunk.ArtifactFormat));
        command.Parameters.AddWithValue("$artifact_sha256", ToDbValue(chunk.ArtifactSha256));
        command.Parameters.AddWithValue("$artifact_size_bytes", chunk.ArtifactSizeBytes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", ToDatabaseStatus(chunk.InitialStatus));
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertLocalExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TranscriptionJobRecord job,
        LocalTranscriptionExecutionIdentity? execution,
        CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO transcription_local_job (
                job_id,
                model_catalog_version,
                model_catalog_revision,
                model_format,
                model_path,
                model_size_bytes,
                model_sha256,
                runtime_version,
                runtime_commit,
                runtime_source_archive_sha256,
                native_bundle_manifest_sha256,
                bridge_abi_version,
                worker_protocol_version,
                requested_backend,
                resolved_backend,
                backend_history_json,
                thread_count,
                inference_parameters_json,
                chunk_profile_version,
                run_identity_sha256,
                policy_defer_reason,
                native_crash_count,
                created_at,
                updated_at)
            VALUES (
                $job_id,
                $model_catalog_version,
                $model_catalog_revision,
                $model_format,
                $model_path,
                $model_size_bytes,
                $model_sha256,
                $runtime_version,
                $runtime_commit,
                $runtime_source_archive_sha256,
                $native_bundle_manifest_sha256,
                $bridge_abi_version,
                $worker_protocol_version,
                $requested_backend,
                $resolved_backend,
                $backend_history_json,
                $thread_count,
                $inference_parameters_json,
                $chunk_profile_version,
                $run_identity_sha256,
                $policy_defer_reason,
                0,
                $created_at,
                $updated_at);
            """;
        command.Parameters.AddWithValue("$job_id", job.Id);
        command.Parameters.AddWithValue("$model_catalog_version", execution.ModelCatalogVersion);
        command.Parameters.AddWithValue("$model_catalog_revision", execution.ModelCatalogRevision);
        command.Parameters.AddWithValue("$model_format", execution.ModelFormat);
        command.Parameters.AddWithValue("$model_path", execution.ModelPath);
        command.Parameters.AddWithValue("$model_size_bytes", execution.ModelSizeBytes);
        command.Parameters.AddWithValue("$model_sha256", execution.ModelSha256);
        command.Parameters.AddWithValue("$runtime_version", execution.RuntimeVersion);
        command.Parameters.AddWithValue("$runtime_commit", execution.RuntimeCommit);
        command.Parameters.AddWithValue(
            "$runtime_source_archive_sha256",
            execution.RuntimeSourceArchiveSha256);
        command.Parameters.AddWithValue(
            "$native_bundle_manifest_sha256",
            execution.NativeBundleManifestSha256);
        command.Parameters.AddWithValue("$bridge_abi_version", execution.BridgeAbiVersion);
        command.Parameters.AddWithValue("$worker_protocol_version", execution.WorkerProtocolVersion);
        command.Parameters.AddWithValue(
            "$requested_backend",
            ToDatabaseLocalBackend(execution.RequestedBackend));
        command.Parameters.AddWithValue(
            "$resolved_backend",
            execution.ResolvedBackend is null
                ? DBNull.Value
                : ToDatabaseLocalBackend(execution.ResolvedBackend.Value));
        command.Parameters.AddWithValue("$backend_history_json", ToDbValue(execution.BackendHistoryJson));
        command.Parameters.AddWithValue("$thread_count", execution.ThreadCount);
        command.Parameters.AddWithValue("$inference_parameters_json", execution.InferenceParametersJson);
        command.Parameters.AddWithValue("$chunk_profile_version", execution.ChunkProfileVersion);
        command.Parameters.AddWithValue("$run_identity_sha256", execution.RunIdentitySha256);
        command.Parameters.AddWithValue("$policy_defer_reason", ToDbValue(execution.PolicyDeferReason));
        command.Parameters.AddWithValue("$created_at", ToDatabaseTimestamp(job.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated_at", ToDatabaseTimestamp(job.UpdatedAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Local execution identity for transcription job '{job.Id}' was not inserted.");
        }
    }

    private static LocalTranscriptionJobRecord ReadLocalJob(SqliteDataReader reader)
    {
        var execution = new LocalTranscriptionExecutionIdentity(
            ModelCatalogVersion: reader.GetInt32(1),
            ModelCatalogRevision: reader.GetString(2),
            ModelFormat: reader.GetString(3),
            ModelPath: reader.GetString(4),
            ModelSizeBytes: reader.GetInt64(5),
            ModelSha256: reader.GetString(6),
            RuntimeVersion: reader.GetString(7),
            RuntimeCommit: reader.GetString(8),
            RuntimeSourceArchiveSha256: reader.GetString(9),
            NativeBundleManifestSha256: reader.GetString(10),
            BridgeAbiVersion: reader.GetInt32(11),
            WorkerProtocolVersion: reader.GetInt32(12),
            RequestedBackend: ParseLocalBackend(reader.GetString(13)),
            ResolvedBackend: reader.IsDBNull(14) ? null : ParseLocalBackend(reader.GetString(14)),
            BackendHistoryJson: GetNullableString(reader, 15),
            ThreadCount: reader.GetInt32(16),
            InferenceParametersJson: reader.GetString(17),
            ChunkProfileVersion: reader.GetInt32(18),
            RunIdentitySha256: reader.GetString(19),
            PolicyDeferReason: GetNullableString(reader, 20));
        return new LocalTranscriptionJobRecord(
            JobId: reader.GetString(0),
            Execution: execution,
            NativeCrashCount: reader.GetInt32(21),
            LastCrashBackend: reader.IsDBNull(22) ? null : ParseLocalBackend(reader.GetString(22)),
            ProcessingDurationMilliseconds: reader.IsDBNull(23) ? null : reader.GetInt64(23),
            PeakWorkingSetBytes: reader.IsDBNull(24) ? null : reader.GetInt64(24),
            CreatedAtUtc: ParseTimestamp(reader.GetString(25)),
            UpdatedAtUtc: ParseTimestamp(reader.GetString(26)));
    }

    private static LocalTranscriptionChunkAttemptRecord ReadLocalAttempt(SqliteDataReader reader)
    {
        var workerEndedAt = GetNullableTimestamp(reader, 6);
        var exitCategory = GetNullableString(reader, 7);
        if ((workerEndedAt is null) != (exitCategory is null))
        {
            throw new InvalidDataException(
                "A local worker attempt must be either open or fully terminal.");
        }

        return new LocalTranscriptionChunkAttemptRecord(
            JobId: reader.GetString(0),
            ChunkId: reader.GetString(1),
            AttemptIndex: reader.GetInt32(2),
            RequestedBackend: ParseLocalBackend(reader.GetString(3)),
            ResolvedBackend: reader.IsDBNull(4) ? null : ParseLocalBackend(reader.GetString(4)),
            WorkerStartedAtUtc: ParseTimestamp(reader.GetString(5)),
            WorkerEndedAtUtc: workerEndedAt,
            Status: exitCategory is null
                ? LocalTranscriptionAttemptStatus.Running
                : ParseLocalAttemptStatus(exitCategory),
            DecodeDurationMilliseconds: reader.IsDBNull(8) ? null : reader.GetInt64(8),
            InferenceDurationMilliseconds: reader.IsDBNull(9) ? null : reader.GetInt64(9),
            PeakWorkingSetBytes: reader.IsDBNull(10) ? null : reader.GetInt64(10),
            StableFailureCategory: GetNullableString(reader, 11),
            CreatedAtUtc: ParseTimestamp(reader.GetString(12)));
    }

    private static void AddLocalAttemptCompletionParameters(
        SqliteCommand command,
        LocalTranscriptionChunkAttemptCompletion completion)
    {
        command.Parameters.AddWithValue(
            "$resolved_backend",
            completion.ResolvedBackend is null
                ? DBNull.Value
                : ToDatabaseLocalBackend(completion.ResolvedBackend.Value));
        command.Parameters.AddWithValue(
            "$worker_ended_at",
            ToDatabaseTimestamp(completion.WorkerEndedAtUtc));
        command.Parameters.AddWithValue(
            "$exit_category",
            ToDatabaseLocalAttemptStatus(completion.Status));
        command.Parameters.AddWithValue(
            "$decode_duration_milliseconds",
            completion.DecodeDurationMilliseconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$inference_duration_milliseconds",
            completion.InferenceDurationMilliseconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$peak_working_set_bytes",
            completion.PeakWorkingSetBytes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$stable_failure_category",
            ToDbValue(completion.StableFailureCategory));
        command.Parameters.AddWithValue("$job_id", completion.JobId);
        command.Parameters.AddWithValue("$chunk_id", completion.ChunkId);
        command.Parameters.AddWithValue("$attempt_index", completion.AttemptIndex);
        command.Parameters.AddWithValue(
            "$worker_started_at",
            ToDatabaseTimestamp(completion.WorkerStartedAtUtc));
    }

    private static void AddEnqueueParameters(
        SqliteCommand command,
        TranscriptionJobEnqueueRequest request)
    {
        var queuedAt = ToDatabaseTimestamp(request.QueuedAtUtc);
        command.Parameters.AddWithValue("$id", request.JobId);
        command.Parameters.AddWithValue("$session_id", request.SessionId);
        command.Parameters.AddWithValue("$engine_id", request.EngineId);
        command.Parameters.AddWithValue("$execution_kind", ToDatabaseExecutionKind(request.ExecutionKind));
        command.Parameters.AddWithValue("$model_id", request.ModelId);
        command.Parameters.AddWithValue("$engine_options_json", ToDbValue(request.EngineOptionsJson));
        command.Parameters.AddWithValue("$requested_language", ToDbValue(request.RequestedLanguage));
        command.Parameters.AddWithValue("$input_audio_path", request.InputAudioPath);
        command.Parameters.AddWithValue("$input_sha256", request.InputSha256);
        command.Parameters.AddWithValue("$input_size_bytes", request.InputSizeBytes);
        command.Parameters.AddWithValue(
            "$input_duration_seconds",
            request.InputDurationSeconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$queued_at", queuedAt);
        command.Parameters.AddWithValue("$created_at", queuedAt);
        command.Parameters.AddWithValue("$updated_at", queuedAt);
        command.Parameters.AddWithValue("$remote_consent_revision", ToDbValue(request.RemoteConsentRevision));
        command.Parameters.AddWithValue(
            "$remote_consent_at",
            request.RemoteConsentAtUtc is null
                ? DBNull.Value
                : ToDatabaseTimestamp(request.RemoteConsentAtUtc.Value));
        command.Parameters.AddWithValue("$privacy_policy_json", ToDbValue(request.PrivacyPolicyJson));
        command.Parameters.AddWithValue("$manifest_version", request.ManifestVersion);
        command.Parameters.AddWithValue("$manifest_path", ToDbValue(request.ManifestPath));
        command.Parameters.AddWithValue("$replace_existing", request.ReplaceExisting ? 1 : 0);
        command.Parameters.AddWithValue("$trigger_kind", ToDatabaseTriggerKind(request.TriggerKind));
        command.Parameters.AddWithValue("$supersedes_job_id", ToDbValue(request.SupersedesJobId));
    }

    private static TranscriptionJobRecord ReadJob(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(0),
            SessionId: reader.GetString(1),
            EngineId: reader.GetString(2),
            ExecutionKind: ParseExecutionKind(reader.GetString(3)),
            ModelId: reader.GetString(4),
            EngineOptionsJson: GetNullableString(reader, 5),
            RequestedLanguage: GetNullableString(reader, 6),
            DetectedLanguage: GetNullableString(reader, 7),
            InputAudioPath: reader.GetString(8),
            InputSha256: reader.GetString(9),
            InputSizeBytes: reader.GetInt64(10),
            InputDurationSeconds: reader.IsDBNull(11) ? null : reader.GetDouble(11),
            Status: ParseJobStatus(reader.GetString(12)),
            Progress: reader.GetDouble(13),
            CurrentChunkIndex: reader.IsDBNull(14) ? null : reader.GetInt32(14),
            CurrentChunkId: GetNullableString(reader, 15),
            QueuedAtUtc: ParseTimestamp(reader.GetString(16)),
            CreatedAtUtc: ParseTimestamp(reader.GetString(17)),
            UpdatedAtUtc: ParseTimestamp(reader.GetString(18)),
            AttemptCount: reader.GetInt32(19),
            NextAttemptAtUtc: GetNullableTimestamp(reader, 20),
            LastAttemptAtUtc: GetNullableTimestamp(reader, 21),
            StableErrorCode: GetNullableString(reader, 22),
            ErrorMessage: GetNullableString(reader, 23),
            TranscriptMarkdownPath: GetNullableString(reader, 24),
            TranscriptJsonPath: GetNullableString(reader, 25),
            UsageJson: GetNullableString(reader, 26),
            RemoteConsentRevision: GetNullableString(reader, 27),
            RemoteConsentAtUtc: GetNullableTimestamp(reader, 28),
            PrivacyPolicyJson: GetNullableString(reader, 29),
            ManifestVersion: reader.GetInt32(30),
            ManifestPath: GetNullableString(reader, 31),
            ArtifactPublicationState: ParsePublicationState(reader.GetString(32)),
            StagedTranscriptMarkdownPath: GetNullableString(reader, 33),
            StagedTranscriptJsonPath: GetNullableString(reader, 34),
            StagedTranscriptMarkdownSha256: GetNullableString(reader, 35),
            StagedTranscriptJsonSha256: GetNullableString(reader, 36),
            ReplaceExisting: reader.GetInt64(37) != 0,
            CancellationRequested: reader.GetInt64(38) != 0,
            CompletedAtUtc: GetNullableTimestamp(reader, 39),
            CancelledAtUtc: GetNullableTimestamp(reader, 40),
            TriggerKind: ParseTriggerKind(reader.GetString(41)),
            SupersedesJobId: GetNullableString(reader, 42));

    private static TranscriptionChunkRecord ReadChunk(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(0),
            JobId: reader.GetString(1),
            SequenceIndex: reader.GetInt32(2),
            ParentChunkId: GetNullableString(reader, 3),
            SplitDepth: reader.GetInt32(4),
            StartMilliseconds: reader.GetInt64(5),
            EndMilliseconds: reader.GetInt64(6),
            OverlapMilliseconds: reader.GetInt64(7),
            ArtifactPath: GetNullableString(reader, 8),
            ArtifactFormat: GetNullableString(reader, 9),
            ArtifactSha256: GetNullableString(reader, 10),
            ArtifactSizeBytes: reader.IsDBNull(11) ? null : reader.GetInt64(11),
            Status: ParseChunkStatus(reader.GetString(12)),
            AttemptCount: reader.GetInt32(13),
            CreatedAtUtc: ParseTimestamp(reader.GetString(14)),
            UpdatedAtUtc: ParseTimestamp(reader.GetString(15)),
            EngineRequestId: GetNullableString(reader, 16),
            ResultPath: GetNullableString(reader, 17),
            ResultSha256: GetNullableString(reader, 18),
            ResultMetadataJson: GetNullableString(reader, 19),
            UsageJson: GetNullableString(reader, 20),
            StableErrorCode: GetNullableString(reader, 21),
            ErrorMessage: GetNullableString(reader, 22));

    private static LocalTranscriptionChunkAttemptStart Normalize(
        LocalTranscriptionChunkAttemptStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.AttemptIndex is < 0 or > MaxLocalAttemptIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                start.AttemptIndex,
                $"Local worker attempt index must be between zero and {MaxLocalAttemptIndex}.");
        }

        if (!Enum.IsDefined(start.RequestedBackend))
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                start.RequestedBackend,
                "Unknown requested local backend.");
        }

        return start with
        {
            JobId = RequireBoundedValue(
                start.JobId,
                nameof(start.JobId),
                MaxLocalAttemptIdentifierLength),
            ChunkId = RequireBoundedValue(
                start.ChunkId,
                nameof(start.ChunkId),
                MaxLocalAttemptIdentifierLength),
            WorkerStartedAtUtc = start.WorkerStartedAtUtc.ToUniversalTime()
        };
    }

    private static LocalTranscriptionChunkAttemptCompletion Normalize(
        LocalTranscriptionChunkAttemptCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.AttemptIndex is < 0 or > MaxLocalAttemptIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                completion.AttemptIndex,
                $"Local worker attempt index must be between zero and {MaxLocalAttemptIndex}.");
        }

        if (!Enum.IsDefined(completion.Status)
            || completion.Status == LocalTranscriptionAttemptStatus.Running)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                completion.Status,
                "A terminal local worker outcome is required.");
        }

        if (completion.ResolvedBackend is { } resolvedBackend
            && (!Enum.IsDefined(resolvedBackend)
                || resolvedBackend == LocalTranscriptionBackend.Auto))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                completion.ResolvedBackend,
                "A resolved local backend must be cpu, metal, or vulkan.");
        }

        if (completion.Status == LocalTranscriptionAttemptStatus.Completed
            && completion.ResolvedBackend is null)
        {
            throw new ArgumentException(
                "A completed local worker attempt requires its resolved backend.",
                nameof(completion));
        }

        ValidateNonNegative(
            completion.DecodeDurationMilliseconds,
            nameof(completion.DecodeDurationMilliseconds));
        ValidateNonNegative(
            completion.InferenceDurationMilliseconds,
            nameof(completion.InferenceDurationMilliseconds));
        ValidateNonNegative(
            completion.PeakWorkingSetBytes,
            nameof(completion.PeakWorkingSetBytes));

        var workerStartedAt = completion.WorkerStartedAtUtc.ToUniversalTime();
        var workerEndedAt = completion.WorkerEndedAtUtc.ToUniversalTime();
        if (workerEndedAt < workerStartedAt)
        {
            throw new ArgumentException(
                "A local worker attempt cannot end before it starts.",
                nameof(completion));
        }

        var stableFailureCategory = OptionalStableToken(
            completion.StableFailureCategory,
            nameof(completion.StableFailureCategory),
            MaxStableFailureCategoryLength);
        var isFailure = completion.Status is
            LocalTranscriptionAttemptStatus.Crashed
            or LocalTranscriptionAttemptStatus.OutOfMemory
            or LocalTranscriptionAttemptStatus.Timeout
            or LocalTranscriptionAttemptStatus.DecodeFailure
            or LocalTranscriptionAttemptStatus.NativeFailure
            or LocalTranscriptionAttemptStatus.ProtocolFailure;
        if (isFailure != (stableFailureCategory is not null))
        {
            throw new ArgumentException(
                isFailure
                    ? "A failed local worker attempt requires a stable failure category."
                    : "A successful, cancelled, or preempted attempt cannot carry a failure category.",
                nameof(completion));
        }

        return completion with
        {
            JobId = RequireBoundedValue(
                completion.JobId,
                nameof(completion.JobId),
                MaxLocalAttemptIdentifierLength),
            ChunkId = RequireBoundedValue(
                completion.ChunkId,
                nameof(completion.ChunkId),
                MaxLocalAttemptIdentifierLength),
            WorkerStartedAtUtc = workerStartedAt,
            WorkerEndedAtUtc = workerEndedAt,
            StableFailureCategory = stableFailureCategory
        };
    }

    private static TranscriptionJobEnqueueRequest Normalize(TranscriptionJobEnqueueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.ExecutionKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.ExecutionKind,
                "Unknown transcription execution kind.");
        }

        if (!Enum.IsDefined(request.TriggerKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.TriggerKind,
                "Unknown transcription trigger kind.");
        }

        var engineId = RequireValue(request.EngineId, nameof(request.EngineId));
        var requiredExecutionKind = TranscriptionEngineIds.GetRequiredExecutionKind(engineId);
        if (request.ExecutionKind != requiredExecutionKind)
        {
            throw new ArgumentException(
                $"Engine '{engineId}' requires execution kind '{requiredExecutionKind}'.",
                nameof(request));
        }

        if (request.InputSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.InputSizeBytes,
                "Input size cannot be negative.");
        }

        if (request.InputDurationSeconds is { } duration
            && (!double.IsFinite(duration) || duration < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                duration,
                "Input duration must be finite and non-negative.");
        }

        if (request.ManifestVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.ManifestVersion,
                "Manifest version must be positive.");
        }

        var consentRevision = OptionalValue(request.RemoteConsentRevision);
        var consentAt = request.RemoteConsentAtUtc;
        if (request.ExecutionKind == TranscriptionExecutionKind.Remote
            && (consentRevision is null || consentAt is null))
        {
            throw new ArgumentException(
                "A remote transcription job requires a frozen consent revision and timestamp.",
                nameof(request));
        }

        var localExecution = request.LocalExecution is null
            ? null
            : NormalizeLocalExecution(request.LocalExecution);
        if (request.ExecutionKind == TranscriptionExecutionKind.Local && localExecution is null)
        {
            throw new ArgumentException(
                "A local transcription job requires a frozen model and runtime identity.",
                nameof(request));
        }

        if (request.ExecutionKind == TranscriptionExecutionKind.Remote && localExecution is not null)
        {
            throw new ArgumentException(
                "A remote transcription job cannot include a local model identity.",
                nameof(request));
        }

        var jobId = RequireValue(request.JobId, nameof(request.JobId));
        var supersedesJobId = OptionalValue(request.SupersedesJobId);
        if (string.Equals(jobId, supersedesJobId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A transcription job cannot supersede itself.", nameof(request));
        }

        return request with
        {
            JobId = jobId,
            SessionId = RequireValue(request.SessionId, nameof(request.SessionId)),
            EngineId = engineId,
            ModelId = RequireValue(request.ModelId, nameof(request.ModelId)),
            InputAudioPath = RequireValue(request.InputAudioPath, nameof(request.InputAudioPath)),
            InputSha256 = NormalizeSha256(request.InputSha256, nameof(request.InputSha256)),
            QueuedAtUtc = request.QueuedAtUtc.ToUniversalTime(),
            EngineOptionsJson = OptionalValue(request.EngineOptionsJson),
            RequestedLanguage = OptionalValue(request.RequestedLanguage),
            RemoteConsentRevision = consentRevision,
            RemoteConsentAtUtc = consentAt?.ToUniversalTime(),
            PrivacyPolicyJson = OptionalValue(request.PrivacyPolicyJson),
            ManifestPath = OptionalValue(request.ManifestPath),
            SupersedesJobId = supersedesJobId,
            LocalExecution = localExecution
        };
    }

    private static LocalTranscriptionExecutionIdentity NormalizeLocalExecution(
        LocalTranscriptionExecutionIdentity execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.ModelCatalogVersion <= 0
            || execution.ModelSizeBytes <= 0
            || execution.BridgeAbiVersion <= 0
            || execution.WorkerProtocolVersion <= 0
            || execution.ThreadCount is <= 0 or > 64
            || execution.ChunkProfileVersion <= 0)
        {
            throw new ArgumentException(
                "Local model/runtime versions, sizes, and thread count must be positive and bounded.",
                nameof(execution));
        }

        if (!Enum.IsDefined(execution.RequestedBackend)
            || execution.ResolvedBackend is { } resolved
            && (!Enum.IsDefined(resolved) || resolved == LocalTranscriptionBackend.Auto))
        {
            throw new ArgumentException("Local backend selection is invalid.", nameof(execution));
        }

        var inferenceParameters = RequireJson(
            execution.InferenceParametersJson,
            nameof(execution.InferenceParametersJson));
        var backendHistory = execution.BackendHistoryJson is null
            ? null
            : NormalizeBackendHistory(
                execution.BackendHistoryJson,
                nameof(execution.BackendHistoryJson));
        return execution with
        {
            ModelCatalogRevision = RequireValue(
                execution.ModelCatalogRevision,
                nameof(execution.ModelCatalogRevision)),
            ModelFormat = RequireValue(execution.ModelFormat, nameof(execution.ModelFormat)),
            ModelPath = Path.GetFullPath(RequireValue(execution.ModelPath, nameof(execution.ModelPath))),
            ModelSha256 = NormalizeSha256(execution.ModelSha256, nameof(execution.ModelSha256)),
            RuntimeVersion = RequireValue(execution.RuntimeVersion, nameof(execution.RuntimeVersion)),
            RuntimeCommit = RequireValue(execution.RuntimeCommit, nameof(execution.RuntimeCommit)),
            RuntimeSourceArchiveSha256 = NormalizeSha256(
                execution.RuntimeSourceArchiveSha256,
                nameof(execution.RuntimeSourceArchiveSha256)),
            NativeBundleManifestSha256 = NormalizeSha256(
                execution.NativeBundleManifestSha256,
                nameof(execution.NativeBundleManifestSha256)),
            InferenceParametersJson = inferenceParameters,
            RunIdentitySha256 = NormalizeSha256(
                execution.RunIdentitySha256,
                nameof(execution.RunIdentitySha256)),
            BackendHistoryJson = backendHistory,
            PolicyDeferReason = OptionalValue(execution.PolicyDeferReason)
        };
    }

    private static string RequireJson(string value, string parameterName)
    {
        var normalized = RequireValue(value, parameterName);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            return normalized;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A valid JSON value is required.", parameterName, exception);
        }
    }

    private static string NormalizeBackendHistory(string value, string parameterName)
    {
        var normalized = RequireBoundedValue(
            value,
            parameterName,
            MaxBackendHistoryJsonLength);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException(
                    "Local backend history must be a JSON array.",
                    parameterName);
            }

            var backends = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (backends.Count == MaxBackendHistoryEntries)
                {
                    throw new ArgumentException(
                        $"Local backend history cannot exceed {MaxBackendHistoryEntries} entries.",
                        parameterName);
                }

                if (element.ValueKind != JsonValueKind.String
                    || element.GetString() is not { } token)
                {
                    throw new ArgumentException(
                        "Local backend history entries must be backend tokens.",
                        parameterName);
                }

                LocalTranscriptionBackend backend;
                try
                {
                    backend = ParseLocalBackend(token);
                }
                catch (InvalidDataException exception)
                {
                    throw new ArgumentException(
                        "Local backend history contains an unknown backend token.",
                        parameterName,
                        exception);
                }

                if (backend == LocalTranscriptionBackend.Auto)
                {
                    throw new ArgumentException(
                        "Local backend history accepts only resolved backend tokens.",
                        parameterName);
                }

                backends.Add(ToDatabaseLocalBackend(backend));
            }

            return JsonSerializer.Serialize(backends);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A valid JSON value is required.", parameterName, exception);
        }
    }

    private static string AppendBackendHistory(
        string? backendHistoryJson,
        LocalTranscriptionBackend backend)
    {
        if (backend == LocalTranscriptionBackend.Auto || !Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "Only a resolved backend can be appended to local history.");
        }

        var backends = backendHistoryJson is null
            ? []
            : JsonSerializer.Deserialize<List<string>>(backendHistoryJson)
                ?? throw new InvalidDataException("Local backend history cannot be null JSON.");
        if (backends.Count > MaxBackendHistoryEntries)
        {
            throw new InvalidDataException("Local backend history exceeds its bounded entry count.");
        }

        if (backends.Count == MaxBackendHistoryEntries)
        {
            backends.RemoveAt(0);
        }

        backends.Add(ToDatabaseLocalBackend(backend));
        return JsonSerializer.Serialize(backends);
    }

    private static void EnsureSameAttemptStart(
        LocalTranscriptionChunkAttemptRecord existing,
        LocalTranscriptionChunkAttemptStart start)
    {
        if (existing.RequestedBackend != start.RequestedBackend
            || existing.WorkerStartedAtUtc != start.WorkerStartedAtUtc)
        {
            throw new InvalidOperationException(
                $"Local worker attempt '{start.JobId}/{start.ChunkId}/{start.AttemptIndex}' "
                + "already has a different start identity.");
        }
    }

    private static void EnsureSameAttemptCompletion(
        LocalTranscriptionChunkAttemptRecord existing,
        LocalTranscriptionChunkAttemptCompletion completion)
    {
        if (existing.WorkerEndedAtUtc != completion.WorkerEndedAtUtc
            || existing.Status != completion.Status
            || existing.ResolvedBackend != completion.ResolvedBackend
            || existing.DecodeDurationMilliseconds != completion.DecodeDurationMilliseconds
            || existing.InferenceDurationMilliseconds != completion.InferenceDurationMilliseconds
            || existing.PeakWorkingSetBytes != completion.PeakWorkingSetBytes
            || !string.Equals(
                existing.StableFailureCategory,
                completion.StableFailureCategory,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Terminal local worker attempt "
                + $"'{completion.JobId}/{completion.ChunkId}/{completion.AttemptIndex}' is immutable.");
        }
    }

    private static long? SumOptionalDurations(long? left, long? right) =>
        left is null && right is null
            ? null
            : checked((left ?? 0) + (right ?? 0));

    private static long? AddOptionalDuration(long? current, long? additional) =>
        additional is null
            ? current
            : checked((current ?? 0) + additional.Value);

    private static long? MaxOptional(long? left, long? right) =>
        left is null
            ? right
            : right is null
                ? left
                : Math.Max(left.Value, right.Value);

    private static bool HasSameFrozenIdentity(
        TranscriptionJobRecord job,
        TranscriptionJobEnqueueRequest request) =>
        string.Equals(job.Id, request.JobId, StringComparison.Ordinal)
        && string.Equals(job.SessionId, request.SessionId, StringComparison.Ordinal)
        && string.Equals(job.EngineId, request.EngineId, StringComparison.Ordinal)
        && job.ExecutionKind == request.ExecutionKind
        && string.Equals(job.ModelId, request.ModelId, StringComparison.Ordinal)
        && string.Equals(job.EngineOptionsJson, request.EngineOptionsJson, StringComparison.Ordinal)
        && string.Equals(job.RequestedLanguage, request.RequestedLanguage, StringComparison.Ordinal)
        && string.Equals(job.InputAudioPath, request.InputAudioPath, StringComparison.Ordinal)
        && string.Equals(job.InputSha256, request.InputSha256, StringComparison.Ordinal)
        && job.InputSizeBytes == request.InputSizeBytes
        && job.InputDurationSeconds == request.InputDurationSeconds
        && job.QueuedAtUtc == request.QueuedAtUtc
        && string.Equals(
            job.RemoteConsentRevision,
            request.RemoteConsentRevision,
            StringComparison.Ordinal)
        && job.RemoteConsentAtUtc == request.RemoteConsentAtUtc
        && string.Equals(job.PrivacyPolicyJson, request.PrivacyPolicyJson, StringComparison.Ordinal)
        && job.ManifestVersion == request.ManifestVersion
        && string.Equals(job.ManifestPath, request.ManifestPath, StringComparison.Ordinal)
        && job.ReplaceExisting == request.ReplaceExisting
        && job.TriggerKind == request.TriggerKind
        && string.Equals(job.SupersedesJobId, request.SupersedesJobId, StringComparison.Ordinal);

    private static bool IsConfigurationRemediationCode(string? stableErrorCode) =>
        stableErrorCode is "invalid_engine_configuration"
            or "zdr_route_unavailable"
            or "native_crash"
            or "insufficient_memory";

    private static IReadOnlyList<TranscriptionChunkDefinition> NormalizeChunks(
        IReadOnlyList<TranscriptionChunkDefinition> chunks,
        bool requireAllParentsInManifest = true)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("A transcription manifest requires at least one chunk.", nameof(chunks));
        }

        var normalized = new List<TranscriptionChunkDefinition>(chunks.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<int>();
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            var id = RequireValue(chunk.Id, nameof(chunk.Id));
            var parentId = OptionalValue(chunk.ParentChunkId);
            if (!ids.Add(id))
            {
                throw new ArgumentException($"Chunk id '{id}' appears more than once.", nameof(chunks));
            }

            if (!indexes.Add(chunk.SequenceIndex) || chunk.SequenceIndex < 0)
            {
                throw new ArgumentException(
                    $"Chunk sequence index '{chunk.SequenceIndex}' is invalid or duplicated.",
                    nameof(chunks));
            }

            if (chunk.SplitDepth < 0
                || chunk.StartMilliseconds < 0
                || chunk.EndMilliseconds <= chunk.StartMilliseconds
                || chunk.OverlapMilliseconds < 0
                || chunk.OverlapMilliseconds > chunk.EndMilliseconds - chunk.StartMilliseconds
                || chunk.ArtifactSizeBytes < 0)
            {
                throw new ArgumentException($"Chunk '{id}' has invalid bounds or sizes.", nameof(chunks));
            }

            if (parentId is not null && string.Equals(id, parentId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Chunk '{id}' cannot be its own parent.", nameof(chunks));
            }

            if (chunk.InitialStatus is not (TranscriptionChunkStatus.Pending or TranscriptionChunkStatus.Split))
            {
                throw new ArgumentException(
                    $"Chunk '{id}' must enter a manifest as pending or split.",
                    nameof(chunks));
            }

            normalized.Add(chunk with
            {
                Id = id,
                ParentChunkId = parentId,
                ArtifactPath = OptionalValue(chunk.ArtifactPath),
                ArtifactFormat = OptionalValue(chunk.ArtifactFormat),
                ArtifactSha256 = chunk.ArtifactSha256 is null
                    ? null
                    : NormalizeSha256(chunk.ArtifactSha256, nameof(chunk.ArtifactSha256))
            });
        }

        if (requireAllParentsInManifest)
        {
            var allIds = normalized.Select(static chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var chunk in normalized)
            {
                if (chunk.ParentChunkId is not null && !allIds.Contains(chunk.ParentChunkId))
                {
                    throw new ArgumentException(
                        $"Parent chunk '{chunk.ParentChunkId}' is absent from the manifest.",
                        nameof(chunks));
                }
            }

            _ = OrderForInsert(normalized, []);
        }

        return normalized;
    }

    private static TranscriptionChunkCompletion Normalize(TranscriptionChunkCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return completion with
        {
            JobId = RequireValue(completion.JobId, nameof(completion.JobId)),
            ChunkId = RequireValue(completion.ChunkId, nameof(completion.ChunkId)),
            ResultPath = RequireValue(completion.ResultPath, nameof(completion.ResultPath)),
            ResultSha256 = NormalizeSha256(completion.ResultSha256, nameof(completion.ResultSha256)),
            CompletedAtUtc = completion.CompletedAtUtc.ToUniversalTime(),
            EngineRequestId = OptionalValue(completion.EngineRequestId),
            ResultMetadataJson = OptionalValue(completion.ResultMetadataJson),
            UsageJson = OptionalValue(completion.UsageJson)
        };
    }

    private static TranscriptionChunkSplitRequest Normalize(TranscriptionChunkSplitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var jobId = RequireValue(request.JobId, nameof(request.JobId));
        var parentChunkId = RequireValue(request.ParentChunkId, nameof(request.ParentChunkId));
        var children = NormalizeChunks(request.Children, requireAllParentsInManifest: false).ToArray();
        if (children.Length != 2)
        {
            throw new ArgumentException("A chunk split requires exactly two children.", nameof(request));
        }

        foreach (var child in children)
        {
            if (!string.Equals(child.ParentChunkId, parentChunkId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Split child '{child.Id}' must reference parent '{parentChunkId}'.",
                    nameof(request));
            }

            if (child.InitialStatus != TranscriptionChunkStatus.Pending)
            {
                throw new ArgumentException(
                    $"Split child '{child.Id}' must start as pending.",
                    nameof(request));
            }
        }

        return request with
        {
            JobId = jobId,
            ParentChunkId = parentChunkId,
            Children = children,
            SplitAtUtc = request.SplitAtUtc.ToUniversalTime()
        };
    }

    private static TranscriptionArtifactStage Normalize(TranscriptionArtifactStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage with
        {
            JobId = RequireValue(stage.JobId, nameof(stage.JobId)),
            StagedMarkdownPath = RequireValue(stage.StagedMarkdownPath, nameof(stage.StagedMarkdownPath)),
            StagedJsonPath = RequireValue(stage.StagedJsonPath, nameof(stage.StagedJsonPath)),
            FinalMarkdownPath = RequireValue(stage.FinalMarkdownPath, nameof(stage.FinalMarkdownPath)),
            FinalJsonPath = RequireValue(stage.FinalJsonPath, nameof(stage.FinalJsonPath)),
            StagedMarkdownSha256 = NormalizeSha256(
                stage.StagedMarkdownSha256,
                nameof(stage.StagedMarkdownSha256)),
            StagedJsonSha256 = NormalizeSha256(stage.StagedJsonSha256, nameof(stage.StagedJsonSha256)),
            StagedAtUtc = stage.StagedAtUtc.ToUniversalTime()
        };
    }

    private static TranscriptionArtifactPublication Normalize(
        TranscriptionArtifactPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return publication with
        {
            JobId = RequireValue(publication.JobId, nameof(publication.JobId)),
            FinalMarkdownPath = RequireValue(
                publication.FinalMarkdownPath,
                nameof(publication.FinalMarkdownPath)),
            FinalJsonPath = RequireValue(publication.FinalJsonPath, nameof(publication.FinalJsonPath)),
            PublishedAtUtc = publication.PublishedAtUtc.ToUniversalTime(),
            DetectedLanguage = OptionalValue(publication.DetectedLanguage),
            UsageJson = OptionalValue(publication.UsageJson)
        };
    }

    private static IReadOnlyList<TranscriptionChunkDefinition> OrderForInsert(
        IReadOnlyList<TranscriptionChunkDefinition> chunks,
        IEnumerable<string> alreadyPresentIds)
    {
        var remaining = chunks.ToDictionary(static chunk => chunk.Id, StringComparer.Ordinal);
        var available = alreadyPresentIds.ToHashSet(StringComparer.Ordinal);
        var ordered = new List<TranscriptionChunkDefinition>(chunks.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(chunk => chunk.ParentChunkId is null || available.Contains(chunk.ParentChunkId))
                .OrderBy(static chunk => chunk.SequenceIndex)
                .ThenBy(static chunk => chunk.Id, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                throw new ArgumentException("The transcription chunk manifest contains a parent cycle.", nameof(chunks));
            }

            foreach (var chunk in ready)
            {
                ordered.Add(chunk);
                available.Add(chunk.Id);
                remaining.Remove(chunk.Id);
            }
        }

        return ordered;
    }

    private static void EnsureSameDefinition(
        TranscriptionChunkRecord persisted,
        TranscriptionChunkDefinition replacement)
    {
        if (persisted.SequenceIndex != replacement.SequenceIndex
            || !string.Equals(persisted.ParentChunkId, replacement.ParentChunkId, StringComparison.Ordinal)
            || persisted.SplitDepth != replacement.SplitDepth
            || persisted.StartMilliseconds != replacement.StartMilliseconds
            || persisted.EndMilliseconds != replacement.EndMilliseconds
            || persisted.OverlapMilliseconds != replacement.OverlapMilliseconds
            || !string.Equals(persisted.ArtifactPath, replacement.ArtifactPath, StringComparison.Ordinal)
            || !string.Equals(persisted.ArtifactFormat, replacement.ArtifactFormat, StringComparison.Ordinal)
            || !string.Equals(persisted.ArtifactSha256, replacement.ArtifactSha256, StringComparison.Ordinal)
            || persisted.ArtifactSizeBytes != replacement.ArtifactSizeBytes)
        {
            throw new InvalidOperationException(
                $"Persisted chunk '{persisted.Id}' does not match the replacement manifest.");
        }
    }

    private static void ValidateSplitGeometry(
        TranscriptionChunkRecord parent,
        IReadOnlyList<TranscriptionChunkDefinition> children)
    {
        var ordered = children
            .OrderBy(static child => child.StartMilliseconds)
            .ThenBy(static child => child.EndMilliseconds)
            .ThenBy(static child => child.Id, StringComparer.Ordinal)
            .ToArray();
        var left = ordered[0];
        var right = ordered[1];
        var expectedDepth = checked(parent.SplitDepth + 1);
        if (left.SplitDepth != expectedDepth
            || right.SplitDepth != expectedDepth
            || left.StartMilliseconds != parent.StartMilliseconds
            || right.EndMilliseconds != parent.EndMilliseconds
            || left.OverlapMilliseconds != parent.OverlapMilliseconds
            || left.EndMilliseconds >= parent.EndMilliseconds
            || right.StartMilliseconds <= parent.StartMilliseconds
            || left.EndMilliseconds < right.StartMilliseconds
            || right.OverlapMilliseconds != left.EndMilliseconds - right.StartMilliseconds)
        {
            throw new ArgumentException(
                $"Children do not form a deterministic split of parent chunk '{parent.Id}'.",
                nameof(children));
        }
    }

    private static void EnsureSameSplitChildren(
        IReadOnlyList<TranscriptionChunkRecord> persisted,
        IReadOnlyList<TranscriptionChunkDefinition> requested)
    {
        if (persisted.Count != 2)
        {
            throw new InvalidOperationException("A persisted split must contain exactly two direct children.");
        }

        var requestedById = requested.ToDictionary(static child => child.Id, StringComparer.Ordinal);
        foreach (var child in persisted)
        {
            if (!requestedById.TryGetValue(child.Id, out var definition)
                || !string.Equals(child.ParentChunkId, definition.ParentChunkId, StringComparison.Ordinal)
                || child.SplitDepth != definition.SplitDepth
                || child.StartMilliseconds != definition.StartMilliseconds
                || child.EndMilliseconds != definition.EndMilliseconds
                || child.OverlapMilliseconds != definition.OverlapMilliseconds
                || !string.Equals(child.ArtifactPath, definition.ArtifactPath, StringComparison.Ordinal)
                || !string.Equals(child.ArtifactFormat, definition.ArtifactFormat, StringComparison.Ordinal)
                || !string.Equals(child.ArtifactSha256, definition.ArtifactSha256, StringComparison.Ordinal)
                || child.ArtifactSizeBytes != definition.ArtifactSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Persisted split child '{child.Id}' differs from the deterministic request.");
            }
        }
    }

    private static void EnsureSameCompletion(
        TranscriptionChunkRecord existing,
        TranscriptionChunkCompletion completion)
    {
        if (!string.Equals(existing.ResultPath, completion.ResultPath, StringComparison.Ordinal)
            || !string.Equals(existing.ResultSha256, completion.ResultSha256, StringComparison.Ordinal)
            || !string.Equals(existing.EngineRequestId, completion.EngineRequestId, StringComparison.Ordinal)
            || !string.Equals(existing.ResultMetadataJson, completion.ResultMetadataJson, StringComparison.Ordinal)
            || !string.Equals(existing.UsageJson, completion.UsageJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Completed chunk '{existing.Id}' cannot be overwritten with a different checkpoint.");
        }
    }

    private static void EnsureSameStage(
        TranscriptionJobRecord existing,
        TranscriptionArtifactStage stage)
    {
        if (!string.Equals(
                existing.StagedTranscriptMarkdownPath,
                stage.StagedMarkdownPath,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.StagedTranscriptJsonPath,
                stage.StagedJsonPath,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.StagedTranscriptMarkdownSha256,
                stage.StagedMarkdownSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.StagedTranscriptJsonSha256,
                stage.StagedJsonSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.TranscriptMarkdownPath,
                stage.FinalMarkdownPath,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.TranscriptJsonPath,
                stage.FinalJsonPath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Staged job '{existing.Id}' cannot be overwritten with different artifacts.");
        }
    }

    private static bool IsTerminal(TranscriptionJobStatus status) =>
        status is TranscriptionJobStatus.Completed
            or TranscriptionJobStatus.Cancelled
            or TranscriptionJobStatus.Failed;

    private static void ValidateProgress(double progress)
    {
        if (!double.IsFinite(progress) || progress is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(progress),
                progress,
                "Progress must be finite and between zero and one.");
        }
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string RequireBoundedValue(
        string value,
        string parameterName,
        int maximumLength)
    {
        var normalized = RequireValue(value, parameterName);
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string? OptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? OptionalBoundedValue(
        string? value,
        string parameterName,
        int maximumLength)
    {
        var normalized = OptionalValue(value);
        if (normalized?.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string? OptionalStableToken(
        string? value,
        string parameterName,
        int maximumLength)
    {
        var normalized = OptionalBoundedValue(value, parameterName, maximumLength);
        if (normalized is not null && normalized.Any(static character =>
                !((character >= 'a' && character <= 'z')
                  || (character >= '0' && character <= '9')
                  || character is '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "Stable category accepts lowercase ASCII letters, digits, dot, dash, and underscore.",
                parameterName);
        }

        return normalized;
    }

    private static void ValidateNonNegative(long? value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Technical telemetry values cannot be negative.");
        }
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        var normalized = RequireValue(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 value must contain exactly 64 hexadecimal characters.", parameterName);
        }

        return normalized;
    }

    private static object ToDbValue(string? value) => value is null ? DBNull.Value : value;

    private static string ToDatabaseTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string ToDatabaseTriggerKind(TranscriptionTriggerKind triggerKind) =>
        triggerKind switch
        {
            TranscriptionTriggerKind.Manual => "manual",
            TranscriptionTriggerKind.Automatic => "automatic",
            _ => throw new ArgumentOutOfRangeException(
                nameof(triggerKind),
                triggerKind,
                "Unknown transcription trigger kind.")
        };

    private static TranscriptionTriggerKind ParseTriggerKind(string value) =>
        value switch
        {
            "manual" => TranscriptionTriggerKind.Manual,
            "automatic" => TranscriptionTriggerKind.Automatic,
            _ => throw new InvalidDataException($"Unknown transcription trigger kind '{value}'.")
        };

    private static string ToDatabaseLocalBackend(LocalTranscriptionBackend backend) =>
        backend switch
        {
            LocalTranscriptionBackend.Auto => "auto",
            LocalTranscriptionBackend.Cpu => "cpu",
            LocalTranscriptionBackend.Metal => "metal",
            LocalTranscriptionBackend.Vulkan => "vulkan",
            _ => throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "Unknown local transcription backend.")
        };

    private static LocalTranscriptionBackend ParseLocalBackend(string value) =>
        value switch
        {
            "auto" => LocalTranscriptionBackend.Auto,
            "cpu" => LocalTranscriptionBackend.Cpu,
            "metal" => LocalTranscriptionBackend.Metal,
            "vulkan" => LocalTranscriptionBackend.Vulkan,
            _ => throw new InvalidDataException($"Unknown local transcription backend '{value}'.")
        };

    private static string ToDatabaseLocalAttemptStatus(LocalTranscriptionAttemptStatus status) =>
        status switch
        {
            LocalTranscriptionAttemptStatus.Completed => "completed",
            LocalTranscriptionAttemptStatus.Cancelled => "cancelled",
            LocalTranscriptionAttemptStatus.Preempted => "preempted",
            LocalTranscriptionAttemptStatus.Crashed => "crashed",
            LocalTranscriptionAttemptStatus.OutOfMemory => "out_of_memory",
            LocalTranscriptionAttemptStatus.Timeout => "timeout",
            LocalTranscriptionAttemptStatus.DecodeFailure => "decode_failure",
            LocalTranscriptionAttemptStatus.NativeFailure => "native_failure",
            LocalTranscriptionAttemptStatus.ProtocolFailure => "protocol_failure",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "A terminal local worker outcome is required.")
        };

    private static LocalTranscriptionAttemptStatus ParseLocalAttemptStatus(string value) =>
        value switch
        {
            "completed" => LocalTranscriptionAttemptStatus.Completed,
            "cancelled" => LocalTranscriptionAttemptStatus.Cancelled,
            "preempted" => LocalTranscriptionAttemptStatus.Preempted,
            "crashed" => LocalTranscriptionAttemptStatus.Crashed,
            "out_of_memory" => LocalTranscriptionAttemptStatus.OutOfMemory,
            "timeout" => LocalTranscriptionAttemptStatus.Timeout,
            "decode_failure" => LocalTranscriptionAttemptStatus.DecodeFailure,
            "native_failure" => LocalTranscriptionAttemptStatus.NativeFailure,
            "protocol_failure" => LocalTranscriptionAttemptStatus.ProtocolFailure,
            _ => throw new InvalidDataException(
                $"Unknown local worker attempt exit category '{value}'.")
        };

    private static string ToDatabaseExecutionKind(TranscriptionExecutionKind executionKind) =>
        executionKind switch
        {
            TranscriptionExecutionKind.Local => "local",
            TranscriptionExecutionKind.Remote => "remote",
            _ => throw new ArgumentOutOfRangeException(
                nameof(executionKind),
                executionKind,
                "Unknown transcription execution kind.")
        };

    private static TranscriptionExecutionKind ParseExecutionKind(string value) =>
        value switch
        {
            "local" => TranscriptionExecutionKind.Local,
            "remote" => TranscriptionExecutionKind.Remote,
            _ => throw new InvalidDataException($"Unknown transcription execution kind '{value}'.")
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

    private static TranscriptionJobStatus ParseJobStatus(string value) =>
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

    private static string ToDatabaseStatus(TranscriptionChunkStatus status) =>
        status switch
        {
            TranscriptionChunkStatus.Pending => "pending",
            TranscriptionChunkStatus.Preparing => "preparing",
            TranscriptionChunkStatus.Uploading => "uploading",
            TranscriptionChunkStatus.Processing => "processing",
            TranscriptionChunkStatus.Completed => "completed",
            TranscriptionChunkStatus.Split => "split",
            TranscriptionChunkStatus.Cancelled => "cancelled",
            TranscriptionChunkStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown transcription chunk status.")
        };

    private static TranscriptionChunkStatus ParseChunkStatus(string value) =>
        value switch
        {
            "pending" => TranscriptionChunkStatus.Pending,
            "preparing" => TranscriptionChunkStatus.Preparing,
            "uploading" => TranscriptionChunkStatus.Uploading,
            "processing" => TranscriptionChunkStatus.Processing,
            "completed" => TranscriptionChunkStatus.Completed,
            "split" => TranscriptionChunkStatus.Split,
            "cancelled" => TranscriptionChunkStatus.Cancelled,
            "failed" => TranscriptionChunkStatus.Failed,
            _ => throw new InvalidDataException($"Unknown transcription chunk status '{value}'.")
        };

    private static TranscriptionArtifactPublicationState ParsePublicationState(string value) =>
        value switch
        {
            "none" => TranscriptionArtifactPublicationState.None,
            "staged" => TranscriptionArtifactPublicationState.Staged,
            "promoted" => TranscriptionArtifactPublicationState.Promoted,
            _ => throw new InvalidDataException($"Unknown transcription artifact publication state '{value}'.")
        };

    private sealed record ChunkSequenceCandidate(
        string Id,
        long StartMilliseconds,
        long EndMilliseconds,
        int SplitDepth);

    private sealed record LocalAttemptSequenceState(
        int NextAttemptIndex,
        bool HasOpenAttempt);
}
