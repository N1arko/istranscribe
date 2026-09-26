using IsTranscribe.Host.Audio.Processes;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;

namespace IsTranscribe.Host.Audio.Sessions;

public static class AudioSessionSnapshotCollector
{
    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.snapshots
    public static IReadOnlyList<AudioSignalObservationSnapshot> Collect(
        IEnumerable<ObservedProcessSnapshot> observedProcesses,
        IEnumerable<SessionProbe> sessionProbes,
        DateTimeOffset observedAtUtc)
    {
        var snapshots = new List<AudioSignalObservationSnapshot>();
        var processes = observedProcesses.ToArray();

        foreach (var probe in sessionProbes)
        {
            var matchedProcess = processes.FirstOrDefault(process => process.ProcessTreeIds.Contains(probe.ProcessId));
            if (matchedProcess is not null)
            {
                snapshots.Add(new AudioSignalObservationSnapshot(
                    observedAtUtc,
                    matchedProcess.RootProcessName,
                    matchedProcess.RootProcessId,
                    probe.State.ToString(),
                    ConvertPeakToDbfs(probe.MasterPeakValue),
                    probe.RenderDeviceId,
                    IsWhitelisted: true,
                    IsProcessTreeMatch: probe.ProcessId != matchedProcess.RootProcessId));

                continue;
            }

            if (!TryResolveUnknownProcess(probe.ProcessId, out var processName))
            {
                continue;
            }

            snapshots.Add(new AudioSignalObservationSnapshot(
                observedAtUtc,
                processName,
                probe.ProcessId,
                probe.State.ToString(),
                ConvertPeakToDbfs(probe.MasterPeakValue),
                probe.RenderDeviceId,
                IsWhitelisted: false,
                IsProcessTreeMatch: false));
        }

        return snapshots;
    }

    internal static double ConvertPeakToDbfs(float masterPeakValue)
    {
        if (masterPeakValue <= 0f)
        {
            return -100d;
        }

        return 20d * Math.Log10(masterPeakValue);
    }

    private static bool TryResolveUnknownProcess(int processId, out string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = $"{process.ProcessName}.exe";
            return true;
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        processName = string.Empty;
        return false;
    }
}

public sealed record SessionProbe(
    int ProcessId,
    AudioSessionState State,
    float MasterPeakValue,
    string RenderDeviceId);
