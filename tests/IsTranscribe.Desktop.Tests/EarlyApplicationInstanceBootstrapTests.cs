using IsTranscribe.Core.Platform;

namespace IsTranscribe.Desktop.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
/// </summary>
public sealed class EarlyApplicationInstanceBootstrapTests
{
    [Fact]
    public void EntryPointAcquiresTheInstanceRoleBeforeStartingAvalonia()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root, "src", "IsTranscribe.App.Windows", "Program.cs"));
        var app = File.ReadAllText(Path.Combine(
            root, "src", "IsTranscribe.Desktop", "App.axaml.cs"));

        var acquire = program.IndexOf(".AcquirePrimaryAsync(", StringComparison.Ordinal);
        var start = program.IndexOf("BuildAvaloniaApp().StartWithClassicDesktopLifetime", StringComparison.Ordinal);
        Assert.True(acquire >= 0 && start > acquire);
        Assert.Contains("DesktopApp.SetPreRegisteredInstanceCoordinator(coordinatorLease)", program, StringComparison.Ordinal);
        Assert.Contains("DesktopApp.ClearPreRegisteredInstanceCoordinator(coordinatorLease)", program, StringComparison.Ordinal);
        Assert.Contains("app.DisposeInstanceCoordinatorAfterStartupFailure()", program, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(", app, StringComparison.Ordinal);
        Assert.Contains("?? _composition.CreateInstanceCoordinator()", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryCoordinatorIsHandedToTheAvaloniaApplication()
    {
        var coordinator = new FakeCoordinator(ApplicationInstanceRole.Primary);
        var bootstrap = new EarlyApplicationInstanceBootstrap(coordinator);

        var result = await bootstrap.AcquirePrimaryAsync(CancellationToken.None);

        Assert.NotNull(result);
        await result.DisposeAsync();
        await result.DisposeAsync();
        Assert.Equal(1, coordinator.RegisterCalls);
        Assert.Equal(0, coordinator.NotifyCalls);
        Assert.Equal(1, coordinator.DisposeCalls);
    }

    [Fact]
    public async Task SecondaryNotifiesPrimaryAndExitsBeforeAvaloniaStarts()
    {
        var coordinator = new FakeCoordinator(ApplicationInstanceRole.Secondary);
        var bootstrap = new EarlyApplicationInstanceBootstrap(coordinator);

        var result = await bootstrap.AcquirePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, coordinator.RegisterCalls);
        Assert.Equal(1, coordinator.NotifyCalls);
        Assert.Equal(1, coordinator.DisposeCalls);
    }

    [Fact]
    public async Task SecondaryCoordinatorIsDisposedWhenNotificationFails()
    {
        var coordinator = new FakeCoordinator(
            ApplicationInstanceRole.Secondary,
            notifyException: new InvalidOperationException("notification failed"));
        var bootstrap = new EarlyApplicationInstanceBootstrap(coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await bootstrap.AcquirePrimaryAsync(CancellationToken.None));

        Assert.Equal(1, coordinator.DisposeCalls);
    }

    [Fact]
    public async Task CoordinatorIsDisposedWhenEarlyRegistrationFails()
    {
        var coordinator = new FakeCoordinator(
            ApplicationInstanceRole.Primary,
            registerException: new InvalidOperationException("registration failed"));
        var bootstrap = new EarlyApplicationInstanceBootstrap(coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await bootstrap.AcquirePrimaryAsync(CancellationToken.None));

        Assert.Equal(1, coordinator.DisposeCalls);
    }

    [Fact]
    public async Task SecondaryDisposeFailureIsReportedAfterSuccessfulNotification()
    {
        var coordinator = new FakeCoordinator(
            ApplicationInstanceRole.Secondary,
            disposeException: new InvalidOperationException("dispose failed"));
        var bootstrap = new EarlyApplicationInstanceBootstrap(coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await bootstrap.AcquirePrimaryAsync(CancellationToken.None));

        Assert.Equal(1, coordinator.NotifyCalls);
        Assert.Equal(1, coordinator.DisposeCalls);
    }

    private sealed class FakeCoordinator(
        ApplicationInstanceRole role,
        Exception? notifyException = null,
        Exception? registerException = null,
        Exception? disposeException = null) : IApplicationInstanceCoordinator
    {
        public event EventHandler? ActivationRequested
        {
            add { }
            remove { }
        }

        public int RegisterCalls { get; private set; }

        public int NotifyCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public ValueTask<ApplicationInstanceRole> RegisterAsync(CancellationToken cancellationToken)
        {
            RegisterCalls++;
            return registerException is null
                ? ValueTask.FromResult(role)
                : ValueTask.FromException<ApplicationInstanceRole>(registerException);
        }

        public ValueTask<bool> NotifyPrimaryAsync(CancellationToken cancellationToken)
        {
            NotifyCalls++;
            return notifyException is null
                ? ValueTask.FromResult(true)
                : ValueTask.FromException<bool>(notifyException);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return disposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeException);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
