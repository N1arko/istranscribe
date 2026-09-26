using IsTranscribe.Host.Audio.Capture;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification
/// </summary>
public sealed class CaptureAdapterLifecycleArbiterTests
{
    [Fact]
    public async Task NaturalFinalizationWaitsForPromotionAndObservesPromotedArtifact()
    {
        var arbiter = new CaptureAdapterLifecycleArbiter();
        var promotionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePromotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promoted = false;
        var promotion = Task.Run(() => arbiter.TryPromote(() =>
        {
            promotionEntered.TrySetResult();
            releasePromotion.Task.GetAwaiter().GetResult();
            promoted = true;
        }));
        await promotionEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var finalization = Task.Run(() => arbiter.Finalize(
            () => promoted ? "artifact" : null,
            () => null));
        Assert.False(finalization.IsCompleted);

        releasePromotion.TrySetResult();
        await promotion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("artifact", await finalization.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PromotionBecomesNoOpAfterNaturalFinalizationClaim()
    {
        var arbiter = new CaptureAdapterLifecycleArbiter();
        var finalizationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promoted = false;
        var finalization = Task.Run(() => arbiter.Finalize(
            () =>
            {
                finalizationEntered.TrySetResult();
                releaseFinalization.Task.GetAwaiter().GetResult();
                return "artifact";
            },
            () => "artifact"));
        await finalizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var promotion = Task.Run(() => arbiter.TryPromote(() => promoted = true));
        Assert.False(promotion.IsCompleted);

        releaseFinalization.TrySetResult();
        await Task.WhenAll(finalization, promotion).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(promoted);
    }

    [Fact]
    public void PromotionBecomesNoOpAfterExplicitStopClaim()
    {
        var arbiter = new CaptureAdapterLifecycleArbiter();
        var promoted = false;

        arbiter.RequestStop(() => { });
        var accepted = arbiter.TryPromote(() => promoted = true);

        Assert.False(accepted);
        Assert.False(promoted);
        Assert.True(arbiter.IsStopRequested);
    }

    [Fact]
    public void FailedStopInitiationCanBeRetried()
    {
        var arbiter = new CaptureAdapterLifecycleArbiter();
        var attempts = 0;

        Assert.Throws<IOException>(() => arbiter.RequestStop(() =>
        {
            attempts++;
            throw new IOException("Injected stop initiation failure.");
        }));
        Assert.False(arbiter.IsStopRequested);

        arbiter.RequestStop(() => attempts++);

        Assert.Equal(2, attempts);
        Assert.True(arbiter.IsStopRequested);
    }
}
