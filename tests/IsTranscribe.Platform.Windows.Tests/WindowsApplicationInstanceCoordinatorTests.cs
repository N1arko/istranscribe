using IsTranscribe.Core.Platform;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
/// </summary>
public sealed class WindowsApplicationInstanceCoordinatorTests
{
    [Fact]
    public async Task ActivationBeforeSubscriptionIsDeliveredOnceAfterSubscription()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var applicationName = "isTranscribe.InstanceTests." + Guid.NewGuid().ToString("N");
        await using var primary = CreateCoordinator(applicationName);
        await using var secondary = CreateCoordinator(applicationName);
        Assert.Equal(
            ApplicationInstanceRole.Primary,
            await primary.RegisterAsync(CancellationToken.None));
        Assert.Equal(
            ApplicationInstanceRole.Secondary,
            await secondary.RegisterAsync(CancellationToken.None));

        Assert.True(await secondary.NotifyPrimaryAsync(CancellationToken.None));
        await WaitUntilAsync(() => primary.HasPendingActivation, TimeSpan.FromSeconds(5));

        var activations = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref activations);
        await WaitUntilAsync(() => Volatile.Read(ref activations) == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref activations));
        Assert.False(primary.HasPendingActivation);
    }

    [Fact]
    public async Task DisposeIsIdempotentAndReleasesTheNamedInstanceIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var applicationName = "isTranscribe.InstanceTests." + Guid.NewGuid().ToString("N");
        var primary = CreateCoordinator(applicationName);
        Assert.Equal(
            ApplicationInstanceRole.Primary,
            await primary.RegisterAsync(CancellationToken.None));

        await primary.DisposeAsync();
        await primary.DisposeAsync();

        await using var replacement = CreateCoordinator(applicationName);
        Assert.Equal(
            ApplicationInstanceRole.Primary,
            await replacement.RegisterAsync(CancellationToken.None));
    }

    private static WindowsApplicationInstanceCoordinator CreateCoordinator(string applicationName)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "isTranscribe-instance-tests",
            Guid.NewGuid().ToString("N"));
        return new WindowsApplicationInstanceCoordinator(
            applicationName,
            new BootstrapFileLogger(Path.Combine(directory, "bootstrap.log")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The instance-coordinator condition did not become true.");
            }

            await Task.Delay(20);
        }
    }
}
