using System.Globalization;
using Microsoft.Data.Sqlite;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Persistence;

/// <summary>
/// Reads the immutable meeting context needed to render a transcript on an independent
/// connection owned by the transcription worker.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// </remarks>
public sealed class TranscriptionSessionContextReader
{
    private readonly string _connectionString;

    public TranscriptionSessionContextReader(LocalAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
    }

    public async ValueTask<TranscriptionSessionContext?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A meeting session id is required.", nameof(sessionId));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                COALESCE(NULLIF(TRIM(display_title), ''), NULLIF(TRIM(source_app), ''), 'Meeting'),
                started_at,
                duration_seconds,
                primary_audio_path,
                current_transcription_job_id,
                temp_session_path
            FROM meeting_session
            WHERE id = $session_id
              AND status IN ('ready', 'saved')
              AND user_discarded = 0;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TranscriptionSessionContext(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(
                reader.GetString(2),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            reader.IsDBNull(3) ? null : TimeSpan.FromSeconds(reader.GetDouble(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}

public sealed record TranscriptionSessionContext(
    string SessionId,
    string DisplayTitle,
    DateTimeOffset StartedAtUtc,
    TimeSpan? Duration,
    string? PrimaryAudioPath,
    string? CurrentTranscriptionJobId,
    string? TempSessionPath = null);
