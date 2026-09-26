namespace IsTranscribe.Host.Audio.Processes;

public sealed record ObservedProcessSnapshot(
    int RootProcessId,
    string RootProcessName,
    IReadOnlySet<int> ProcessTreeIds);

public sealed record ProcessWatcherSnapshot(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ObservedProcessSnapshot> Processes)
{
    public static ProcessWatcherSnapshot Empty { get; } = new(DateTimeOffset.MinValue, Array.Empty<ObservedProcessSnapshot>());
}
