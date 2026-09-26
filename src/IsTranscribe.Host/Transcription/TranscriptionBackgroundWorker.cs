using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Secrets;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.Host.Transcription;

public sealed class TranscriptionBackgroundWorker : IAsyncDisposable
{
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetrySchedulerInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RetrySchedule = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly BootstrapFileLogger _logger;
    private readonly MeetingSessionRepository _repository;
    private readonly ArtifactPathResolver _artifactPathResolver;
    private readonly ITranscriptionProvider _provider;
    private readonly Func<ApplicationSettings> _settingsAccessor;
    private readonly Func<AppSecrets> _secretsAccessor;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _dispatchLoop;
    private Task? _retrySchedulerLoop;

    public TranscriptionBackgroundWorker(
        BootstrapFileLogger logger,
        MeetingSessionRepository repository,
        ArtifactPathResolver artifactPathResolver,
        ITranscriptionProvider provider,
        Func<ApplicationSettings> settingsAccessor,
        Func<AppSecrets> secretsAccessor)
    {
        _logger = logger;
        _repository = repository;
        _artifactPathResolver = artifactPathResolver;
        _provider = provider;
        _settingsAccessor = settingsAccessor;
        _secretsAccessor = secretsAccessor;
    }

    // @spec spec://modules/app/FEAT-006-fireworks-transcription-and-markdown-export#queue-management.ordering
    // @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#operational-model.schedule
    public void Start()
    {
        if (_dispatchLoop is not null)
        {
            return;
        }

        _dispatchLoop = RunDispatchLoopAsync(_stopCts.Token);
        _retrySchedulerLoop = RunRetrySchedulerLoopAsync(_stopCts.Token);
    }

    public ValueTask ProcessOnceAsync(CancellationToken cancellationToken)
        => new(DispatchSingleItemAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        if (_dispatchLoop is not null)
        {
            await _dispatchLoop.ConfigureAwait(false);
        }

        if (_retrySchedulerLoop is not null)
        {
            await _retrySchedulerLoop.ConfigureAwait(false);
        }

        _stopCts.Dispose();
    }

    private async Task RunDispatchLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DispatchInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await DispatchSingleItemAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunRetrySchedulerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RetrySchedulerInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var promoted = _repository.PromoteDueScheduledRetries(DateTimeOffset.UtcNow);
            if (promoted > 0)
            {
                _logger.LogEvent("Info", "TX_QUEUED", "Scheduled retries promoted back to queue.", jobName: "RetrySchedulerJob", metadata: new Dictionary<string, object?> { ["promoted"] = promoted });
            }
        }
    }

    private async Task DispatchSingleItemAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var workItem = _repository.TryClaimNextQueuedTranscription(now);
        if (workItem is null)
        {
            return;
        }

        _logger.LogEvent("Info", "TX_UPLOADING", "Dispatching transcription request.", sessionId: workItem.Id, jobName: "TranscriptionDispatchJob");

        var settings = _settingsAccessor();
        var secrets = _secretsAccessor();
        if (string.IsNullOrWhiteSpace(secrets.FireworksApiKey))
        {
            _repository.MarkTranscriptionFailed(
                workItem.Id,
                errorCode: "TX_INVALID_API_KEY",
                errorMessage: "Fireworks API key is missing.",
                nowUtc: now,
                scheduleRetry: false,
                nextRetryAtUtc: null);
            _logger.LogEvent("Error", "TX_FAILED", "Transcription failed because API key is missing.", sessionId: workItem.Id, jobName: "TranscriptionDispatchJob");
            return;
        }

        var audioPath = ResolveAudioPath(workItem);
        if (audioPath is null)
        {
            _repository.MarkTranscriptionFailed(
                workItem.Id,
                errorCode: "TX_INPUT_MISSING",
                errorMessage: "No readable audio artifact for transcription.",
                nowUtc: now,
                scheduleRetry: false,
                nextRetryAtUtc: null);
            _logger.LogEvent("Error", "TX_FAILED", "Transcription failed because audio input artifact is missing.", sessionId: workItem.Id, jobName: "TranscriptionDispatchJob");
            return;
        }

        try
        {
            _repository.MarkTranscriptionProcessing(workItem.Id, now);
            _logger.LogEvent("Info", "TX_PROCESSING", "Transcription request accepted by provider.", sessionId: workItem.Id, jobName: "TranscriptionDispatchJob");

            var response = await _provider.TranscribeAsync(
                new TranscriptionRequest(
                    SessionId: workItem.Id,
                    AudioPath: audioPath,
                    Model: workItem.TranscriptionModel ?? settings.Transcription.Model,
                    DiarizationEnabled: workItem.DiarizationEnabled,
                    Language: workItem.Language,
                    MinSpeakers: settings.Transcription.MinSpeakers,
                    MaxSpeakers: settings.Transcription.MaxSpeakers),
                secrets.FireworksApiKey!,
                cancellationToken).ConfigureAwait(false);

            var (markdownPath, jsonPath) = MaterializeArtifacts(settings, workItem, response);
            _repository.MarkTranscriptionCompleted(workItem.Id, markdownPath, jsonPath, DateTimeOffset.UtcNow);
            _logger.LogEvent("Info", "TX_COMPLETED", "Transcription artifacts materialized successfully.", sessionId: workItem.Id, jobName: "TranscriptionDispatchJob");
        }
        catch (TranscriptionProviderException exception)
        {
            HandleProviderFailure(settings, workItem, exception);
        }
        catch (Exception exception)
        {
            var wrapped = new TranscriptionProviderException("TX_INTERNAL_ERROR", exception.Message, TranscriptionFailureKind.Terminal, exception);
            HandleProviderFailure(settings, workItem, wrapped);
        }
    }

    private void HandleProviderFailure(ApplicationSettings settings, MeetingSessionTranscriptionWorkItem workItem, TranscriptionProviderException exception)
    {
        var now = DateTimeOffset.UtcNow;
        var canAutoRetry = settings.Transcription.AutoRetry
            && exception.Kind is TranscriptionFailureKind.Transient or TranscriptionFailureKind.Recoverable
            && workItem.RetryAttemptCount < Math.Min(settings.Transcription.RetryCount, RetrySchedule.Length);

        var nextRetry = canAutoRetry
            ? now + RetrySchedule[Math.Min(workItem.RetryAttemptCount, RetrySchedule.Length - 1)]
            : (DateTimeOffset?)null;

        _repository.MarkTranscriptionFailed(
            workItem.Id,
            errorCode: exception.ErrorCode,
            errorMessage: exception.Message,
            nowUtc: now,
            scheduleRetry: canAutoRetry,
            nextRetryAtUtc: nextRetry);

        _logger.LogEvent(
            canAutoRetry ? "Warning" : "Error",
            canAutoRetry ? "TX_RETRY_SCHEDULED" : "TX_FAILED",
            exception.Message,
            sessionId: workItem.Id,
            jobName: "TranscriptionDispatchJob",
            metadata: new Dictionary<string, object?>
            {
                ["error_code"] = exception.ErrorCode,
                ["failure_kind"] = exception.Kind.ToString(),
                ["next_retry_at_utc"] = nextRetry?.ToString("O")
            });
    }

    private (string markdownPath, string jsonPath) MaterializeArtifacts(
        ApplicationSettings settings,
        MeetingSessionTranscriptionWorkItem workItem,
        TranscriptionResponse response)
    {
        var sessionId = Guid.ParseExact(workItem.Id, "N");
        var markdownPath = _artifactPathResolver.GetTranscriptFilePath(settings, sessionId, workItem.CreatedAtUtc, workItem.SourceApp, workItem.Mode, "md");
        var jsonPath = _artifactPathResolver.GetTranscriptFilePath(settings, sessionId, workItem.CreatedAtUtc, workItem.SourceApp, workItem.Mode, "json");

        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

        File.WriteAllText(markdownPath, TranscriptionMarkdownRenderer.Render(workItem, response));
        File.WriteAllText(jsonPath, response.RawJson);

        return (markdownPath, jsonPath);
    }

    private static string? ResolveAudioPath(MeetingSessionTranscriptionWorkItem workItem)
    {
        if (IsReadableFile(workItem.AudioMixPath))
        {
            return workItem.AudioMixPath;
        }

        if (IsReadableFile(workItem.AudioOutputPath))
        {
            return workItem.AudioOutputPath;
        }

        if (IsReadableFile(workItem.AudioMicPath))
        {
            return workItem.AudioMicPath;
        }

        return null;
    }

    private static bool IsReadableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
