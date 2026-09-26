using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#behavior.states
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// </remarks>
public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public void Current_transcription_rows_show_only_bounded_provider_neutral_metadata()
    {
        var recording = RecordingWithTranscription("provider-request:abc-123");
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            recentRecordings: [recording]));
        using var viewModel = new DiagnosticsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeDesktopShell());

        Assert.Equal("remote.openrouter", FindValue(viewModel, "TranscriptionEngine"));
        Assert.Equal("provider/whisper", FindValue(viewModel, "TranscriptionModel"));
        Assert.Equal("2/5", FindValue(viewModel, "TranscriptionChunks"));
        Assert.Equal(
            "provider-request:abc-123",
            FindValue(viewModel, "TranscriptionProviderRequestId"));
        Assert.DoesNotContain("private meeting title", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-audio", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-transcript", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("gsk_synthetic-secret", viewModel.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_drop_an_unbounded_provider_request_id()
    {
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(
            recentRecordings: [RecordingWithTranscription(new string('x', 129))]));
        using var viewModel = new DiagnosticsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeDesktopShell());

        Assert.DoesNotContain(
            viewModel.Rows,
            row => row.Label == "String.Diagnostics.Field.TranscriptionProviderRequestId");
    }

    [Fact]
    public void Local_transcription_rows_show_backend_runtime_hashes_threads_and_processing_time()
    {
        var recording = RecordingWithTranscription(providerRequestId: null);
        recording = recording with
        {
            Transcription = recording.Transcription! with
            {
                EngineId = "local.whisper",
                ModelId = "small",
                LocalDiagnostics = new RuntimeLocalTranscriptionDiagnosticsSnapshot(
                    RequestedBackend: "auto",
                    ResolvedBackend: "metal",
                    ThreadCount: 6,
                    RuntimeVersion: "whisper.cpp-1.9.1",
                    NativeBundleManifestSha256: new string('a', 64),
                    ModelSha256: new string('b', 64),
                    ProcessingDurationMilliseconds: 125_000)
            }
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(recentRecordings: [recording]));
        using var viewModel = new DiagnosticsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeDesktopShell());

        Assert.Equal("auto", FindValue(viewModel, "TranscriptionRequestedBackend"));
        Assert.Equal("metal", FindValue(viewModel, "TranscriptionResolvedBackend"));
        Assert.Equal("6", FindValue(viewModel, "TranscriptionThreads"));
        Assert.Equal("whisper.cpp-1.9.1", FindValue(viewModel, "TranscriptionRuntimeVersion"));
        Assert.Equal(new string('a', 64), FindValue(viewModel, "TranscriptionNativeManifest"));
        Assert.Equal(new string('b', 64), FindValue(viewModel, "TranscriptionModelSha256"));
        Assert.Equal("2:05", FindValue(viewModel, "TranscriptionProcessingDuration"));
    }

    [Fact]
    public void Capability_issue_has_a_localized_diagnostic_row_for_every_runtime_value()
    {
        foreach (var issue in Enum.GetValues<RuntimeCapabilityIssue>())
        {
            var capability = new RuntimeCapabilitySnapshot(
                RuntimeCapabilityState.Degraded,
                SupportsProcessOutputCapture: true,
                Summary: "raw capability detail")
            {
                Issue = issue,
                HasActiveOutput = true,
                HasActiveMicrophone = true
            };
            var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(capability: capability));
            using var viewModel = new DiagnosticsViewModel(
                runtime,
                new FakeLocalizationService(),
                new FakeDesktopShell());

            var row = Assert.Single(
                viewModel.Rows,
                candidate => candidate.Label == "String.Diagnostics.Field.CapabilityIssue");
            Assert.Equal($"String.Diagnostics.CapabilityIssue.{issue}", row.Value);
            Assert.DoesNotContain("raw capability detail", viewModel.SummaryText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Configured_microphone_failure_remains_visible_when_endpoint_inventory_is_ready()
    {
        var capability = new RuntimeCapabilitySnapshot(
            RuntimeCapabilityState.Degraded,
            SupportsProcessOutputCapture: true,
            Summary: string.Empty)
        {
            Issue = RuntimeCapabilityIssue.ConfiguredMicrophoneUnavailable,
            HasActiveOutput = true,
            HasActiveMicrophone = true
        };
        var runtime = new FakeApplicationRuntime(SnapshotFactory.Create(capability: capability));
        using var viewModel = new DiagnosticsViewModel(
            runtime,
            new FakeLocalizationService(),
            new FakeDesktopShell());

        Assert.Equal(
            "String.Diagnostics.CapabilityIssue.ConfiguredMicrophoneUnavailable",
            FindValue(viewModel, "CapabilityIssue"));
        Assert.Equal("String.Diagnostics.Endpoint.Ready", FindValue(viewModel, "OutputReadiness"));
        Assert.Equal("String.Diagnostics.Endpoint.Ready", FindValue(viewModel, "MicrophoneReadiness"));
    }

    [Fact]
    public void Language_row_tracks_the_localized_active_ui_language()
    {
        var strings = new FakeLocalizationService();
        using var viewModel = new DiagnosticsViewModel(
            new FakeApplicationRuntime(SnapshotFactory.Create()),
            strings,
            new FakeDesktopShell());

        Assert.Equal("String.Settings.Language.Russian", FindValue(viewModel, "Language"));

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("String.Settings.Language.English", FindValue(viewModel, "Language"));
    }

    [Fact]
    public void Language_switch_preserves_the_exact_diagnostic_status_and_error_category()
    {
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new DiagnosticsViewModel(
            new FakeApplicationRuntime(SnapshotFactory.Create()),
            strings,
            new FakeDesktopShell());

        viewModel.MarkCopied();
        Assert.Equal("ru:String.Diagnostics.Copied", viewModel.StatusMessage);

        strings.SetLanguage(UiLanguage.English);
        Assert.Equal("en:String.Diagnostics.Copied", viewModel.StatusMessage);

        viewModel.MarkActionFailed();
        Assert.Equal("en:String.Diagnostics.Unavailable", viewModel.ErrorMessage);
        strings.SetLanguage(UiLanguage.Russian);

        Assert.Equal("ru:String.Diagnostics.Unavailable", viewModel.ErrorMessage);
        Assert.False(viewModel.HasStatus);
    }

    private static string FindValue(DiagnosticsViewModel viewModel, string fieldSuffix) =>
        Assert.Single(
            viewModel.Rows,
            row => row.Label == $"String.Diagnostics.Field.{fieldSuffix}").Value;

    private static RecentRecordingSnapshot RecordingWithTranscription(string? providerRequestId) => new(
        Guid.NewGuid(),
        "private source",
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(10),
        "/private-audio/meeting.mp3",
        RequiresAttention: false)
    {
        DisplayTitle = "private meeting title",
        TranscriptMarkdownPath = "/private-transcript/meeting.md",
        TranscriptJsonPath = "/private-transcript/meeting.json",
        Transcription = new RuntimeTranscriptionJobSnapshot(
            "job-private",
            "remote.openrouter",
            "provider/whisper",
            RuntimeTranscriptionJobState.Processing,
            Progress: 0.4,
            CurrentChunkIndex: 1,
            NextAttemptAtUtc: null,
            StableErrorCode: null,
            ErrorMessage: "gsk_synthetic-secret")
        {
            ChunkCount = 5,
            ProviderRequestId = providerRequestId
        }
    };
}
