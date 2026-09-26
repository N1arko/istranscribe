using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the destructive boundary of the packaged lifecycle acceptance harness.
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
/// </summary>
public sealed class WindowsReleaseLifecycleHarnessContractTests
{
    [Fact]
    public void HarnessExercisesTheStableInstallUpgradeAndDowngradeContour()
    {
        var script = ReadHarness();

        Assert.Contains("[string]$BaseMsixPath", script, StringComparison.Ordinal);
        Assert.Contains("[string]$UpgradeMsixPath", script, StringComparison.Ordinal);
        Assert.Contains("[string]$EvidencePath", script, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage -Path $BaseMsixFullPath", script, StringComparison.Ordinal);
        Assert.Contains("-Path $UpgradeMsixFullPath", script, StringComparison.Ordinal);
        Assert.Contains("-ForceApplicationShutdown", script, StringComparison.Ordinal);
        Assert.Contains("0x80073CFB", script, StringComparison.Ordinal);
        Assert.Contains("0x80073D06", script, StringComparison.Ordinal);
        Assert.Contains("Get-StartApps", script, StringComparison.Ordinal);
        Assert.Contains("shell:AppsFolder\\$ApplicationUserModelId", script, StringComparison.Ordinal);
        Assert.Contains("repeatedLaunchReusedProcess", script, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage -Package", script, StringComparison.Ordinal);
        Assert.Contains("recoveredOwnedRegistration", script, StringComparison.Ordinal);
        Assert.Contains("ownedProcessCleanupFailed", script, StringComparison.Ordinal);
        Assert.Contains("ownedProcessesRemaining", script, StringComparison.Ordinal);
        Assert.Contains("ownedProcessesRunning", script, StringComparison.Ordinal);
        Assert.Contains("exact harness-owned application processes remain", script, StringComparison.Ordinal);
        Assert.Contains("if ($PackageMutationStarted -and -not $LeaveInstalled)", script, StringComparison.Ordinal);
        Assert.Contains("$candidate = Get-ExactPackageRegistration -ExpectedName $PackageName", script, StringComparison.Ordinal);
        Assert.Contains("$candidate.Publisher -cne $BaseMetadata.Publisher", script, StringComparison.Ordinal);
        Assert.Contains("does not match an exact harness-owned package", script, StringComparison.Ordinal);
        Assert.Contains("reinstall_and_recover_data", script, StringComparison.Ordinal);
        Assert.Contains("reinstallLaunchVerified = $true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-ForceUpdateFromAnyVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-AppxPackage -LiteralPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FullLifecycleRequiresADisposableCleanUserProfile()
    {
        var script = ReadHarness();

        Assert.Contains("enforce_disposable_clean_profile", script, StringComparison.Ordinal);
        Assert.Contains("canonicalDataRootAbsent", script, StringComparison.Ordinal);
        Assert.Contains("legacyRunValueAbsent", script, StringComparison.Ordinal);
        Assert.Contains("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", script, StringComparison.Ordinal);
        Assert.Contains("\"IsTranscribe.App\"", script, StringComparison.Ordinal);
        Assert.Contains("\"isTranscribe\"", script, StringComparison.Ordinal);
        Assert.Contains("cleanDisposableProfileEligible", script, StringComparison.Ordinal);
        Assert.Contains("if ($PreflightOnly)", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("enforce_disposable_clean_profile", StringComparison.Ordinal) >
            script.IndexOf("if ($PreflightOnly)", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitLocalSmokeCanExerciseAnExistingDataRootWithoutRelocatingOrDeletingIt()
    {
        var script = ReadHarness();

        Assert.Contains("[switch]$AllowExistingDataRootForLocalSmoke", script, StringComparison.Ordinal);
        Assert.Contains("local_existing_profile_lifecycle", script, StringComparison.Ordinal);
        Assert.Contains("localExistingProfileEligible", script, StringComparison.Ordinal);
        Assert.Contains("selectedProfileEligible", script, StringComparison.Ordinal);
        Assert.Contains("-not $Evidence.preflight.canonicalDataRootAbsent", script, StringComparison.Ordinal);
        Assert.Contains("$Evidence.preflight.packageRegistrationAbsent -and", script, StringComparison.Ordinal);
        Assert.Contains("$Evidence.preflight.legacyRunValueAbsent -and", script, StringComparison.Ordinal);
        Assert.Contains("$Evidence.preflight.legacyProcessAbsent -and", script, StringComparison.Ordinal);
        Assert.Contains("$Evidence.preflight.currentProcessAbsent", script, StringComparison.Ordinal);
        Assert.Contains("explicit existing-data local-smoke mode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory]::Move($CanonicalDataRoot", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $CanonicalDataRoot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustPreflightRecordsHashesAndBlocksOnlyTheExactUntrustedRootResult()
    {
        var script = ReadHarness();

        Assert.Contains("[switch]$PreflightOnly", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath", script, StringComparison.Ordinal);
        Assert.Contains("[Security.Cryptography.SHA256]::Create()", script, StringComparison.Ordinal);
        Assert.Contains("signerSubject", script, StringComparison.Ordinal);
        Assert.Contains("signerThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("[IsTranscribe.Acceptance.WinTrustVerifier]::Verify", script, StringComparison.Ordinal);
        Assert.Contains("\"0x800B0109\"", script, StringComparison.Ordinal);
        Assert.Contains("package_signature_hash_mismatch", script, StringComparison.Ordinal);
        Assert.Contains("package_not_signed", script, StringComparison.Ordinal);
        Assert.Contains("certificate_not_trusted_for_appx", script, StringComparison.Ordinal);
        Assert.Contains("securitySettingsModified = $false", script, StringComparison.Ordinal);
        Assert.Contains("appPackageAndCanonicalDataUntouchedByPreflight", script, StringComparison.Ordinal);
        Assert.Contains("$Evidence.status = if (-not $PackageMutationStarted", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$FailureReason -in $blockedReasons", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("package_signature_hash_mismatch", StringComparison.Ordinal) <
            script.IndexOf("$Observation.winVerifyTrust, \"0x800B0109\"", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("signature_publisher_mismatch", StringComparison.Ordinal) <
            script.IndexOf("$Observation.winVerifyTrust, \"0x800B0109\"", StringComparison.Ordinal));

        foreach (var forbiddenCommand in new[]
        {
            "Import-Certificate",
            "Import-PfxCertificate",
            "New-SelfSignedCertificate",
            "Set-ExecutionPolicy",
            "Cert:\\LocalMachine",
            "TrustedPeople",
            "-AllowUnsigned"
        })
        {
            Assert.DoesNotContain(forbiddenCommand, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EveryDeploymentRehashesALockedPackageImmediatelyBeforeAddAppxPackage()
    {
        var script = ReadHarness();

        Assert.Contains("Open-VerifiedPackageLease", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::Read", script, StringComparison.Ordinal);
        Assert.Contains("matchesInitialSha256", script, StringComparison.Ordinal);
        Assert.Contains("matchesInitialSigner", script, StringComparison.Ordinal);
        Assert.Contains("preInstallIntegrity = $PreInstallIntegrity", script, StringComparison.Ordinal);
        Assert.Contains("-Operation \"install_base\"", script, StringComparison.Ordinal);
        Assert.Contains("-Operation \"upgrade_in_place\"", script, StringComparison.Ordinal);
        Assert.Contains("-Operation \"reject_downgrade\"", script, StringComparison.Ordinal);
        Assert.Contains("-Operation \"reinstall_and_recover_data\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceIsOutsideCanonicalDataAndPromotedAtomically()
    {
        var script = ReadHarness();
        var rejection = script.IndexOf(
            "Test-PathInsideOrEqual -Path $EvidenceFullPath -ParentPath $CanonicalDataRoot",
            StringComparison.Ordinal);
        var directoryCreation = script.IndexOf(
            "[IO.Directory]::CreateDirectory($EvidenceDirectory)",
            StringComparison.Ordinal);

        Assert.True(rejection >= 0);
        Assert.True(directoryCreation > rejection);
        Assert.Contains("Write-EvidenceAtomically", script, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Replace($temporaryPath, $Destination", script, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Move($temporaryPath, $Destination)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupAndProcessControlAreBoundToExactOwnedPackageState()
    {
        var script = ReadHarness();

        Assert.Contains("preexisting_package_registration", script, StringComparison.Ordinal);
        Assert.Contains("Get-PackageRegistrationByFullName", script, StringComparison.Ordinal);
        Assert.Contains("-Package $ownedPackageFullName", script, StringComparison.Ordinal);
        Assert.Contains("$script:OwnsPackageRegistration = $BaseRegistration.PackageFullName", script, StringComparison.Ordinal);
        Assert.Contains("$script:OwnsPackageRegistration = $UpgradeRegistration.PackageFullName", script, StringComparison.Ordinal);
        Assert.Contains("Test-ChildPath -Path $process.Path -ParentPath $Owned.Value.InstallLocation", script, StringComparison.Ordinal);
        Assert.Contains("-InstallLocation $BaseRegistration.InstallLocation", script, StringComparison.Ordinal);
        Assert.Contains("-InstallLocation $UpgradeRegistration.InstallLocation", script, StringComparison.Ordinal);
        Assert.Contains(
            "Test-ChildPath -Path $SentinelPath -ParentPath $CanonicalDataRoot",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $SentinelPath -Force", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $CanonicalDataRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -Recurse", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -InputObject $process", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Id", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleInstanceSamplingIgnoresOnlyAProcessThatExitedDuringPathInspection()
    {
        var script = ReadHarness();

        Assert.Contains("$process.Refresh()", script, StringComparison.Ordinal);
        Assert.Contains("$processExitedDuringInspection = $process.HasExited", script, StringComparison.Ordinal);
        Assert.Contains("[Diagnostics.Process]::GetProcessById($process.Id)", script, StringComparison.Ordinal);
        Assert.Contains("catch [ArgumentException]", script, StringComparison.Ordinal);
        Assert.Contains("if ($processExitedDuringInspection)", script, StringComparison.Ordinal);
        Assert.Contains("$script:PackageProcessPathInspectionUnavailable = $true", script, StringComparison.Ordinal);
        Assert.Contains("-not $script:PackageProcessPathInspectionUnavailable", script, StringComparison.Ordinal);
        Assert.Contains("remained unavailable through the bounded inspection window", script, StringComparison.Ordinal);
        Assert.Contains("$script:FailureReason = \"package_process_path_unavailable\"", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("if ($processExitedDuringInspection)", StringComparison.Ordinal) <
            script.IndexOf("$script:FailureReason = \"package_process_path_unavailable\"", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceDeclaresTheHarnessCoverageBoundary()
    {
        var script = ReadHarness();

        Assert.Contains("PowerShell Add-AppxPackage with ForceApplicationShutdown", script, StringComparison.Ordinal);
        Assert.Contains("appInstallerUiRunningUpdateFlow", script, StringComparison.Ordinal);
        Assert.Contains("customRecordingsFolder", script, StringComparison.Ordinal);
        Assert.Contains("audioCapture", script, StringComparison.Ordinal);
        Assert.Contains("processLoopback", script, StringComparison.Ordinal);
        Assert.Contains("autostart", script, StringComparison.Ordinal);
        Assert.Contains("low-level canonical-root preservation only", script, StringComparison.Ordinal);
    }

    private static string ReadHarness() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "packaging",
        "windows",
        "Test-WindowsReleaseLifecycle.ps1"));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root from the test output path.");
    }
}
