using System.Runtime.Versioning;
using IsTranscribe.Core.Platform;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#scope.in
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupTaskAutostartServiceTests
{
    public static TheoryData<int, bool> RegistrationStates => new()
    {
        { (int)WindowsStartupTaskState.Disabled, false },
        { (int)WindowsStartupTaskState.DisabledByUser, false },
        { (int)WindowsStartupTaskState.Enabled, true },
        { (int)WindowsStartupTaskState.DisabledByPolicy, false },
        { (int)WindowsStartupTaskState.EnabledByPolicy, true }
    };

    public static TheoryData<int> ExplicitEnableBlockers => new()
    {
        (int)WindowsStartupTaskState.DisabledByUser,
        (int)WindowsStartupTaskState.DisabledByPolicy
    };

    public static TheoryData<int> RejectedEnableResults => new()
    {
        (int)WindowsStartupTaskState.Disabled,
        (int)WindowsStartupTaskState.DisabledByUser,
        (int)WindowsStartupTaskState.DisabledByPolicy
    };

    public static TheoryData<int> AlreadyDisabledStates => new()
    {
        (int)WindowsStartupTaskState.Disabled,
        (int)WindowsStartupTaskState.DisabledByUser,
        (int)WindowsStartupTaskState.DisabledByPolicy
    };

    [Theory]
    [MemberData(nameof(RegistrationStates))]
    public async Task GetStateMapsEveryWindowsState(
        int stateValue,
        bool expectedEnabled)
    {
        var state = (WindowsStartupTaskState)stateValue;
        var adapter = new FakeStartupTaskAdapter(state);
        var service = new WindowsStartupTaskAutostartService(adapter);

        var registration = await service.GetStateAsync(CancellationToken.None);

        Assert.Equal(expectedEnabled, registration.IsEnabled);
        Assert.Equal(WindowsStartupTaskAutostartService.TaskId, adapter.LastTaskId);
        Assert.Equal(1, adapter.GetStateCount);
    }

    [Theory]
    [InlineData((int)WindowsStartupTaskState.DisabledByUser, (int)AutostartControlConstraint.DisabledByUser)]
    [InlineData((int)WindowsStartupTaskState.DisabledByPolicy, (int)AutostartControlConstraint.DisabledByPolicy)]
    [InlineData((int)WindowsStartupTaskState.EnabledByPolicy, (int)AutostartControlConstraint.EnabledByPolicy)]
    public async Task GetStatePreservesWindowsControlConstraint(
        int stateValue,
        int expectedConstraintValue)
    {
        var service = new WindowsStartupTaskAutostartService(
            new FakeStartupTaskAdapter((WindowsStartupTaskState)stateValue));

        var registration = await service.GetStateAsync(CancellationToken.None);

        Assert.Equal((AutostartControlConstraint)expectedConstraintValue, registration.Constraint);
    }

    [Fact]
    public async Task EnableRequestsTheDisabledTaskOnce()
    {
        var adapter = new FakeStartupTaskAdapter(WindowsStartupTaskState.Disabled)
        {
            EnableResult = WindowsStartupTaskState.Enabled
        };
        var service = new WindowsStartupTaskAutostartService(adapter);

        var registration = await service.SetEnabledAsync(true, CancellationToken.None);

        Assert.True(registration.IsEnabled);
        Assert.Equal(1, adapter.RequestEnableCount);
        Assert.Equal(WindowsStartupTaskAutostartService.TaskId, adapter.LastTaskId);
    }

    [Theory]
    [InlineData((int)WindowsStartupTaskState.Enabled)]
    [InlineData((int)WindowsStartupTaskState.EnabledByPolicy)]
    public async Task EnableIsIdempotentWhenWindowsAlreadyEnablesTheTask(
        int stateValue)
    {
        var state = (WindowsStartupTaskState)stateValue;
        var adapter = new FakeStartupTaskAdapter(state);
        var service = new WindowsStartupTaskAutostartService(adapter);

        var registration = await service.SetEnabledAsync(true, CancellationToken.None);

        Assert.True(registration.IsEnabled);
        Assert.Equal(0, adapter.RequestEnableCount);
    }

    [Theory]
    [MemberData(nameof(ExplicitEnableBlockers))]
    public async Task ExplicitEnableFailsWithoutRetryWhenWindowsBlocksIt(
        int stateValue)
    {
        var state = (WindowsStartupTaskState)stateValue;
        var adapter = new FakeStartupTaskAdapter(state);
        var service = new WindowsStartupTaskAutostartService(adapter);

        var exception = await Assert.ThrowsAsync<WindowsStartupTaskStateBlockedException>(() =>
            service.SetEnabledAsync(true, CancellationToken.None).AsTask());

        Assert.Equal(state, exception.State);
        Assert.Equal(0, adapter.RequestEnableCount);
        Assert.Equal(1, adapter.GetStateCount);
    }

    [Theory]
    [MemberData(nameof(RejectedEnableResults))]
    public async Task RejectedEnableResultFailsAfterOneWindowsRequest(
        int resultValue)
    {
        var result = (WindowsStartupTaskState)resultValue;
        var adapter = new FakeStartupTaskAdapter(WindowsStartupTaskState.Disabled)
        {
            EnableResult = result
        };
        var service = new WindowsStartupTaskAutostartService(adapter);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            service.SetEnabledAsync(true, CancellationToken.None).AsTask());

        Assert.Equal(1, adapter.RequestEnableCount);
    }

    [Fact]
    public async Task DisableRequestsTheEnabledTaskOnce()
    {
        var adapter = new FakeStartupTaskAdapter(WindowsStartupTaskState.Enabled)
        {
            DisableResult = WindowsStartupTaskState.Disabled
        };
        var service = new WindowsStartupTaskAutostartService(adapter);

        var registration = await service.SetEnabledAsync(false, CancellationToken.None);

        Assert.False(registration.IsEnabled);
        Assert.Equal(1, adapter.DisableCount);
        Assert.Equal(WindowsStartupTaskAutostartService.TaskId, adapter.LastTaskId);
    }

    [Theory]
    [MemberData(nameof(AlreadyDisabledStates))]
    public async Task DisableIsIdempotentForEveryDisabledState(int stateValue)
    {
        var state = (WindowsStartupTaskState)stateValue;
        var adapter = new FakeStartupTaskAdapter(state);
        var service = new WindowsStartupTaskAutostartService(adapter);

        var registration = await service.SetEnabledAsync(false, CancellationToken.None);

        Assert.False(registration.IsEnabled);
        Assert.Equal(0, adapter.DisableCount);
    }

    [Fact]
    public async Task DisableFailsWhenPolicyRequiresStartup()
    {
        var adapter = new FakeStartupTaskAdapter(WindowsStartupTaskState.EnabledByPolicy);
        var service = new WindowsStartupTaskAutostartService(adapter);

        var exception = await Assert.ThrowsAsync<WindowsStartupTaskStateBlockedException>(() =>
            service.SetEnabledAsync(false, CancellationToken.None).AsTask());

        Assert.Equal(WindowsStartupTaskState.EnabledByPolicy, exception.State);
        Assert.Equal(0, adapter.DisableCount);
    }

    [Fact]
    public async Task DisableFailsWhenWindowsKeepsTheTaskEnabled()
    {
        var adapter = new FakeStartupTaskAdapter(WindowsStartupTaskState.Enabled)
        {
            DisableResult = WindowsStartupTaskState.Enabled
        };
        var service = new WindowsStartupTaskAutostartService(adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetEnabledAsync(false, CancellationToken.None).AsTask());

        Assert.Equal(1, adapter.DisableCount);
    }

    [Fact]
    public void FactorySelectsThePackagedServiceLazily()
    {
        var expected = new StubAutostartService();
        var unpackagedFactoryCount = 0;

        var selected = WindowsAutostartServiceFactory.Create(
            new FakePackageIdentityDetector(hasPackageIdentity: true),
            () => expected,
            () =>
            {
                unpackagedFactoryCount++;
                return new StubAutostartService();
            });

        Assert.Same(expected, selected);
        Assert.Equal(0, unpackagedFactoryCount);
    }

    [Fact]
    public void FactoryKeepsTheRunKeyCompatibleUnpackagedContour()
    {
        var expected = new StubAutostartService();
        var packagedFactoryCount = 0;

        var selected = WindowsAutostartServiceFactory.Create(
            new FakePackageIdentityDetector(hasPackageIdentity: false),
            () =>
            {
                packagedFactoryCount++;
                return new StubAutostartService();
            },
            () => expected);

        Assert.Same(expected, selected);
        Assert.Equal(0, packagedFactoryCount);
    }

    [Fact]
    public void CurrentTestHostHasNoWindowsPackageIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(new WindowsPackageIdentityDetector().HasPackageIdentity());
    }

    private sealed class FakeStartupTaskAdapter(WindowsStartupTaskState state)
        : IWindowsStartupTaskAdapter
    {
        public WindowsStartupTaskState State { get; set; } = state;

        public WindowsStartupTaskState EnableResult { get; init; } = WindowsStartupTaskState.Enabled;

        public WindowsStartupTaskState DisableResult { get; init; } = WindowsStartupTaskState.Disabled;

        public int GetStateCount { get; private set; }

        public int RequestEnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public string? LastTaskId { get; private set; }

        public ValueTask<WindowsStartupTaskState> GetStateAsync(
            string taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTaskId = taskId;
            GetStateCount++;
            return ValueTask.FromResult(State);
        }

        public ValueTask<WindowsStartupTaskState> RequestEnableAsync(
            string taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTaskId = taskId;
            RequestEnableCount++;
            State = EnableResult;
            return ValueTask.FromResult(State);
        }

        public ValueTask<WindowsStartupTaskState> DisableAsync(
            string taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTaskId = taskId;
            DisableCount++;
            State = DisableResult;
            return ValueTask.FromResult(State);
        }
    }

    private sealed class FakePackageIdentityDetector(bool hasPackageIdentity)
        : IWindowsPackageIdentityDetector
    {
        public bool HasPackageIdentity() => hasPackageIdentity;
    }

    private sealed class StubAutostartService : IAutostartService
    {
        public ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AutostartRegistrationState(IsEnabled: false));

        public ValueTask<AutostartRegistrationState> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AutostartRegistrationState(enabled));
    }
}
