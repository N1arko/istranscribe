using IsTranscribe.DetectionAcceptance;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#validation-levels
/// @spec spec://modules/app/FEAT-011.A-consent-aware-windows-validation#acceptance
/// </summary>
public sealed class CompatibilityEvidenceIndexTests
{
    [Fact]
    public void ContractVerifierCoversEveryRequiredScenarioAndUsesZenForBrowserSurfaces()
    {
        var result = ContractCompatibilityVerifier.Verify();

        Assert.True(result.AllScenariosVerified);
        Assert.True(result.GenericBrowserFallbackVerified);
        Assert.True(result.BrowserPlaybackNegativeVerified);
        Assert.Equal(LiveEvidenceMatrixBuilder.RequiredScenarioIds, result.Scenarios.Select(static row => row.ScenarioId));
        Assert.All(result.Scenarios, static row => Assert.True(row.Verified, row.FailureReason));
        Assert.All(
            result.Scenarios.Where(static row => row.Surface == "browser"),
            static row => Assert.Equal("zen.exe", row.ProcessName));
    }

    [Fact]
    public void LivePassIsTheHighestValidationLevel()
    {
        var level = CompatibilityEvidenceIndexBuilder.ResolveValidationLevel(
            MatrixRowStatus.Passed,
            Client("zen.exe"));

        Assert.Equal(ValidationEvidenceLevel.LiveVerified, level);
    }

    [Fact]
    public void SafeClientPreflightProducesEnvironmentObservedLevel()
    {
        var level = CompatibilityEvidenceIndexBuilder.ResolveValidationLevel(
            MatrixRowStatus.NotRun,
            Client("zen.exe"));

        Assert.Equal(ValidationEvidenceLevel.EnvironmentObserved, level);
    }

    [Fact]
    public void MissingClientRetainsContractVerifiedLevel()
    {
        var level = CompatibilityEvidenceIndexBuilder.ResolveValidationLevel(
            MatrixRowStatus.Blocked,
            client: null);

        Assert.Equal(ValidationEvidenceLevel.ContractVerified, level);
    }

    [Fact]
    public void VerifiedCompatibilitySucceedsWithIncompleteDiagnosticLiveMatrix()
    {
        var exitCode = CompatibilityAggregateExitPolicy.GetExitCode(
            MatrixOutcomeStatus.Incomplete,
            CompatibilityIndexStatus.Verified);

        Assert.Equal(0, exitCode);
    }

    private static ClientEvidence Client(string processName) => new(
        ProcessId: 42,
        processName,
        new SourceRevisionEvidence(
            ObservationValueStatus.Observed,
            "1.0",
            "file_version_info",
            Reason: null),
        ExecutableSha256: new string('a', 64));
}
