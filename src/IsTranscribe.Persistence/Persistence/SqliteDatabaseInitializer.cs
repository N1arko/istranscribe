using System.Globalization;
using System.Reflection;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Persistence;

public sealed class SqliteDatabaseInitializer(LocalAppPaths paths)
{
    private readonly LocalAppPaths _paths = paths;

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#sqlite.rules
    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#sqlite.migrations
    public async ValueTask<SqliteConnection> InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.DataDirectory);

        var connection = new SqliteConnection($"Data Source={_paths.DatabaseFilePath};Pooling=False");

        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
            await ApplyPendingMigrationsAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task ApplyPendingMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var migrations = GetMigrations();
        var currentVersion = await ReadUserVersionAsync(connection, cancellationToken);

        foreach (var migration in migrations.Where(migration => migration.Version > currentVersion))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, migration.Sql, cancellationToken, transaction);
            await ExecuteNonQueryAsync(connection, $"PRAGMA user_version = {migration.Version};", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<SqliteMigration> GetMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourcePrefix = "IsTranscribe.Host.Persistence.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(static resourceName => resourceName.StartsWith(resourcePrefix, StringComparison.Ordinal) && resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(resourceName => SqliteMigration.FromResourceName(resourceName, ReadEmbeddedText(assembly, resourceName)))
            .OrderBy(static migration => migration.Version)
            .ToArray();
    }

    private static string ReadEmbeddedText(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record SqliteMigration(int Version, string Description, string Sql)
    {
        public static SqliteMigration FromResourceName(string resourceName, string sql)
        {
            var lastDotIndex = resourceName.LastIndexOf('.');
            var previousDotIndex = resourceName.LastIndexOf('.', lastDotIndex - 1);
            if (lastDotIndex <= 0 || previousDotIndex < 0 || previousDotIndex == lastDotIndex - 1)
            {
                throw new InvalidOperationException($"Embedded migration '{resourceName}' does not include a file name.");
            }

            var fileName = resourceName[(previousDotIndex + 1)..lastDotIndex];
            var separatorIndex = fileName.IndexOf('_');
            if (separatorIndex <= 0 || !int.TryParse(fileName[..separatorIndex], out var version))
            {
                throw new InvalidOperationException($"Embedded migration '{resourceName}' does not follow the 'NNN_description.sql' naming convention.");
            }

            return new SqliteMigration(version, fileName[(separatorIndex + 1)..], sql);
        }
    }
}
