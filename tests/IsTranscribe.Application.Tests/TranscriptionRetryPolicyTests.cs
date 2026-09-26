using IsTranscribe.Application.Transcription;
using IsTranscribe.Core.Transcription;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// </summary>
public sealed class TranscriptionRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    public void TransientScheduleUsesOneFiveAndFifteenMinutes(int attempts, int expectedMinutes)
    {
        var policy = new TranscriptionRetryPolicy(() => 0.5);

        var decision = policy.Decide(Error(TranscriptionFailureDisposition.TryAgain), attempts, Now);

        Assert.Equal(TranscriptionFailureAction.ScheduleRetry, decision.Action);
        Assert.Equal(Now.AddMinutes(expectedMinutes), decision.NextAttemptAt);
    }

    [Fact]
    public void EngineSuggestedDelayTakesPriorityOverLocalSchedule()
    {
        var policy = new TranscriptionRetryPolicy(() => 0);
        var error = new TranscriptionError(
            TranscriptionErrorCategory.RateLimited,
            "rate_limited",
            "The engine asked the client to wait.",
            suggestedDelay: TimeSpan.FromMinutes(7),
            disposition: TranscriptionFailureDisposition.TryAgain);

        var decision = policy.Decide(error, completedTransientAttempts: 0, Now);

        Assert.Equal(Now.AddMinutes(7), decision.NextAttemptAt);
    }

    [Fact]
    public void FourthTransientFailureRequiresUserAttention()
    {
        var decision = new TranscriptionRetryPolicy(() => 0.5)
            .Decide(Error(TranscriptionFailureDisposition.TryAgain), completedTransientAttempts: 3, Now);

        Assert.Equal(TranscriptionFailureAction.RequireAttention, decision.Action);
        Assert.Null(decision.NextAttemptAt);
    }

    [Theory]
    [InlineData(TranscriptionFailureDisposition.SplitInput, TranscriptionFailureAction.SplitChunk)]
    [InlineData(TranscriptionFailureDisposition.AttentionRequired, TranscriptionFailureAction.RequireAttention)]
    [InlineData(TranscriptionFailureDisposition.Terminal, TranscriptionFailureAction.Fail)]
    public void NonTransientDispositionMapsWithoutScheduling(
        TranscriptionFailureDisposition disposition,
        TranscriptionFailureAction expected)
    {
        var decision = new TranscriptionRetryPolicy()
            .Decide(Error(disposition), completedTransientAttempts: 0, Now);

        Assert.Equal(expected, decision.Action);
        Assert.Null(decision.NextAttemptAt);
    }

    private static TranscriptionError Error(TranscriptionFailureDisposition disposition) => new(
        TranscriptionErrorCategory.Network,
        "network_error",
        "The engine could not be reached.",
        disposition: disposition);
}
