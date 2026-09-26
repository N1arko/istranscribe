namespace IsTranscribe.Application.Transcription.Remote;

/// <summary>
/// Stable vault entry names. Values are never included in settings or runtime snapshots.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public static class RemoteTranscriptionSecrets
{
    public const string Groq = "transcription.remote.groq.api-key";

    public const string OpenRouter = "transcription.remote.openrouter.api-key";

    public static string ForEngine(string engineId) => engineId switch
    {
        GroqTranscriptionEngine.EngineId => Groq,
        OpenRouterTranscriptionEngine.EngineId => OpenRouter,
        _ => throw new ArgumentOutOfRangeException(
            nameof(engineId),
            engineId,
            "The engine does not use a remote API key.")
    };
}
