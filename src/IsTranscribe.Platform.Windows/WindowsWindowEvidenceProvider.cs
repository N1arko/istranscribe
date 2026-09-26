using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Diagnostics;

namespace IsTranscribe.Platform.Windows;

/// <summary>
/// Reduces top-level window and UI Automation state to matched rule identifiers without returning raw titles.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowEvidenceProvider : IMeetingWindowEvidenceProvider
{
    private static readonly TimeSpan DefaultControlReadTimeout = TimeSpan.FromMilliseconds(750);
    private readonly WindowsUiAutomationReadGate _controlReadGate;

    public WindowsWindowEvidenceProvider(BootstrapFileLogger logger)
        : this(new WindowsUiAutomationControlReader(logger), DefaultControlReadTimeout, logger)
    {
    }

    internal WindowsWindowEvidenceProvider(
        IWindowsUiAutomationControlReader controlReader,
        TimeSpan? controlReadTimeout = null,
        BootstrapFileLogger? logger = null)
    {
        _controlReadGate = new WindowsUiAutomationReadGate(
            controlReader,
            controlReadTimeout ?? DefaultControlReadTimeout,
            logger);
    }

    public async ValueTask<IReadOnlyList<MeetingWindowEvidenceSnapshot>> ObserveAsync(
        AudioPlatformSnapshot audioPlatform,
        MeetingProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioPlatform);
        ArgumentNullException.ThrowIfNull(profiles);

        cancellationToken.ThrowIfCancellationRequested();
        var nativeWindows = EnumerateTopLevelWindows();
        var observedWindows = nativeWindows
            .Where(window => audioPlatform.Processes.Any(process =>
                process.ProcessTreeIds.Contains(window.ProcessId)))
            .ToArray();
        var probes = new List<WindowsWindowProbeSnapshot>(observedWindows.Length);
        foreach (var window in observedWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var controls = ShouldReadControls(audioPlatform, profiles, window)
                ? await _controlReadGate.ReadAsync(window.Handle, cancellationToken).ConfigureAwait(false)
                : [];
            probes.Add(new WindowsWindowProbeSnapshot(
                window.ProcessId,
                window.Title,
                window.IsForeground,
                controls,
                window.Handle.ToInt64()));
        }

        return WindowsWindowEvidenceMatcher.Match(
            audioPlatform,
            profiles,
            probes,
            DateTimeOffset.UtcNow);
    }

    private static bool ShouldReadControls(
        AudioPlatformSnapshot audioPlatform,
        MeetingProfileRegistry profiles,
        NativeWindowSnapshot window)
    {
        var process = audioPlatform.Processes.First(item => item.ProcessTreeIds.Contains(window.ProcessId));
        var hasActiveRenderSignal = audioPlatform.Signals.Any(signal =>
            signal.RootProcessId == process.RootProcessId &&
            string.Equals(signal.SessionState, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase));
        return profiles.MatchProcess(process.ProcessName).Any(profile =>
            profile.MatchesDedicatedProcess(process.ProcessName)
            || profile.WindowRules.Any(rule => rule.Matches(window.Title))
            || profile.HardExclusionRules?.Any(rule => rule.Matches(window.Title)) == true
            || (profile.AllowControlOnlyBrowserMatch && hasActiveRenderSignal));
    }

    internal static IReadOnlyList<NativeWindowSnapshot> EnumerateTopLevelWindows()
    {
        var results = new List<NativeWindowSnapshot>();
        var foreground = GetForegroundWindow();
        EnumWindows(
            (windowHandle, parameter) =>
            {
                if (!IsWindowVisible(windowHandle))
                {
                    return true;
                }

                _ = GetWindowThreadProcessId(windowHandle, out var processId);
                if (processId == 0)
                {
                    return true;
                }

                var titleLength = GetWindowTextLength(windowHandle);
                var title = string.Empty;
                if (titleLength > 0)
                {
                    var buffer = new StringBuilder(titleLength + 1);
                    _ = GetWindowText(windowHandle, buffer, buffer.Capacity);
                    title = buffer.ToString();
                }

                results.Add(new NativeWindowSnapshot(
                    windowHandle,
                    checked((int)processId),
                    title,
                    windowHandle == foreground));
                return true;
            },
            IntPtr.Zero);
        return results;
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder value, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    internal sealed record NativeWindowSnapshot(
        IntPtr Handle,
        int ProcessId,
        string Title,
        bool IsForeground);
}

/// <summary>
/// Bounds one privacy-reduced UI Automation read so detection shutdown and cadence remain responsive.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal sealed class WindowsUiAutomationReadGate(
    IWindowsUiAutomationControlReader reader,
    TimeSpan timeout,
    BootstrapFileLogger? logger = null)
{
    private readonly object _gate = new();
    private readonly IWindowsUiAutomationControlReader _reader = reader;
    private readonly TimeSpan _timeout = timeout;
    private readonly BootstrapFileLogger? _logger = logger;
    private Task<IReadOnlyList<string>>? _activeRead;
    private IntPtr _activeWindowHandle;
    private bool _timeoutLogged;

    public async ValueTask<IReadOnlyList<string>> ReadAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<string>> read;
        lock (_gate)
        {
            if (_activeRead?.IsCompleted == true)
            {
                _activeRead = null;
                _activeWindowHandle = IntPtr.Zero;
            }

            if (_activeRead is not null && _activeWindowHandle != windowHandle)
            {
                return [];
            }

            if (_activeRead is null)
            {
                _activeWindowHandle = windowHandle;
                _activeRead = Task.Factory.StartNew<IReadOnlyList<string>>(
                    () =>
                    {
                        try
                        {
                            return _reader.ReadButtonNames(windowHandle);
                        }
                        catch
                        {
                            return [];
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            read = _activeRead;
        }

        try
        {
            return await read.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!_timeoutLogged)
            {
                _timeoutLogged = true;
                _logger?.LogEvent(
                    "Warning",
                    "DETECTION_UIA_TIMEOUT",
                    "UI Automation control evidence exceeded its bounded read window.");
            }

            return [];
        }
    }
}

/// <summary>
/// Applies data-first profile rules to privacy-reduced Windows window probes.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.contract
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
internal static class WindowsWindowEvidenceMatcher
{
    public static IReadOnlyList<MeetingWindowEvidenceSnapshot> Match(
        AudioPlatformSnapshot audioPlatform,
        MeetingProfileRegistry profiles,
        IReadOnlyList<WindowsWindowProbeSnapshot> windows,
        DateTimeOffset observedAtUtc)
    {
        var results = new List<MatchedWindowResult>();
        foreach (var process in audioPlatform.Processes)
        {
            var hasActiveRenderSignal = audioPlatform.Signals.Any(signal =>
                signal.RootProcessId == process.RootProcessId &&
                string.Equals(signal.SessionState, "AudioSessionStateActive", StringComparison.OrdinalIgnoreCase));
            foreach (var profile in profiles.MatchProcess(process.ProcessName))
            {
                var processWindows = windows
                    .Where(window => profile.ProcessTreeBehavior == MeetingProcessTreeBehavior.RootOnly
                        ? window.ProcessId == process.RootProcessId
                        : process.ProcessTreeIds.Contains(window.ProcessId))
                    .ToArray();
                if (processWindows.Length == 0)
                {
                    continue;
                }

                var isBrowser = profile.MatchesBrowserProcess(process.ProcessName)
                    && !profile.MatchesDedicatedProcess(process.ProcessName);
                if (isBrowser)
                {
                    foreach (var window in processWindows)
                    {
                        var evidence = MatchWindow(profile, isBrowser: true, hasActiveRenderSignal, window);
                        if (!HasRelevantEvidence(evidence))
                        {
                            continue;
                        }

                        var boundaryKey = GetWindowBoundaryKey(window);
                        results.Add(new MatchedWindowResult(
                            new MeetingWindowEvidenceSnapshot(
                                observedAtUtc,
                                $"browser:{process.RootProcessId}:{boundaryKey}",
                                profile.Id,
                                profile.DisplayName,
                                MeetingCandidateContext.BrowserService,
                                process.RootProcessId,
                                process.ProcessName,
                                evidence),
                            boundaryKey,
                            profile.IsFallbackProfile));
                    }

                    continue;
                }

                var dedicatedEvidence = processWindows
                    .SelectMany(window => MatchWindow(profile, isBrowser: false, hasActiveRenderSignal, window))
                    .DistinctBy(static fact => (fact.Kind, fact.RuleId))
                    .ToArray();
                if (!HasRelevantEvidence(dedicatedEvidence))
                {
                    continue;
                }

                results.Add(new MatchedWindowResult(
                    new MeetingWindowEvidenceSnapshot(
                        observedAtUtc,
                        $"{profile.Id}:desktop:{process.RootProcessId}",
                        profile.Id,
                        profile.DisplayName,
                        MeetingCandidateContext.DedicatedApplication,
                        process.RootProcessId,
                        process.ProcessName,
                        dedicatedEvidence),
                    WindowBoundaryKey: "desktop",
                    profile.IsFallbackProfile));
            }
        }

        var windowsWithStrongSpecificEvidence = results
            .Where(static result => !result.IsFallbackProfile)
            .Where(static result => result.Snapshot.Context == MeetingCandidateContext.BrowserService)
            .Where(static result => HasStrongMeetingEvidence(result.Snapshot.Evidence))
            .Select(static result => new WindowBoundary(
                result.Snapshot.RootProcessId,
                result.WindowBoundaryKey))
            .ToHashSet();
        return results
            .Where(result => !result.IsFallbackProfile
                || !windowsWithStrongSpecificEvidence.Contains(new WindowBoundary(
                    result.Snapshot.RootProcessId,
                    result.WindowBoundaryKey)))
            .Select(static result => result.Snapshot)
            .ToArray();
    }

    private static IReadOnlyList<MeetingEvidenceFact> MatchWindow(
        MeetingAppProfile profile,
        bool isBrowser,
        bool hasActiveRenderSignal,
        WindowsWindowProbeSnapshot window)
    {
        var hardExclusion = profile.HardExclusionRules?
            .FirstOrDefault(rule => rule.Matches(window.Title));
        if (hardExclusion is not null)
        {
            return
            [
                new MeetingEvidenceFact(
                    MeetingEvidenceKind.HardExclusion,
                    hardExclusion.RuleId,
                    hardExclusion.Strength,
                    ProviderId: "windows-window")
            ];
        }

        var evidence = new List<MeetingEvidenceFact>();
        var matchedWindowRules = profile.WindowRules
            .Where(rule => rule.Matches(window.Title))
            .ToArray();
        foreach (var rule in matchedWindowRules)
        {
            evidence.Add(new MeetingEvidenceFact(
                MeetingEvidenceKind.MeetingWindow,
                rule.RuleId,
                rule.Strength,
                ProviderId: "windows-window"));
        }

        if (!isBrowser || matchedWindowRules.Length > 0
            || (profile.AllowControlOnlyBrowserMatch && hasActiveRenderSignal))
        {
            foreach (var rule in profile.ControlRules)
            {
                var strength = rule.MatchStrength(window.AccessibleButtonNames);
                if (strength > 0)
                {
                    evidence.Add(new MeetingEvidenceFact(
                        MeetingEvidenceKind.MeetingControls,
                        rule.RuleId,
                        strength,
                        "windows-uia"));
                }
            }
        }

        if (window.IsForeground && (matchedWindowRules.Length > 0
            || evidence.Any(static fact => fact.Kind == MeetingEvidenceKind.MeetingControls)))
        {
            evidence.Add(new MeetingEvidenceFact(
                MeetingEvidenceKind.ForegroundWindow,
                "window.foreground",
                ProviderId: "windows-window"));
        }

        return evidence
            .DistinctBy(static fact => (fact.Kind, fact.RuleId))
            .ToArray();
    }

    private static bool HasRelevantEvidence(IReadOnlyList<MeetingEvidenceFact> evidence) =>
        evidence.Any(static fact => fact.Kind is
            MeetingEvidenceKind.MeetingWindow or
            MeetingEvidenceKind.MeetingControls or
            MeetingEvidenceKind.HardExclusion);

    private static bool HasStrongMeetingEvidence(IReadOnlyList<MeetingEvidenceFact> evidence) =>
        evidence.Any(static fact =>
            (fact.Kind == MeetingEvidenceKind.MeetingControls && fact.NormalizedStrength >= 0.5)
            || (fact.Kind == MeetingEvidenceKind.MeetingWindow && fact.NormalizedStrength >= 0.75));

    private static string GetWindowBoundaryKey(WindowsWindowProbeSnapshot window) =>
        window.WindowId == 0
            ? $"pid{window.ProcessId.ToString(CultureInfo.InvariantCulture)}"
            : unchecked((ulong)window.WindowId).ToString("x", CultureInfo.InvariantCulture);

    private sealed record MatchedWindowResult(
        MeetingWindowEvidenceSnapshot Snapshot,
        string WindowBoundaryKey,
        bool IsFallbackProfile);

    private sealed record WindowBoundary(int RootProcessId, string WindowBoundaryKey);
}

internal sealed record WindowsWindowProbeSnapshot(
    int ProcessId,
    string Title,
    bool IsForeground,
    IReadOnlyList<string> AccessibleButtonNames,
    long WindowId = 0);
