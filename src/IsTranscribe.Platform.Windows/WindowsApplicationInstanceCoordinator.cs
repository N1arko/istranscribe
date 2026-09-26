using System.IO.Pipes;
using System.Text;
using IsTranscribe.Core.Platform;
using IsTranscribe.Host.Bootstrap;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Per-user Windows single-instance coordinator with named-pipe activation.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#desktop-baseline
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
/// </remarks>
public sealed class WindowsApplicationInstanceCoordinator(
    string applicationName,
    BootstrapFileLogger logger) : IApplicationInstanceCoordinator
{
    public WindowsApplicationInstanceCoordinator()
        : this("isTranscribe", CreateLogger())
    {
    }

    private readonly AppIdentity _identity = new AppIdentityFactory().Create(applicationName);
    private readonly object _activationGate = new();
    private readonly BootstrapFileLogger _logger = logger;
    private EventHandler? _activationRequested;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private Mutex? _mutex;
    private bool _activationPending;
    private bool _registered;
    private bool _isPrimary;
    private int _disposeState;

    public event EventHandler? ActivationRequested
    {
        add
        {
            if (value is null)
            {
                return;
            }

            bool replayPending;
            lock (_activationGate)
            {
                _activationRequested += value;
                replayPending = _activationPending;
                _activationPending = false;
            }

            if (replayPending)
            {
                value(this, EventArgs.Empty);
            }
        }
        remove
        {
            lock (_activationGate)
            {
                _activationRequested -= value;
            }
        }
    }

    internal bool HasPendingActivation
    {
        get
        {
            lock (_activationGate)
            {
                return _activationPending;
            }
        }
    }

    public ValueTask<ApplicationInstanceRole> RegisterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (_registered)
        {
            return ValueTask.FromResult(_isPrimary
                ? ApplicationInstanceRole.Primary
                : ApplicationInstanceRole.Secondary);
        }

        _mutex = new Mutex(initiallyOwned: false, _identity.MutexName, out var createdNew);
        _registered = true;
        _isPrimary = createdNew;

        if (!createdNew)
        {
            return ValueTask.FromResult(ApplicationInstanceRole.Secondary);
        }

        _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenerTask = ListenAsync(_listenerCancellation.Token);
        return ValueTask.FromResult(ApplicationInstanceRole.Primary);
    }

    public async ValueTask<bool> NotifyPrimaryAsync(CancellationToken cancellationToken)
    {
        if (!_registered || _isPrimary)
        {
            return false;
        }

        using var client = new NamedPipeClientStream(
            ".",
            _identity.ActivationPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true);
            await writer.WriteAsync(ActivationMessageSerializer.Serialize(ActivationMessage.OpenOrFocusApp())).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            _logger.Error(exception, "Failed to notify the primary release-v2 instance.");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var listenerCancellation = Interlocked.Exchange(ref _listenerCancellation, null);
        var listenerTask = Interlocked.Exchange(ref _listenerTask, null);
        var mutex = Interlocked.Exchange(ref _mutex, null);
        try
        {
            try
            {
                if (listenerCancellation is not null)
                {
                    await listenerCancellation.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
            }

            if (listenerTask is not null)
            {
                try
                {
                    await listenerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (listenerCancellation?.IsCancellationRequested == true)
                {
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Release-v2 activation listener stopped with an error during disposal.");
                }
            }
        }
        finally
        {
            listenerCancellation?.Dispose();
            mutex?.Dispose();
            lock (_activationGate)
            {
                _activationRequested = null;
                _activationPending = false;
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _identity.ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var message = ActivationMessageSerializer.Deserialize(payload);
                if (string.Equals(message.Intent, "open-or-focus-app", StringComparison.Ordinal))
                {
                    PublishActivationRequested();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Release-v2 activation listener failed.");
            }
        }
    }

    private void PublishActivationRequested()
    {
        EventHandler? handlers;
        lock (_activationGate)
        {
            handlers = _activationRequested;
            if (handlers is null)
            {
                _activationPending = true;
                return;
            }
        }

        handlers(this, EventArgs.Empty);
    }

    private static BootstrapFileLogger CreateLogger()
    {
        var paths = new LocalAppPaths("isTranscribe");
        return new BootstrapFileLogger(paths.HostLogFilePath);
    }
}
