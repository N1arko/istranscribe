using IsTranscribe.Host.Capabilities;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.App.Configuration;

internal static class CapabilityAwareSettingsSanitizer
{
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#degraded
    public static ApplicationSettings Sanitize(ApplicationSettings settings, HostCapabilitySnapshot? capability)
    {
        if (capability?.State != HostCapabilityState.Degraded)
        {
            return settings;
        }

        var hasProcessOutput = settings.Recording.DefaultSourcesAuto.Contains("process_output", StringComparer.OrdinalIgnoreCase);
        if (!hasProcessOutput)
        {
            return settings;
        }

        var sanitizedAutoSources = settings.Recording.DefaultSourcesAuto
            .Select(static source => string.Equals(source, "process_output", StringComparison.OrdinalIgnoreCase) ? "device_loopback" : source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return settings with
        {
            Recording = settings.Recording with
            {
                DefaultSourcesAuto = sanitizedAutoSources
            }
        };
    }
}
