using IsTranscribe.Host.Capabilities;

namespace IsTranscribe.Host.Bootstrap;

public interface IBackgroundServicesCoordinator
{
    ValueTask StartAsync(HostCapabilitySnapshot capability, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed class NullBackgroundServicesCoordinator : IBackgroundServicesCoordinator
{
    public bool IsRunning { get; private set; }

    public ValueTask StartAsync(HostCapabilitySnapshot capability, CancellationToken cancellationToken)
    {
        IsRunning = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        return ValueTask.CompletedTask;
    }
}
