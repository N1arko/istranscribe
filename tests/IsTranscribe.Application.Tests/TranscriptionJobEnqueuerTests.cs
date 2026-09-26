using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Transcription;
using IsTranscribe.Application.Transcription.Local;
using IsTranscribe.Application.Transcription.Remote;
using IsTranscribe.Core.Settings;
using IsTranscribe.Core.Transcription;
using IsTranscribe.Host.Persistence;
using IsTranscribe.Host.Settings;
using IsTranscribe.Transcription.Local.Models;
using Microsoft.Data.Sqlite;
using TranscriptionOs = IsTranscribe.Core.Transcription.TranscriptionOperatingSystem;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.consent
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.actions
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#sibling-engines
/// </summary>
public sealed class TranscriptionJobEnqueuerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 11, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmedReplacementFreezesConsentModelAndInputHashWithoutInvokingTheEngine()
    {
        const string syntheticCredential = "synthetic-groq-enqueue-key-7d6f";
        await using var fixture = await EnqueueFixture.CreateAsync(hasPublishedTranscript: true);
        var engine = new RecordingEngine(CreateCapabilities(
            GroqTranscriptionEngine.EngineId,
            [new TranscriptionModelCapability("model-fallback", "Fallback")]));
        var vault = new RecordingSecretVault((RemoteTranscriptionSecrets.Groq, syntheticCredential));
        var enqueuer = fixture.CreateEnqueuer(engine, vault);
        var preferences = CreatePreferences(
            GroqTranscriptionEngine.EngineId,
            modelId: "whisper-large-v3-turbo",
            RemoteTranscriptionDisclosureCatalog.GroqRevision);
        var discovered = Models(
            GroqTranscriptionEngine.EngineId,
            new TranscriptionModelCapability("whisper-large-v3-turbo", "Selected"));

        var confirmation = await Assert.ThrowsAsync<TranscriptionCommandException>(() =>
            enqueuer.EnqueueAsync(
                    fixture.Session,
                    preferences,
                    discovered,
                    replaceExisting: false,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("replacement_confirmation_required", confirmation.Code);
        Assert.Equal(0, vault.ReadCount);

        var result = await enqueuer.EnqueueAsync(
            fixture.Session,
            preferences,
            discovered,
            replaceExisting: true,
            CancellationToken.None);

        Assert.True(result.Created);
        var job = result.Job;
        Assert.Equal(GroqTranscriptionEngine.EngineId, job.EngineId);
        Assert.Equal(TranscriptionExecutionKind.Remote, job.ExecutionKind);
        Assert.Equal("whisper-large-v3-turbo", job.ModelId);
        Assert.Equal("auto", job.RequestedLanguage);
        Assert.Equal(fixture.Session.PrimaryAudioPath, job.InputAudioPath);
        Assert.Equal(fixture.AudioBytes.LongLength, job.InputSizeBytes);
        Assert.Equal(Sha256(fixture.AudioBytes), job.InputSha256);
        Assert.Equal(FixedNow, job.QueuedAtUtc);
        Assert.Equal(FixedNow, job.RemoteConsentAtUtc);
        Assert.Equal(RemoteTranscriptionDisclosureCatalog.GroqRevision, job.RemoteConsentRevision);
        Assert.Equal(TranscriptionTriggerKind.Manual, job.TriggerKind);
        Assert.True(job.ReplaceExisting);
        Assert.Null(job.EngineOptionsJson);
        Assert.Equal(
            Path.Combine(fixture.WorkerRoot, job.Id, "manifest.json"),
            job.ManifestPath);
        Assert.Equal(AudioChunkPlanner.CurrentManifestVersion, job.ManifestVersion);
        Assert.DoesNotContain(
            syntheticCredential,
            job.PrivacyPolicyJson ?? string.Empty,
            StringComparison.Ordinal);
        using (var policy = JsonDocument.Parse(Assert.IsType<string>(job.PrivacyPolicyJson)))
        {
            Assert.Equal(
                GroqTranscriptionEngine.EngineId,
                policy.RootElement.GetProperty("engineId").GetString());
            Assert.True(policy.RootElement.GetProperty("groqAccountDataControlsRequired").GetBoolean());
        }

        Assert.Equal(1, vault.ReadCount);
        Assert.Equal(RemoteTranscriptionSecrets.Groq, Assert.Single(vault.ReadKeys));
        Assert.Equal(0, engine.TranscribeCallCount);
        await fixture.AssertStorageDoesNotContainAsync(syntheticCredential);
    }

    [Fact]
    public async Task ConsentCredentialAndModelFailuresStopBeforeQueueOrNetworkActivity()
    {
        await using var fixture = await EnqueueFixture.CreateAsync();
        var engine = new RecordingEngine(CreateCapabilities(
            GroqTranscriptionEngine.EngineId,
            [new TranscriptionModelCapability("whisper-large-v3-turbo", "Available")]));
        var vault = new RecordingSecretVault();
        var enqueuer = fixture.CreateEnqueuer(engine, vault);
        var discovered = Models(
            GroqTranscriptionEngine.EngineId,
            new TranscriptionModelCapability("whisper-large-v3-turbo", "Available"));

        var missingConsent = await Assert.ThrowsAsync<TranscriptionCommandException>(() =>
            enqueuer.EnqueueAsync(
                    fixture.Session,
                    CreatePreferences(
                        GroqTranscriptionEngine.EngineId,
                        "whisper-large-v3-turbo",
                        disclosureRevision: null,
                        disclosureAccepted: false),
                    discovered,
                    replaceExisting: false,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("remote_consent_required", missingConsent.Code);
        Assert.Equal(0, vault.ReadCount);

        var missingCredential = await Assert.ThrowsAsync<TranscriptionCommandException>(() =>
            enqueuer.EnqueueAsync(
                    fixture.Session,
                    CreatePreferences(
                        GroqTranscriptionEngine.EngineId,
                        "whisper-large-v3-turbo",
                        RemoteTranscriptionDisclosureCatalog.GroqRevision),
                    discovered,
                    replaceExisting: false,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("credential_required", missingCredential.Code);
        Assert.Equal(1, vault.ReadCount);

        discovered = Models(GroqTranscriptionEngine.EngineId, new TranscriptionModelCapability("unavailable-account-model", "Unavailable"));
        var unavailableModel = await Assert.ThrowsAsync<TranscriptionCommandException>(() =>
            enqueuer.EnqueueAsync(
                    fixture.Session,
                    CreatePreferences(
                        GroqTranscriptionEngine.EngineId,
                        "model-unavailable",
                        RemoteTranscriptionDisclosureCatalog.GroqRevision),
                    discovered,
                    replaceExisting: false,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("model_unavailable", unavailableModel.Code);
        Assert.Equal(1, vault.ReadCount);
        Assert.Equal(0, engine.TranscribeCallCount);
        Assert.Equal(0, await fixture.CountJobsAsync());
    }

    [Fact]
    public async Task OpenRouterUsesCanonicalDiscoveredModelAndFreezesZdrOptionsWithoutNetwork()
    {
        const string syntheticCredential = "synthetic-openrouter-enqueue-key-a942";
        await using var fixture = await EnqueueFixture.CreateAsync();
        var engine = new RecordingEngine(CreateCapabilities(
            OpenRouterTranscriptionEngine.EngineId,
            models: [],
            supportsModelDiscovery: true));
        var vault = new RecordingSecretVault((RemoteTranscriptionSecrets.OpenRouter, syntheticCredential));
        var enqueuer = fixture.CreateEnqueuer(engine, vault);
        var preferences = CreatePreferences(
            OpenRouterTranscriptionEngine.EngineId,
            modelId: null,
            RemoteTranscriptionDisclosureCatalog.OpenRouterRevision,
            requireZeroDataRetention: true);
        var discovered = Models(
            OpenRouterTranscriptionEngine.EngineId,
            new TranscriptionModelCapability("model-secondary", "Secondary"),
            new TranscriptionModelCapability(OpenRouterTranscriptionEngine.DefaultModelId, "Recommended", IsRecommended: true));

        var result = await enqueuer.EnqueueAsync(
            fixture.Session,
            preferences,
            discovered,
            replaceExisting: false,
            cancellationToken: CancellationToken.None,
            triggerKind: TranscriptionTriggerKind.Automatic);

        Assert.Equal(OpenRouterTranscriptionEngine.DefaultModelId, result.Job.ModelId);
        Assert.Equal(TranscriptionTriggerKind.Automatic, result.Job.TriggerKind);
        Assert.Equal(RemoteTranscriptionDisclosureCatalog.OpenRouterRevision, result.Job.RemoteConsentRevision);
        Assert.Contains(
            "\"requireZeroDataRetention\":true",
            Assert.IsType<string>(result.Job.EngineOptionsJson),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"requireZeroDataRetention\":true",
            Assert.IsType<string>(result.Job.PrivacyPolicyJson),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            syntheticCredential,
            result.Job.EngineOptionsJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            syntheticCredential,
            result.Job.PrivacyPolicyJson,
            StringComparison.Ordinal);
        Assert.Equal(1, vault.ReadCount);
        Assert.Equal(0, engine.TranscribeCallCount);
        await fixture.AssertStorageDoesNotContainAsync(syntheticCredential);
    }

    [Fact]
    public async Task ConfigurationRetryKeepsCanonicalModelAndFreezesChangedZdrInANewJob()
    {
        const string syntheticCredential = "synthetic-openrouter-reconfigure-key-41f2";
        await using var fixture = await EnqueueFixture.CreateAsync();
        var engine = new RecordingEngine(CreateCapabilities(
            OpenRouterTranscriptionEngine.EngineId,
            models: [],
            supportsModelDiscovery: true));
        var vault = new RecordingSecretVault((RemoteTranscriptionSecrets.OpenRouter, syntheticCredential));
        var enqueuer = fixture.CreateEnqueuer(engine, vault);
        var models = Models(
            OpenRouterTranscriptionEngine.EngineId,
            new TranscriptionModelCapability("model-zdr-unavailable", "Old model"),
            new TranscriptionModelCapability(OpenRouterTranscriptionEngine.DefaultModelId, "Current model"));
        var original = await enqueuer.EnqueueAsync(
            fixture.Session,
            CreatePreferences(
                OpenRouterTranscriptionEngine.EngineId,
                "model-zdr-unavailable",
                RemoteTranscriptionDisclosureCatalog.OpenRouterRevision,
                requireZeroDataRetention: true),
            models,
            replaceExisting: false,
            CancellationToken.None);
        var repository = new TranscriptionJobRepository(fixture.Paths);
        Assert.NotNull(await repository.ClaimNextDueAsync(FixedNow, CancellationToken.None));
        Assert.True(await repository.RequireAttentionAsync(
            original.Job.Id,
            "zdr_route_unavailable",
            "Choose a compatible model or update ZDR.",
            currentChunkId: null,
            engineRequestId: "request-zdr",
            FixedNow.AddSeconds(1),
            CancellationToken.None));
        var updatedAudioBytes = fixture.AudioBytes.Concat(new byte[] { 17, 29, 43 }).ToArray();
        await File.WriteAllBytesAsync(
            Assert.IsType<string>(fixture.Session.PrimaryAudioPath),
            updatedAudioBytes);

        var replacement = await enqueuer.SupersedeConfigurationAsync(
            original.Job.Id,
            fixture.Session,
            CreatePreferences(
                OpenRouterTranscriptionEngine.EngineId,
                "model-current",
                RemoteTranscriptionDisclosureCatalog.OpenRouterRevision,
                requireZeroDataRetention: false),
            models,
            CancellationToken.None);

        Assert.True(replacement.Created);
        Assert.NotEqual(original.Job.Id, replacement.Job.Id);
        Assert.Equal(original.Job.EngineId, replacement.Job.EngineId);
        Assert.Equal(OpenRouterTranscriptionEngine.DefaultModelId, replacement.Job.ModelId);
        Assert.Contains(
            "\"requireZeroDataRetention\":false",
            Assert.IsType<string>(replacement.Job.EngineOptionsJson),
            StringComparison.Ordinal);
        Assert.Equal(original.Job.InputAudioPath, replacement.Job.InputAudioPath);
        Assert.NotEqual(original.Job.InputSha256, replacement.Job.InputSha256);
        Assert.Equal(Sha256(updatedAudioBytes), replacement.Job.InputSha256);
        Assert.Equal(updatedAudioBytes.LongLength, replacement.Job.InputSizeBytes);

        var frozenOriginal = await repository.GetAsync(original.Job.Id, CancellationToken.None);
        Assert.NotNull(frozenOriginal);
        Assert.Equal(TranscriptionJobStatus.Cancelled, frozenOriginal.Status);
        Assert.Equal(OpenRouterTranscriptionEngine.DefaultModelId, frozenOriginal.ModelId);
        Assert.Contains(
            "\"requireZeroDataRetention\":true",
            Assert.IsType<string>(frozenOriginal.EngineOptionsJson),
            StringComparison.Ordinal);
        Assert.Equal("superseded", frozenOriginal.StableErrorCode);
        Assert.Equal(0, engine.TranscribeCallCount);
        await fixture.AssertStorageDoesNotContainAsync(syntheticCredential);
    }

    [Fact]
    public async Task ConfigurationRetryRequiresAChangedConfigurationAndKeepsTheProvider()
    {
        const string syntheticCredential = "synthetic-groq-reconfigure-key-943a";
        await using var fixture = await EnqueueFixture.CreateAsync();
        var engine = new RecordingEngine(CreateCapabilities(
            GroqTranscriptionEngine.EngineId,
            [new TranscriptionModelCapability("whisper-large-v3-turbo", "Original")]));
        var vault = new RecordingSecretVault((RemoteTranscriptionSecrets.Groq, syntheticCredential));
        var enqueuer = fixture.CreateEnqueuer(engine, vault);
        var preferences = CreatePreferences(
            GroqTranscriptionEngine.EngineId,
            "whisper-large-v3-turbo",
            RemoteTranscriptionDisclosureCatalog.GroqRevision);
        var models = Models(
            GroqTranscriptionEngine.EngineId,
            new TranscriptionModelCapability("whisper-large-v3-turbo", "Original"));
        var original = await enqueuer.EnqueueAsync(
            fixture.Session,
            preferences,
            models,
            replaceExisting: false,
            CancellationToken.None);
        var repository = new TranscriptionJobRepository(fixture.Paths);
        Assert.NotNull(await repository.ClaimNextDueAsync(FixedNow, CancellationToken.None));
        Assert.True(await repository.RequireAttentionAsync(
            original.Job.Id,
            "invalid_engine_configuration",
            "Choose another model.",
            currentChunkId: null,
            engineRequestId: "request-config",
            FixedNow.AddSeconds(1),
            CancellationToken.None));

        var unchanged = await Assert.ThrowsAsync<TranscriptionCommandException>(() =>
            enqueuer.SupersedeConfigurationAsync(
                    original.Job.Id,
                    fixture.Session,
                    preferences,
                    models,
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("configuration_unchanged", unchanged.Code);

        var providerChange = await Assert.ThrowsAsync<TranscriptionCommandException>(() =>
            enqueuer.SupersedeConfigurationAsync(
                    original.Job.Id,
                    fixture.Session,
                    CreatePreferences(
                        OpenRouterTranscriptionEngine.EngineId,
                        "another-model",
                        RemoteTranscriptionDisclosureCatalog.OpenRouterRevision),
                    Models(
                        OpenRouterTranscriptionEngine.EngineId,
                        new TranscriptionModelCapability("another-model", "Another")),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("provider_change_requires_new_run", providerChange.Code);
        Assert.Equal(1, await fixture.CountJobsAsync());
        var stillAttention = await repository.GetAsync(original.Job.Id, CancellationToken.None);
        Assert.NotNull(stillAttention);
        Assert.Equal(TranscriptionJobStatus.AttentionRequired, stillAttention.Status);
        Assert.Equal("invalid_engine_configuration", stillAttention.StableErrorCode);
        Assert.Equal(0, engine.TranscribeCallCount);
        await fixture.AssertStorageDoesNotContainAsync(syntheticCredential);
    }

    [Fact]
    public async Task LocalEnqueueHoldsModelUsageLeaseUntilFrozenIdentityIsDurable()
    {
        await using var fixture = await EnqueueFixture.CreateAsync();
        var engine = new RecordingEngine(CreateCapabilities(
            LocalWhisperTranscriptionEngine.EngineId,
            [new TranscriptionModelCapability("large-v3-turbo", "Local transcription")],
            executionKind: TranscriptionExecutionKind.Local,
            requiresNetwork: false));
        var leaseProvider = new RecordingLocalExecutionLeaseProvider(
            new TranscriptionJobRepository(fixture.Paths),
            fixture.Root);
        var enqueuer = fixture.CreateEnqueuer(
            engine,
            new RecordingSecretVault(),
            leaseProvider);

        var result = await enqueuer.EnqueueAsync(
            fixture.Session,
            CreatePreferences(
                LocalWhisperTranscriptionEngine.EngineId,
                "base",
                disclosureRevision: null),
            Models(
                LocalWhisperTranscriptionEngine.EngineId,
                new TranscriptionModelCapability("large-v3-turbo", "Local transcription")),
            replaceExisting: false,
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(TranscriptionExecutionKind.Local, result.Job.ExecutionKind);
        Assert.Equal(1, leaseProvider.AcquireCount);
        Assert.Equal(1, leaseProvider.DisposeCount);
        Assert.True(leaseProvider.WasDurableWhenDisposed);
        var frozen = await new TranscriptionJobRepository(fixture.Paths)
            .GetLocalExecutionAsync(result.Job.Id, CancellationToken.None);
        Assert.NotNull(frozen);
        Assert.Equal(
            LocalWhisperRuntimeIdentity.PinnedV1.NativeBundleManifestSha256,
            frozen.Execution.NativeBundleManifestSha256);
        Assert.Equal(0, engine.TranscribeCallCount);
    }

    [Fact]
    public void LegacyModelSelectionCannotDowngradeNewLocalJobs()
    {
        var preferences = CreatePreferences(LocalWhisperTranscriptionEngine.EngineId, "base", null);
        Assert.Equal("large-v3-turbo", preferences.Canonicalize().GetModelId(LocalWhisperTranscriptionEngine.EngineId));
        Assert.Equal("large-v3-turbo", WhisperModelCatalog.LoadEmbedded().RecommendedModel.Id);
        Assert.Equal("base", WhisperModelCatalog.LoadEmbedded().GetRequiredForFrozenRun("base").Id);
    }

    [Theory]
    [InlineData("invalid_engine_configuration", true)]
    [InlineData("zdr_route_unavailable", true)]
    [InlineData("native_crash", true)]
    [InlineData("insufficient_memory", true)]
    [InlineData("invalid_key", false)]
    [InlineData("insufficient_credit", false)]
    [InlineData("provider_busy", false)]
    [InlineData(null, false)]
    public void RetryOnlySupersedesConfigurationRemediationErrors(
        string? stableErrorCode,
        bool expected) =>
        Assert.Equal(
            expected,
            TranscriptionJobEnqueuer.RequiresConfigurationSupersede(stableErrorCode));

    private static TranscriptionPreferences CreatePreferences(
        string engineId,
        string? modelId,
        string? disclosureRevision,
        bool disclosureAccepted = true,
        bool requireZeroDataRetention = true) =>
        new(
            SelectedEngineId: engineId,
            AutomaticEnabled: false,
            Language: "en",
            Engines:
            [
                new TranscriptionEnginePreference(
                    engineId,
                    modelId,
                    disclosureAccepted,
                    disclosureRevision)
            ],
            RequireZeroDataRetention: requireZeroDataRetention);

    private static IReadOnlyDictionary<string, IReadOnlyList<TranscriptionModelCapability>> Models(
        string engineId,
        params TranscriptionModelCapability[] models) =>
        new Dictionary<string, IReadOnlyList<TranscriptionModelCapability>>(StringComparer.Ordinal)
        {
            [engineId] = models
        };

    private static TranscriptionEngineCapabilities CreateCapabilities(
        string engineId,
        IReadOnlyList<TranscriptionModelCapability> models,
        bool supportsModelDiscovery = false,
        TranscriptionExecutionKind executionKind = TranscriptionExecutionKind.Remote,
        bool requiresNetwork = true) =>
        new(
            engineId,
            engineId,
            executionKind,
            RequiresNetwork: requiresNetwork,
            PrivacyDisclosure: requiresNetwork
                ? "Synthetic remote disclosure."
                : "Synthetic local disclosure.",
            SupportedPlatforms:
            [
                new TranscriptionPlatformTarget(
                    GetCurrentOperatingSystem(),
                    RuntimeInformation.ProcessArchitecture)
            ],
            Models: models,
            SupportedLanguageCodes: [],
            SupportsAutomaticLanguageDetection: true,
            SupportsDiarization: false,
            TimestampCapabilities: TranscriptionTimestampCapabilities.Segment,
            SupportsModelDiscovery: supportsModelDiscovery);

    private static TranscriptionOs GetCurrentOperatingSystem() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TranscriptionOs.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? TranscriptionOs.MacOS
                : TranscriptionOs.Linux;

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class RecordingEngine(TranscriptionEngineCapabilities capabilities)
        : ITranscriptionEngine
    {
        public int TranscribeCallCount { get; private set; }

        public TranscriptionEngineCapabilities Capabilities { get; } = capabilities;

        public ValueTask<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            TranscribeCallCount++;
            throw new InvalidOperationException("Enqueue must not invoke a transcription engine.");
        }
    }

    private sealed class RecordingSecretVault(params (string Key, string Value)[] values)
        : ISecretVault
    {
        private readonly IReadOnlyDictionary<string, string> _values = values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public List<string> ReadKeys { get; } = [];

        public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken)
        {
            ReadCount++;
            ReadKeys.Add(key);
            return ValueTask.FromResult(_values.GetValueOrDefault(key));
        }

        public ValueTask WriteAsync(string key, string? value, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Enqueue must not mutate the secret vault.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLocalExecutionLeaseProvider(
        TranscriptionJobRepository repository,
        string root) : ILocalTranscriptionExecutionLeaseProvider
    {
        public static string ModelSha256For(string modelId) =>
            Sha256(Encoding.UTF8.GetBytes($"synthetic-model:{modelId}"));

        public int AcquireCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool WasDurableWhenDisposed { get; private set; }

        public ValueTask<LocalTranscriptionExecutionLease> AcquireExecutionLeaseAsync(
            string modelId,
            string inputSha256,
            string requestedLanguage,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            Assert.Equal("large-v3-turbo", modelId);
            Assert.Equal("auto", requestedLanguage);
            var modelSha256 = ModelSha256For(modelId);
            var runtime = LocalWhisperRuntimeIdentity.PinnedV1;
            var identity = new LocalTranscriptionExecutionIdentity(
                ModelCatalogVersion: 1,
                ModelCatalogRevision: "5359861c739e955e79d9a303bcbc70fb988958b1",
                ModelFormat: "whisper.cpp.ggml-f16.v1",
                ModelPath: Path.Combine(root, "models", $"ggml-{modelId}.bin"),
                ModelSizeBytes: 4,
                ModelSha256: modelSha256,
                RuntimeVersion: runtime.RuntimeVersion,
                RuntimeCommit: runtime.RuntimeCommit,
                RuntimeSourceArchiveSha256: runtime.RuntimeSourceArchiveSha256,
                NativeBundleManifestSha256: runtime.NativeBundleManifestSha256,
                BridgeAbiVersion: runtime.BridgeAbiVersion,
                WorkerProtocolVersion: runtime.WorkerProtocolVersion,
                RequestedBackend: LocalTranscriptionBackend.Cpu,
                ResolvedBackend: null,
                ThreadCount: 4,
                InferenceParametersJson: "{\"language\":\"en\"}",
                ChunkProfileVersion: 1,
                RunIdentitySha256: Sha256(Encoding.UTF8.GetBytes(inputSha256)));
            return ValueTask.FromResult(new LocalTranscriptionExecutionLease(
                identity,
                new RecordingUsageLease(this, repository, modelSha256)));
        }

        private sealed class RecordingUsageLease(
            RecordingLocalExecutionLeaseProvider owner,
            TranscriptionJobRepository repository,
            string modelSha256) : IModelPayloadUsageLease
        {
            private int _disposed;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                owner.DisposeCount++;
                owner.WasDurableWhenDisposed = await repository.IsLocalModelRetainedAsync(
                    modelSha256,
                    CancellationToken.None);
            }
        }
    }

    private sealed class EnqueueFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private EnqueueFixture(
            string root,
            LocalAppPaths paths,
            SqliteConnection connection,
            MeetingSessionListItem session,
            byte[] audioBytes)
        {
            Root = root;
            Paths = paths;
            _connection = connection;
            Session = session;
            AudioBytes = audioBytes;
            WorkerRoot = Path.Combine(root, "transcription-worker");
        }

        public string Root { get; }

        public LocalAppPaths Paths { get; }

        public MeetingSessionListItem Session { get; }

        public byte[] AudioBytes { get; }

        public string WorkerRoot { get; }

        public static async ValueTask<EnqueueFixture> CreateAsync(bool hasPublishedTranscript = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"istranscribe-enqueue-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new LocalAppPaths("isTranscribe", root);
            var connection = await new SqliteDatabaseInitializer(paths)
                .InitializeAsync(CancellationToken.None);
            var id = Guid.NewGuid();
            var sessionDirectory = Path.Combine(root, "recordings", id.ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            var audioPath = Path.Combine(sessionDirectory, "meeting.mp3");
            var audioBytes = Enumerable.Range(0, 4_096)
                .Select(static value => (byte)(value % 251))
                .ToArray();
            await File.WriteAllBytesAsync(audioPath, audioBytes);
            var markdownPath = hasPublishedTranscript
                ? Path.Combine(sessionDirectory, "existing.transcript.md")
                : null;
            var jsonPath = hasPublishedTranscript
                ? Path.Combine(sessionDirectory, "existing.transcript.json")
                : null;
            var record = MeetingSessionRecord.Create(
                id,
                FixedNow.AddMinutes(-5),
                "manual",
                "mixed",
                "output-device",
                "microphone-device") with
            {
                Status = "ready",
                EndedAtUtc = FixedNow,
                DurationSeconds = 300,
                PrimaryAudioPath = audioPath,
                TranscriptMarkdownPath = markdownPath,
                TranscriptJsonPath = jsonPath,
                TranscriptionStatus = hasPublishedTranscript ? "completed" : "not_started"
            };
            await new MeetingSessionRepository(connection)
                .UpsertAsync(record, CancellationToken.None);
            var session = new MeetingSessionListItem(
                Id: record.Id,
                SourceApp: record.SourceApp,
                StartedAtUtc: record.StartedAtUtc,
                EndedAtUtc: record.EndedAtUtc,
                DurationSeconds: record.DurationSeconds,
                RecordingStatus: record.Status,
                TranscriptionStatus: record.TranscriptionStatus,
                AudioOutputPath: null,
                AudioMicPath: null,
                AudioMixPath: null,
                TranscriptMarkdownPath: markdownPath,
                ErrorCode: null,
                ErrorMessage: null,
                PrimaryAudioPath: audioPath,
                TranscriptJsonPath: jsonPath);
            return new EnqueueFixture(root, paths, connection, session, audioBytes);
        }

        public TranscriptionJobEnqueuer CreateEnqueuer(
            ITranscriptionEngine engine,
            ISecretVault vault,
            ILocalTranscriptionExecutionLeaseProvider? localExecutionLeases = null) =>
            new(
                new TranscriptionJobRepository(Paths),
                new TranscriptionEngineRegistry([engine]),
                vault,
                WorkerRoot,
                new FixedTimeProvider(FixedNow),
                localExecutionLeases);

        public async ValueTask<long> CountJobsAsync()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM transcription_job;";
            return (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L);
        }

        public async ValueTask AssertStorageDoesNotContainAsync(string sentinel)
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4_096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                var contents = Encoding.UTF8.GetString(copy.ToArray());
                Assert.DoesNotContain(sentinel, contents, StringComparison.Ordinal);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
