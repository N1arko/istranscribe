namespace IsTranscribe.Host.Audio.Capture;

/// <summary>
/// Completes an adapter stop notification even when closing its source artifact fails.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
internal sealed class CaptureStopCompletion
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completionStarted;

    public Task WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    public void Complete(
        Action finalize,
        Action<Exception> reportFinalizationFailure,
        Action notifyStopped)
    {
        ArgumentNullException.ThrowIfNull(finalize);
        ArgumentNullException.ThrowIfNull(reportFinalizationFailure);
        ArgumentNullException.ThrowIfNull(notifyStopped);

        if (Interlocked.Exchange(ref _completionStarted, 1) != 0)
        {
            return;
        }

        try
        {
            try
            {
                finalize();
            }
            catch (Exception exception)
            {
                reportFinalizationFailure(exception);
            }
        }
        finally
        {
            _completion.TrySetResult();
            notifyStopped();
        }
    }
}
