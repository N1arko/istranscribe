using System.Runtime.Versioning;
using IsTranscribe.Core.Platform;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackagedWindowsAutostartMigrationServiceTests
{
    private const string LegacyExecutablePath = @"C:\Legacy\IsTranscribe.App.exe";
    private const string ValidLegacyCommand = "\"C:\\Legacy\\IsTranscribe.App.exe\" --autostart";

    [Fact]
    public async Task StateReadIsPure()
    {
        var packaged = new StubAutostartService(isEnabled: false);
        var legacy = new StubRunKeyStore();
        var service = CreateService(packaged, legacy);

        var state = await service.GetStateAsync(CancellationToken.None);

        Assert.False(state.IsEnabled);
        Assert.Equal(1, packaged.GetCount);
        Assert.Equal(0, packaged.SetCount);
        Assert.Equal(0, legacy.DeleteCount);
    }

    [Fact]
    public async Task FirstMigrationAppliesPersistedFalseAndRemovesLegacyRegistration()
    {
        var packaged = new StubAutostartService(isEnabled: true);
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: false,
            lastObservedPackagedState: null,
            CancellationToken.None);

        Assert.False(state.IsEnabled);
        Assert.False(state.DesiredEnabled);
        Assert.False(state.PackagedObservedEnabled);
        Assert.Equal(1, packaged.SetCount);
        Assert.Equal(1, legacy.DeleteCount);
    }

    [Fact]
    public async Task FirstMigrationAppliesPersistedTrueWithoutLegacyRegistration()
    {
        var packaged = new StubAutostartService(isEnabled: false);
        var legacy = new StubRunKeyStore();
        var service = CreateService(packaged, legacy);

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: true,
            lastObservedPackagedState: null,
            CancellationToken.None);

        Assert.True(state.IsEnabled);
        Assert.True(state.DesiredEnabled);
        Assert.True(state.PackagedObservedEnabled);
        Assert.Equal(1, packaged.SetCount);
    }

    [Fact]
    public async Task WindowsUserDenialPreservesDesiredTrueAndRemovesLegacyRegistration()
    {
        var packaged = new StubAutostartService(
            isEnabled: false,
            constraint: AutostartControlConstraint.DisabledByUser);
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: true,
            lastObservedPackagedState: true,
            CancellationToken.None);

        Assert.False(state.IsEnabled);
        Assert.True(state.DesiredEnabled);
        Assert.False(state.PackagedObservedEnabled);
        Assert.Equal(0, packaged.SetCount);
        Assert.Equal(1, legacy.DeleteCount);
    }

    [Fact]
    public async Task WindowsPolicyEnablePreservesDesiredFalse()
    {
        var packaged = new StubAutostartService(
            isEnabled: true,
            constraint: AutostartControlConstraint.EnabledByPolicy);
        var service = CreateService(packaged, new StubRunKeyStore());

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: false,
            lastObservedPackagedState: false,
            CancellationToken.None);

        Assert.True(state.IsEnabled);
        Assert.False(state.DesiredEnabled);
        Assert.True(state.PackagedObservedEnabled);
        Assert.Equal(0, packaged.SetCount);
    }

    [Fact]
    public async Task ExternalWindowsReEnableBecomesTheNewDesiredState()
    {
        var packaged = new StubAutostartService(isEnabled: true);
        var service = CreateService(packaged, new StubRunKeyStore());

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: false,
            lastObservedPackagedState: false,
            CancellationToken.None);

        Assert.True(state.IsEnabled);
        Assert.True(state.DesiredEnabled);
        Assert.True(state.PackagedObservedEnabled);
        Assert.Equal(0, packaged.SetCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitSettingUsesPackagedTaskAndRemovesLegacyRegistration(bool enabled)
    {
        var packaged = new StubAutostartService(isEnabled: !enabled);
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        var state = await service.SetEnabledAsync(enabled, CancellationToken.None);

        Assert.Equal(enabled, state.IsEnabled);
        Assert.Equal(1, packaged.SetCount);
        Assert.Equal(1, legacy.DeleteCount);
    }

    [Fact]
    public async Task UnexpectedEnableFailureRetainsOnlyAWorkingLegacyRegistration()
    {
        var packaged = new StubAutostartService(isEnabled: false)
        {
            EnableFailure = new InvalidOperationException("transient failure")
        };
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: true,
            lastObservedPackagedState: false,
            CancellationToken.None);

        Assert.True(state.IsEnabled);
        Assert.True(state.DesiredEnabled);
        Assert.False(state.PackagedObservedEnabled);
        Assert.IsType<InvalidOperationException>(state.Failure);
        Assert.Equal(0, legacy.DeleteCount);
        Assert.Equal(ValidLegacyCommand, legacy.Command);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("\"C:\\Missing\\IsTranscribe.App.exe\" --autostart")]
    [InlineData("\"C:\\Legacy\\unrelated.exe\" --autostart")]
    public async Task UnexpectedEnableFailureClearsInvalidOrMissingLegacyRegistration(string command)
    {
        var packaged = new StubAutostartService(isEnabled: false)
        {
            EnableFailure = new InvalidOperationException("transient failure")
        };
        var legacy = new StubRunKeyStore { Command = command };
        var service = CreateService(packaged, legacy);

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: true,
            lastObservedPackagedState: false,
            CancellationToken.None);

        Assert.False(state.IsEnabled);
        Assert.NotNull(state.Failure);
        Assert.Equal(1, legacy.DeleteCount);
        Assert.Null(legacy.Command);
    }

    [Fact]
    public async Task UnexpectedEnableFailureClearsLegacyAfterTargetStateIsObserved()
    {
        var packaged = new StubAutostartService(isEnabled: false)
        {
            EnableFailure = new InvalidOperationException("response lost"),
            MutateBeforeFailure = true
        };
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        var state = await service.ReconcileDesiredStateAsync(
            desiredEnabled: true,
            lastObservedPackagedState: false,
            CancellationToken.None);

        Assert.True(state.IsEnabled);
        Assert.True(state.PackagedObservedEnabled);
        Assert.NotNull(state.Failure);
        Assert.Equal(1, legacy.DeleteCount);
    }

    [Fact]
    public async Task ExplicitEnableFailureRetainsWorkingLegacyRegistration()
    {
        var packaged = new StubAutostartService(isEnabled: false)
        {
            EnableFailure = new InvalidOperationException("transient failure")
        };
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetEnabledAsync(true, CancellationToken.None).AsTask());

        Assert.Equal(0, legacy.DeleteCount);
        Assert.Equal(ValidLegacyCommand, legacy.Command);
    }

    [Fact]
    public async Task CancelledExplicitEnableRetainsWorkingLegacyRegistration()
    {
        var packaged = new StubAutostartService(isEnabled: false);
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SetEnabledAsync(true, cancellation.Token).AsTask());

        Assert.Equal(0, legacy.DeleteCount);
        Assert.Equal(ValidLegacyCommand, legacy.Command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterExplicitPlatformMutationReconcilesCleanupBeforeRethrow(
        bool targetState)
    {
        using var cancellation = new CancellationTokenSource();
        var packaged = new StubAutostartService(isEnabled: !targetState)
        {
            EnableFailure = targetState ? new OperationCanceledException() : null,
            DisableFailure = targetState ? null : new OperationCanceledException(),
            MutateBeforeFailure = true,
            BeforeFailure = cancellation.Cancel
        };
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SetEnabledAsync(targetState, cancellation.Token).AsTask());

        Assert.Equal(targetState, packaged.IsEnabled);
        Assert.Equal(1, legacy.DeleteCount);
        Assert.Null(legacy.Command);
    }

    [Fact]
    public async Task CancellationAfterReconciliationDisableClearsLegacyBeforeRethrow()
    {
        using var cancellation = new CancellationTokenSource();
        var packaged = new StubAutostartService(isEnabled: true)
        {
            DisableFailure = new OperationCanceledException(),
            MutateBeforeFailure = true,
            BeforeFailure = cancellation.Cancel
        };
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReconcileDesiredStateAsync(
                desiredEnabled: false,
                lastObservedPackagedState: true,
                cancellation.Token).AsTask());

        Assert.False(packaged.IsEnabled);
        Assert.Equal(1, legacy.DeleteCount);
    }

    [Fact]
    public async Task LegacyCleanupFailureIsReported()
    {
        var packaged = new StubAutostartService(isEnabled: true);
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand, IgnoreDeletes = true };
        var service = CreateService(packaged, legacy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileDesiredStateAsync(true, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CleanupOnlyRemovesLegacyRegistrationWithoutChangingPackagedState()
    {
        var packaged = new StubAutostartService(isEnabled: false);
        var legacy = new StubRunKeyStore { Command = ValidLegacyCommand };
        var service = CreateService(packaged, legacy);

        await service.CleanupMigrationArtifactsAsync(CancellationToken.None);

        Assert.Equal(0, packaged.SetCount);
        Assert.Equal(1, legacy.DeleteCount);
    }

    private static PackagedWindowsAutostartMigrationService CreateService(
        StubAutostartService packaged,
        StubRunKeyStore legacy) =>
        new(
            packaged,
            legacy,
            path => string.Equals(path, LegacyExecutablePath, StringComparison.OrdinalIgnoreCase));

    private sealed class StubAutostartService : IAutostartService
    {
        private bool _isEnabled;
        private AutostartControlConstraint _constraint;

        public StubAutostartService(
            bool isEnabled,
            AutostartControlConstraint constraint = AutostartControlConstraint.None)
        {
            _isEnabled = isEnabled;
            _constraint = constraint;
        }

        public Exception? EnableFailure { get; init; }

        public Exception? DisableFailure { get; init; }

        public bool MutateBeforeFailure { get; init; }

        public Action? BeforeFailure { get; init; }

        public bool IsEnabled => _isEnabled;

        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public ValueTask<AutostartRegistrationState> GetStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            return ValueTask.FromResult(new AutostartRegistrationState(_isEnabled, _constraint));
        }

        public ValueTask<AutostartRegistrationState> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCount++;
            var failure = enabled ? EnableFailure : DisableFailure;
            if (failure is not null)
            {
                if (MutateBeforeFailure)
                {
                    _isEnabled = enabled;
                    _constraint = AutostartControlConstraint.None;
                }

                BeforeFailure?.Invoke();
                throw failure;
            }

            _isEnabled = enabled;
            _constraint = AutostartControlConstraint.None;
            return ValueTask.FromResult(new AutostartRegistrationState(_isEnabled));
        }
    }

    private sealed class StubRunKeyStore : IWindowsRunKeyStore
    {
        public string? Command { get; set; }

        public bool IgnoreDeletes { get; init; }

        public int DeleteCount { get; private set; }

        public string? Read(string valueName) => Command;

        public void Write(string valueName, string command) => Command = command;

        public void Delete(string valueName)
        {
            DeleteCount++;
            if (!IgnoreDeletes)
            {
                Command = null;
            }
        }
    }
}
