using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Immutable metadata for the app-owned public Whisper model catalog.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </remarks>
public sealed class WhisperModelCatalog
{
    private const string EmbeddedResourceName =
        "IsTranscribe.Transcription.Local.Models.whisper-model-catalog.v1.json";

    private static readonly Lazy<WhisperModelCatalog> EmbeddedCatalog = new(LoadEmbeddedCore);
    private readonly IReadOnlyDictionary<string, WhisperModelDescriptor> _modelsById;

    private WhisperModelCatalog(
        int schemaVersion,
        string catalogVersion,
        string catalogId,
        string upstreamRevision,
        Uri sourceRepository,
        Uri modelOriginRepository,
        string modelSource,
        string licenseSpdx,
        Uri licenseUri,
        WhisperModelFormat format,
        IReadOnlyList<WhisperModelDescriptor> models)
    {
        SchemaVersion = schemaVersion;
        CatalogVersion = catalogVersion;
        CatalogId = catalogId;
        UpstreamRevision = upstreamRevision;
        SourceRepository = sourceRepository;
        ModelOriginRepository = modelOriginRepository;
        ModelSource = modelSource;
        LicenseSpdx = licenseSpdx;
        LicenseUri = licenseUri;
        Format = format;
        Models = models;
        _modelsById = new ReadOnlyDictionary<string, WhisperModelDescriptor>(
            models.ToDictionary(model => model.Id, StringComparer.Ordinal));
    }

    public int SchemaVersion { get; }

    public string CatalogVersion { get; }

    public string CatalogId { get; }

    public string UpstreamRevision { get; }

    public Uri SourceRepository { get; }

    public Uri ModelOriginRepository { get; }

    public string ModelSource { get; }

    public string LicenseSpdx { get; }

    public Uri LicenseUri { get; }

    public WhisperModelFormat Format { get; }

    public IReadOnlyList<WhisperModelDescriptor> Models { get; }

    public WhisperModelDescriptor RecommendedModel => Models.Single(model => model.Recommended);

    public static WhisperModelCatalog LoadEmbedded() => EmbeddedCatalog.Value;

    public WhisperModelDescriptor GetRequired(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        return _modelsById.TryGetValue(modelId, out var model)
            ? model
            : throw new KeyNotFoundException($"The model id '{modelId}' is not in catalog {CatalogVersion}.");
    }

    // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#modes.values
    // Existing jobs retain their frozen model identity. These entries are never advertised
    // for new jobs or settings; payload integrity still uses the durable execution hash.
    public WhisperModelDescriptor GetRequiredForFrozenRun(string modelId)
    {
        if (_modelsById.TryGetValue(modelId, out var model)) return model;
        var legacy = modelId switch
        {
            "base" => (147951465L, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 388000000L),
            "small" => (487601967L, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", 852000000L),
            "medium" => (1533763059L, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", 2100000000L),
            _ => throw new KeyNotFoundException("The frozen model identity is unsupported.")
        };
        return RecommendedModel with
        {
            Id = modelId,
            UpstreamModelId = "openai/whisper-" + modelId,
            FileName = "ggml-" + modelId + ".bin",
            DownloadSizeBytes = legacy.Item1,
            Sha256 = legacy.Item2,
            ExpectedMemoryBytes = legacy.Item3,
            CatalogVersion = "1",
            Recommended = false,
            DownloadUri = new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{UpstreamRevision}/ggml-{modelId}.bin")
        };
    }

    private static WhisperModelCatalog LoadEmbeddedCore()
    {
        var assembly = typeof(WhisperModelCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded model catalog '{EmbeddedResourceName}' is missing.");

        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("The embedded model catalog is empty.");

        ValidateDocument(document);

        var format = new WhisperModelFormat(
            document.Format!.Token!,
            ParseMagic(document.Format.MagicLittleEndian!),
            Version.Parse(document.Format.MinimumRuntimeVersion!));
        var models = document.Models!
            .Select(model => new WhisperModelDescriptor(
                model.Id!,
                model.UiPreset!,
                model.UpstreamModelId!,
                model.FileName!,
                new Uri(model.DownloadUrl!, UriKind.Absolute),
                model.DownloadSizeBytes,
                model.Sha256!,
                model.ExpectedMemoryBytes,
                model.Multilingual,
                model.Recommended,
                document.CatalogVersion!,
                document.UpstreamRevision!,
                format,
                document.LicenseSpdx!,
                new Uri(document.LicenseUrl!, UriKind.Absolute),
                new Uri(document.SourceRepository!, UriKind.Absolute),
                new Uri(document.ModelOriginRepository!, UriKind.Absolute),
                document.ModelSource!))
            .ToArray();

        return new WhisperModelCatalog(
            document.SchemaVersion,
            document.CatalogVersion!,
            document.CatalogId!,
            document.UpstreamRevision!,
            new Uri(document.SourceRepository!, UriKind.Absolute),
            new Uri(document.ModelOriginRepository!, UriKind.Absolute),
            document.ModelSource!,
            document.LicenseSpdx!,
            new Uri(document.LicenseUrl!, UriKind.Absolute),
            format,
            Array.AsReadOnly(models));
    }

    private static void ValidateDocument(CatalogDocument document)
    {
        if (document.SchemaVersion != 1 || document.CatalogVersion != "2")
        {
            throw new InvalidOperationException("The embedded model catalog schema/version is unsupported.");
        }

        RequireToken(document.CatalogId, "catalogId");
        if (!IsLowerHex(document.UpstreamRevision, 40))
        {
            throw new InvalidOperationException("upstreamRevision must be an exact lowercase Git commit.");
        }

        RequireHttps(document.SourceRepository, "sourceRepository");
        RequireHttps(document.ModelOriginRepository, "modelOriginRepository");
        RequireText(document.ModelSource, "modelSource");
        if (!string.Equals(document.LicenseSpdx, "MIT", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The initial Whisper weights catalog must carry its reviewed MIT license id.");
        }

        RequireHttps(document.LicenseUrl, "licenseUrl");
        if (document.Format is null)
        {
            throw new InvalidOperationException("The embedded model catalog has no format contract.");
        }

        RequireToken(document.Format.Token, "format.token");
        _ = ParseMagic(document.Format.MagicLittleEndian);
        if (!Version.TryParse(document.Format.MinimumRuntimeVersion, out var minimumRuntime)
            || minimumRuntime < new Version(1, 9, 1))
        {
            throw new InvalidOperationException("format.minimumRuntimeVersion is invalid.");
        }

        if (document.Models is null || document.Models.Count != 1)
        {
            throw new InvalidOperationException("Catalog v2 must contain exactly full large-v3-turbo.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in document.Models)
        {
            RequireToken(model.Id, "models[].id");
            if (!ids.Add(model.Id!))
            {
                throw new InvalidOperationException($"Duplicate model id '{model.Id}'.");
            }

            RequireToken(model.UiPreset, "models[].uiPreset");
            RequireText(model.UpstreamModelId, "models[].upstreamModelId");
            RequireFileName(model.FileName);
            RequireHttps(model.DownloadUrl, "models[].downloadUrl");
            if (!model.DownloadUrl!.Contains(document.UpstreamRevision!, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Model '{model.Id}' does not use the pinned upstream revision.");
            }

            if (model.DownloadSizeBytes <= 4 || model.ExpectedMemoryBytes <= 0)
            {
                throw new InvalidOperationException($"Model '{model.Id}' has invalid size metadata.");
            }

            if (!IsLowerHex(model.Sha256, 64))
            {
                throw new InvalidOperationException($"Model '{model.Id}' has an invalid SHA-256.");
            }

            if (!model.Multilingual)
            {
                throw new InvalidOperationException($"Model '{model.Id}' must be multilingual.");
            }
        }

        if (!ids.SetEquals(["large-v3-turbo"])
            || document.Models.Count(model => model.Recommended) != 1
            || !document.Models.Single(model => model.Recommended).Id!.Equals("large-v3-turbo", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Canonical model ids or recommendation are invalid.");
        }
    }

    private static uint ParseMagic(string? value)
    {
        if (value is null
            || !value.StartsWith("0x", StringComparison.Ordinal)
            || !uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var magic))
        {
            throw new InvalidOperationException("format.magicLittleEndian must be a uint32 hexadecimal value.");
        }

        return magic;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && Regex.IsMatch(value, "^[0-9a-f]+$", RegexOptions.CultureInvariant);

    private static void RequireToken(string? value, string field)
    {
        if (value is null || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException($"{field} is not a safe catalog token.");
        }
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{field} is required.");
        }
    }

    private static void RequireFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("models[].fileName is unsafe.");
        }
    }

    private static void RequireHttps(string? value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{field} must be an absolute HTTPS URI.");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; init; }
        public string? CatalogVersion { get; init; }
        public string? CatalogId { get; init; }
        public string? UpstreamRevision { get; init; }
        public string? SourceRepository { get; init; }
        public string? ModelOriginRepository { get; init; }
        public string? ModelSource { get; init; }
        public string? LicenseSpdx { get; init; }
        public string? LicenseUrl { get; init; }
        public FormatDocument? Format { get; init; }
        public List<ModelDocument>? Models { get; init; }
    }

    private sealed class FormatDocument
    {
        public string? Token { get; init; }
        public string? MagicLittleEndian { get; init; }
        public string? MinimumRuntimeVersion { get; init; }
    }

    private sealed class ModelDocument
    {
        public string? Id { get; init; }
        public string? UiPreset { get; init; }
        public string? UpstreamModelId { get; init; }
        public string? FileName { get; init; }
        public string? DownloadUrl { get; init; }
        public long DownloadSizeBytes { get; init; }
        public string? Sha256 { get; init; }
        public long ExpectedMemoryBytes { get; init; }
        public bool Multilingual { get; init; }
        public bool Recommended { get; init; }
    }
}

/// <summary>
/// Application-owned compatibility token for an upstream model payload.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#models</remarks>
public sealed record WhisperModelFormat(string Token, uint MagicLittleEndian, Version MinimumRuntimeVersion);

/// <summary>
/// Fully pinned descriptor of one downloadable multilingual model.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// </remarks>
public sealed record WhisperModelDescriptor(
    string Id,
    string UiPreset,
    string UpstreamModelId,
    string FileName,
    Uri DownloadUri,
    long DownloadSizeBytes,
    string Sha256,
    long ExpectedMemoryBytes,
    bool Multilingual,
    bool Recommended,
    string CatalogVersion,
    string UpstreamRevision,
    WhisperModelFormat Format,
    string LicenseSpdx,
    Uri LicenseUri,
    Uri SourceRepository,
    Uri ModelOriginRepository,
    string ModelSource);
