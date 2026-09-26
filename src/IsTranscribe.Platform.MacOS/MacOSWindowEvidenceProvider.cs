using System.Globalization;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;

namespace IsTranscribe.Platform.MacOS;

internal static class MacOSProcessIdentity
{
    public static string Normalize(string processName, string? bundleId = null)
    {
        var value = $"{processName} {bundleId}".ToLowerInvariant();
        if (value.Contains("zoom")) return "zoom.exe";
        if (value.Contains("teams")) return "ms-teams.exe";
        if (value.Contains("google chrome") || value.Contains("com.google.chrome")) return "chrome.exe";
        if (value.Contains("microsoft edge") || value.Contains("com.microsoft.edgemac")) return "msedge.exe";
        if (value.Contains("firefox")) return "firefox.exe";
        if (value.Contains("zen")) return "zen.exe";
        if (value.Contains("safari")) return "safari.exe";
        if (value.Contains("yandex") && value.Contains("browser")) return "yandex.exe";
        if (value.Contains("telemost")) return "telemost.exe";
        if (value.Contains("kontur") || value.Contains("ktalk") || value.Contains("tolk")) return "ktalk.exe";

        var trimmed = processName.Trim().ToLowerInvariant();
        return trimmed.EndsWith(".exe", StringComparison.Ordinal) ? trimmed : $"{trimmed}.exe";
    }
}

/// <summary>
/// Reduces Core Graphics and Accessibility state to stable matched rule identifiers.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#audio-detection
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
public sealed class MacOSWindowEvidenceProvider : IMeetingWindowEvidenceProvider
{
    private readonly IMacOSMeetingNative _native;

    public MacOSWindowEvidenceProvider() : this(new MacOSMeetingNative())
    {
    }

    internal MacOSWindowEvidenceProvider(IMacOSMeetingNative native) => _native = native;

    public ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
        AudioPlatformSnapshot audioPlatform,
        MeetingProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioPlatform);
        ArgumentNullException.ThrowIfNull(profiles);
        cancellationToken.ThrowIfCancellationRequested();
        var observedInventory = _native.EnumerateWindows(includeAccessibility: false);
        var zoomMeetingRoots = audioPlatform.Processes
            .Where(process => HasActiveRender(audioPlatform, process))
            .Where(process => profiles.MatchProcess(process.ProcessName)
                .Any(static profile => string.Equals(profile.Id, "zoom", StringComparison.Ordinal)))
            .Where(process => process.ProcessTreeIds.Any(_native.HasZoomMeetingHost))
            .Select(static process => process.RootProcessId)
            .ToHashSet();
        var inventory = observedInventory
            .Select(window =>
            {
                var process = FindMatchingProcess(window, audioPlatform.Processes);
                return process is not null
                       && zoomMeetingRoots.Contains(process.RootProcessId)
                    ? window with { Title = "Zoom Meeting" }
                    : window;
            })
            .ToList();
        foreach (var process in audioPlatform.Processes.Where(process =>
                     zoomMeetingRoots.Contains(process.RootProcessId)))
        {
            if (inventory.Any(window => MatchesProcess(window, process)))
            {
                continue;
            }

            // Core Graphics omits layer-zero windows that moved to another macOS Space.
            // Keep the stable Zoom candidate while its meeting host and attributed audio remain active.
            inventory.Add(new MacOSNativeWindow(
                process.RootProcessId,
                WindowId: 0,
                IsForeground: false,
                OwnerName: process.ProcessName,
                Title: "Zoom Meeting",
                AccessibleControlNames: []));
        }
        var titleQueries = new Dictionary<(int ProcessId, uint WindowId), IReadOnlyList<int>>();
        var titleQueriedProcessIds = new HashSet<int>();
        var processNamesByWindowProcessId = new Dictionary<int, string>();
        foreach (var window in inventory)
        {
            var process = FindMatchingProcess(window, audioPlatform.Processes);
            if (process is null)
            {
                continue;
            }

            processNamesByWindowProcessId.TryAdd(window.ProcessId, process.ProcessName);
            var matchedProfiles = profiles.MatchProcess(process.ProcessName);
            if (!zoomMeetingRoots.Contains(process.RootProcessId)
                && HasActiveRender(audioPlatform, process)
                && matchedProfiles.Any(profile => NeedsAccessibilityTitle(profile, window.Title))
                && titleQueriedProcessIds.Add(window.ProcessId))
            {
                titleQueries.TryAdd(
                    (window.ProcessId, window.WindowId),
                    AccessibilityProcessIds(window, process));
            }
        }

        IReadOnlyDictionary<(int ProcessId, uint WindowId), string> accessibleTitlesByWindow =
            _native.HasAccessibilityPermission
            ? titleQueries.ToDictionary(
                static query => query.Key,
                query => string.Join('\n', query.Value
                    .Select(_native.ReadAccessibleWindowTitle)
                    .SelectMany(static title => title.Split(
                        '\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase)))
            : new Dictionary<(int ProcessId, uint WindowId), string>();
        var controlQueries = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var window in inventory)
        {
            var process = FindMatchingProcess(window, audioPlatform.Processes);
            if (process is null || !HasActiveRender(audioPlatform, process)) continue;
            if (zoomMeetingRoots.Contains(process.RootProcessId)) continue;
            var resolvedTitle = ResolveWindowTitle(
                window,
                accessibleTitlesByWindow,
                processNamesByWindowProcessId,
                profiles);
            if (profiles.MatchProcess(process.ProcessName).Any(profile =>
                    NeedsAccessibilityControls(profile, process.ProcessName, resolvedTitle)))
            {
                controlQueries.TryAdd(window.ProcessId, AccessibilityProcessIds(window, process));
            }
        }

        IReadOnlyDictionary<int, IReadOnlyList<string>> controlsByProcessId = _native.HasAccessibilityPermission
            ? controlQueries.ToDictionary(
                static query => query.Key,
                query => (IReadOnlyList<string>)query.Value
                    .SelectMany(_native.ReadAccessibleControlNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray())
            : new Dictionary<int, IReadOnlyList<string>>();
        var windows = inventory.Select(window => new MacOSWindowProbe(
                window.ProcessId,
                window.WindowId,
                MacOSProcessIdentity.Normalize(window.OwnerName),
                ResolveWindowTitle(window, accessibleTitlesByWindow, processNamesByWindowProcessId, profiles),
                window.IsForeground,
                controlsByProcessId.GetValueOrDefault(window.ProcessId, [])))
            .ToArray();
        return ValueTask.FromResult(Match(audioPlatform, profiles, windows, DateTimeOffset.UtcNow));
    }

    internal static IReadOnlyList<MeetingWindowEvidenceSnapshot> Match(
        AudioPlatformSnapshot audioPlatform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<MacOSWindowProbe> windows,
        DateTimeOffset observedAtUtc)
    {
        var results = new List<MatchedWindow>();
        foreach (var process in audioPlatform.Processes)
        {
            var hasActiveRender = audioPlatform.Signals.Any(signal =>
                signal.RootProcessId == process.RootProcessId && IsActive(signal.SessionState));
            foreach (var profile in profiles.MatchProcess(process.ProcessName))
            {
                var processWindows = windows.Where(window =>
                        process.ProcessTreeIds.Contains(window.ProcessId)
                        || string.Equals(window.CanonicalProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (processWindows.Length == 0) continue;

                var browser = profile.MatchesBrowserProcess(process.ProcessName)
                              && !profile.MatchesDedicatedProcess(process.ProcessName);
                if (browser)
                {
                    foreach (var window in processWindows)
                    {
                        var evidence = MatchWindow(profile, true, hasActiveRender, window);
                        if (!HasRelevantEvidence(evidence)) continue;
                        var boundary = window.WindowId == 0
                            ? $"pid{window.ProcessId.ToString(CultureInfo.InvariantCulture)}"
                            : window.WindowId.ToString("x", CultureInfo.InvariantCulture);
                        results.Add(new MatchedWindow(
                            new MeetingWindowEvidenceSnapshot(
                                observedAtUtc,
                                $"browser:{process.RootProcessId}:{boundary}",
                                profile.Id,
                                profile.DisplayName,
                                MeetingCandidateContext.BrowserService,
                                process.RootProcessId,
                                process.ProcessName,
                                evidence),
                            boundary,
                            profile.IsFallbackProfile));
                    }
                    continue;
                }

                var desktopEvidence = processWindows
                    .SelectMany(window => MatchWindow(profile, false, hasActiveRender, window))
                    .DistinctBy(static fact => (fact.Kind, fact.RuleId))
                    .ToArray();
                if (!HasRelevantEvidence(desktopEvidence)) continue;
                results.Add(new MatchedWindow(
                    new MeetingWindowEvidenceSnapshot(
                        observedAtUtc,
                        $"{profile.Id}:desktop:{process.RootProcessId}",
                        profile.Id,
                        profile.DisplayName,
                        MeetingCandidateContext.DedicatedApplication,
                        process.RootProcessId,
                        process.ProcessName,
                        desktopEvidence),
                    "desktop",
                    profile.IsFallbackProfile));
            }
        }

        var specificRoots = results
            .Where(static result => !result.IsFallback && result.Snapshot.Context == MeetingCandidateContext.BrowserService)
            .Where(static result => HasStrongEvidence(result.Snapshot.Evidence))
            .Select(static result => result.Snapshot.RootProcessId)
            .ToHashSet();
        return results
            .Where(result => !result.IsFallback || !specificRoots.Contains(result.Snapshot.RootProcessId))
            .Select(static result => result.Snapshot)
            .ToArray();
    }

    private static IReadOnlyList<MeetingEvidenceFact> MatchWindow(
        MeetingAppProfile profile,
        bool browser,
        bool hasActiveRender,
        MacOSWindowProbe window)
    {
        var exclusion = profile.HardExclusionRules?.FirstOrDefault(rule => rule.Matches(window.Title));
        if (exclusion is not null)
        {
            return [new MeetingEvidenceFact(MeetingEvidenceKind.HardExclusion, exclusion.RuleId, exclusion.Strength, "macos-window")];
        }

        var facts = profile.WindowRules.Where(rule => rule.Matches(window.Title))
            .Select(rule => new MeetingEvidenceFact(
                MeetingEvidenceKind.MeetingWindow,
                rule.RuleId,
                rule.Strength,
                "macos-window"))
            .ToList();
        if (!browser || facts.Count > 0 || (profile.AllowControlOnlyBrowserMatch && hasActiveRender))
        {
            foreach (var rule in profile.ControlRules)
            {
                var strength = rule.MatchStrength(window.AccessibleControlNames);
                if (strength > 0)
                {
                    facts.Add(new MeetingEvidenceFact(MeetingEvidenceKind.MeetingControls, rule.RuleId, strength, "macos-accessibility"));
                }
            }
        }

        if (window.IsForeground && facts.Any(static fact => fact.Kind is MeetingEvidenceKind.MeetingWindow or MeetingEvidenceKind.MeetingControls))
        {
            facts.Add(new MeetingEvidenceFact(MeetingEvidenceKind.ForegroundWindow, "window.foreground", ProviderId: "macos-window"));
        }
        return facts.DistinctBy(static fact => (fact.Kind, fact.RuleId)).ToArray();
    }

    private static bool IsActive(string state) =>
        string.Equals(state, "active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase);

    private static string ResolveWindowTitle(
        MacOSNativeWindow window,
        IReadOnlyDictionary<(int ProcessId, uint WindowId), string> accessibleTitlesByWindow,
        IReadOnlyDictionary<int, string> processNamesByWindowProcessId,
        MeetingProfileRegistry profiles)
    {
        if (!accessibleTitlesByWindow.TryGetValue((window.ProcessId, window.WindowId), out var accessibleTitle)
            || string.IsNullOrWhiteSpace(accessibleTitle))
        {
            return window.Title;
        }

        var processName = processNamesByWindowProcessId.GetValueOrDefault(
            window.ProcessId,
            MacOSProcessIdentity.Normalize(window.OwnerName));
        var matchedProfiles = profiles.MatchProcess(processName);
        if (!matchedProfiles.Any(profile => NeedsAccessibilityTitle(profile, window.Title)))
        {
            return window.Title;
        }

        var isDedicated = matchedProfiles.Any(profile => profile.MatchesDedicatedProcess(processName));
        if (isDedicated)
        {
            return accessibleTitle;
        }

        if (!window.IsForeground)
        {
            return window.Title;
        }

        var titles = accessibleTitle.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return titles.FirstOrDefault(title => matchedProfiles.Any(profile =>
                   profile.HardExclusionRules?.Any(rule => rule.Matches(title)) == true
                   || profile.WindowRules.Any(rule => rule.Matches(title))))
               ?? titles.FirstOrDefault()
               ?? window.Title;
    }

    private static bool NeedsAccessibilityTitle(MeetingAppProfile profile, string windowTitle) =>
        profile.HardExclusionRules?.Any(rule => rule.Matches(windowTitle)) != true
        && !profile.WindowRules.Any(rule => rule.Matches(windowTitle));

    private static bool MatchesProcess(MacOSNativeWindow window, ObservedProcessSnapshot process) =>
        process.ProcessTreeIds.Contains(window.ProcessId)
        || string.Equals(
            MacOSProcessIdentity.Normalize(window.OwnerName),
            process.ProcessName,
            StringComparison.OrdinalIgnoreCase);

    private static ObservedProcessSnapshot? FindMatchingProcess(
        MacOSNativeWindow window,
        IReadOnlyList<ObservedProcessSnapshot> processes) =>
        processes.FirstOrDefault(process => process.ProcessTreeIds.Contains(window.ProcessId))
        ?? processes.FirstOrDefault(process => string.Equals(
            MacOSProcessIdentity.Normalize(window.OwnerName),
            process.ProcessName,
            StringComparison.OrdinalIgnoreCase));

    private static bool HasActiveRender(AudioPlatformSnapshot platform, ObservedProcessSnapshot process) =>
        platform.Signals.Any(signal => signal.RootProcessId == process.RootProcessId && IsActive(signal.SessionState));

    private static IReadOnlyList<int> AccessibilityProcessIds(
        MacOSNativeWindow window,
        ObservedProcessSnapshot process) =>
        window.ProcessId == process.RootProcessId
            ? [process.RootProcessId]
            : [window.ProcessId, process.RootProcessId];

    private static bool NeedsAccessibilityControls(
        MeetingAppProfile profile,
        string processName,
        string windowTitle)
    {
        if (profile.ControlRules.Count == 0
            || profile.HardExclusionRules?.Any(rule => rule.Matches(windowTitle)) == true
            || profile.WindowRules.Any(rule => rule.Matches(windowTitle)))
        {
            return false;
        }

        var browser = profile.MatchesBrowserProcess(processName)
                      && !profile.MatchesDedicatedProcess(processName);
        return !browser || profile.AllowControlOnlyBrowserMatch;
    }

    private static bool HasRelevantEvidence(IReadOnlyList<MeetingEvidenceFact> evidence) => evidence.Any(static fact =>
        fact.Kind is MeetingEvidenceKind.MeetingWindow or MeetingEvidenceKind.MeetingControls or MeetingEvidenceKind.HardExclusion);

    private static bool HasStrongEvidence(IReadOnlyList<MeetingEvidenceFact> evidence) => evidence.Any(static fact =>
        (fact.Kind == MeetingEvidenceKind.MeetingControls && fact.NormalizedStrength >= 0.5)
        || (fact.Kind == MeetingEvidenceKind.MeetingWindow && fact.NormalizedStrength >= 0.75));

    private sealed record MatchedWindow(MeetingWindowEvidenceSnapshot Snapshot, string Boundary, bool IsFallback);
}

internal sealed record MacOSWindowProbe(
    int ProcessId,
    uint WindowId,
    string CanonicalProcessName,
    string Title,
    bool IsForeground,
    IReadOnlyList<string> AccessibleControlNames);
