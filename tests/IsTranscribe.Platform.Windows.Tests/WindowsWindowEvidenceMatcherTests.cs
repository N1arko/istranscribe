using IsTranscribe.Core.Audio;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.initial
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
public sealed class WindowsWindowEvidenceMatcherTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-11T12:00:00Z");

    [Fact]
    public void BrowserMediaTitleCreatesOnlyPrivacyReducedHardExclusion()
    {
        var evidence = WindowsWindowEvidenceMatcher.Match(
            Platform("chrome.exe", rootProcessId: 42, childProcessId: 43),
            new MeetingProfileRegistry(),
            [new WindowsWindowProbeSnapshot(43, "YouTube — Google Chrome", true, ["Play", "Pause"])],
            Now);

        var excluded = Assert.Single(evidence);
        Assert.Equal("generic-browser", excluded.ProfileId);
        Assert.Single(excluded.Evidence, static fact =>
            fact.Kind == MeetingEvidenceKind.HardExclusion &&
            fact.RuleId == "generic-browser.media-window");
        Assert.DoesNotContain(excluded.Evidence, static fact =>
            fact.Kind is MeetingEvidenceKind.MeetingWindow or MeetingEvidenceKind.MeetingControls);
    }

    [Fact]
    public void GoogleMeetWindowCreatesPrivacyReducedBrowserEvidence()
    {
        const string rawTitle = "Planning call — Google Meet";
        var evidence = WindowsWindowEvidenceMatcher.Match(
            Platform("chrome.exe", rootProcessId: 42, childProcessId: 43),
            new MeetingProfileRegistry(),
            [new WindowsWindowProbeSnapshot(43, rawTitle, true, ["Mute", "Leave call"])],
            Now);

        var snapshot = Assert.Single(evidence, static item => item.ProfileId == "google-meet");
        Assert.Equal(MeetingCandidateContext.BrowserService, snapshot.Context);
        Assert.Contains(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingWindow);
        Assert.Contains(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
        Assert.DoesNotContain(rawTitle, snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleMeetLandingPageProducesOnlyWeakBrandContext()
    {
        var evidence = WindowsWindowEvidenceMatcher.Match(
            Platform("chrome.exe", rootProcessId: 42, childProcessId: 43),
            new MeetingProfileRegistry(),
            [
                new WindowsWindowProbeSnapshot(
                    43,
                    "Google Meet",
                    true,
                    ["New meeting", "Enter a code or link", "Join"])
            ],
            Now);

        var snapshot = Assert.Single(evidence);
        var window = Assert.Single(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingWindow);
        Assert.Equal("google-meet.brand-window", window.RuleId);
        Assert.Equal(0.15, window.Strength);
        Assert.DoesNotContain(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
    }

    [Fact]
    public void DedicatedClientControlsCanIdentifyMeetingWithGenericWindowTitle()
    {
        var evidence = WindowsWindowEvidenceMatcher.Match(
            Platform("zoom.exe", rootProcessId: 42, childProcessId: 42),
            new MeetingProfileRegistry(),
            [new WindowsWindowProbeSnapshot(42, "Quarterly planning", false, ["Unmute", "Leave"])],
            Now);

        var snapshot = Assert.Single(evidence);
        Assert.Equal("zoom", snapshot.ProfileId);
        Assert.Contains(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
    }

    [Fact]
    public void ZoomWorkplaceHomeWindowIsNotMeetingEvidence()
    {
        var evidence = WindowsWindowEvidenceMatcher.Match(
            Platform("zoom.exe", rootProcessId: 42, childProcessId: 42),
            new MeetingProfileRegistry(),
            [
                new WindowsWindowProbeSnapshot(
                    42,
                    "Zoom Workplace",
                    true,
                    ["Начать новую конференцию с видео", "Подключиться", "Запланировать"])
            ],
            Now);

        Assert.Empty(evidence);
    }

    [Fact]
    public void TelemostHomeWindowProducesOnlyWeakBrandContext()
    {
        var evidence = WindowsWindowEvidenceMatcher.Match(
            Platform("yandextelemost.exe", rootProcessId: 42, childProcessId: 42),
            new MeetingProfileRegistry(),
            [
                new WindowsWindowProbeSnapshot(
                    42,
                    "Яндекс Телемост",
                    true,
                    ["Создать видеовстречу", "Подключиться", "Запланировать"])
            ],
            Now);

        var snapshot = Assert.Single(evidence);
        var window = Assert.Single(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingWindow);
        Assert.Equal(0.25, window.Strength);
        Assert.DoesNotContain(snapshot.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
    }

    [Fact]
    public async Task UiAutomationReadGateReturnsWithinItsBoundWhenReaderStalls()
    {
        var gate = new WindowsUiAutomationReadGate(
            new SlowControlReader(TimeSpan.FromSeconds(1)),
            TimeSpan.FromMilliseconds(40));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var names = await gate.ReadAsync(new IntPtr(42), CancellationToken.None);

        Assert.Empty(names);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(400), started.Elapsed.ToString());
    }

    [Fact]
    [Trait("Category", "Live")]
    public void UiAutomationReaderReturnsPrivacyReducedButtonNamesWhenCalculatorIsOpen()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LIVE_UIA"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var calculatorProcesses = System.Diagnostics.Process.GetProcessesByName("CalculatorApp")
            .Where(static process => process.MainWindowHandle != IntPtr.Zero)
            .ToArray();
        if (calculatorProcesses.Length == 0)
        {
            return;
        }

        var calculatorTitles = calculatorProcesses
            .Select(static process => process.MainWindowTitle)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .ToHashSet(StringComparer.Ordinal);
        var hostProcesses = System.Diagnostics.Process.GetProcessesByName("ApplicationFrameHost")
            .Where(process => process.MainWindowHandle != IntPtr.Zero &&
                              calculatorTitles.Contains(process.MainWindowTitle))
            .ToArray();
        var candidates = calculatorProcesses.Concat(hostProcesses).ToArray();

        var logPath = Path.Combine(Path.GetTempPath(), $"istranscribe-uia-{Guid.NewGuid():N}.log");
        try
        {
            var reader = new WindowsUiAutomationControlReader(new BootstrapFileLogger(logPath));
            IReadOnlyList<string> names = [];
            foreach (var process in candidates)
            {
                names = reader.ReadButtonNames(process.MainWindowHandle);
                if (names.Count > 0)
                {
                    break;
                }
            }

            var diagnostic = File.Exists(logPath) ? File.ReadAllText(logPath) : "UIA reader returned no diagnostic.";
            Assert.True(names.Count > 0, diagnostic);
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }

            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveIdleMeetingClientsDoNotProduceStrongMeetingEvidence()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LIVE_PROFILES"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var processes = new[]
            {
                FindVisibleProcess("Zoom"),
                FindVisibleProcess("ms-teams"),
                FindVisibleProcess("YandexTelemost")
            }
            .Where(static process => process is not null)
            .Cast<System.Diagnostics.Process>()
            .ToArray();
        Assert.Equal(3, processes.Length);
        var logPath = Path.Combine(Path.GetTempPath(), $"istranscribe-profile-{Guid.NewGuid():N}.log");
        try
        {
            var audio = new AudioPlatformSnapshot(
                DateTimeOffset.UtcNow,
                new AudioPlatformCapabilities(true, true, "live"),
                [],
                [],
                processes.Select(static process => new ObservedProcessSnapshot(
                    process.Id,
                    $"{process.ProcessName}.exe",
                    new HashSet<int> { process.Id })).ToArray(),
                []);
            var evidence = await new WindowsWindowEvidenceProvider(new BootstrapFileLogger(logPath))
                .ObserveAsync(audio, new MeetingProfileRegistry(), CancellationToken.None);

            Assert.DoesNotContain(evidence, static snapshot => snapshot.ProfileId == "zoom");
            Assert.DoesNotContain(evidence, static snapshot => snapshot.ProfileId == "microsoft-teams");
            var telemost = Assert.Single(evidence, static snapshot => snapshot.ProfileId == "yandex-telemost");
            Assert.All(
                telemost.Evidence.Where(static fact => fact.Kind == MeetingEvidenceKind.MeetingWindow),
                static fact => Assert.True(fact.Strength <= 0.25));
            Assert.DoesNotContain(
                telemost.Evidence,
                static fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveGoogleMeetLandingRemainsWeakBrowserEvidence()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(
                Environment.GetEnvironmentVariable("ISTRANSCRIBE_RUN_LIVE_MEET_LANDING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var chrome = System.Diagnostics.Process.GetProcessesByName("chrome")
            .FirstOrDefault(static process =>
                process.MainWindowHandle != IntPtr.Zero &&
                process.MainWindowTitle.Contains("Google Meet", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(chrome);
        var logPath = Path.Combine(Path.GetTempPath(), $"istranscribe-meet-{Guid.NewGuid():N}.log");
        try
        {
            var audio = new AudioPlatformSnapshot(
                DateTimeOffset.UtcNow,
                new AudioPlatformCapabilities(true, true, "live"),
                [],
                [],
                [
                    new ObservedProcessSnapshot(
                        chrome.Id,
                        "chrome.exe",
                        new HashSet<int> { chrome.Id })
                ],
                []);
            var evidence = await new WindowsWindowEvidenceProvider(new BootstrapFileLogger(logPath))
                .ObserveAsync(audio, new MeetingProfileRegistry(), CancellationToken.None);

            var meet = Assert.Single(evidence, static snapshot => snapshot.ProfileId == "google-meet");
            var window = Assert.Single(meet.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingWindow);
            Assert.Equal("google-meet.brand-window", window.RuleId);
            Assert.Equal(0.15, window.Strength);
            Assert.DoesNotContain(meet.Evidence, static fact => fact.Kind == MeetingEvidenceKind.MeetingControls);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    private static AudioPlatformSnapshot Platform(
        string processName,
        int rootProcessId,
        int childProcessId) => new(
            Now,
            new AudioPlatformCapabilities(true, true, "test"),
            [],
            [],
            [
                new ObservedProcessSnapshot(
                    rootProcessId,
                    processName,
                    new HashSet<int> { rootProcessId, childProcessId })
            ],
            []);

    private static System.Diagnostics.Process? FindVisibleProcess(string processName) =>
        System.Diagnostics.Process.GetProcessesByName(processName)
            .FirstOrDefault(static process =>
                process.MainWindowHandle != IntPtr.Zero &&
                !string.IsNullOrWhiteSpace(process.MainWindowTitle));

    private sealed class SlowControlReader(TimeSpan delay) : IWindowsUiAutomationControlReader
    {
        public IReadOnlyList<string> ReadButtonNames(IntPtr windowHandle)
        {
            Thread.Sleep(delay);
            return ["Mute", "Leave"];
        }
    }
}
