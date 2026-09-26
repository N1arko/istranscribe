using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Application.Recovery;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class SqliteRecoveryCoordinatorTests
{
    [Fact]
    public async Task EvaluateAsync_NormalizesUncleanStatesAndMarksMissingArtifacts()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var logger = new BootstrapFileLogger(Path.Combine(root, "logs", "host.log"));
            var createdAt = new DateTimeOffset(2026, 4, 13, 10, 0, 0, TimeSpan.Zero);
            var outputArtifactPath = Path.Combine(root, "temp", "session-output.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(outputArtifactPath)!);
            await File.WriteAllBytesAsync(outputArtifactPath, [1, 2, 3, 4], CancellationToken.None);

            var interruptedSessionId = Guid.NewGuid();
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    interruptedSessionId,
                    createdAt,
                    mode: "auto",
                    sourceType: "process",
                    outputDeviceId: "out-1",
                    microphoneDeviceId: "mic-1") with
                {
                    Status = "recording",
                    TranscriptionStatus = "uploading",
                    AudioOutputPath = outputArtifactPath
                },
                CancellationToken.None);

            var recoverableV2SessionId = Guid.NewGuid();
            var recoverableV2Temp = Path.Combine(root, "temp", recoverableV2SessionId.ToString("N"));
            var recoverableV2Output = Path.Combine(recoverableV2Temp, "output.wav");
            Directory.CreateDirectory(recoverableV2Temp);
            await File.WriteAllBytesAsync(recoverableV2Output, [1, 2, 3, 4], CancellationToken.None);
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    recoverableV2SessionId,
                    createdAt,
                    mode: "ask",
                    sourceType: "process_output",
                    outputDeviceId: null,
                    microphoneDeviceId: null) with
                {
                    Status = "recording",
                    TempSessionPath = recoverableV2Temp,
                    AudioOutputPath = recoverableV2Output
                },
                CancellationToken.None);

            var missingArtifactSessionId = Guid.NewGuid();
            await repository.UpsertAsync(
                MeetingSessionRecord.Create(
                    missingArtifactSessionId,
                    createdAt,
                    mode: "auto",
                    sourceType: "process",
                    outputDeviceId: "out-1",
                    microphoneDeviceId: "mic-1") with
                {
                    Status = "saved",
                    TranscriptionStatus = "retry_scheduled",
                    NextRetryAtUtc = createdAt.AddMinutes(-1),
                    AudioOutputPath = @"C:\missing-output.wav"
                },
                CancellationToken.None);

            var coordinator = new SqliteRecoveryCoordinator(logger, () => connection);

            var decision = await coordinator.EvaluateAsync(CancellationToken.None);

            Assert.True(decision.RequiresAttention);

            var interrupted = await ReadSessionAsync(connection, interruptedSessionId.ToString("N"));
            Assert.Equal("failed", interrupted.status);
            Assert.Equal("legacy_inactive", interrupted.transcriptionStatus);
            Assert.Equal("RECOVERY_UNCLEAN_SHUTDOWN", interrupted.errorCode);
            Assert.Null(interrupted.artifactErrorCode);

            var recoverableV2 = await ReadSessionAsync(connection, recoverableV2SessionId.ToString("N"));
            Assert.Equal("stopping", recoverableV2.status);
            Assert.Equal("not_started", recoverableV2.transcriptionStatus);
            Assert.Null(recoverableV2.errorCode);
            Assert.Equal("artifact_recovery_pending", recoverableV2.artifactErrorCode);

            var missing = await ReadSessionAsync(connection, missingArtifactSessionId.ToString("N"));
            Assert.Equal("saved", missing.status);
            Assert.Equal("legacy_inactive", missing.transcriptionStatus);
            Assert.Null(missing.errorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    [Fact]
    public async Task EvaluateAsyncDeactivatesUnexpectedLegacyWorkSilentlyAndPreservesMetadata()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var createdAt = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);
            var statuses = new[] { "queued", "uploading", "processing", "retry_scheduled", "failed" };
            foreach (var status in statuses)
            {
                await repository.UpsertAsync(
                    MeetingSessionRecord.Create(
                        Guid.NewGuid(),
                        createdAt,
                        "ask",
                        "mixed",
                        "output-id",
                        "microphone-id") with
                    {
                        Status = "saved",
                        SourceApp = status,
                        AudioOutputPath = $"missing-{status}-output.wav",
                        AudioMicPath = $"missing-{status}-mic.wav",
                        AudioMixPath = $"missing-{status}-mix.ogg",
                        PrimaryAudioPath = $"missing-{status}-primary.mp3",
                        TranscriptMarkdownPath = $"transcript-{status}.md",
                        TranscriptJsonPath = $"transcript-{status}.json",
                        TranscriptionStatus = status,
                        QueuedAtUtc = createdAt.AddMinutes(1),
                        UpdatedAtUtc = createdAt.AddMinutes(2),
                        RetryAttemptCount = 3,
                        NextRetryAtUtc = createdAt.AddMinutes(30),
                        LastRetryAtUtc = createdAt.AddMinutes(2),
                        TranscriptionModel = "legacy-model",
                        DiarizationEnabled = false,
                        Language = "ru",
                        ErrorCode = "TX_LEGACY",
                        ErrorMessage = "Legacy failure."
                    },
                    CancellationToken.None);
            }

            var coordinator = new SqliteRecoveryCoordinator(
                new BootstrapFileLogger(Path.Combine(root, "logs", "host.log")),
                () => connection);

            var decision = await coordinator.EvaluateAsync(CancellationToken.None);

            Assert.False(decision.RequiresAttention);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    source_app,
                    transcription_status,
                    audio_output_path,
                    audio_mic_path,
                    audio_mix_path,
                    primary_audio_path,
                    transcript_md_path,
                    transcript_json_path,
                    queued_at,
                    updated_at,
                    retry_attempt_count,
                    next_retry_at,
                    last_retry_at,
                    transcription_model,
                    diarization_enabled,
                    language,
                    error_code,
                    error_message
                FROM meeting_session
                ORDER BY source_app;
                """;
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            var readCount = 0;
            while (await reader.ReadAsync(CancellationToken.None))
            {
                var originalStatus = reader.GetString(0);
                Assert.Equal(
                    originalStatus == "failed" ? "legacy_failed" : "legacy_inactive",
                    reader.GetString(1));
                Assert.Equal($"missing-{originalStatus}-output.wav", reader.GetString(2));
                Assert.Equal($"missing-{originalStatus}-mic.wav", reader.GetString(3));
                Assert.Equal($"missing-{originalStatus}-mix.ogg", reader.GetString(4));
                Assert.Equal($"missing-{originalStatus}-primary.mp3", reader.GetString(5));
                Assert.Equal($"transcript-{originalStatus}.md", reader.GetString(6));
                Assert.Equal($"transcript-{originalStatus}.json", reader.GetString(7));
                Assert.Equal(createdAt.AddMinutes(1).ToString("O"), reader.GetString(8));
                Assert.Equal(createdAt.AddMinutes(2).ToString("O"), reader.GetString(9));
                Assert.Equal(3, reader.GetInt32(10));
                Assert.Equal(createdAt.AddMinutes(30).ToString("O"), reader.GetString(11));
                Assert.Equal(createdAt.AddMinutes(2).ToString("O"), reader.GetString(12));
                Assert.Equal("legacy-model", reader.GetString(13));
                Assert.Equal(0L, reader.GetInt64(14));
                Assert.Equal("ru", reader.GetString(15));
                Assert.Equal("TX_LEGACY", reader.GetString(16));
                Assert.Equal("Legacy failure.", reader.GetString(17));
                readCount++;
            }

            Assert.Equal(statuses.Length, readCount);
            await reader.DisposeAsync();
            var repeatedDecision = await coordinator.EvaluateAsync(CancellationToken.None);
            Assert.False(repeatedDecision.RequiresAttention);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(
        string status,
        string transcriptionStatus,
        string? errorCode,
        string? artifactErrorCode)> ReadSessionAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, transcription_status, error_code, artifact_error_code
            FROM meeting_session
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
