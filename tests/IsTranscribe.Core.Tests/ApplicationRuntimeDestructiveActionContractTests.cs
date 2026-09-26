using IsTranscribe.Application.Runtime;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </summary>
public sealed class ApplicationRuntimeDestructiveActionContractTests
{
    [Fact]
    public void RuntimeBoundaryExposesConfirmedActiveAndRecentRecordingRemoval()
    {
        var discard = typeof(IApplicationRuntime).GetMethod(
            nameof(IApplicationRuntime.DiscardRecordingAsync));
        var removeRecent = typeof(IApplicationRuntime).GetMethod(
            nameof(IApplicationRuntime.RemoveRecentRecordingAsync));
        var acknowledgeAttention = typeof(IApplicationRuntime).GetMethod(
            nameof(IApplicationRuntime.AcknowledgeAttentionAsync));

        Assert.NotNull(discard);
        Assert.Equal(typeof(ValueTask), discard.ReturnType);
        Assert.Equal([typeof(CancellationToken)], discard.GetParameters().Select(static item => item.ParameterType));

        Assert.NotNull(removeRecent);
        Assert.Equal(typeof(ValueTask), removeRecent.ReturnType);
        Assert.Equal(
            [typeof(Guid), typeof(bool), typeof(CancellationToken)],
            removeRecent.GetParameters().Select(static item => item.ParameterType));

        Assert.NotNull(acknowledgeAttention);
        Assert.Equal(typeof(ValueTask), acknowledgeAttention.ReturnType);
        Assert.Equal(
            [typeof(Guid), typeof(CancellationToken)],
            acknowledgeAttention.GetParameters().Select(static item => item.ParameterType));
    }
}
