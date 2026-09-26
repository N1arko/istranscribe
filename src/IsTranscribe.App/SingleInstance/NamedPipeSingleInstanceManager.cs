using System.IO;
using System.IO.Pipes;
using System.Text;
using IsTranscribe.Host.Bootstrap;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.App.SingleInstance;

public sealed record SingleInstanceRegistration(bool IsPrimary, string ActivationPipeName)
{
    public async Task<bool> ForwardActivationAsync(ActivationMessage message, CancellationToken cancellationToken)
    {
        if (IsPrimary)
        {
            return false;
        }

        using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(1000, cancellationToken);
            await using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true);
            await writer.WriteAsync(ActivationMessageSerializer.Serialize(message));
            await writer.FlushAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class NamedPipeSingleInstanceManager(BootstrapFileLogger logger) : IAsyncDisposable
{
    private readonly BootstrapFileLogger _logger = logger;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private Mutex? _mutex;

    public event EventHandler<ActivationMessage>? ActivationReceived;

    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#single-instance
    public Task<SingleInstanceRegistration> RegisterAsync(AppIdentity identity, CancellationToken cancellationToken)
    {
        _mutex = new Mutex(initiallyOwned: true, identity.MutexName, out var createdNew);
        if (!createdNew)
        {
            _logger.Info("Secondary launch detected.");
            return Task.FromResult(new SingleInstanceRegistration(false, identity.ActivationPipeName));
        }

        _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenerTask = ListenAsync(identity.ActivationPipeName, _listenerCancellation.Token);
        _logger.Info($"Primary instance lock acquired: {identity.MutexName}");
        return Task.FromResult(new SingleInstanceRegistration(true, identity.ActivationPipeName));
    }

    public async ValueTask DisposeAsync()
    {
        if (_listenerCancellation is not null)
        {
            await _listenerCancellation.CancelAsync();
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listenerCancellation?.Dispose();
        _mutex?.Dispose();
    }

    private async Task ListenAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var payload = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            try
            {
                var message = ActivationMessageSerializer.Deserialize(payload);
                ActivationReceived?.Invoke(this, message);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to process activation payload.");
            }
        }
    }
}
