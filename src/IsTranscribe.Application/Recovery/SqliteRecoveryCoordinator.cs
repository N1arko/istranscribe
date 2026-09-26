using Microsoft.Data.Sqlite;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Application.Recovery;

public sealed class SqliteRecoveryCoordinator(
    BootstrapFileLogger logger,
    Func<SqliteConnection?> connectionAccessor) : IRecoveryCoordinator
{
    private readonly BootstrapFileLogger _logger = logger;
    private readonly Func<SqliteConnection?> _connectionAccessor = connectionAccessor;

    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.algorithm
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.recording
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.transcription
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    public async ValueTask<RecoveryLaunchDecision> EvaluateAsync(CancellationToken cancellationToken)
    {
        var connection = _connectionAccessor();
        if (connection is null)
        {
            return RecoveryLaunchDecision.None;
        }

        _logger.LogEvent(
            level: "Info",
            eventCode: "RECOVERY_SCAN_STARTED",
            message: "Startup recovery scan started.",
            jobName: "StartupRecoveryJob");

        // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#startup-recovery.idempotency
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int failedRecordingRows;
        int deactivatedTranscriptionRows;
        try
        {
            failedRecordingRows = await NormalizeRecordingStatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            deactivatedTranscriptionRows = await DeactivateLegacyTranscriptionStatesAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        var totalChanged = failedRecordingRows + deactivatedTranscriptionRows;

        var detail = $"recording={failedRecordingRows}; transcription_deactivated={deactivatedTranscriptionRows}";
        _logger.LogEvent(
            level: "Info",
            eventCode: "RECOVERY_SCAN_COMPLETED",
            message: "Startup recovery scan completed.",
            jobName: "StartupRecoveryJob",
            metadata: new Dictionary<string, object?>
            {
                ["recording_rows_changed"] = failedRecordingRows,
                ["transcription_rows_deactivated"] = deactivatedTranscriptionRows,
                ["total_rows_changed"] = totalChanged
            });

        if (failedRecordingRows == 0)
        {
            return RecoveryLaunchDecision.None;
        }

        return new RecoveryLaunchDecision(
            RequiresAttention: true,
            Summary: "Recovery actions were applied to unfinished sessions.",
            Detail: detail);
    }

    private static async Task<int> NormalizeRecordingStatesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                status = CASE
                    WHEN status IN ('recording', 'stopping', 'paused')
                         AND COALESCE(temp_session_path, '') <> '' THEN 'stopping'
                    WHEN status IN ('recording', 'stopping', 'paused') THEN 'failed'
                    WHEN status IN ('prebuffering', 'awaiting_confirmation') THEN 'discarded'
                    ELSE status
                END,
                error_code = CASE
                    WHEN status IN ('recording', 'stopping', 'paused')
                         AND COALESCE(temp_session_path, '') = '' THEN COALESCE(error_code, 'RECOVERY_UNCLEAN_SHUTDOWN')
                    ELSE error_code
                END,
                error_message = CASE
                    WHEN status IN ('recording', 'stopping', 'paused')
                         AND COALESCE(temp_session_path, '') = '' THEN COALESCE(error_message, 'Session was interrupted by unclean shutdown and recovered at startup.')
                    ELSE error_message
                END,
                artifact_error_code = CASE
                    WHEN status IN ('recording', 'stopping', 'paused')
                         AND COALESCE(temp_session_path, '') <> '' THEN COALESCE(artifact_error_code, 'artifact_recovery_pending')
                    ELSE artifact_error_code
                END,
                artifact_error_message = CASE
                    WHEN status IN ('recording', 'stopping', 'paused')
                         AND COALESCE(temp_session_path, '') <> '' THEN COALESCE(artifact_error_message, 'Recording sources will be reconciled by the artifact recovery pipeline.')
                    ELSE artifact_error_message
                END,
                updated_at = $updated_at
            WHERE status IN ('recording', 'stopping', 'paused', 'prebuffering', 'awaiting_confirmation');
            """;
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Legacy transcription work is inert in release v2. This recovery guard applies the same
    // fail-closed mapping as migration 005 to rows written later by an older binary.
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    private static async Task<int> DeactivateLegacyTranscriptionStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE meeting_session
            SET
                transcription_status = CASE
                    WHEN transcription_status IN ('queued', 'uploading', 'processing', 'retry_scheduled')
                        THEN 'legacy_inactive'
                    WHEN transcription_status = 'failed'
                        THEN 'legacy_failed'
                    ELSE transcription_status
                END
            WHERE transcription_status IN (
                'queued',
                'uploading',
                'processing',
                'retry_scheduled',
                'failed');
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
