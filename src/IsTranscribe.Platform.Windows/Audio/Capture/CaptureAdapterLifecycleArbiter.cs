namespace IsTranscribe.Host.Audio.Capture;

/// <summary>
/// Serializes prebuffer promotion, stop ownership and finalization for one capture adapter.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
/// </remarks>
internal sealed class CaptureAdapterLifecycleArbiter
{
    private readonly object _gate = new();
    private bool _finalized;
    private bool _stopRequested;

    public bool IsStopRequested
    {
        get
        {
            lock (_gate)
            {
                return _stopRequested;
            }
        }
    }

    public void RequestStop(Action requestStop)
    {
        ArgumentNullException.ThrowIfNull(requestStop);

        lock (_gate)
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;
        }

        try
        {
            requestStop();
        }
        catch
        {
            lock (_gate)
            {
                if (!_finalized)
                {
                    _stopRequested = false;
                }
            }

            throw;
        }
    }

    public bool TryPromote(Action promote)
    {
        ArgumentNullException.ThrowIfNull(promote);

        lock (_gate)
        {
            if (_stopRequested || _finalized)
            {
                return false;
            }

            promote();
            return true;
        }
    }

    public TResult Finalize<TResult>(Func<TResult> finalize, Func<TResult> getFinalizedResult)
    {
        ArgumentNullException.ThrowIfNull(finalize);
        ArgumentNullException.ThrowIfNull(getFinalizedResult);

        lock (_gate)
        {
            if (_finalized)
            {
                return getFinalizedResult();
            }

            try
            {
                return finalize();
            }
            finally
            {
                _finalized = true;
            }
        }
    }
}
