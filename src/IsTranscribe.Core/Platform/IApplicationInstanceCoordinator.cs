namespace IsTranscribe.Core.Platform;

/// <summary>
/// Coordinates one desktop process per user while keeping the transport platform-specific.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline
/// </remarks>
public interface IApplicationInstanceCoordinator : IAsyncDisposable
{
    event EventHandler? ActivationRequested;

    ValueTask<ApplicationInstanceRole> RegisterAsync(CancellationToken cancellationToken);

    ValueTask<bool> NotifyPrimaryAsync(CancellationToken cancellationToken);
}

public enum ApplicationInstanceRole
{
    Primary,
    Secondary
}
