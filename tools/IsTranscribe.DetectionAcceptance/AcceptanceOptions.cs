using IsTranscribe.Core.Detection;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Closed command contract for one FEAT-011 profile/surface acceptance scenario.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#validation-levels
/// </remarks>
public sealed record AcceptanceOptions(
    string ProfileId,
    string Surface,
    string OutputDirectory,
    bool OperatorReady,
    TimeSpan Timeout,
    string? PreferredClientProcessName)
{
    private static readonly HashSet<string> SupportedProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "zoom",
        "microsoft-teams",
        "google-meet",
        "yandex-telemost",
        "kontur-talk"
    };

    public static AcceptanceOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? profileId = null;
        string? surface = null;
        string? outputDirectory = null;
        string? preferredClientProcessName = null;
        var operatorReady = false;
        var timeoutSeconds = 15;

        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--profile":
                    profileId = ReadValue(arguments, ref index, "--profile");
                    break;
                case "--surface":
                    surface = ReadValue(arguments, ref index, "--surface");
                    break;
                case "--output":
                    outputDirectory = ReadValue(arguments, ref index, "--output");
                    break;
                case "--timeout-seconds":
                    var rawTimeout = ReadValue(arguments, ref index, "--timeout-seconds");
                    if (!int.TryParse(rawTimeout, out timeoutSeconds) || timeoutSeconds is < 1 or > 60)
                    {
                        throw new ArgumentException("--timeout-seconds must be between 1 and 60.");
                    }

                    break;
                case "--client-process":
                    preferredClientProcessName = NormalizeProcessName(
                        ReadValue(arguments, ref index, "--client-process"));
                    break;
                case "--operator-ready":
                    operatorReady = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arguments[index]}");
            }
        }

        profileId = profileId?.Trim().ToLowerInvariant();
        surface = surface?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(profileId) || !SupportedProfiles.Contains(profileId))
        {
            throw new ArgumentException("--profile must name a supported built-in meeting profile.");
        }

        if (surface is not ("desktop" or "browser"))
        {
            throw new ArgumentException("--surface must be desktop or browser.");
        }

        if (preferredClientProcessName is not null)
        {
            var profile = new MeetingProfileRegistry().FindById(profileId)!;
            var allowedProcessNames = surface == "desktop"
                ? profile.DedicatedProcessNames
                : profile.BrowserProcessNames;
            if (!allowedProcessNames.Contains(preferredClientProcessName, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--client-process must belong to the selected profile and surface.");
            }
        }

        outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "acceptance",
                "FEAT-011",
                "windows-x64")
            : Path.GetFullPath(outputDirectory);

        return new AcceptanceOptions(
            profileId,
            surface,
            outputDirectory,
            operatorReady,
            TimeSpan.FromSeconds(timeoutSeconds),
            preferredClientProcessName);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        index++;
        if (index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[index];
    }

    private static string NormalizeProcessName(string value)
    {
        var trimmed = value.Trim();
        if (!string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal)
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("--client-process must be an executable name, not a path.");
        }

        var normalized = trimmed.ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.Ordinal)
            ? normalized
            : $"{normalized}.exe";
    }
}
