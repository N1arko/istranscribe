using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IsTranscribe.Application;
using IsTranscribe.Application.Diagnostics;
using IsTranscribe.Core.Detection;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Core.Settings;
using IsTranscribe.Host.Settings;
using IsTranscribe.Platform.Windows;
using Microsoft.Data.Sqlite;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Runs one operator-authorized, real Windows detection scenario in an isolated
/// storage root and persists its full release-gate outcome.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#release-policy
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AcceptanceRunner
{
    private const string ApplicationName = "isTranscribe";
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    public async Task<AcceptanceRunResult> RunAsync(
        AcceptanceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var runId = CreateRunId(generatedAtUtc, options);
        var target = CreateTargetEvidence();
        var build = CreateBuildEvidence();
        var client = ClientProcessDiscovery.Discover(
            options.ProfileId,
            options.Surface,
            options.PreferredClientProcessName);
        var subject = CreateSubjectEvidence(options, client);

        if (!client.Found)
        {
            var blocked = CreateIncompleteEvidence(
                runId,
                generatedAtUtc,
                target,
                build,
                subject,
                client.DiscoveryFailed ? RunOutcomeStatus.Failed : RunOutcomeStatus.Blocked,
                client.ReasonCode ?? "client_process_not_found");
            return await PersistAsync(
                    options,
                    blocked,
                    sourceLogPath: null,
                    retainSourceLog: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (!options.OperatorReady)
        {
            var notRun = CreateIncompleteEvidence(
                runId,
                generatedAtUtc,
                target,
                build,
                subject,
                RunOutcomeStatus.NotRun,
                "operator_consent_missing");
            return await PersistAsync(
                    options,
                    notRun,
                    sourceLogPath: null,
                    retainSourceLog: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            return await RunLiveAsync(
                    options,
                    runId,
                    generatedAtUtc,
                    target,
                    build,
                    subject,
                    client,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failed = CreateIncompleteEvidence(
                runId,
                generatedAtUtc,
                target,
                build,
                subject,
                RunOutcomeStatus.Failed,
                exception is OperationCanceledException ? "run_cancelled" : "acceptance_runner_failed");
            return await PersistAsync(
                    options,
                    failed,
                    sourceLogPath: null,
                    retainSourceLog: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task<AcceptanceRunResult> RunLiveAsync(
        AcceptanceOptions options,
        string runId,
        DateTimeOffset generatedAtUtc,
        TargetEvidence target,
        BuildEvidence build,
        SubjectEvidence subject,
        ClientDiscoveryResult client,
        CancellationToken cancellationToken)
    {
        var sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            $"istranscribe-feat011-{Guid.NewGuid():N}");
        try
        {
            var runtimeRoot = Path.Combine(sandboxRoot, "runtime");
            var recordingsRoot = Path.Combine(sandboxRoot, "recordings");
            var transcriptsRoot = Path.Combine(sandboxRoot, "transcripts");
            Directory.CreateDirectory(recordingsRoot);
            Directory.CreateDirectory(transcriptsRoot);
            var paths = new LocalAppPaths(ApplicationName, runtimeRoot);
            await PrepareIsolatedSettingsAsync(
                    paths,
                    recordingsRoot,
                    transcriptsRoot,
                    cancellationToken)
                .ConfigureAwait(false);

            var readyAtUtc = DateTimeOffset.UtcNow;
            MeetingPromptSnapshot? prompt = null;
            long? elapsedMilliseconds = null;
            string? failureReason = null;
            var countingAudio = new CountingAudioPlatform(
                new WindowsAudioPlatform(new BootstrapFileLogger(paths.HostLogFilePath)));
            using var fileCreationAudit = new FileCreationAudit(
                [recordingsRoot, paths.TempDirectory, transcriptsRoot]);
            ApplicationRuntime? runtime = null;
            using var httpEvents = new ManagedHttpEventCounter();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                runtime = new ApplicationRuntime(
                    new WindowsApplicationPlatformRuntimeAdapter(),
                    runtimeRoot,
                    countingAudio);
                await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
                prompt = await WaitForPromptAsync(runtime, options.Timeout, cancellationToken)
                    .ConfigureAwait(false);
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                if (prompt is null)
                {
                    failureReason = "ask_timeout";
                }
                else
                {
                    client = client.BindToProcess(ReadRootProcessId(prompt.CandidateId));
                    subject = CreateSubjectEvidence(options, client);
                    await runtime.ResolveMeetingPromptAsync(
                            prompt.CandidateId,
                            MeetingPromptUserAction.Skip,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                failureReason = "run_cancelled";
            }
            catch (Exception)
            {
                failureReason = "runtime_execution_failed";
            }
            finally
            {
                stopwatch.Stop();
                if (runtime is not null)
                {
                    try
                    {
                        await runtime.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        failureReason = "runtime_shutdown_timeout";
                    }
                    catch (Exception)
                    {
                        failureReason = "runtime_shutdown_failed";
                    }
                }
                else
                {
                    await countingAudio.DisposeAsync().ConfigureAwait(false);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
            var scannedFileCounts = new[]
            {
            CountFiles(recordingsRoot),
            CountFiles(paths.TempDirectory),
            CountFiles(transcriptsRoot)
        };
            var unexpectedFileCount = scannedFileCounts.Any(static count => count < 0)
                ? -1
                : scannedFileCounts.Sum() + fileCreationAudit.CreatedPathCount;
            var meetingSessionCount = await CountMeetingSessionsAsync(
                    paths.DatabaseFilePath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var privacyInspection = LogPrivacyInspector.Inspect(
                paths.HostLogFilePath,
                client.RawWindowTitles,
                prompt is null
                    ? null
                    : new ExpectedPromptLogEvidence(
                        prompt.CandidateId,
                        prompt.ProfileId,
                        prompt.ConfidenceScore));
            var askPassed = prompt is not null
                && elapsedMilliseconds.HasValue
                && elapsedMilliseconds.Value <= LiveEvidenceValidator.ReleaseAskLimitMilliseconds;
            var promptPassed = prompt is not null
                && string.Equals(prompt.ProfileId, options.ProfileId, StringComparison.OrdinalIgnoreCase)
                && CandidateMatchesSurface(prompt.CandidateId, options.Surface)
                && ReadRootProcessId(prompt.CandidateId) == subject.Client.ProcessId;
            var audioPassed = countingAudio.CaptureStartCount == 0
                && unexpectedFileCount == 0
                && meetingSessionCount == 0;
            var privacyPassed = privacyInspection.DetectionLogSchemaValid
                && privacyInspection.RawWindowTitleMatchCount == 0
                && privacyInspection.AudioPayloadFieldCount == 0
                && httpEvents.EventCount == 0;

            failureReason ??= DetermineFailureReason(
                target,
                subject,
                askPassed,
                promptPassed,
                audioPassed,
                privacyPassed);
            var passed = failureReason is null;
            var evidence = new LiveEvidence(
                LiveEvidenceValidator.SchemaVersion,
                runId,
                $"{options.ProfileId}.{options.Surface}.positive",
                generatedAtUtc,
                target,
                build,
                subject,
                new ObservationEvidence(
                    new ActivityReadyEvidence(
                        CheckStatus.Pass,
                        "operator_confirmation",
                        readyAtUtc),
                    new AskEvidence(
                        askPassed ? CheckStatus.Pass : CheckStatus.Fail,
                        elapsedMilliseconds,
                        LiveEvidenceValidator.ReleaseAskLimitMilliseconds),
                    new PromptEvidence(
                        promptPassed ? CheckStatus.Pass : CheckStatus.Fail,
                        prompt is not null,
                        prompt?.CandidateId,
                        prompt?.ProfileId,
                        prompt is null ? null : "awaiting_confirmation",
                        prompt?.ConfidenceScore),
                    new PreconfirmationAudioEvidence(
                        audioPassed ? CheckStatus.Pass : CheckStatus.Fail,
                        countingAudio.CaptureStartCount,
                        unexpectedFileCount,
                        meetingSessionCount,
                        ["runtime_temp", "configured_recordings_root", "configured_transcripts_root"]),
                    new PrivacyEvidence(
                        privacyPassed ? CheckStatus.Pass : CheckStatus.Fail,
                        privacyInspection.DetectionLogSchemaValid,
                        privacyInspection.RawWindowTitleMatchCount,
                        privacyInspection.AudioPayloadFieldCount,
                        httpEvents.EventCount)),
                new OutcomeEvidence(
                    passed ? RunOutcomeStatus.Passed : RunOutcomeStatus.Failed,
                    failureReason));

            return await PersistAsync(
                    options,
                    evidence,
                    paths.HostLogFilePath,
                    retainSourceLog: privacyPassed,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteOwnedSandbox(sandboxRoot);
        }
    }

    private static async Task PrepareIsolatedSettingsAsync(
        LocalAppPaths paths,
        string recordingsRoot,
        string transcriptsRoot,
        CancellationToken cancellationToken)
    {
        paths.EnsureAppOwnedDirectoriesExist();
        var settings = ApplicationSettings.Default with
        {
            OnboardingCompleted = true,
            Storage = ApplicationSettings.Default.Storage with
            {
                RecordingsFolder = recordingsRoot,
                TranscriptsFolder = transcriptsRoot,
                FailedTempFolder = paths.TempDirectory
            },
            ReleaseV2 = ReleaseV2Settings.Default with
            {
                OnboardingCompleted = true,
                ServiceEnabled = true,
                RecordingsFolder = recordingsRoot
            }
        };
        await new JsonApplicationSettingsStore(paths)
            .SaveAsync(settings, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MeetingPromptSnapshot?> WaitForPromptAsync(
        ApplicationRuntime runtime,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed <= timeout)
        {
            if (runtime.Snapshot.PendingMeetingPrompt is { } prompt)
            {
                return prompt;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static LiveEvidence CreateIncompleteEvidence(
        string runId,
        DateTimeOffset generatedAtUtc,
        TargetEvidence target,
        BuildEvidence build,
        SubjectEvidence subject,
        RunOutcomeStatus outcome,
        string reasonCode) => new(
            LiveEvidenceValidator.SchemaVersion,
            runId,
            $"{subject.ProfileId}.{subject.Surface}.positive",
            generatedAtUtc,
            target,
            build,
            subject,
            new ObservationEvidence(
                new ActivityReadyEvidence(CheckStatus.NotObserved, null, null),
                new AskEvidence(
                    CheckStatus.NotObserved,
                    ElapsedMilliseconds: null,
                    LiveEvidenceValidator.ReleaseAskLimitMilliseconds),
                new PromptEvidence(
                    CheckStatus.NotObserved,
                    Published: null,
                    CandidateId: null,
                    ObservedProfileId: null,
                    ActivityState: null,
                    ConfidenceScore: null),
                new PreconfirmationAudioEvidence(
                    CheckStatus.NotObserved,
                    CaptureStartCount: null,
                    UnexpectedFileCount: null,
                    MeetingSessionCount: null,
                    ScannedRoots: []),
                new PrivacyEvidence(
                    CheckStatus.NotObserved,
                    DetectionLogSchemaValid: null,
                    RawWindowTitleMatchCount: null,
                    AudioPayloadFieldCount: null,
                    ManagedHttpRequestCount: null)),
            new OutcomeEvidence(outcome, reasonCode));

    private static TargetEvidence CreateTargetEvidence() => new(
        Environment.OSVersion.Version.ToString(),
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

    private static BuildEvidence CreateBuildEvidence()
    {
        var runtimeAssembly = typeof(ApplicationRuntime).Assembly;
        var runnerAssembly = typeof(AcceptanceRunner).Assembly;
        var informationalVersion = runnerAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? runnerAssembly.GetName().Version?.ToString()
            ?? "unknown";
        var revision = FirstNonEmpty(
            Environment.GetEnvironmentVariable("GITHUB_SHA"),
            Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION"));
        var sourceRevision = revision is null
            ? new SourceRevisionEvidence(
                ObservationValueStatus.Unavailable,
                Value: null,
                Source: null,
                Reason: "repository_has_no_vcs_metadata")
            : new SourceRevisionEvidence(
                ObservationValueStatus.Observed,
                revision,
                "build_environment",
                Reason: null);
        return new BuildEvidence(
            informationalVersion,
            BuildFingerprint.TryHashFile(runtimeAssembly.Location),
            BuildFingerprint.TryHashFile(runnerAssembly.Location),
            BuildFingerprint.CreateDependencyManifest(),
            sourceRevision);
    }

    private static SubjectEvidence CreateSubjectEvidence(
        AcceptanceOptions options,
        ClientDiscoveryResult client)
    {
        var version = client.Version is null
            ? new SourceRevisionEvidence(
                ObservationValueStatus.NotObserved,
                Value: null,
                Source: null,
                Reason: client.Found ? "file_version_unavailable" : "client_process_not_found")
            : new SourceRevisionEvidence(
                ObservationValueStatus.Observed,
                client.Version,
                "file_version_info",
                Reason: null);
        return new SubjectEvidence(
            options.ProfileId,
            options.Surface,
            new ClientEvidence(
                client.ProcessId,
                client.ProcessName,
                version,
                client.ExecutableSha256),
            new SourceRevisionEvidence(
                ObservationValueStatus.NotExposed,
                Value: null,
                Source: null,
                Reason: options.Surface == "browser"
                    ? "web_service_version_not_exposed"
                    : "service_version_is_client_version"));
    }

    private static string? DetermineFailureReason(
        TargetEvidence target,
        SubjectEvidence subject,
        bool askPassed,
        bool promptPassed,
        bool audioPassed,
        bool privacyPassed)
    {
        if (!string.Equals(target.Architecture, "x64", StringComparison.OrdinalIgnoreCase))
        {
            return "target_architecture_not_x64";
        }

        if (subject.Client.Version.Status != ObservationValueStatus.Observed)
        {
            return "client_version_not_observed";
        }

        if (string.IsNullOrWhiteSpace(subject.Client.ExecutableSha256))
        {
            return "client_executable_hash_not_observed";
        }

        if (!askPassed)
        {
            return "ask_gate_failed";
        }

        if (!promptPassed)
        {
            return "prompt_profile_mismatch";
        }

        if (!audioPassed)
        {
            return "preconfirmation_audio_gate_failed";
        }

        return privacyPassed ? null : "privacy_gate_failed";
    }

    private static bool CandidateMatchesSurface(string candidateId, string surface) =>
        surface switch
        {
            "desktop" => candidateId.Contains(":desktop:", StringComparison.Ordinal),
            "browser" => candidateId.StartsWith("browser:", StringComparison.Ordinal),
            _ => false
        };

    private static int? ReadRootProcessId(string candidateId)
    {
        var parts = candidateId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var value = candidateId.StartsWith("browser:", StringComparison.Ordinal)
            ? parts.ElementAtOrDefault(1)
            : parts.LastOrDefault();
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var processId)
            ? processId
            : null;
    }

    private static async Task<int> CountMeetingSessionsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return -1;
        }

        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM meeting_session;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return -1;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static int CountFiles(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
                : -1;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static string CreateRunId(DateTimeOffset generatedAtUtc, AcceptanceOptions options)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{generatedAtUtc:yyyyMMdd'T'HHmmss'Z'}-{options.ProfileId}-{options.Surface}-{suffix}";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static async Task<AcceptanceRunResult> PersistAsync(
        AcceptanceOptions options,
        LiveEvidence evidence,
        string? sourceLogPath,
        bool retainSourceLog,
        CancellationToken cancellationToken)
    {
        var artifactDirectory = await EvidenceArtifactWriter.WriteAsync(
                options.OutputDirectory,
                evidence,
                sourceLogPath,
                retainSourceLog,
                cancellationToken)
            .ConfigureAwait(false);
        return new AcceptanceRunResult(evidence, artifactDirectory);
    }

    private static void TryDeleteOwnedSandbox(string sandboxRoot)
    {
        try
        {
            Directory.Delete(sandboxRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record AcceptanceRunResult(
    LiveEvidence Evidence,
    string ArtifactDirectory);
