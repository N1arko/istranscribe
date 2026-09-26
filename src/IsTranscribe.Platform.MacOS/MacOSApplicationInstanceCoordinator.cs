using System.IO.Pipes;
using System.Text;
using System.Security.Cryptography;
using IsTranscribe.Core.Platform;

namespace IsTranscribe.Platform.MacOS;

/// <summary>
/// Coordinates one per-user macOS process and forwards open/focus activation to the primary process.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#system-services
/// </remarks>
public sealed class MacOSApplicationInstanceCoordinator : IApplicationInstanceCoordinator
{
    private readonly string _lockPath;
    private readonly string _pipeName;
    private readonly object _activationGate = new();
    private EventHandler? _activationRequested;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private FileStream? _lockStream;
    private bool _activationPending;
    private bool _registered;
    private bool _primary;
    private int _disposed;

    public MacOSApplicationInstanceCoordinator() : this("isTranscribe")
    {
    }

    internal MacOSApplicationInstanceCoordinator(string applicationName)
    {
        var rawIdentity = $"{Sanitize(applicationName)}-{GetUserId()}";
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawIdentity)))[..12].ToLowerInvariant();
        _lockPath = Path.Combine(Path.GetTempPath(), $"istranscribe-{identity}.lock");
        _pipeName = $"ist-{identity}";
    }

    public event EventHandler? ActivationRequested
    {
        add
        {
            if (value is null) return;
            var replay = false;
            lock (_activationGate)
            {
                _activationRequested += value;
                replay = _activationPending;
                _activationPending = false;
            }
            if (replay) value(this, EventArgs.Empty);
        }
        remove
        {
            lock (_activationGate) _activationRequested -= value;
        }
    }

    public ValueTask<ApplicationInstanceRole> RegisterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_registered)
        {
            return ValueTask.FromResult(_primary ? ApplicationInstanceRole.Primary : ApplicationInstanceRole.Secondary);
        }

        try
        {
            _lockStream = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            _primary = true;
        }
        catch (IOException)
        {
            _primary = false;
        }
        _registered = true;
        if (_primary)
        {
            _listenerCancellation = new CancellationTokenSource();
            _listenerTask = ListenAsync(_listenerCancellation.Token);
        }
        return ValueTask.FromResult(_primary ? ApplicationInstanceRole.Primary : ApplicationInstanceRole.Secondary);
    }

    public async ValueTask<bool> NotifyPrimaryAsync(CancellationToken cancellationToken)
    {
        if (!_registered || _primary) return false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
                await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true);
                await writer.WriteAsync("open-or-focus-app").ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) when (attempt < 19)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_listenerCancellation is not null) await _listenerCancellation.CancelAsync().ConfigureAwait(false);
        if (_listenerTask is not null)
        {
            try { await _listenerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _listenerCancellation?.Dispose();
        _lockStream?.Dispose();
        lock (_activationGate)
        {
            _activationRequested = null;
            _activationPending = false;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                if (string.Equals(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), "open-or-focus-app", StringComparison.Ordinal))
                {
                    PublishActivation();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private void PublishActivation()
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

    private static string Sanitize(string value) => new(value
        .Where(static character => char.IsAsciiLetterOrDigit(character) || character == '-')
        .ToArray());

    private static uint GetUserId() => OperatingSystem.IsMacOS() ? getuid() : 0;

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getuid();
}
