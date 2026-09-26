using System.Runtime.Versioning;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyAutostartServiceTests
{
    [Fact]
    public async Task EnableAndDisableAreVerifiedAndIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new InMemoryRunKeyStore();
        var executablePath = Path.Combine(Path.GetTempPath(), "isTranscribe", "isTranscribe.exe");
        var service = new WindowsRunKeyAutostartService(store, () => executablePath);

        Assert.False((await service.GetStateAsync(CancellationToken.None)).IsEnabled);

        Assert.True((await service.SetEnabledAsync(true, CancellationToken.None)).IsEnabled);
        Assert.Equal($"\"{Path.GetFullPath(executablePath)}\" --autostart", store.Command);
        Assert.Equal(1, store.WriteCount);

        Assert.True((await service.SetEnabledAsync(true, CancellationToken.None)).IsEnabled);
        Assert.Equal(1, store.WriteCount);

        Assert.False((await service.SetEnabledAsync(false, CancellationToken.None)).IsEnabled);
        Assert.Null(store.Command);
        Assert.Equal(1, store.DeleteCount);

        Assert.False((await service.SetEnabledAsync(false, CancellationToken.None)).IsEnabled);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public async Task EnableReplacesARegistrationForAnOldExecutablePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new InMemoryRunKeyStore
        {
            Command = "\"C:\\Old\\isTranscribe.exe\" --autostart"
        };
        var executablePath = Path.Combine(Path.GetTempPath(), "new", "isTranscribe.exe");
        var service = new WindowsRunKeyAutostartService(store, () => executablePath);

        Assert.False((await service.GetStateAsync(CancellationToken.None)).IsEnabled);

        var enabled = await service.SetEnabledAsync(true, CancellationToken.None);

        Assert.True(enabled.IsEnabled);
        Assert.Equal($"\"{Path.GetFullPath(executablePath)}\" --autostart", store.Command);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public async Task VerificationFailureIsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new InMemoryRunKeyStore { IgnoreWrites = true };
        var service = new WindowsRunKeyAutostartService(
            store,
            () => Path.Combine(Path.GetTempPath(), "isTranscribe.exe"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetEnabledAsync(true, CancellationToken.None).AsTask());
    }

    [Fact]
    public void BuildCommandRejectsRunKeyCommandsOverWindowsLimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var longPath = $"C:\\{new string('a', 250)}\\isTranscribe.exe";

        Assert.Throws<InvalidOperationException>(() =>
            WindowsRunKeyAutostartService.BuildCommand(longPath));
    }

    private sealed class InMemoryRunKeyStore : IWindowsRunKeyStore
    {
        public string? Command { get; set; }

        public bool IgnoreWrites { get; init; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public string? Read(string valueName) => Command;

        public void Write(string valueName, string command)
        {
            WriteCount++;
            if (!IgnoreWrites)
            {
                Command = command;
            }
        }

        public void Delete(string valueName)
        {
            DeleteCount++;
            Command = null;
        }
    }
}
