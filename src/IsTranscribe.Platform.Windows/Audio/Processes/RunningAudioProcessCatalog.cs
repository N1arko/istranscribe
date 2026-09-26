using System.Diagnostics;
using System.Runtime.Versioning;
using IsTranscribe.Host.Audio.Devices;
using IsTranscribe.Host.Audio.Sessions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IsTranscribe.Host.Audio.Processes;

[SupportedOSPlatform("windows")]
public sealed class RunningAudioProcessCatalog(IAudioDeviceManager deviceManager)
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);
    private readonly IAudioDeviceManager _deviceManager = deviceManager;
    private readonly Dictionary<string, DateTimeOffset> _recentActivityByProcess = new(StringComparer.OrdinalIgnoreCase);

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#wizard.steps.applications
    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.applications
    public IReadOnlyList<RunningAudioProcessCandidate> GetCandidates()
    {
        var now = TimeProvider.System.GetUtcNow();
        var probes = WindowsAudioSessionWatcher.CollectSessionProbes(_deviceManager.CurrentSnapshot);
        var grouped = probes
            .GroupBy(static probe => probe.ProcessId)
            .Select(group => new
            {
                ProcessId = group.Key,
                HasAudioActivity = group.Any(static probe => probe.State == AudioSessionState.AudioSessionStateActive || probe.MasterPeakValue > 0.001f)
            })
            .ToArray();

        var results = new List<RunningAudioProcessCandidate>();
        foreach (var probe in grouped)
        {
            try
            {
                using var process = Process.GetProcessById(probe.ProcessId);
                var processName = $"{process.ProcessName}.exe";
                if (probe.HasAudioActivity)
                {
                    _recentActivityByProcess[processName] = now;
                }

                var wasRecentlyActive = _recentActivityByProcess.TryGetValue(processName, out var lastSeen)
                    && now - lastSeen <= RecentWindow;

                if (!probe.HasAudioActivity && !wasRecentlyActive)
                {
                    continue;
                }

                results.Add(new RunningAudioProcessCandidate(
                    DisplayName: ProcessDisplayNameResolver.Resolve(
                        process.Id,
                        processName),
                    ProcessName: processName,
                    ProcessId: probe.ProcessId,
                    HasAudioActivity: probe.HasAudioActivity,
                    WasRecentlyActive: wasRecentlyActive));
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        PruneRecentActivity(now);

        return results
            .GroupBy(static candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static candidate => candidate.HasAudioActivity)
                .ThenByDescending(static candidate => candidate.WasRecentlyActive)
                .ThenBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(static candidate => candidate.HasAudioActivity)
            .ThenByDescending(static candidate => candidate.WasRecentlyActive)
            .ThenBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void PruneRecentActivity(DateTimeOffset now)
    {
        var expired = _recentActivityByProcess
            .Where(static entry => true)
            .Where(entry => now - entry.Value > RecentWindow)
            .Select(static entry => entry.Key)
            .ToArray();

        foreach (var processName in expired)
        {
            _recentActivityByProcess.Remove(processName);
        }
    }
}

public sealed record RunningAudioProcessCandidate(
    string DisplayName,
    string ProcessName,
    int ProcessId,
    bool HasAudioActivity,
    bool WasRecentlyActive);
