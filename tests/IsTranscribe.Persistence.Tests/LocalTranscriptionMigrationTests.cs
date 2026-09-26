using System.Globalization;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IsTranscribe.Persistence.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
/// </summary>
public sealed class LocalTranscriptionMigrationTests
{
    [Fact]
    public async Task MigrationEightPreservesRemoteJobsAndAddsManualTriggerDefault()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await CreateVersionSevenDatabaseAsync(paths);
            await using (var before = new SqliteConnection($"Data Source={paths.DatabaseFilePath};Pooling=False"))
            {
                await before.OpenAsync(CancellationToken.None);
                await InsertSessionAsync(before, "remote-session");
                await using var insert = before.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO transcription_job (
                        id, session_id, engine_id, execution_kind, model_id,
                        input_audio_path, input_sha256, input_size_bytes, input_duration_seconds,
                        status, progress, queued_at, created_at, updated_at,
                        remote_consent_revision, remote_consent_at)
                    VALUES (
                        'remote-job', 'remote-session', 'remote.groq', 'remote', 'whisper-large-v3-turbo',
                        '/recordings/remote.mp3', @sha, 42, 1.0,
                        'queued', 0, @now, @now, @now,
                        'feat015-v1', @now);
                    """;
                insert.Parameters.AddWithValue("@sha", new string('a', 64));
                insert.Parameters.AddWithValue("@now", "2026-08-11T12:00:00.0000000+00:00");
                await insert.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT engine_id, status, trigger_kind, supersedes_job_id
                FROM transcription_job
                WHERE id = 'remote-job';
                """;
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("remote.groq", reader.GetString(0));
            Assert.Equal("queued", reader.GetString(1));
            Assert.Equal("manual", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
            await reader.DisposeAsync();

            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(8L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalIdentityAndAttemptRowsEnforceChecksAndCascadeWithJob()
    {
        var root = CreateRoot();
        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            await InsertSessionAsync(connection, "local-session");

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO transcription_job (
                        id, session_id, engine_id, execution_kind, model_id,
                        input_audio_path, input_sha256, input_size_bytes, input_duration_seconds,
                        status, progress, queued_at, created_at, updated_at, trigger_kind)
                    VALUES (
                        'local-job', 'local-session', 'local.whisper', 'local', 'small',
                        '/recordings/local.mp3', @input_sha, 42, 1.0,
                        'queued', 0, @now, @now, @now, 'automatic');

                    INSERT INTO transcription_chunk (
                        id, job_id, sequence_index, start_milliseconds, end_milliseconds,
                        created_at, updated_at)
                    VALUES ('chunk-0', 'local-job', 0, 0, 1000, @now, @now);

                    INSERT INTO transcription_local_job (
                        job_id, model_catalog_version, model_catalog_revision,
                        model_format, model_path, model_size_bytes, model_sha256,
                        runtime_version, runtime_commit, runtime_source_archive_sha256,
                        native_bundle_manifest_sha256, bridge_abi_version, worker_protocol_version,
                        requested_backend, resolved_backend, backend_history_json,
                        thread_count, inference_parameters_json, chunk_profile_version,
                        run_identity_sha256, native_crash_count, created_at, updated_at)
                    VALUES (
                        'local-job', 1, '5359861c739e955e79d9a303bcbc70fb988958b1',
                        'whisper.cpp.ggml-f16.v1', '/models/small.ggml.bin', 487601967, @model_sha,
                        '1.9.1', 'f049fff95a089aa9969deb009cdd4892b3e74916', @source_sha,
                        @bundle_sha, 1, 1,
                        'auto', 'cpu', '["cpu"]',
                        4, '{"temperature":0,"language":"auto"}', 1,
                        @run_sha, 0, @now, @now);

                    INSERT INTO transcription_local_chunk_attempt (
                        job_id, chunk_id, attempt_index, requested_backend, resolved_backend,
                        worker_started_at, worker_ended_at, exit_category,
                        decode_duration_milliseconds, inference_duration_milliseconds,
                        peak_working_set_bytes, created_at)
                    VALUES (
                        'local-job', 'chunk-0', 0, 'auto', 'cpu',
                        @now, @now, 'completed', 12, 34, 1048576, @now);
                    """;
                insert.Parameters.AddWithValue("@input_sha", new string('a', 64));
                insert.Parameters.AddWithValue("@model_sha", new string('b', 64));
                insert.Parameters.AddWithValue("@source_sha", new string('c', 64));
                insert.Parameters.AddWithValue("@bundle_sha", new string('d', 64));
                insert.Parameters.AddWithValue("@run_sha", new string('e', 64));
                insert.Parameters.AddWithValue("@now", "2026-08-11T12:00:00.0000000+00:00");
                Assert.Equal(4, await insert.ExecuteNonQueryAsync(CancellationToken.None));
            }

            await using (var invalid = connection.CreateCommand())
            {
                invalid.CommandText =
                    """
                    UPDATE transcription_local_job
                    SET requested_backend = 'cuda'
                    WHERE job_id = 'local-job';
                    """;
                await Assert.ThrowsAsync<SqliteException>(
                    () => invalid.ExecuteNonQueryAsync(CancellationToken.None));
            }

            await using (var count = connection.CreateCommand())
            {
                count.CommandText =
                    """
                    SELECT
                        (SELECT COUNT(*) FROM transcription_local_job),
                        (SELECT COUNT(*) FROM transcription_local_chunk_attempt);
                    """;
                await using var reader = await count.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM transcription_job WHERE id = 'local-job';";
                Assert.Equal(1, await delete.ExecuteNonQueryAsync(CancellationToken.None));
                delete.CommandText =
                    """
                    SELECT
                        (SELECT COUNT(*) FROM transcription_local_job),
                        (SELECT COUNT(*) FROM transcription_local_chunk_attempt);
                    """;
                await using var reader = await delete.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.Equal(0L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-local-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task InsertSessionAsync(SqliteConnection connection, string sessionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO meeting_session (id, created_at, status, mode, source_type)
            VALUES (@id, @created_at, 'saved', 'manual', 'mixed');
            """;
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@created_at", "2026-08-11T12:00:00.0000000+00:00");
        Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async Task CreateVersionSevenDatabaseAsync(LocalAppPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabaseFilePath};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(CancellationToken.None);
        }

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
            .Where(static migration => migration.Version <= 7)
            .OrderBy(static migration => migration.Version)
            .ToArray();
        Assert.Equal(7, migrations.Length);

        foreach (var migration in migrations)
        {
            await using var stream = assembly.GetManifestResourceStream(migration.ResourceName)
                ?? throw new InvalidOperationException(
                    $"Migration resource '{migration.ResourceName}' was not found.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            command.CommandText = $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static int ParseMigrationVersion(string resourceName)
    {
        var lastDot = resourceName.LastIndexOf('.');
        var previousDot = resourceName.LastIndexOf('.', lastDot - 1);
        var fileName = resourceName[(previousDot + 1)..lastDot];
        var separator = fileName.IndexOf('_');
        return int.Parse(fileName[..separator], CultureInfo.InvariantCulture);
    }
}
