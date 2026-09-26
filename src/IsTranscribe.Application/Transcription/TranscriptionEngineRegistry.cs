using System.Runtime.InteropServices;
using IsTranscribe.Core.Transcription;
using TranscriptionOs = IsTranscribe.Core.Transcription.TranscriptionOperatingSystem;

namespace IsTranscribe.Application.Transcription;

/// <summary>
/// Validated, provider-neutral catalog used by runtime commands and the queue worker.
/// Constructing the catalog never contacts an engine endpoint.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#providers
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </remarks>
public sealed class TranscriptionEngineRegistry
{
    private readonly IReadOnlyDictionary<string, ITranscriptionEngine> _engines;

    public TranscriptionEngineRegistry(IEnumerable<ITranscriptionEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        var snapshot = engines.ToArray();
        foreach (var engine in snapshot)
        {
            ArgumentNullException.ThrowIfNull(engine);
            engine.Capabilities.Validate();
        }

        var duplicate = snapshot
            .GroupBy(static engine => engine.Capabilities.EngineId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Transcription engine id '{duplicate.Key}' is registered more than once.",
                nameof(engines));
        }

        _engines = snapshot.ToDictionary(
            static engine => engine.Capabilities.EngineId,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<ITranscriptionEngine> Engines => _engines.Values
        .OrderBy(static engine => engine.Capabilities.DisplayName, StringComparer.Ordinal)
        .ToArray();

    public ITranscriptionEngine GetRequired(string engineId)
    {
        var normalized = NormalizeEngineId(engineId);
        return _engines.TryGetValue(normalized, out var engine)
            ? engine
            : throw new KeyNotFoundException($"Transcription engine '{normalized}' is unavailable.");
    }

    public bool TryGet(string? engineId, out ITranscriptionEngine? engine)
    {
        engine = null;
        return !string.IsNullOrWhiteSpace(engineId)
            && _engines.TryGetValue(engineId.Trim().ToLowerInvariant(), out engine);
    }

    public IReadOnlyList<ITranscriptionEngine> GetSupportedCurrentPlatform()
    {
        var operatingSystem = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TranscriptionOs.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? TranscriptionOs.MacOS
                : TranscriptionOs.Linux;
        var architecture = RuntimeInformation.ProcessArchitecture;
        return Engines
            .Where(engine => engine.Capabilities.SupportedPlatforms.Any(target =>
                target.OperatingSystem == operatingSystem && target.Architecture == architecture))
            .ToArray();
    }

    private static string NormalizeEngineId(string engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            throw new ArgumentException("A transcription engine id is required.", nameof(engineId));
        }

        return engineId.Trim().ToLowerInvariant();
    }
}
