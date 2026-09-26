namespace IsTranscribe.Transcription.Worker.Tests.Support;

/// <summary>
/// Keeps lifecycle and native backend suites on one in-process worker lane, matching
/// the application's single active local-worker invariant.
/// </summary>
internal static class WorkerLifecycleCollection
{
    public const string Name = "Local worker lifecycle";
}
