using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Host.Tests;

public sealed class AppRuleRepositoryTests
{
    [Fact]
    public async Task ReplaceAllAsync_PreservesOrder_AndNormalizesProcessNames()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var paths = new LocalAppPaths("isTranscribe", root);
            await using var database = await new SqliteDatabaseInitializer(paths).InitializeAsync(CancellationToken.None);
            var repository = new AppRuleRepository(database);

            await repository.ReplaceAllAsync(
            [
                AppRuleRecord.Create("Zoom", "Zoom"),
                AppRuleRecord.Create("Meet", "chrome.exe"),
                AppRuleRecord.Create("Zoom duplicate", "zoom.exe")
            ], CancellationToken.None);

            var rules = await repository.ListAsync(CancellationToken.None);

            Assert.Equal(2, rules.Count);
            Assert.Equal("zoom.exe", rules[0].ProcessName);
            Assert.Equal("chrome.exe", rules[1].ProcessName);
            Assert.Equal("Zoom", rules[0].DisplayName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
