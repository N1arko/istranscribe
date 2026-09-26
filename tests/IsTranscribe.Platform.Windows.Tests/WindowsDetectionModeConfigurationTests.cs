using IsTranscribe.Core.Detection;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// </summary>
public sealed class WindowsDetectionModeConfigurationTests
{
    [Theory]
    [InlineData(null, null, MeetingDetectionMode.Live)]
    [InlineData("--background", null, MeetingDetectionMode.Live)]
    [InlineData("--detection-shadow", null, MeetingDetectionMode.Shadow)]
    [InlineData(null, "shadow", MeetingDetectionMode.Shadow)]
    [InlineData("--background", "SHADOW", MeetingDetectionMode.Shadow)]
    public void ShippingCompositionCanResolveLiveOrShadowMode(
        string? argument,
        string? environmentValue,
        MeetingDetectionMode expected)
    {
        var arguments = argument is null ? null : new[] { argument };

        var mode = WindowsDetectionModeConfiguration.Resolve(arguments, environmentValue);

        Assert.Equal(expected, mode);
    }
}
