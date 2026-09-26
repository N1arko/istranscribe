using System.Text.Json;
using System.Text.Json.Nodes;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.Host.Settings;

public sealed class JsonApplicationSettingsStore(LocalAppPaths paths) : IApplicationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly LocalAppPaths _paths = paths;

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#settings.rw
    public async ValueTask<ApplicationSettings> LoadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);

        if (!File.Exists(_paths.SettingsFilePath))
        {
            await SaveAsync(ApplicationSettings.Default, cancellationToken);
            return ApplicationSettings.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_paths.SettingsFilePath);
            var persistedNode = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject;
            if (persistedNode is null)
            {
                throw new InvalidDataException("settings.json must contain a JSON object.");
            }

            var mergedNode = JsonSerializer.SerializeToNode(ApplicationSettings.Default, SerializerOptions) as JsonObject
                ?? throw new InvalidOperationException("Failed to materialize default settings.");

            MergeInto(mergedNode, persistedNode);
            ApplyLegacyBootstrapBridge(mergedNode, persistedNode);

            var settings = mergedNode.Deserialize<ApplicationSettings>(SerializerOptions)
                ?? throw new InvalidDataException("settings.json could not be deserialized.");

            return settings.Canonicalize();
        }
        catch (JsonException)
        {
            await SaveAsync(ApplicationSettings.Default, cancellationToken);
            return ApplicationSettings.Default;
        }
        catch (InvalidDataException)
        {
            await SaveAsync(ApplicationSettings.Default, cancellationToken);
            return ApplicationSettings.Default;
        }
    }

    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#settings.rw
    // @spec spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#settings.validation
    public ValueTask SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) =>
        SaveCoreAsync(settings, releaseV2: false, cancellationToken);

    /// <summary>
    /// Saves release-v2 settings while retaining inactive legacy transcription data verbatim.
    /// </summary>
    /// <remarks>
    /// @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
    /// </remarks>
    public ValueTask SaveReleaseV2Async(
        ApplicationSettings settings,
        CancellationToken cancellationToken) =>
        SaveCoreAsync(settings, releaseV2: true, cancellationToken);

    private async ValueTask SaveCoreAsync(
        ApplicationSettings settings,
        bool releaseV2,
        CancellationToken cancellationToken)
    {
        var canonical = settings.Canonicalize();
        if (releaseV2)
        {
            ApplicationSettingsValidator.ValidateForReleaseV2Save(canonical);
        }
        else
        {
            ApplicationSettingsValidator.ValidateForSave(canonical);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions);
        await AtomicFileWriter.WriteAsync(_paths.SettingsFilePath, payload, cancellationToken);
    }

    private static void ApplyLegacyBootstrapBridge(JsonObject target, JsonObject persisted)
    {
        var general = target["general"] as JsonObject ?? new JsonObject();
        target["general"] = general;

        if (persisted.TryGetPropertyValue("minimize_to_tray_on_close", out var minimizeNode) && minimizeNode is not null)
        {
            general["minimize_to_tray_on_close"] = minimizeNode.DeepClone();
        }

        if (persisted.TryGetPropertyValue("notifications_enabled", out var notificationsNode) && notificationsNode is not null)
        {
            general["notifications"] = notificationsNode.DeepClone();
        }
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject)
            {
                if (target[property.Key] is not JsonObject targetObject)
                {
                    targetObject = new JsonObject();
                    target[property.Key] = targetObject;
                }

                MergeInto(targetObject, sourceObject);
                continue;
            }

            target[property.Key] = property.Value?.DeepClone();
        }
    }
}
