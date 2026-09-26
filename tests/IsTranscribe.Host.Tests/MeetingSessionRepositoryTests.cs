using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#migration
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </summary>
public sealed class MeetingSessionRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_PersistsAndUpdatesMeetingSessionFields()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var sessionId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 4, 7, 12, 0, 0, TimeSpan.Zero);

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    sessionId,
                    createdAt,
                    mode: "manual",
                    sourceType: "mixed",
                    outputDeviceId: "render-1",
                    microphoneDeviceId: "mic-1"),
                CancellationToken.None);

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    sessionId,
                    createdAt,
                    mode: "manual",
                    sourceType: "mixed",
                    outputDeviceId: "render-1",
                    microphoneDeviceId: "mic-1") with
                {
                    EndedAtUtc = createdAt.AddMinutes(3),
                    Status = "saved",
                    DurationSeconds = 180,
                    QueuedAtUtc = createdAt.AddMinutes(3),
                    UpdatedAtUtc = createdAt.AddMinutes(3),
                    RetryAttemptCount = 2,
                    NextRetryAtUtc = createdAt.AddMinutes(8),
                    LastRetryAtUtc = createdAt.AddMinutes(4),
                    AudioOutputPath = @"C:\Recordings\output.wav",
                    AudioMicPath = @"C:\Recordings\mic.wav",
                    AudioMixPath = @"C:\Recordings\mix.wav"
                },
                CancellationToken.None);

            await using var command = database.CreateCommand();
            command.CommandText =
                """
                SELECT status, duration_seconds, audio_output_path, audio_mic_path, audio_mix_path, retry_attempt_count
                FROM meeting_session
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("saved", reader.GetString(0));
            Assert.Equal(180d, reader.GetDouble(1));
            Assert.Equal(@"C:\Recordings\output.wav", reader.GetString(2));
            Assert.Equal(@"C:\Recordings\mic.wav", reader.GetString(3));
            Assert.Equal(@"C:\Recordings\mix.wav", reader.GetString(4));
            Assert.Equal(2, reader.GetInt32(5));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ListRecent_OrdersByStartedAtDescAndSkipsEphemeralStatuses()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var t0 = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

            await repository.UpsertAsync(MeetingSessionRecord.Create(Guid.NewGuid(), t0, "manual", "mixed", "out-1", "mic-1") with { Status = "saved" }, CancellationToken.None);
            await repository.UpsertAsync(MeetingSessionRecord.Create(Guid.NewGuid(), t0.AddMinutes(10), "manual", "mixed", "out-1", "mic-1") with { Status = "saved" }, CancellationToken.None);
            await repository.UpsertAsync(MeetingSessionRecord.Create(Guid.NewGuid(), t0.AddMinutes(20), "ask", "process", "out-1", "mic-1") with { Status = "awaiting_confirmation" }, CancellationToken.None);

            var recent = repository.ListRecent(limit: 10, offset: 0);

            Assert.Equal(2, recent.Count);
            Assert.True(recent[0].StartedAtUtc >= recent[1].StartedAtUtc);
            Assert.DoesNotContain(recent, static session => session.RecordingStatus == "awaiting_confirmation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    [Fact]
    public async Task ListRecentProjectsBothLegacyTranscriptPathsAndUsesJsonFolderFallback()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var createdAt = new DateTimeOffset(2026, 7, 12, 11, 0, 0, TimeSpan.Zero);
            var transcriptDirectory = Path.Combine(root, "transcripts");
            var markdownPath = Path.Combine(transcriptDirectory, "meeting.md");
            var jsonPath = Path.Combine(transcriptDirectory, "meeting.json");
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    Guid.NewGuid(),
                    createdAt,
                    "ask",
                    "mixed",
                    "out",
                    "mic") with
                {
                    Status = "saved",
                    SourceApp = "Both",
                    TranscriptMarkdownPath = markdownPath,
                    TranscriptJsonPath = jsonPath,
                    TranscriptionStatus = "completed"
                },
                CancellationToken.None);
            var jsonOnlyPath = Path.Combine(transcriptDirectory, "json-only.json");
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    Guid.NewGuid(),
                    createdAt.AddMinutes(1),
                    "ask",
                    "mixed",
                    "out",
                    "mic") with
                {
                    Status = "saved",
                    SourceApp = "Json only",
                    TranscriptJsonPath = jsonOnlyPath,
                    TranscriptionStatus = "completed"
                },
                CancellationToken.None);

            var recent = repository.ListRecent(limit: 10, offset: 0);

            Assert.Equal(2, recent.Count);
            Assert.Equal("Json only", recent[0].SourceApp);
            Assert.Null(recent[0].TranscriptMarkdownPath);
            Assert.Equal(jsonOnlyPath, recent[0].TranscriptJsonPath);
            Assert.Equal(transcriptDirectory, recent[0].SessionDirectoryPath);
            Assert.Equal("Both", recent[1].SourceApp);
            Assert.Equal(markdownPath, recent[1].TranscriptMarkdownPath);
            Assert.Equal(jsonPath, recent[1].TranscriptJsonPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactTransitionsAreConditionalRecoverableAndCleanupIsIdempotent()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var sessionId = Guid.NewGuid();
            var id = sessionId.ToString("N");
            var createdAt = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
            var tempPath = Path.Combine(root, "temp", "sessions", id);
            var manifestPath = Path.Combine(tempPath, "sources.json");
            var outputPath = Path.Combine(tempPath, "output.wav");
            var micPath = Path.Combine(tempPath, "mic.wav");
            var stagedPath = Path.Combine(root, "recordings", $"{id}.mp3.partial");
            var primaryPath = Path.Combine(root, "recordings", $"{id}.mp3");

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(sessionId, createdAt, "manual", "mixed", "out-1", "mic-1") with
                {
                    TempSessionPath = tempPath,
                    AudioOutputPath = outputPath,
                    ArtifactProgress = 0.05
                },
                CancellationToken.None);

            Assert.True(repository.TryTransitionArtifactStage(
                id,
                ["recording"],
                "stopping",
                0.1,
                createdAt.AddMinutes(1)));
            Assert.False(repository.TryTransitionArtifactStage(
                id,
                ["recording"],
                "stopping",
                0.1,
                createdAt.AddMinutes(1)));

            Assert.True(repository.TryCheckpointArtifact(
                id,
                ["stopping"],
                new MeetingSessionArtifactCheckpoint(
                    Status: "processing",
                    TempSessionPath: tempPath,
                    SourceManifestPath: manifestPath,
                    StagedPrimaryPath: stagedPath,
                    AudioOutputPath: outputPath,
                    AudioMicPath: micPath,
                    AudioMixPath: null,
                    DurationSeconds: 61.5,
                    ArtifactProgress: 0.35),
                createdAt.AddMinutes(1)));

            var processing = Assert.Single(repository.ListRecoverableArtifactWork());
            Assert.Equal("processing", processing.Status);
            Assert.Equal(tempPath, processing.TempSessionPath);
            Assert.Equal(manifestPath, processing.SourceManifestPath);
            Assert.Equal(stagedPath, processing.StagedPrimaryPath);
            Assert.Equal(outputPath, processing.AudioOutputPath);
            Assert.Equal(micPath, processing.AudioMicPath);
            Assert.Equal(61.5, processing.DurationSeconds);
            Assert.Equal(0.35, processing.ArtifactProgress);

            Assert.True(repository.TryTransitionArtifactStage(
                id,
                ["processing"],
                "verifying",
                0.8,
                createdAt.AddMinutes(2)));
            Assert.True(repository.TryTransitionArtifactStage(
                id,
                ["verifying"],
                "promoting",
                0.9,
                createdAt.AddMinutes(2)));
            Assert.True(repository.TryMarkArtifactReady(
                id,
                ["promoting"],
                primaryPath,
                createdAt.AddMinutes(2)));
            Assert.False(repository.TryMarkArtifactReady(
                id,
                ["promoting"],
                primaryPath,
                createdAt.AddMinutes(2)));

            var cleanup = Assert.Single(repository.ListRecoverableArtifactWork());
            Assert.Equal("ready", cleanup.Status);
            Assert.Equal(primaryPath, cleanup.PrimaryAudioPath);
            Assert.True(cleanup.SourceCleanupPending);

            var recent = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal(primaryPath, recent.PrimaryAudioPath);
            Assert.Equal(1, recent.ArtifactProgress);
            Assert.True(recent.SourceCleanupPending);

            Assert.True(repository.TryCompleteSourceCleanup(id, createdAt.AddMinutes(3)));
            Assert.False(repository.TryCompleteSourceCleanup(id, createdAt.AddMinutes(3)));
            Assert.Empty(repository.ListRecoverableArtifactWork());

            await using var command = database.CreateCommand();
            command.CommandText =
                """
                SELECT
                    status,
                    primary_audio_path,
                    temp_session_path,
                    source_manifest_path,
                    staged_primary_path,
                    audio_output_path,
                    audio_mic_path,
                    artifact_progress,
                    source_cleanup_pending,
                    transcription_status
                FROM meeting_session
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("ready", reader.GetString(0));
            Assert.Equal(primaryPath, reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            Assert.True(reader.IsDBNull(5));
            Assert.True(reader.IsDBNull(6));
            Assert.Equal(1, reader.GetDouble(7));
            Assert.Equal(0L, reader.GetInt64(8));
            Assert.Equal("not_started", reader.GetString(9));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AttentionTransitionPreservesReadableSourcesAndDoesNotRemainQueuedForRecovery()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var sessionId = Guid.NewGuid();
            var id = sessionId.ToString("N");
            var createdAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
            var tempPath = Path.Combine(root, "temp", "sessions", id);
            var outputPath = Path.Combine(tempPath, "output.wav");

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(sessionId, createdAt, "ask", "process", "out-1", "mic-1") with
                {
                    Status = "processing",
                    TempSessionPath = tempPath,
                    AudioOutputPath = outputPath,
                    ArtifactProgress = 0.4
                },
                CancellationToken.None);

            Assert.True(repository.TryMarkArtifactAttentionRequired(
                id,
                ["processing"],
                "encoder_unavailable",
                "MP3 encoder is unavailable.",
                createdAt.AddMinutes(1),
                artifactProgress: 0.45));
            Assert.False(repository.TryMarkArtifactAttentionRequired(
                id,
                ["processing"],
                "encoder_unavailable",
                "MP3 encoder is unavailable.",
                createdAt.AddMinutes(1)));

            Assert.Empty(repository.ListRecoverableArtifactWork());
            var recent = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("attention_required", recent.RecordingStatus);
            Assert.Equal(tempPath, recent.TempSessionPath);
            Assert.Equal(outputPath, recent.AudioOutputPath);
            Assert.Equal("encoder_unavailable", recent.ArtifactErrorCode);
            Assert.Equal("MP3 encoder is unavailable.", recent.ArtifactErrorMessage);
            Assert.Equal(0.45, recent.ArtifactProgress);
            Assert.False(recent.SourceCleanupPending);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryQueueTranscriptionRetry_QueuesOnlyFailedSessionsWithArtifacts()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var createdAt = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
            var retryableSessionId = Guid.NewGuid();

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(retryableSessionId, createdAt, "manual", "mixed", "out-1", "mic-1") with
                {
                    Status = "saved",
                    TranscriptionStatus = "failed",
                    AudioOutputPath = @"C:\Recordings\output.wav",
                    RetryAttemptCount = 3,
                    ErrorCode = "TX_TIMEOUT"
                },
                CancellationToken.None);

            var queued = repository.TryQueueTranscriptionRetry(retryableSessionId.ToString("N"), createdAt.AddMinutes(1));

            Assert.True(queued);

            await using var command = database.CreateCommand();
            command.CommandText = "SELECT transcription_status, retry_attempt_count, error_code FROM meeting_session WHERE id = $id;";
            command.Parameters.AddWithValue("$id", retryableSessionId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("queued", reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.True(reader.IsDBNull(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TryClaimNextQueuedTranscription_ClaimsOldestQueuedSession()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var baseTime = new DateTimeOffset(2026, 4, 11, 10, 0, 0, TimeSpan.Zero);

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(Guid.NewGuid(), baseTime, "manual", "mixed", "out-1", "mic-1") with
                {
                    Status = "saved",
                    SourceApp = "First",
                    TranscriptionStatus = "queued",
                    QueuedAtUtc = baseTime,
                    AudioOutputPath = @"C:\Recordings\first.wav"
                },
                CancellationToken.None);

            await repository.UpsertAsync(
                MeetingSessionRecord.Create(Guid.NewGuid(), baseTime.AddMinutes(1), "manual", "mixed", "out-1", "mic-1") with
                {
                    Status = "saved",
                    SourceApp = "Second",
                    TranscriptionStatus = "queued",
                    QueuedAtUtc = baseTime.AddMinutes(1),
                    AudioOutputPath = @"C:\Recordings\second.wav"
                },
                CancellationToken.None);

            var claimed = repository.TryClaimNextQueuedTranscription(baseTime.AddMinutes(2));

            Assert.NotNull(claimed);
            Assert.Equal("First", claimed!.SourceApp);

            await using var command = database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM meeting_session WHERE transcription_status = 'uploading';";
            var uploadingCount = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1, uploadingCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    [Fact]
    public async Task LegacyInactiveAndFailedStatusesCannotBeRetriedClaimedOrPromoted()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var createdAt = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
            var failedId = Guid.NewGuid();
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(failedId, createdAt, "ask", "mixed", "out", "mic") with
                {
                    Status = "saved",
                    TranscriptionStatus = "legacy_failed",
                    AudioOutputPath = "legacy-output.wav",
                    ErrorCode = "TX_LEGACY"
                },
                CancellationToken.None);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    Guid.NewGuid(),
                    createdAt.AddMinutes(1),
                    "ask",
                    "mixed",
                    "out",
                    "mic") with
                {
                    Status = "saved",
                    TranscriptionStatus = "legacy_inactive",
                    AudioOutputPath = "legacy-inactive-output.wav",
                    NextRetryAtUtc = createdAt.AddMinutes(-1)
                },
                CancellationToken.None);

            Assert.False(repository.TryQueueTranscriptionRetry(
                failedId.ToString("N"),
                createdAt.AddMinutes(2)));
            Assert.Null(repository.TryClaimNextQueuedTranscription(createdAt.AddMinutes(2)));
            Assert.Equal(0, repository.PromoteDueScheduledRetries(createdAt.AddMinutes(2)));

            await using var command = database.CreateCommand();
            command.CommandText =
                """
                SELECT transcription_status
                FROM meeting_session
                ORDER BY transcription_status;
                """;
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("legacy_failed", reader.GetString(0));
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("legacy_inactive", reader.GetString(0));
            Assert.False(await reader.ReadAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public async Task RecentRemoval_HidesHistoryWithoutClearingFilePaths()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var sessionId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 7, 12, 17, 0, 0, TimeSpan.Zero);
            var primaryPath = Path.Combine(root, "recordings", "meeting.mp3");
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(sessionId, createdAt, "manual", "mixed", "out", "mic") with
                {
                    Status = "ready",
                    PrimaryAudioPath = primaryPath
                },
                CancellationToken.None);

            var removal = repository.TryBeginRecentRecordingRemoval(
                sessionId.ToString("N"),
                deleteAudioFile: false,
                createdAt.AddMinutes(1));

            Assert.NotNull(removal);
            Assert.Empty(repository.ListRecent(limit: 10, offset: 0));
            await using var command = database.CreateCommand();
            command.CommandText =
                "SELECT status, primary_audio_path FROM meeting_session WHERE id = $id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("history_removed", reader.GetString(0));
            Assert.Equal(primaryPath, reader.GetString(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
    [Fact]
    public async Task RecentFileRemoval_RetainsPathsUntilDeletionCheckpointCompletes()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var sessionId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero);
            var primaryPath = Path.Combine(root, "recordings", "meeting.mp3");
            var tempPath = Path.Combine(root, "temp", "sessions", sessionId.ToString("N"));
            var transcriptMarkdownPath = Path.Combine(root, "transcripts", "meeting.md");
            var transcriptJsonPath = Path.Combine(root, "transcripts", "meeting.json");
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(sessionId, createdAt, "manual", "mixed", "out", "mic") with
                {
                    Status = "ready",
                    PrimaryAudioPath = primaryPath,
                    TempSessionPath = tempPath,
                    SourceCleanupPending = true,
                    TranscriptMarkdownPath = transcriptMarkdownPath,
                    TranscriptJsonPath = transcriptJsonPath,
                    TranscriptionStatus = "completed",
                    QueuedAtUtc = createdAt.AddMinutes(-5),
                    RetryAttemptCount = 2,
                    NextRetryAtUtc = createdAt.AddMinutes(30),
                    LastRetryAtUtc = createdAt.AddMinutes(-1),
                    TranscriptionModel = "legacy-model",
                    DiarizationEnabled = false,
                    Language = "ru",
                    ErrorCode = "TX_HISTORY",
                    ErrorMessage = "Retained legacy detail."
                },
                CancellationToken.None);

            var pending = repository.TryBeginRecentRecordingRemoval(
                sessionId.ToString("N"),
                deleteAudioFile: true,
                createdAt.AddMinutes(1));

            Assert.NotNull(pending);
            Assert.Equal(primaryPath, pending.PrimaryAudioPath);
            var pendingRow = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("file_deletion_pending", pendingRow.RecordingStatus);
            Assert.Equal(primaryPath, pendingRow.PrimaryAudioPath);

            Assert.True(repository.TryMarkRecentRecordingRemovalFailed(
                sessionId.ToString("N"),
                "File is in use.",
                createdAt.AddMinutes(2)));
            var failedRow = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("recording_removal_failed", failedRow.ArtifactErrorCode);
            Assert.Equal(primaryPath, failedRow.PrimaryAudioPath);

            Assert.NotNull(repository.TryBeginRecentRecordingRemoval(
                sessionId.ToString("N"),
                deleteAudioFile: true,
                createdAt.AddMinutes(3)));
            Assert.True(repository.TryCompleteRecentRecordingRemoval(
                sessionId.ToString("N"),
                createdAt.AddMinutes(4)));
            Assert.Empty(repository.ListRecent(limit: 10, offset: 0));

            await using var command = database.CreateCommand();
            command.CommandText =
                """
                SELECT status, primary_audio_path, temp_session_path, source_cleanup_pending
                     , transcription_status, transcript_md_path, transcript_json_path
                     , queued_at, retry_attempt_count, next_retry_at, last_retry_at
                     , transcription_model, diarization_enabled, language, error_code, error_message
                FROM meeting_session
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("history_removed", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal(0L, reader.GetInt64(3));
            Assert.Equal("completed", reader.GetString(4));
            Assert.Equal(transcriptMarkdownPath, reader.GetString(5));
            Assert.Equal(transcriptJsonPath, reader.GetString(6));
            Assert.Equal(createdAt.AddMinutes(-5).ToString("O"), reader.GetString(7));
            Assert.Equal(2, reader.GetInt32(8));
            Assert.Equal(createdAt.AddMinutes(30).ToString("O"), reader.GetString(9));
            Assert.Equal(createdAt.AddMinutes(-1).ToString("O"), reader.GetString(10));
            Assert.Equal("legacy-model", reader.GetString(11));
            Assert.Equal(0L, reader.GetInt64(12));
            Assert.Equal("ru", reader.GetString(13));
            Assert.Equal("TX_HISTORY", reader.GetString(14));
            Assert.Equal("Retained legacy detail.", reader.GetString(15));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
    [Fact]
    public async Task ActiveDiscard_CheckpointsIntentBeforeClearingRecoverablePaths()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(database);
            var sessionId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 7, 12, 19, 0, 0, TimeSpan.Zero);
            var tempPath = Path.Combine(root, "temp", "sessions", sessionId.ToString("N"));
            var outputPath = Path.Combine(tempPath, "output.wav");
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(sessionId, createdAt, "manual", "mixed", "out", "mic") with
                {
                    TempSessionPath = tempPath,
                    AudioOutputPath = outputPath
                },
                CancellationToken.None);

            Assert.True(repository.TryBeginActiveDiscard(
                sessionId.ToString("N"),
                createdAt.AddMinutes(1),
                durationSeconds: 60));
            var pending = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("discarding", pending.RecordingStatus);
            Assert.Equal(tempPath, pending.TempSessionPath);
            Assert.Equal(outputPath, pending.AudioOutputPath);

            Assert.True(repository.TryCompleteActiveDiscard(
                sessionId.ToString("N"),
                createdAt.AddMinutes(1)));
            var discarded = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("discarded", discarded.RecordingStatus);
            Assert.Null(discarded.TempSessionPath);
            Assert.Null(discarded.AudioOutputPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
