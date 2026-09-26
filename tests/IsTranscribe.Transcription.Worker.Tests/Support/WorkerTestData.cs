using IsTranscribe.Transcription.Local.Protocol;

namespace IsTranscribe.Transcription.Worker.Tests.Support;

internal static class WorkerTestData
{
    public const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string OtherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static WorkerProbePayload Probe() => new(
        "/models/ggml-small.bin",
        Hash,
        "cpu",
        MaximumThreads: 4);

    public static WorkerStartPayload Start() => new(
        "/recordings/meeting.mp3",
        OtherHash,
        "/models/ggml-small.bin",
        Hash,
        "auto",
        "cpu",
        StartMilliseconds: 0,
        EndMilliseconds: 60_000,
        MaximumThreads: 4);

    public static WorkerProgressPayload Progress(long completed = 30_000) => new(
        "inferencing",
        completed,
        TotalMilliseconds: 60_000,
        WorkingSetBytes: 100_000_000,
        CpuMilliseconds: 2_000);

    public static WorkerResultPayload Result(string text = "Привет, мир.") => new(
        "ru",
        ProcessingMilliseconds: 2_500,
        RuntimeVersion: "whisper.cpp-v1.9.1",
        ModelSha256: Hash,
        Backend: "cpu",
        Segments:
        [
            new WorkerSegmentPayload(0, 2_000, text),
        ]);
}
