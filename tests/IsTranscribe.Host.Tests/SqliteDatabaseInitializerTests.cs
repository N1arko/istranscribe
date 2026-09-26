using System.Globalization;
using Microsoft.Data.Sqlite;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
/// </remarks>
public sealed class SqliteDatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesSchemaAndAppliesUserVersion()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var initializer = new SqliteDatabaseInitializer(new LocalAppPaths("isTranscribe", root));
            var connection = await initializer.InitializeAsync(CancellationToken.None);

            try
            {
                Assert.Equal(7, await ReadPragmaIntAsync(connection, "user_version"));
                Assert.Equal(2, await CountTablesAsync(connection, "meeting_session", "app_rule"));
            }
            finally
            {
                connection.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_UsesWalJournalMode()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var initializer = new SqliteDatabaseInitializer(new LocalAppPaths("isTranscribe", root));
            var connection = await initializer.InitializeAsync(CancellationToken.None);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                var journalMode = Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

                Assert.Equal("wal", journalMode?.ToLowerInvariant());
            }
            finally
            {
                connection.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> CountTablesAsync(SqliteConnection connection, params string[] tableNames)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ($name0, $name1);
            """;
        command.Parameters.AddWithValue("$name0", tableNames[0]);
        command.Parameters.AddWithValue("$name1", tableNames[1]);
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadPragmaIntAsync(SqliteConnection connection, string pragmaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragmaName};";
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
