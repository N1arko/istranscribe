using System.Text.Json;
using IsTranscribe.DetectionAcceptance;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </summary>
public sealed class DetectionAcceptanceEvidenceTests
{
    [Fact]
    public void PassedEvidenceRequiresEveryReleaseGate()
    {
        var valid = ValidPassedEvidence();
        Assert.Empty(LiveEvidenceValidator.Validate(valid));

        var invalidCases = new[]
        {
            valid with { Target = valid.Target with { Architecture = "arm64" } },
            valid with { Build = valid.Build with { RuntimeAssemblySha256 = new string('b', 64) } },
            valid with
            {
                Subject = valid.Subject with
                {
                    Client = valid.Subject.Client with
                    {
                        Version = valid.Subject.Client.Version with
                        {
                            Status = ObservationValueStatus.NotObserved,
                            Value = null
                        }
                    }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    Ask = valid.Observation.Ask with { ElapsedMilliseconds = 15_001 }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    Ask = valid.Observation.Ask with
                    {
                        ElapsedMilliseconds = 45_000,
                        LimitMilliseconds = 60_000
                    }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    Prompt = valid.Observation.Prompt with { ObservedProfileId = "microsoft-teams" }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    Prompt = valid.Observation.Prompt with { CandidateId = "browser:42:1001" }
                }
            },
            valid with
            {
                Subject = valid.Subject with
                {
                    Client = valid.Subject.Client with { ProcessId = 84 }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    PreconfirmationAudio = valid.Observation.PreconfirmationAudio with
                    {
                        CaptureStartCount = 1
                    }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    PreconfirmationAudio = valid.Observation.PreconfirmationAudio with
                    {
                        UnexpectedFileCount = 1
                    }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    PreconfirmationAudio = valid.Observation.PreconfirmationAudio with
                    {
                        MeetingSessionCount = 1
                    }
                }
            },
            valid with
            {
                Observation = valid.Observation with
                {
                    Privacy = valid.Observation.Privacy with { ManagedHttpRequestCount = 1 }
                }
            }
        };

        Assert.All(invalidCases, evidence => Assert.NotEmpty(LiveEvidenceValidator.Validate(evidence)));
    }

    [Fact]
    public void IncompleteRunIsValidOnlyWithAnExplicitReason()
    {
        var incomplete = ValidPassedEvidence() with
        {
            Observation = new ObservationEvidence(
                new ActivityReadyEvidence(CheckStatus.NotObserved, null, null),
                new AskEvidence(CheckStatus.NotObserved, null, 15_000),
                new PromptEvidence(CheckStatus.NotObserved, null, null, null, null, null),
                new PreconfirmationAudioEvidence(CheckStatus.NotObserved, null, null, null, []),
                new PrivacyEvidence(CheckStatus.NotObserved, null, null, null, null)),
            Outcome = new OutcomeEvidence(RunOutcomeStatus.NotRun, "operator_consent_missing")
        };

        Assert.Empty(LiveEvidenceValidator.Validate(incomplete));
        Assert.NotEmpty(LiveEvidenceValidator.Validate(incomplete with
        {
            Outcome = incomplete.Outcome with { ReasonCode = null }
        }));
    }

    [Fact]
    public void JsonContractUsesClosedMachineReadableStatusNames()
    {
        var json = JsonSerializer.Serialize(ValidPassedEvidence(), EvidenceJson.Options);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "feat-011-live-v1",
            document.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(
            "not_exposed",
            document.RootElement
                .GetProperty("subject")
                .GetProperty("service_version")
                .GetProperty("status")
                .GetString());
        Assert.Equal(
            "passed",
            document.RootElement.GetProperty("outcome").GetProperty("status").GetString());
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "feat-011-live-v1.schema.json")));
        Assert.Equal(
            "feat-011-live-v1",
            schema.RootElement
                .GetProperty("properties")
                .GetProperty("schema_version")
                .GetProperty("const")
                .GetString());
        Assert.True(schema.RootElement.GetProperty("allOf").GetArrayLength() > 0);
        Assert.Equal(
            15_000,
            schema.RootElement
                .GetProperty("$defs")
                .GetProperty("ask")
                .GetProperty("properties")
                .GetProperty("limit_milliseconds")
                .GetProperty("const")
                .GetInt32());
    }

    [Fact]
    public void MatrixPassRequiresEverySurfaceOnTheSameFreshBuild()
    {
        var template = ValidPassedEvidence();
        var loaded = LiveEvidenceMatrixBuilder.RequiredScenarioIds
            .Select(scenarioId =>
            {
                var parts = scenarioId.Split('.');
                var profileId = parts[0];
                var surface = parts[1];
                var candidateId = surface == "browser"
                    ? "browser:42:1001"
                    : $"{profileId}:desktop:42";
                var evidence = template with
                {
                    RunId = $"fixture-{profileId}-{surface}",
                    ScenarioId = scenarioId,
                    Subject = template.Subject with
                    {
                        ProfileId = profileId,
                        Surface = surface
                    },
                    Observation = template.Observation with
                    {
                        Prompt = template.Observation.Prompt with
                        {
                            CandidateId = candidateId,
                            ObservedProfileId = profileId
                        }
                    }
                };
                return new LoadedLiveEvidence(evidence, IntegrityValid: true);
            })
            .ToArray();

        var passed = LiveEvidenceMatrixBuilder.Build(
            loaded,
            template.Build.DependencySha256,
            template.GeneratedAtUtc.AddHours(1));
        var incomplete = LiveEvidenceMatrixBuilder.Build(
            loaded[..^1],
            template.Build.DependencySha256,
            template.GeneratedAtUtc.AddHours(1));

        Assert.Equal(MatrixOutcomeStatus.Passed, passed.Status);
        Assert.All(passed.Rows, static row => Assert.Equal(MatrixRowStatus.Passed, row.Status));
        Assert.Equal(MatrixOutcomeStatus.Incomplete, incomplete.Status);
        Assert.Contains(incomplete.Rows, static row => row.Status == MatrixRowStatus.Missing);
    }

    [Fact]
    public void PrivacyInspectorDecodesEscapedTitlesAndBindsThePromptDecision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-log-privacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "host.log");
        const string sensitiveTitle = "Секретная встреча — план";
        try
        {
            File.WriteAllLines(
                logPath,
                [
                    CreateDecisionLine("zoom:desktop:42", "zoom", 84),
                    JsonSerializer.Serialize(new
                    {
                        timestamp_utc = DateTimeOffset.Parse("2026-07-11T12:00:01Z"),
                        level = "Info",
                        event_code = "FIXTURE_EVENT",
                        session_id = (string?)null,
                        job_name = (string?)null,
                        message = sensitiveTitle,
                        metadata = new { }
                    }),
                    JsonSerializer.Serialize(new
                    {
                        timestamp_utc = DateTimeOffset.Parse("2026-07-11T12:00:02Z"),
                        level = "Info",
                        event_code = "FIXTURE_PAYLOAD",
                        session_id = (string?)null,
                        job_name = (string?)null,
                        message = "fixture",
                        metadata = new { values = Enumerable.Repeat(1, 64).ToArray() }
                    })
                ]);

            var matching = LogPrivacyInspector.Inspect(
                logPath,
                [sensitiveTitle],
                new ExpectedPromptLogEvidence("zoom:desktop:42", "zoom", 84));
            var mismatched = LogPrivacyInspector.Inspect(
                logPath,
                [sensitiveTitle],
                new ExpectedPromptLogEvidence("zoom:desktop:99", "zoom", 84));

            Assert.True(matching.DetectionLogSchemaValid);
            Assert.True(matching.RawWindowTitleMatchCount > 0);
            Assert.True(matching.AudioPayloadFieldCount > 0);
            Assert.False(mismatched.DetectionLogSchemaValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivacyFailingSourceLogIsNotRetainedInTheArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-redacted-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourceLog = Path.Combine(root, "source.log");
        await File.WriteAllTextAsync(sourceLog, "private title that must not persist");
        var evidence = ValidPassedEvidence() with
        {
            RunId = "privacy-failure-fixture",
            Observation = ValidPassedEvidence().Observation with
            {
                Privacy = new PrivacyEvidence(CheckStatus.Fail, false, 1, 0, 0)
            },
            Outcome = new OutcomeEvidence(RunOutcomeStatus.Failed, "privacy_gate_failed")
        };
        try
        {
            var artifact = await EvidenceArtifactWriter.WriteAsync(
                root,
                evidence,
                sourceLog,
                retainSourceLog: false,
                CancellationToken.None);
            var retainedLog = await File.ReadAllTextAsync(Path.Combine(artifact, "host.log"));

            Assert.DoesNotContain("private title", retainedLog, StringComparison.Ordinal);
            Assert.Contains("privacy gate did not pass", retainedLog, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileCreationAuditSeesAnArtifactThatWasImmediatelyDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"istranscribe-file-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var audit = new FileCreationAudit([root]);
            var transientPath = Path.Combine(root, "transient.raw");
            await File.WriteAllTextAsync(transientPath, "fixture");
            File.Delete(transientPath);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (audit.CreatedPathCount == 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(audit.CreatedPathCount > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateDecisionLine(string candidateId, string profileId, int score) =>
        JsonSerializer.Serialize(new
        {
            timestamp_utc = DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
            level = "Info",
            event_code = "DETECTION_DECISION",
            session_id = (string?)null,
            job_name = (string?)null,
            message = "Meeting detection state changed.",
            metadata = new
            {
                candidate_count = 1,
                candidates = new[]
                {
                    new
                    {
                        candidate_id = candidateId,
                        profile_id = profileId,
                        score,
                        band = "ask",
                        suppressed = false,
                        reason_codes = new[] { "band:ask" },
                        evidence = new[]
                        {
                            new
                            {
                                kind = "MeetingControls",
                                rule_id = "zoom.call-controls",
                                provider_id = "fixture",
                                strength = 1d
                            }
                        },
                        contributions = new[]
                        {
                            new
                            {
                                kind = "MeetingControls",
                                rule_id = "zoom.call-controls",
                                strength = 1d,
                                weight = 40,
                                score_delta = 40
                            }
                        }
                    }
                },
                candidates_truncated = false,
                prompt_visible = true,
                prompt_candidate_id = candidateId,
                shadow_would_prompt_candidate_id = (string?)null,
                degraded_signals = Array.Empty<string>()
            }
        });

    private static LiveEvidence ValidPassedEvidence()
    {
        var hash = new string('a', 64);
        return new LiveEvidence(
            LiveEvidenceValidator.SchemaVersion,
            "20260711T120000Z-zoom-desktop-fixture",
            "zoom.desktop.positive",
            DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
            new TargetEvidence("10.0.26200", "Microsoft Windows", "x64"),
            new BuildEvidence(
                "1.0.0",
                hash,
                hash,
                new Dictionary<string, string>
                {
                    ["IsTranscribe.Core.dll"] = hash,
                    ["IsTranscribe.Host.dll"] = hash,
                    ["IsTranscribe.Persistence.dll"] = hash,
                    ["IsTranscribe.Platform.Windows.dll"] = hash,
                    ["IsTranscribe.DetectionAcceptance.dll"] = hash,
                    ["feat-011-live-v1.schema.json"] = hash
                },
                new SourceRevisionEvidence(
                    ObservationValueStatus.Unavailable,
                    null,
                    null,
                    "repository_has_no_vcs_metadata")),
            new SubjectEvidence(
                "zoom",
                "desktop",
                new ClientEvidence(
                    42,
                    "Zoom.exe",
                    new SourceRevisionEvidence(
                        ObservationValueStatus.Observed,
                        "7.0.5",
                        "file_version_info",
                        null),
                    hash),
                new SourceRevisionEvidence(
                    ObservationValueStatus.NotExposed,
                    null,
                    null,
                    "service_version_is_client_version")),
            new ObservationEvidence(
                new ActivityReadyEvidence(
                    CheckStatus.Pass,
                    "operator_confirmation",
                    DateTimeOffset.Parse("2026-07-11T11:59:55Z")),
                new AskEvidence(CheckStatus.Pass, 6_124, 15_000),
                new PromptEvidence(
                    CheckStatus.Pass,
                    true,
                    "zoom:desktop:42",
                    "zoom",
                    "awaiting_confirmation",
                    84),
                new PreconfirmationAudioEvidence(
                    CheckStatus.Pass,
                    0,
                    0,
                    0,
                    ["runtime_temp", "configured_recordings_root"]),
                new PrivacyEvidence(CheckStatus.Pass, true, 0, 0, 0)),
            new OutcomeEvidence(RunOutcomeStatus.Passed, null));
    }
}
