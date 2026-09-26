using IsTranscribe.Host.Capabilities;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Bootstrap;

public sealed class OperationalBackgroundServicesCoordinator(
    IBackgroundServicesCoordinator inner,
    LocalAppPaths paths,
    BootstrapFileLogger logger) : IBackgroundServicesCoordinator, IAsyncDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan TempRetention = TimeSpan.FromDays(7);

    private readonly IBackgroundServicesCoordinator _inner = inner;
    private readonly LocalAppPaths _paths = paths;
    private readonly BootstrapFileLogger _logger = logger;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _cleanupLoop;
    private bool _running;

    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#operational-model.jobs
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#operational-model.schedule
    public async ValueTask StartAsync(HostCapabilitySnapshot capability, CancellationToken cancellationToken)
    {
        if (_running)
        {
            return;
        }

        await _inner.StartAsync(capability, cancellationToken).ConfigureAwait(false);
        _running = true;
        _cleanupLoop = RunCleanupLoopAsync(_stopCts.Token);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (!_running)
        {
            return;
        }

        _stopCts.Cancel();
        if (_cleanupLoop is not null)
        {
            await _cleanupLoop.ConfigureAwait(false);
            _cleanupLoop = null;
        }

        await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
        _running = false;
    }

    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        if (_cleanupLoop is not null)
        {
            await _cleanupLoop.ConfigureAwait(false);
        }

        _stopCts.Dispose();
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        await RunCleanupPassAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RunCleanupPassAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunCleanupPassAsync(CancellationToken cancellationToken)
    {
        _logger.LogEvent("Info", "CLEANUP_STARTED", "Cleanup job started.", jobName: "CleanupJob");

        var deletedFiles = 0;
        var skippedFiles = 0;
        try
        {
            Directory.CreateDirectory(_paths.TempDirectory);
            var threshold = DateTimeOffset.UtcNow - TempRetention;
            var files = Directory.EnumerateFiles(_paths.TempDirectory, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToArray();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.LastWriteTimeUtc > threshold.UtcDateTime)
                {
                    skippedFiles++;
                    continue;
                }

                try
                {
                    file.Delete();
                    deletedFiles++;
                }
                catch
                {
                    skippedFiles++;
                    _logger.LogEvent(
                        "Warning",
                        "CLEANUP_SKIPPED",
                        "Failed to delete stale temp artifact.",
                        jobName: "CleanupJob",
                        metadata: new Dictionary<string, object?>
                        {
                            ["path"] = file.FullName
                        });
                }
            }

            _logger.LogEvent(
                "Info",
                "CLEANUP_COMPLETED",
                "Cleanup job completed.",
                jobName: "CleanupJob",
                metadata: new Dictionary<string, object?>
                {
                    ["deleted_files"] = deletedFiles,
                    ["skipped_files"] = skippedFiles
                });
        }
        catch (OperationCanceledException)
        {
            // Graceful host shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogEvent(
                "Error",
                "CLEANUP_SKIPPED",
                "Cleanup job failed unexpectedly.",
                jobName: "CleanupJob",
                metadata: new Dictionary<string, object?>
                {
                    ["exception"] = exception.ToString()
                });
        }

        await Task.CompletedTask;
    }
}
