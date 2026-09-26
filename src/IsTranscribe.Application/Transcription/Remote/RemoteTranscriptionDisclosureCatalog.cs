using System.Text.Json;

namespace IsTranscribe.Application.Transcription.Remote;

/// <summary>
/// Versioned disclosures frozen into every remote job.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </remarks>
public static class RemoteTranscriptionDisclosureCatalog
{
    public const string GroqRevision = "groq-2026-08-11";
    public const string OpenRouterRevision = "openrouter-zdr-2026-08-11";

    public static string GetRequiredRevision(string engineId) => engineId switch
    {
        GroqTranscriptionEngine.EngineId => GroqRevision,
        OpenRouterTranscriptionEngine.EngineId => OpenRouterRevision,
        _ => throw new ArgumentOutOfRangeException(
            nameof(engineId),
            engineId,
            "The engine does not use a remote transcription disclosure.")
    };

    public static Uri GetPolicyUri(string engineId) => engineId switch
    {
        GroqTranscriptionEngine.EngineId => new Uri("https://console.groq.com/docs/your-data"),
        OpenRouterTranscriptionEngine.EngineId => new Uri(
            "https://openrouter.ai/docs/guides/privacy/data-collection"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(engineId),
            engineId,
            "The engine does not expose a remote transcription policy.")
    };

    public static string BuildFrozenPolicyJson(string engineId, bool requireZeroDataRetention)
    {
        var revision = GetRequiredRevision(engineId);
        var policy = new FrozenRemoteTranscriptionPolicy(
            Version: 1,
            EngineId: engineId,
            DisclosureRevision: revision,
            PolicyUri: GetPolicyUri(engineId).AbsoluteUri,
            RequireZeroDataRetention: engineId == OpenRouterTranscriptionEngine.EngineId
                && requireZeroDataRetention,
            GroqAccountDataControlsRequired: engineId == GroqTranscriptionEngine.EngineId);
        return JsonSerializer.Serialize(policy, SerializerOptions);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed record FrozenRemoteTranscriptionPolicy(
        int Version,
        string EngineId,
        string DisclosureRevision,
        string PolicyUri,
        bool RequireZeroDataRetention,
        bool GroqAccountDataControlsRequired);
}
