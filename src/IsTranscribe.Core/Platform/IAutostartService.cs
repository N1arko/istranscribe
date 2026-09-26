namespace IsTranscribe.Core.Platform;

/// <summary>
/// Platform-owned per-user launch-at-login registration.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public interface IAutostartService
{
    ValueTask<AutostartRegistrationState> GetStateAsync(CancellationToken cancellationToken);

    ValueTask<AutostartRegistrationState> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles a persisted user preference with a platform registration during an install-contour migration.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install.msix-compatibility
/// </remarks>
public interface IAutostartStateReconciler : IAutostartService
{
    ValueTask<AutostartReconciliationResult> ReconcileDesiredStateAsync(
        bool desiredEnabled,
        bool? lastObservedPackagedState,
        CancellationToken cancellationToken);

    ValueTask CleanupMigrationArtifactsAsync(CancellationToken cancellationToken);
}

public sealed record AutostartRegistrationState(
    bool IsEnabled,
    AutostartControlConstraint Constraint = AutostartControlConstraint.None);

public sealed record AutostartReconciliationResult(
    bool IsEnabled,
    bool DesiredEnabled,
    bool? PackagedObservedEnabled,
    Exception? Failure = null);

public enum AutostartControlConstraint
{
    None,
    DisabledByUser,
    DisabledByPolicy,
    EnabledByPolicy
}
