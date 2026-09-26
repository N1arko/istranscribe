using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Core.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Persistence.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#target-structure
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#migration
/// @spec spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#verification
/// </summary>
public sealed class PersistenceBoundaryTests
{
    [Fact]
    public void PersistenceAssemblyHasNoWindowsUiOrAudioImplementationDependency()
    {
        var assembly = typeof(LocalAppPaths).Assembly;
        var references = assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Equal("IsTranscribe.Persistence", assembly.GetName().Name);
        Assert.DoesNotContain("NAudio", references);
        Assert.DoesNotContain("System.Security.Cryptography.ProtectedData", references);
        Assert.DoesNotContain("System.Windows.Forms", references);
        Assert.DoesNotContain(references, static name => name.StartsWith("Presentation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmbeddedMigrationsInitializeTheCurrentDatabaseSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";

            Assert.Equal(8L, (long)(await command.ExecuteScalarAsync())!);

            command.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('meeting_session')
                WHERE name = 'display_title';
                """;
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

            command.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('meeting_session')
                WHERE name = 'current_transcription_job_id';
                """;
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

            command.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                      'transcription_job',
                      'transcription_chunk',
                      'transcription_local_job',
                      'transcription_local_chunk_attempt');
                """;
            Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);

            command.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('transcription_job')
                WHERE name IN ('trigger_kind', 'supersedes_job_id');
                """;
            Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);

            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationFourBackfillsLegacyPrimaryAudioWithoutChangingSourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-persistence-v3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await CreateVersionThreeDatabaseAsync(paths);

            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    id,
                    status,
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
                    artifact_error_message
                FROM meeting_session
                ORDER BY id;
                """;

            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("legacy-mix", reader.GetString(0));
            Assert.Equal("saved", reader.GetString(1));
            Assert.Equal("output.wav", reader.GetString(2));
            Assert.Equal("mic.wav", reader.GetString(3));
            Assert.Equal("mix.ogg", reader.GetString(4));
            Assert.Equal("mix.ogg", reader.GetString(5));
            Assert.True(reader.IsDBNull(6));
            Assert.True(reader.IsDBNull(7));
            Assert.True(reader.IsDBNull(8));
            Assert.Equal(0d, reader.GetDouble(9));
            Assert.Equal(0L, reader.GetInt64(10));
            Assert.True(reader.IsDBNull(11));
            Assert.True(reader.IsDBNull(12));

            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("legacy-output", reader.GetString(0));
            Assert.Equal("output-only.wav", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            Assert.Equal("output-only.wav", reader.GetString(5));

            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("legacy-z-mic", reader.GetString(0));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal("mic-only.wav", reader.GetString(3));
            Assert.True(reader.IsDBNull(4));
            Assert.Equal("mic-only.wav", reader.GetString(5));
            Assert.False(await reader.ReadAsync(CancellationToken.None));

            await reader.DisposeAsync();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(8L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
    [Fact]
    public async Task MigrationFiveDeactivatesEveryLegacyStatusWithoutChangingArtifactsOrMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-persistence-v4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await CreateVersionFourDatabaseWithLegacyTranscriptionRowsAsync(paths);

            IReadOnlyList<LegacyTranscriptionRow> migratedRows;
            await using (var connection = await new SqliteDatabaseInitializer(paths)
                             .InitializeAsync(CancellationToken.None))
            {
                migratedRows = await ReadLegacyTranscriptionRowsAsync(connection);
                await using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(8L, (long)(await version.ExecuteScalarAsync(CancellationToken.None))!);
            }

            var expectedStatuses = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["failed"] = "legacy_failed",
                ["not_started"] = "not_started",
                ["completed"] = "completed",
                ["processing"] = "legacy_inactive",
                ["queued"] = "legacy_inactive",
                ["retry_scheduled"] = "legacy_inactive",
                ["uploading"] = "legacy_inactive"
            };
            Assert.Equal(expectedStatuses.Count, migratedRows.Count);
            foreach (var row in migratedRows)
            {
                var originalStatus = row.SourceApp["tx:".Length..];
                Assert.Equal(expectedStatuses[originalStatus], row.TranscriptionStatus);
                Assert.Equal($"output-{originalStatus}.wav", row.AudioOutputPath);
                Assert.Equal($"mic-{originalStatus}.wav", row.AudioMicPath);
                Assert.Equal($"mix-{originalStatus}.ogg", row.AudioMixPath);
                Assert.Equal($"primary-{originalStatus}.mp3", row.PrimaryAudioPath);
                Assert.Equal($"transcript-{originalStatus}.md", row.TranscriptMarkdownPath);
                Assert.Equal($"transcript-{originalStatus}.json", row.TranscriptJsonPath);
                Assert.Equal("2026-07-12T09:00:00.0000000+00:00", row.QueuedAt);
                Assert.Equal("2026-07-12T09:05:00.0000000+00:00", row.UpdatedAt);
                Assert.Equal(4, row.RetryAttemptCount);
                Assert.Equal("2026-07-12T09:30:00.0000000+00:00", row.NextRetryAt);
                Assert.Equal("2026-07-12T09:04:00.0000000+00:00", row.LastRetryAt);
                Assert.Equal("legacy-model", row.TranscriptionModel);
                Assert.False(row.DiarizationEnabled);
                Assert.Equal("ru", row.Language);
                Assert.Equal("TX_LEGACY", row.ErrorCode);
                Assert.Equal("Legacy transcription detail.", row.ErrorMessage);
                Assert.Equal("artifact-note", row.ArtifactErrorCode);
                Assert.Equal("Artifact detail.", row.ArtifactErrorMessage);
            }

            await using var repeatedConnection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repeatedRows = await ReadLegacyTranscriptionRowsAsync(repeatedConnection);
            Assert.Equal(migratedRows, repeatedRows);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public async Task MigrationSixPreservesLegacySessionMetadataWithAnEmptyDisplayTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-persistence-v5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await CreateVersionFiveDatabaseWithLegacyTranscriptionRowsAsync(paths);

            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT source_app, primary_audio_path, display_title
                FROM meeting_session
                WHERE source_app = 'tx:completed';
                """;

            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("tx:completed", reader.GetString(0));
            Assert.Equal("primary-completed.mp3", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.False(await reader.ReadAsync(CancellationToken.None));

            await reader.DisposeAsync();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(8L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
    [Fact]
    public async Task RecentRecordingRenameIsDurableAndIndependentFromSourceAndArtifactMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-persistence-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var repository = new MeetingSessionRepository(connection);
            var sessionId = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
            var primaryPath = Path.Combine(root, "recordings", "meeting.mp3");
            var session = MeetingSessionRecord.Create(
                sessionId,
                createdAt,
                "manual",
                "mixed",
                "output-id",
                "microphone-id") with
            {
                Status = "saved",
                SourceApp = "Zoom",
                PrimaryAudioPath = primaryPath,
                EndedAtUtc = createdAt.AddMinutes(25),
                DurationSeconds = 1500
            };
            await repository.UpsertAsync(session, CancellationToken.None);

            Assert.True(repository.TryRenameRecentRecording(
                session.Id,
                "  Weekly product sync  ",
                createdAt.AddMinutes(26)));

            var renamed = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("Weekly product sync", renamed.DisplayTitle);
            Assert.Equal("Zoom", renamed.SourceApp);
            Assert.Equal(primaryPath, renamed.PrimaryAudioPath);

            await repository.UpsertAsync(
                session with
                {
                    Status = "ready",
                    SourceApp = "Zoom Meetings",
                    UpdatedAtUtc = createdAt.AddMinutes(27)
                },
                CancellationToken.None);

            var refreshed = Assert.Single(repository.ListRecent(limit: 10, offset: 0));
            Assert.Equal("Weekly product sync", refreshed.DisplayTitle);
            Assert.Equal("Zoom Meetings", refreshed.SourceApp);
            Assert.Equal(primaryPath, refreshed.PrimaryAudioPath);

            Assert.Throws<ArgumentException>(() => repository.TryRenameRecentRecording(
                session.Id,
                "   ",
                createdAt));
            Assert.Throws<ArgumentOutOfRangeException>(() => repository.TryRenameRecentRecording(
                session.Id,
                new string('x', RecentRecordingTitle.MaxLength + 1),
                createdAt));
            Assert.Throws<ArgumentException>(() => repository.TryRenameRecentRecording(
                session.Id,
                "Line one\nLine two",
                createdAt));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CreateVersionThreeDatabaseAsync(LocalAppPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabaseFilePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);

        var assembly = typeof(SqliteDatabaseInitializer).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(static resourceName =>
                resourceName.Contains(".Persistence.Migrations.", StringComparison.Ordinal)
                && resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(resourceName => new
            {
                ResourceName = resourceName,
                Version = ParseMigrationVersion(resourceName)
            })
            .Where(static migration => migration.Version <= 3)
            .OrderBy(static migration => migration.Version)
            .ToArray();
        Assert.Equal(3, migrations.Length);

        foreach (var migration in migrations)
        {
            await using var stream = assembly.GetManifestResourceStream(migration.ResourceName)
                ?? throw new InvalidOperationException($"Migration resource '{migration.ResourceName}' was not found.");
            using var textReader = new StreamReader(stream);
            var sql = await textReader.ReadToEndAsync(CancellationToken.None);

            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.CommandText = sql;
            await migrationCommand.ExecuteNonQueryAsync(CancellationToken.None);

            migrationCommand.CommandText = $"PRAGMA user_version = {migration.Version};";
            await migrationCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO meeting_session (
                id, created_at, status, mode, source_type,
                audio_output_path, audio_mic_path, audio_mix_path)
            VALUES
                ('legacy-mix', '2026-07-01T10:00:00.0000000+00:00', 'saved', 'ask', 'mixed',
                 'output.wav', 'mic.wav', 'mix.ogg'),
                ('legacy-output', '2026-07-02T10:00:00.0000000+00:00', 'saved', 'manual', 'device',
                 'output-only.wav', NULL, NULL),
                ('legacy-z-mic', '2026-07-03T10:00:00.0000000+00:00', 'saved', 'manual', 'mic',
                 NULL, 'mic-only.wav', NULL);
            """;
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task CreateVersionFourDatabaseWithLegacyTranscriptionRowsAsync(
        LocalAppPaths paths)
    {
        await CreateVersionThreeDatabaseAsync(paths);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabaseFilePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);

        var assembly = typeof(SqliteDatabaseInitializer).Assembly;
        var migrationResource = Assert.Single(
            assembly.GetManifestResourceNames(),
            resourceName => resourceName.Contains(
                                ".Persistence.Migrations.004_",
                                StringComparison.Ordinal)
                            && resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
        await using (var stream = assembly.GetManifestResourceStream(migrationResource)
                                  ?? throw new InvalidOperationException(
                                      $"Migration resource '{migrationResource}' was not found."))
        using (var textReader = new StreamReader(stream))
        await using (var migration = connection.CreateCommand())
        {
            migration.CommandText = await textReader.ReadToEndAsync(CancellationToken.None);
            await migration.ExecuteNonQueryAsync(CancellationToken.None);
            migration.CommandText = "PRAGMA user_version = 4;";
            await migration.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var repository = new MeetingSessionRepository(connection);
        var statuses = new[]
        {
            "queued",
            "uploading",
            "processing",
            "retry_scheduled",
            "failed",
            "completed",
            "not_started"
        };
        var createdAt = new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero);
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
                    SourceApp = $"tx:{status}",
                    AudioOutputPath = $"output-{status}.wav",
                    AudioMicPath = $"mic-{status}.wav",
                    AudioMixPath = $"mix-{status}.ogg",
                    PrimaryAudioPath = $"primary-{status}.mp3",
                    TranscriptMarkdownPath = $"transcript-{status}.md",
                    TranscriptJsonPath = $"transcript-{status}.json",
                    TranscriptionStatus = status,
                    QueuedAtUtc = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero),
                    UpdatedAtUtc = new DateTimeOffset(2026, 7, 12, 9, 5, 0, TimeSpan.Zero),
                    RetryAttemptCount = 4,
                    NextRetryAtUtc = new DateTimeOffset(2026, 7, 12, 9, 30, 0, TimeSpan.Zero),
                    LastRetryAtUtc = new DateTimeOffset(2026, 7, 12, 9, 4, 0, TimeSpan.Zero),
                    TranscriptionModel = "legacy-model",
                    DiarizationEnabled = false,
                    Language = "ru",
                    ErrorCode = "TX_LEGACY",
                    ErrorMessage = "Legacy transcription detail.",
                    ArtifactErrorCode = "artifact-note",
                    ArtifactErrorMessage = "Artifact detail."
                },
                CancellationToken.None);
        }
    }

    private static async Task CreateVersionFiveDatabaseWithLegacyTranscriptionRowsAsync(
        LocalAppPaths paths)
    {
        await CreateVersionFourDatabaseWithLegacyTranscriptionRowsAsync(paths);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabaseFilePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);

        var assembly = typeof(SqliteDatabaseInitializer).Assembly;
        var migrationResource = Assert.Single(
            assembly.GetManifestResourceNames(),
            resourceName => resourceName.Contains(
                                ".Persistence.Migrations.005_",
                                StringComparison.Ordinal)
                            && resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
        await using var stream = assembly.GetManifestResourceStream(migrationResource)
            ?? throw new InvalidOperationException(
                $"Migration resource '{migrationResource}' was not found.");
        using var textReader = new StreamReader(stream);
        await using var migration = connection.CreateCommand();
        migration.CommandText = await textReader.ReadToEndAsync(CancellationToken.None);
        await migration.ExecuteNonQueryAsync(CancellationToken.None);
        migration.CommandText = "PRAGMA user_version = 5;";
        await migration.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<LegacyTranscriptionRow>> ReadLegacyTranscriptionRowsAsync(
        SqliteConnection connection)
    {
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
                error_message,
                artifact_error_code,
                artifact_error_message
            FROM meeting_session
            WHERE source_app LIKE 'tx:%'
            ORDER BY source_app;
            """;

        var rows = new List<LegacyTranscriptionRow>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add(new LegacyTranscriptionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetInt64(14) != 0,
                reader.GetString(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetString(19)));
        }

        return rows;
    }

    private static int ParseMigrationVersion(string resourceName)
    {
        var nameWithoutExtension = resourceName[..^".sql".Length];
        var fileName = nameWithoutExtension[(nameWithoutExtension.LastIndexOf('.') + 1)..];
        var separatorIndex = fileName.IndexOf('_');
        return int.Parse(fileName[..separatorIndex], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record LegacyTranscriptionRow(
        string SourceApp,
        string TranscriptionStatus,
        string AudioOutputPath,
        string AudioMicPath,
        string AudioMixPath,
        string PrimaryAudioPath,
        string TranscriptMarkdownPath,
        string TranscriptJsonPath,
        string QueuedAt,
        string UpdatedAt,
        int RetryAttemptCount,
        string NextRetryAt,
        string LastRetryAt,
        string TranscriptionModel,
        bool DiarizationEnabled,
        string Language,
        string ErrorCode,
        string ErrorMessage,
        string ArtifactErrorCode,
        string ArtifactErrorMessage);
}
