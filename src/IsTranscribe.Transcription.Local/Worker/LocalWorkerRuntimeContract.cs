namespace IsTranscribe.Transcription.Local.Worker;

/// <summary>
/// App-owned identity shared by the parent coordinator and the isolated worker host.
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
/// </summary>
public static class LocalWorkerRuntimeContract
{
    public const string CurrentWorkerVersion = "1.0.0";
}
