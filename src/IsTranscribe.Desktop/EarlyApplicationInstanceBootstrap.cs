using IsTranscribe.Core.Platform;

namespace IsTranscribe.Desktop;

/// <summary>
/// Acquires the per-user instance role before Avalonia and XAML initialization begin.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
/// </remarks>
public sealed class ApplicationInstanceCoordinatorLease(
    IApplicationInstanceCoordinator coordinator) : IAsyncDisposable
{
    private IApplicationInstanceCoordinator? _coordinator = coordinator;

    public IApplicationInstanceCoordinator? Take() =>
        Interlocked.Exchange(ref _coordinator, null);

    public async ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref _coordinator, null);
        if (owned is not null)
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class EarlyApplicationInstanceBootstrap(IApplicationInstanceCoordinator coordinator)
{
    public async ValueTask<ApplicationInstanceCoordinatorLease?> AcquirePrimaryAsync(
        CancellationToken cancellationToken)
    {
        var ownershipTransferred = false;
        Exception? operationFailure = null;
        try
        {
            var role = await coordinator.RegisterAsync(cancellationToken).ConfigureAwait(false);
            if (role == ApplicationInstanceRole.Primary)
            {
                ownershipTransferred = true;
                return new ApplicationInstanceCoordinatorLease(coordinator);
            }

            await coordinator.NotifyPrimaryAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                try
                {
                    await coordinator.DisposeAsync().ConfigureAwait(false);
                }
                catch when (operationFailure is not null)
                {
                    // Preserve the registration/notification failure as the actionable cause.
                }
            }
        }
    }
}
