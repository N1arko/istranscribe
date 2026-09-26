using IsTranscribe.App.Configuration;
using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.App.Tests;

public sealed class CapabilityAwareSettingsSanitizerTests
{
    [Fact]
    public void DegradedSanitizerReplacesProcessOutputWithoutInjectingMicrophone()
    {
        var settings = ApplicationSettings.Default with
        {
            Recording = ApplicationSettings.Default.Recording with
            {
                DefaultSourcesAuto = ["process_output"]
            }
        };

        var sanitized = CapabilityAwareSettingsSanitizer.Sanitize(
            settings,
            new HostCapabilitySnapshot(HostCapabilityState.Degraded, false, "degraded"));

        Assert.Equal(["device_loopback"], sanitized.Recording.DefaultSourcesAuto);
    }

    [Fact]
    public void DegradedSanitizerPreservesExistingMicrophoneSelection()
    {
        var settings = ApplicationSettings.Default with
        {
            Recording = ApplicationSettings.Default.Recording with
            {
                DefaultSourcesAuto = ["process_output", "mic"]
            }
        };

        var sanitized = CapabilityAwareSettingsSanitizer.Sanitize(
            settings,
            new HostCapabilitySnapshot(HostCapabilityState.Degraded, false, "degraded"));

        Assert.Equal(["device_loopback", "mic"], sanitized.Recording.DefaultSourcesAuto);
    }
}
