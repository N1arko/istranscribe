using Microsoft.Data.Sqlite;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Persistence;

public sealed class PersistenceBootstrapper
{
    private readonly LocalAppPaths _paths;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly ISecretStore? _legacySecretStore;
    private readonly SqliteDatabaseInitializer _databaseInitializer;
    private readonly LegacyStorageLayoutMigrator _legacyMigrator;

    public PersistenceBootstrapper(
        LocalAppPaths paths,
        IApplicationSettingsStore settingsStore,
        ISecretStore secretStore,
        SqliteDatabaseInitializer databaseInitializer,
        LegacyStorageLayoutMigrator legacyMigrator)
        : this(paths, settingsStore, databaseInitializer, legacyMigrator)
    {
        _legacySecretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    /// <summary>
    /// Creates the release-v2 persistence contour without constructing a legacy secret reader.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </remarks>
    public PersistenceBootstrapper(
        LocalAppPaths paths,
        IApplicationSettingsStore settingsStore,
        SqliteDatabaseInitializer databaseInitializer,
        LegacyStorageLayoutMigrator legacyMigrator)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _databaseInitializer = databaseInitializer ?? throw new ArgumentNullException(nameof(databaseInitializer));
        _legacyMigrator = legacyMigrator ?? throw new ArgumentNullException(nameof(legacyMigrator));
    }

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#initialization.bootstrap
    public async ValueTask<PersistenceBootstrapResult> InitializeAsync(CancellationToken cancellationToken)
    {
        var secretStore = _legacySecretStore
            ?? throw new InvalidOperationException(
                "Legacy persistence initialization requires an explicit secret store.");
        var database = await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var secrets = await secretStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new PersistenceBootstrapResult(settings, secrets, database);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads release-v2 settings and database state while leaving legacy protected secrets unread.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </remarks>
    public async ValueTask<ReleaseV2PersistenceBootstrapResult> InitializeReleaseV2Async(
        CancellationToken cancellationToken)
    {
        var database = await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new ReleaseV2PersistenceBootstrapResult(settings, database);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private async ValueTask<SqliteConnection> InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        _legacyMigrator.MigrateIfNeeded();
        _paths.EnsureAppOwnedDirectoriesExist();
        return await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PersistenceBootstrapResult(
    ApplicationSettings Settings,
    AppSecrets Secrets,
    SqliteConnection DatabaseConnection);

/// <summary>
/// Release-v2 persistence state intentionally excludes legacy provider secrets.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
/// </remarks>
public sealed record ReleaseV2PersistenceBootstrapResult(
    ApplicationSettings Settings,
    SqliteConnection DatabaseConnection);
