using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class PersistenceBootstrapperTests
{
    /// <summary>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </summary>
    [Fact]
    public async Task ReleaseV2InitializationLeavesLockedCorruptLegacySecretUnread()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            Directory.CreateDirectory(paths.ConfigDirectory);
            var corruptPayload = new byte[] { 0x46, 0x57, 0x00, 0xFF, 0x14 };
            await File.WriteAllBytesAsync(paths.SecretsFilePath, corruptPayload);
            var bootstrapper = new PersistenceBootstrapper(
                paths,
                new JsonApplicationSettingsStore(paths),
                new SqliteDatabaseInitializer(paths),
                new LegacyStorageLayoutMigrator(paths));

            ReleaseV2PersistenceBootstrapResult result;
            await using (var lockedSecret = new FileStream(
                             paths.SecretsFilePath,
                             FileMode.Open,
                             FileAccess.ReadWrite,
                             FileShare.None))
            {
                result = await bootstrapper.InitializeReleaseV2Async(CancellationToken.None);
                Assert.True(lockedSecret.CanRead);
            }

            await using (result.DatabaseConnection)
            {
                Assert.Equal(ApplicationSettings.CurrentSchemaVersion, result.Settings.Version);
                Assert.DoesNotContain(
                    result.GetType().GetProperties(),
                    static property => property.PropertyType == typeof(AppSecrets));
            }

            Assert.Equal(corruptPayload, await File.ReadAllBytesAsync(paths.SecretsFilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#initialization.bootstrap
    /// </summary>
    [Fact]
    public async Task LegacyInitializationStillLoadsExplicitSecretStore()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            var secrets = new CountingSecretStore(new AppSecrets("legacy-key"));
            var bootstrapper = new PersistenceBootstrapper(
                paths,
                new JsonApplicationSettingsStore(paths),
                secrets,
                new SqliteDatabaseInitializer(paths),
                new LegacyStorageLayoutMigrator(paths));

            var result = await bootstrapper.InitializeAsync(CancellationToken.None);

            await using (result.DatabaseConnection)
            {
                Assert.Equal(1, secrets.LoadCount);
                Assert.Equal("legacy-key", result.Secrets.FireworksApiKey);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingSecretStore(AppSecrets secrets) : ISecretStore
    {
        public int LoadCount { get; private set; }

        public ValueTask<AppSecrets> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return ValueTask.FromResult(secrets);
        }

        public ValueTask SaveAsync(AppSecrets value, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
