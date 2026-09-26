using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace IsTranscribe.Host.Audio.Processes;

[SupportedOSPlatform("windows")]
public static class ProcessTreeSnapshotBuilder
{
    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.process-watcher
    public static ProcessWatcherSnapshot BuildSnapshot(
        IEnumerable<string> watchedProcessNames,
        DateTimeOffset observedAtUtc)
    {
        var normalizedNames = watchedProcessNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedNames.Count == 0)
        {
            return new ProcessWatcherSnapshot(observedAtUtc, Array.Empty<ObservedProcessSnapshot>());
        }

        return BuildSnapshot(EnumerateProcesses(), normalizedNames, observedAtUtc);
    }

    // @spec spec://modules/platform/INFRA-003-windows-audio-capture-foundation#observation.process-watcher
    // @spec spec://modules/app/FEAT-011-meeting-detection-v2#app-profiles.contract
    // @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#zen
    internal static ProcessWatcherSnapshot BuildSnapshot(
        IReadOnlyList<ProcessSnapshotEntry> entries,
        IReadOnlySet<string> normalizedNames,
        DateTimeOffset observedAtUtc)
    {
        var entryByProcessId = entries
            .GroupBy(static entry => entry.ProcessId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var childrenByParentId = entries
            .GroupBy(entry => entry.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.Select(child => child.ProcessId).ToArray());

        var roots = entries
            .Where(entry => normalizedNames.Contains(NormalizeProcessName(entry.ExecutableName)))
            .Where(entry => !HasSameExecutableAncestor(entry, entryByProcessId))
            .OrderBy(entry => entry.ProcessId)
            .ToArray();
        var rootProcessIds = roots
            .Select(static entry => entry.ProcessId)
            .ToHashSet();
        var snapshots = roots
            .Select(entry => new ObservedProcessSnapshot(
                entry.ProcessId,
                entry.ExecutableName,
                BuildTree(entry.ProcessId, childrenByParentId, rootProcessIds)))
            .ToArray();

        return new ProcessWatcherSnapshot(observedAtUtc, snapshots);
    }

    private static bool HasSameExecutableAncestor(
        ProcessSnapshotEntry entry,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> entryByProcessId)
    {
        var executableName = NormalizeProcessName(entry.ExecutableName);
        var visited = new HashSet<int> { entry.ProcessId };
        var parentProcessId = entry.ParentProcessId;

        while (parentProcessId > 0
               && visited.Add(parentProcessId)
               && entryByProcessId.TryGetValue(parentProcessId, out var parent))
        {
            if (string.Equals(
                    executableName,
                    NormalizeProcessName(parent.ExecutableName),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            parentProcessId = parent.ParentProcessId;
        }

        return false;
    }

    internal static IReadOnlySet<int> BuildTree(
        int rootProcessId,
        IReadOnlyDictionary<int, int[]> childrenByParentId) =>
        BuildTree(rootProcessId, childrenByParentId, boundaryRootProcessIds: null);

    private static IReadOnlySet<int> BuildTree(
        int rootProcessId,
        IReadOnlyDictionary<int, int[]> childrenByParentId,
        IReadOnlySet<int>? boundaryRootProcessIds)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootProcessId);

        while (stack.Count > 0)
        {
            var processId = stack.Pop();
            if (!visited.Add(processId))
            {
                continue;
            }

            if (!childrenByParentId.TryGetValue(processId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child != rootProcessId && boundaryRootProcessIds?.Contains(child) == true)
                {
                    continue;
                }

                stack.Push(child);
            }
        }

        return visited;
    }

    private static string NormalizeProcessName(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{value}.exe";

    private static IReadOnlyList<ProcessSnapshotEntry> EnumerateProcesses()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process snapshot.");
        }

        try
        {
            var entry = new ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            var results = new List<ProcessSnapshotEntry>();
            if (!Process32First(snapshot, ref entry))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NoMoreFiles)
                {
                    return results;
                }

                throw new Win32Exception(error, "Failed to enumerate the first process.");
            }

            do
            {
                results.Add(new ProcessSnapshotEntry(
                    checked((int)entry.th32ProcessID),
                    checked((int)entry.th32ParentProcessID),
                    entry.szExeFile.TrimEnd('\0')));
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            var lastError = Marshal.GetLastWin32Error();
            if (lastError != NoMoreFiles)
            {
                throw new Win32Exception(lastError, "Failed while enumerating processes.");
            }

            return results;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private const int NoMoreFiles = 18;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [Flags]
    private enum SnapshotFlags : uint
    {
        Process = 0x00000002
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    internal sealed record ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ExecutableName);
}
