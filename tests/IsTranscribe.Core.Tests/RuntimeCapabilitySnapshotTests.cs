using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Detection;
using Xunit;

namespace IsTranscribe.Core.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states
/// </summary>
public sealed class RuntimeCapabilitySnapshotTests
{
    [Fact]
    public void CaptureFailureCanBeTypedWhileEndpointsRemainPresent()
    {
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: "Capture needs attention.")
        {
            Issue = RuntimeCapabilityIssue.MicrophoneCaptureUnavailable,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };

        Assert.Equal(RuntimeCapabilityIssue.MicrophoneCaptureUnavailable, capability.Issue);
        Assert.True(capability.HasActiveOutput);
        Assert.True(capability.HasActiveMicrophone);
    }

    [Fact]
    public void SpeechSnapshotCarriesMicrophoneCaptureHealthWithoutAudioPayload()
    {
        var snapshot = MeetingSpeechActivitySnapshot.Empty with
        {
            MicrophoneCaptureHealth = MeetingSpeechCaptureHealth.Unavailable
        };

        Assert.Equal(
            MeetingSpeechCaptureHealth.Unavailable,
            snapshot.MicrophoneCaptureHealth);
        Assert.Empty(snapshot.RenderByRootProcessId);
    }
}
