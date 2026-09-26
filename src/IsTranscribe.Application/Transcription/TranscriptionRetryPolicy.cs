using IsTranscribe.Core.Transcription;

namespace IsTranscribe.Application.Transcription;

/// <summary>
/// Queue action derived from one normalized engine failure.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#transitions.retry-state-machine
/// </remarks>
public sealed record TranscriptionFailureDecision(
    TranscriptionFailureAction Action,
    DateTimeOffset? NextAttemptAt = null);

public enum TranscriptionFailureAction
{
    ScheduleRetry,
    SplitChunk,
    RequireAttention,
    Fail
}

/// <summary>
/// Bounded three-attempt schedule with an injectable jitter source for deterministic tests.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// </remarks>
public sealed class TranscriptionRetryPolicy(Func<double>? jitterSource = null)
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    ];

    private readonly Func<double> _jitterSource = jitterSource ?? Random.Shared.NextDouble;

    public TranscriptionFailureDecision Decide(
        TranscriptionError error,
        int completedTransientAttempts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (completedTransientAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedTransientAttempts),
                completedTransientAttempts,
                "Completed attempt count cannot be negative.");
        }

        return error.Disposition switch
        {
            TranscriptionFailureDisposition.SplitInput =>
                new TranscriptionFailureDecision(TranscriptionFailureAction.SplitChunk),
            TranscriptionFailureDisposition.AttentionRequired =>
                new TranscriptionFailureDecision(TranscriptionFailureAction.RequireAttention),
            TranscriptionFailureDisposition.TryAgain =>
                Schedule(error, completedTransientAttempts, now),
            _ => new TranscriptionFailureDecision(TranscriptionFailureAction.Fail)
        };
    }

    private TranscriptionFailureDecision Schedule(
        TranscriptionError error,
        int completedTransientAttempts,
        DateTimeOffset now)
    {
        if (completedTransientAttempts >= DefaultDelays.Length)
        {
            return new TranscriptionFailureDecision(TranscriptionFailureAction.RequireAttention);
        }

        var delay = error.SuggestedDelay ?? ApplyJitter(DefaultDelays[completedTransientAttempts]);
        return new TranscriptionFailureDecision(
            TranscriptionFailureAction.ScheduleRetry,
            now.Add(delay));
    }

    private TimeSpan ApplyJitter(TimeSpan delay)
    {
        var sample = _jitterSource();
        if (!double.IsFinite(sample) || sample is < 0 or > 1)
        {
            throw new InvalidOperationException("The jitter source must return a finite value between zero and one.");
        }

        var multiplier = 0.9 + (sample * 0.2);
        return TimeSpan.FromTicks(checked((long)Math.Round(
            delay.Ticks * multiplier,
            MidpointRounding.AwayFromZero)));
    }
}
