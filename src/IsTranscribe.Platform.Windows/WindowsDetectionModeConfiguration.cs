using IsTranscribe.Core.Detection;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Operational release switch for running the shipping detector in privacy-safe
/// shadow mode during quality comparison and rollout.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#shadow-mode-and-diagnostics
/// </remarks>
public static class WindowsDetectionModeConfiguration
{
    public const string ShadowCommandLineSwitch = "--detection-shadow";
    public const string EnvironmentVariableName = "ISTRANSCRIBE_DETECTION_MODE";

    public static MeetingDetectionMode Resolve(
        IReadOnlyList<string>? commandLineArguments,
        string? environmentValue)
    {
        var commandLineRequestsShadow = commandLineArguments?.Any(argument =>
            string.Equals(argument, ShadowCommandLineSwitch, StringComparison.OrdinalIgnoreCase)) == true;
        var environmentRequestsShadow = string.Equals(
            environmentValue?.Trim(),
            "shadow",
            StringComparison.OrdinalIgnoreCase);
        return commandLineRequestsShadow || environmentRequestsShadow
            ? MeetingDetectionMode.Shadow
            : MeetingDetectionMode.Live;
    }
}
