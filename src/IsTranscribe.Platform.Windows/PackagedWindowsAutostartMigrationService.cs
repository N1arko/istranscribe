using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using IsTranscribe.Core.Platform;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Moves an app-owned unpackaged Run-key registration to the packaged StartupTask contract.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PackagedWindowsAutostartMigrationService : IAutostartStateReconciler
{
    private static readonly string[] LegacyExecutableNames =
    [
        "isTranscribe.exe",
        "IsTranscribe.App.exe",
        "IsTranscribe.Desktop.exe"
    ];

    private readonly IAutostartService _packagedService;
    private readonly IWindowsRunKeyStore _legacyStore;
    private readonly Func<string, bool> _fileExists;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public PackagedWindowsAutostartMigrationService(
        IAutostartService packagedService,
        IWindowsRunKeyStore legacyStore,
        Func<string, bool>? fileExists = null)
    {
        _packagedService = packagedService ?? throw new ArgumentNullException(nameof(packagedService));
        _legacyStore = legacyStore ?? throw new ArgumentNullException(nameof(legacyStore));
        _fileExists = fileExists ?? File.Exists;
    }

    public async ValueTask<AutostartRegistrationState> GetStateAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var packaged = await _packagedService
                .GetStateAsync(cancellationToken)
                .ConfigureAwait(false);
            var legacy = ObserveLegacyRegistration();
            return packaged with
            {
                IsEnabled = packaged.IsEnabled || legacy == LegacyRegistrationState.Valid
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<AutostartReconciliationResult> ReconcileDesiredStateAsync(
        bool desiredEnabled,
        bool? lastObservedPackagedState,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var legacy = ObserveLegacyRegistration();
            AutostartRegistrationState current;
            try
            {
                current = await _packagedService
                    .GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (!desiredEnabled || legacy != LegacyRegistrationState.Valid)
                {
                    ClearLegacyRegistration();
                    legacy = LegacyRegistrationState.None;
                }

                var lastObservedEnabled = lastObservedPackagedState ?? false;
                return new AutostartReconciliationResult(
                    IsEnabled: lastObservedEnabled || (desiredEnabled && legacy == LegacyRegistrationState.Valid),
                    DesiredEnabled: desiredEnabled,
                    PackagedObservedEnabled: lastObservedPackagedState,
                    Failure: exception);
            }

            if (lastObservedPackagedState == false &&
                current.IsEnabled &&
                current.Constraint == AutostartControlConstraint.None)
            {
                ClearLegacyRegistration();
                return new AutostartReconciliationResult(
                    IsEnabled: true,
                    DesiredEnabled: true,
                    PackagedObservedEnabled: true);
            }

            if (current.Constraint != AutostartControlConstraint.None)
            {
                ClearLegacyRegistration();
                return new AutostartReconciliationResult(
                    IsEnabled: current.IsEnabled,
                    DesiredEnabled: desiredEnabled,
                    PackagedObservedEnabled: current.IsEnabled);
            }

            if (current.IsEnabled == desiredEnabled)
            {
                ClearLegacyRegistration();
                return new AutostartReconciliationResult(
                    IsEnabled: current.IsEnabled,
                    DesiredEnabled: desiredEnabled,
                    PackagedObservedEnabled: current.IsEnabled);
            }

            AutostartRegistrationState applied;
            try
            {
                applied = await _packagedService
                    .SetEnabledAsync(desiredEnabled, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WindowsStartupTaskStateBlockedException exception)
            {
                var blockedState = exception.ToRegistrationState();
                ClearLegacyRegistration();
                return new AutostartReconciliationResult(
                    IsEnabled: blockedState.IsEnabled,
                    DesiredEnabled: desiredEnabled,
                    PackagedObservedEnabled: blockedState.IsEnabled);
            }
            catch (Exception exception)
            {
                var degraded = await BuildDegradedResultAsync(
                        exception,
                        desiredEnabled,
                        current,
                        legacy)
                    .ConfigureAwait(false);
                RethrowCancellationAfterRecovery(exception, cancellationToken);
                return degraded;
            }

            if (applied.IsEnabled != desiredEnabled)
            {
                return await BuildDegradedResultAsync(
                        new InvalidOperationException(
                            "The packaged launch-at-login service returned an unexpected state."),
                        desiredEnabled,
                        applied,
                        legacy)
                    .ConfigureAwait(false);
            }

            ClearLegacyRegistration();
            return new AutostartReconciliationResult(
                IsEnabled: applied.IsEnabled,
                DesiredEnabled: desiredEnabled,
                PackagedObservedEnabled: applied.IsEnabled);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask CleanupMigrationArtifactsAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearLegacyRegistration();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<AutostartRegistrationState> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AutostartRegistrationState applied;
            try
            {
                applied = await _packagedService
                    .SetEnabledAsync(enabled, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WindowsStartupTaskStateBlockedException)
            {
                ClearLegacyRegistration();
                throw;
            }
            catch (Exception exception)
            {
                return await RecoverExplicitFailureAsync(
                        exception,
                        enabled,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (applied.IsEnabled != enabled)
            {
                return await RecoverExplicitFailureAsync(
                        new InvalidOperationException(
                            "The packaged launch-at-login service returned an unexpected state."),
                        enabled,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ClearLegacyRegistration();
            return applied;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<AutostartReconciliationResult> BuildDegradedResultAsync(
        Exception exception,
        bool desiredEnabled,
        AutostartRegistrationState lastObserved,
        LegacyRegistrationState legacy)
    {
        AutostartRegistrationState observed = lastObserved;
        Exception? observationFailure = null;
        try
        {
            observed = await _packagedService
                .GetStateAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception stateException)
        {
            observationFailure = stateException;
        }

        if (!desiredEnabled || observed.IsEnabled == desiredEnabled)
        {
            ClearLegacyRegistration();
            legacy = LegacyRegistrationState.None;
        }
        else if (legacy == LegacyRegistrationState.Invalid)
        {
            ClearLegacyRegistration();
            legacy = LegacyRegistrationState.None;
        }

        var failure = observationFailure is null
            ? exception
            : new AggregateException(
                "Applying packaged autostart failed and its resulting state could not be observed.",
                exception,
                observationFailure);
        return new AutostartReconciliationResult(
            IsEnabled: observed.IsEnabled || (desiredEnabled && legacy == LegacyRegistrationState.Valid),
            DesiredEnabled: desiredEnabled,
            PackagedObservedEnabled: observed.IsEnabled,
            Failure: failure);
    }

    private async ValueTask<AutostartRegistrationState> RecoverExplicitFailureAsync(
        Exception exception,
        bool desiredEnabled,
        CancellationToken cancellationToken)
    {
        AutostartRegistrationState? observed = null;
        Exception? observationFailure = null;
        try
        {
            observed = await _packagedService
                .GetStateAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception stateException)
        {
            observationFailure = stateException;
        }

        var legacy = ObserveLegacyRegistration();
        if (observed?.IsEnabled == desiredEnabled)
        {
            ClearLegacyRegistration();
            RethrowCancellationAfterRecovery(exception, cancellationToken);
            return observed;
        }

        if (!desiredEnabled || legacy == LegacyRegistrationState.Invalid)
        {
            ClearLegacyRegistration();
        }

        RethrowCancellationAfterRecovery(exception, cancellationToken);
        if (observationFailure is not null)
        {
            throw new AggregateException(
                "Applying packaged autostart failed and its resulting state could not be observed.",
                exception,
                observationFailure);
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
        throw new System.Diagnostics.UnreachableException();
    }

    private static void RethrowCancellationAfterRecovery(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private LegacyRegistrationState ObserveLegacyRegistration()
    {
        var command = _legacyStore.Read(WindowsRunKeyAutostartService.ValueName);
        if (command is null)
        {
            return LegacyRegistrationState.None;
        }

        return TryGetLegacyExecutablePath(command, out var executablePath) &&
               _fileExists(executablePath)
            ? LegacyRegistrationState.Valid
            : LegacyRegistrationState.Invalid;
    }

    private static bool TryGetLegacyExecutablePath(string command, out string executablePath)
    {
        executablePath = string.Empty;
        var trimmed = command.Trim();
        if (trimmed.Length < 4 || trimmed[0] != '"')
        {
            return false;
        }

        var closingQuote = trimmed.IndexOf('"', 1);
        if (closingQuote <= 1 ||
            !string.Equals(
                trimmed[(closingQuote + 1)..].Trim(),
                "--autostart",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var candidate = trimmed[1..closingQuote];
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            var fileName = Path.GetFileName(candidate);
            if (!LegacyExecutableNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            executablePath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void ClearLegacyRegistration()
    {
        if (_legacyStore.Read(WindowsRunKeyAutostartService.ValueName) is null)
        {
            return;
        }

        _legacyStore.Delete(WindowsRunKeyAutostartService.ValueName);
        if (_legacyStore.Read(WindowsRunKeyAutostartService.ValueName) is not null)
        {
            throw new InvalidOperationException(
                "Windows did not remove the legacy isTranscribe launch-at-login registration.");
        }
    }

    private enum LegacyRegistrationState
    {
        None,
        Valid,
        Invalid
    }
}
