using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using IsTranscribe.Core.Detection;
using IsTranscribe.Platform.Windows;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Reads privacy-sensitive process/window identity only in memory so evidence can
/// bind a prompt to the measured client without persisting raw window titles.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#validation-levels
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ClientProcessDiscovery
{
    public static ClientDiscoveryResult Discover(
        string profileId,
        string surface,
        string? preferredClientProcessName = null)
    {
        try
        {
            return DiscoverCore(profileId, surface, preferredClientProcessName);
        }
        catch (Exception)
        {
            return ClientDiscoveryResult.Failed("client_discovery_failed");
        }
    }

    private static ClientDiscoveryResult DiscoverCore(
        string profileId,
        string surface,
        string? preferredClientProcessName)
    {
        var profile = new MeetingProfileRegistry().FindById(profileId)
            ?? throw new ArgumentException($"Unknown profile: {profileId}", nameof(profileId));
        var processNames = surface == "desktop"
            ? profile.DedicatedProcessNames
            : profile.BrowserProcessNames;
        if (!string.IsNullOrWhiteSpace(preferredClientProcessName))
        {
            processNames = processNames
                .Where(name => string.Equals(
                    name,
                    preferredClientProcessName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (processNames.Count == 0)
        {
            return ClientDiscoveryResult.NotFound("surface_not_supported_by_profile");
        }

        var candidates = new List<ClientProcessIdentity>();
        foreach (var configuredName in processNames)
        {
            var processName = Path.GetFileNameWithoutExtension(configuredName);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        process.Refresh();
                        var title = process.MainWindowTitle;
                        var visible = process.MainWindowHandle != IntPtr.Zero
                            && !string.IsNullOrWhiteSpace(title);
                        var titleMatchesProfile = visible
                            && profile.WindowRules.Any(rule => rule.Matches(title));
                        var executablePath = TryGetExecutablePath(process);
                        var version = TryGetVersion(executablePath);
                        var hash = TryHash(executablePath);
                        candidates.Add(new ClientProcessIdentity(
                            process.Id,
                            process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                ? process.ProcessName
                                : $"{process.ProcessName}.exe",
                            title,
                            visible,
                            titleMatchesProfile,
                            version,
                            hash));
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        var selected = candidates
            .OrderByDescending(static candidate => candidate.TitleMatchesProfile)
            .ThenByDescending(static candidate => candidate.Visible)
            .FirstOrDefault();
        if (selected is null)
        {
            return ClientDiscoveryResult.NotFound("client_process_not_found");
        }

        var processIds = candidates
            .Select(static candidate => candidate.ProcessId)
            .ToHashSet();
        var allVisibleTitles = WindowsWindowEvidenceProvider.EnumerateTopLevelWindows()
            .Where(window => processIds.Contains(window.ProcessId))
            .Select(static window => window.Title)
            .Concat(candidates
                .Where(static candidate => candidate.Visible)
                .Select(static candidate => candidate.RawWindowTitle))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ClientDiscoveryResult(
            Found: true,
            DiscoveryFailed: false,
            selected.ProcessId,
            selected.ProcessName,
            allVisibleTitles,
            selected.Version,
            selected.ExecutableSha256,
            candidates,
            ReasonCode: null);
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstNonEmpty(info.ProductVersion, info.FileVersion);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? TryHash(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(executablePath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

}

internal sealed record ClientProcessIdentity(
    int ProcessId,
    string ProcessName,
    string RawWindowTitle,
    bool Visible,
    bool TitleMatchesProfile,
    string? Version,
    string? ExecutableSha256);

internal sealed record ClientDiscoveryResult(
    bool Found,
    bool DiscoveryFailed,
    int? ProcessId,
    string? ProcessName,
    IReadOnlyList<string> RawWindowTitles,
    string? Version,
    string? ExecutableSha256,
    IReadOnlyList<ClientProcessIdentity> Processes,
    string? ReasonCode)
{
    public static ClientDiscoveryResult NotFound(string reasonCode) => new(
        Found: false,
        DiscoveryFailed: false,
        ProcessId: null,
        ProcessName: null,
        RawWindowTitles: [],
        Version: null,
        ExecutableSha256: null,
        Processes: [],
        reasonCode);

    public static ClientDiscoveryResult Failed(string reasonCode) =>
        NotFound(reasonCode) with { DiscoveryFailed = true };

    public ClientDiscoveryResult BindToProcess(int? processId)
    {
        var identity = processId.HasValue
            ? Processes.FirstOrDefault(process => process.ProcessId == processId.Value)
            : null;
        return identity is null
            ? this with
            {
                ProcessId = null,
                ProcessName = null,
                Version = null,
                ExecutableSha256 = null
            }
            : this with
            {
                ProcessId = identity.ProcessId,
                ProcessName = identity.ProcessName,
                Version = identity.Version,
                ExecutableSha256 = identity.ExecutableSha256
            };
    }
}
