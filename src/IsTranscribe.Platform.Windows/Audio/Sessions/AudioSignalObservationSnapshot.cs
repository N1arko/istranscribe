namespace IsTranscribe.Host.Audio.Sessions;

public sealed record AudioSignalObservationSnapshot(
    DateTimeOffset ObservedAtUtc,
    string RootProcessName,
    int RootProcessId,
    string AudioSessionState,
    double SignalLevelDbfs,
    string RenderDeviceId,
    bool IsWhitelisted,
    bool IsProcessTreeMatch);
