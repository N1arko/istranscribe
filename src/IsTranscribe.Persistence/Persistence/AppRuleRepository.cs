using Microsoft.Data.Sqlite;

namespace IsTranscribe.Host.Persistence;

public sealed class AppRuleRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#sqlite.app-rule
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    // @spec spec://modules/app/FEAT-005-application-rules-and-discovery#settings-contract.ordering
    public async ValueTask<IReadOnlyList<AppRuleRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, process_name, enabled, capture_mode_default, notes, priority_index
            FROM app_rule
            ORDER BY priority_index ASC, rowid ASC;
            """;

        var results = new List<AppRuleRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AppRuleRecord(
                Id: reader.GetString(0),
                DisplayName: reader.GetString(1),
                ProcessName: reader.GetString(2),
                Enabled: reader.GetInt64(3) != 0,
                CaptureModeDefault: reader.IsDBNull(4) ? null : reader.GetString(4),
                Notes: reader.IsDBNull(5) ? null : reader.GetString(5),
                PriorityIndex: reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
        }

        return results;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.completion
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    public async ValueTask ReplaceAllAsync(IEnumerable<AppRuleRecord> rules, CancellationToken cancellationToken)
    {
        var normalized = rules
            .Select(AppRuleRecord.Normalize)
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.ProcessName))
            .GroupBy(static rule => rule.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select((rule, index) => rule with { PriorityIndex = index })
            .ToArray();

        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM app_rule;";
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var rule in normalized)
            {
                await using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO app_rule (id, display_name, process_name, enabled, capture_mode_default, notes, priority_index)
                    VALUES ($id, $display_name, $process_name, $enabled, $capture_mode_default, $notes, $priority_index);
                    """;
                insert.Parameters.AddWithValue("$id", rule.Id);
                insert.Parameters.AddWithValue("$display_name", rule.DisplayName);
                insert.Parameters.AddWithValue("$process_name", rule.ProcessName);
                insert.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
                insert.Parameters.AddWithValue("$capture_mode_default", (object?)rule.CaptureModeDefault ?? DBNull.Value);
                insert.Parameters.AddWithValue("$notes", (object?)rule.Notes ?? DBNull.Value);
                insert.Parameters.AddWithValue("$priority_index", rule.PriorityIndex);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.auto-add
    // @spec spec://modules/app/FEAT-002-automatic-detection-and-session-lifecycle#confirmation.unknown-app
    public async ValueTask<AppRuleRecord> UpsertAsync(AppRuleRecord rule, CancellationToken cancellationToken)
    {
        var normalized = AppRuleRecord.Normalize(rule with
        {
            PriorityIndex = rule.PriorityIndex >= 0 ? rule.PriorityIndex : await GetNextPriorityIndexAsync(cancellationToken).ConfigureAwait(false)
        });

        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_rule (id, display_name, process_name, enabled, capture_mode_default, notes, priority_index)
            VALUES ($id, $display_name, $process_name, $enabled, $capture_mode_default, $notes, $priority_index)
            ON CONFLICT(process_name) DO UPDATE SET
                display_name = excluded.display_name,
                enabled = excluded.enabled,
                capture_mode_default = excluded.capture_mode_default,
                notes = excluded.notes;
            """;
        command.Parameters.AddWithValue("$id", normalized.Id);
        command.Parameters.AddWithValue("$display_name", normalized.DisplayName);
        command.Parameters.AddWithValue("$process_name", normalized.ProcessName);
        command.Parameters.AddWithValue("$enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$capture_mode_default", (object?)normalized.CaptureModeDefault ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)normalized.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority_index", normalized.PriorityIndex);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    private async ValueTask<int> GetNextPriorityIndexAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(priority_index), -1) + 1 FROM app_rule;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed record AppRuleRecord(
    string Id,
    string DisplayName,
    string ProcessName,
    bool Enabled,
    string? CaptureModeDefault = null,
    string? Notes = null,
    int PriorityIndex = 0)
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
    public static AppRuleRecord Create(string displayName, string processName) =>
        Normalize(new AppRuleRecord(
            Id: Guid.NewGuid().ToString("N"),
            DisplayName: displayName,
            ProcessName: processName,
            Enabled: true));

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    public static AppRuleRecord Normalize(AppRuleRecord rule)
    {
        var normalizedProcessName = NormalizeProcessName(rule.ProcessName);
        var normalizedDisplayName = string.IsNullOrWhiteSpace(rule.DisplayName)
            ? Path.GetFileNameWithoutExtension(normalizedProcessName)
            : rule.DisplayName.Trim();

        return rule with
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id,
            DisplayName = normalizedDisplayName,
            ProcessName = normalizedProcessName
        };
    }

    // @spec spec://modules/app/FEAT-005-application-rules-and-discovery#settings-contract.normalization
    public static string NormalizeProcessName(string processName)
    {
        var trimmed = processName.Trim().ToLowerInvariant();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}.exe";
    }
}
