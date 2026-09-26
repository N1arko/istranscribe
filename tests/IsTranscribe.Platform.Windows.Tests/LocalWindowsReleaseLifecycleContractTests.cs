using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the lightweight existing-profile Windows package smoke.
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#update-uninstall
/// </summary>
public sealed class LocalWindowsReleaseLifecycleContractTests
{
    [Fact]
    public void LocalSmokeKeepsExistingDataInPlaceAndUsesAContentOnlyEmergencyCopy()
    {
        var script = ReadWrapper();

        Assert.Contains("ConfirmTemporaryMachineTrustAndLocalPackageMutation", script, StringComparison.Ordinal);
        Assert.Contains("AllowExistingDataRootForLocalSmoke", script, StringComparison.Ordinal);
        Assert.Contains("existingDataRootStayedInPlace = $true", script, StringComparison.Ordinal);
        Assert.Contains("Copy-DirectoryContent", script, StringComparison.Ordinal);
        Assert.Contains("Get-ContentDigest", script, StringComparison.Ordinal);
        Assert.Contains("baselineFilesPreserved", script, StringComparison.Ordinal);
        Assert.Contains("rootAclPreserved", script, StringComparison.Ordinal);
        Assert.Contains("documentsMetadataUnchanged", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory]::Move($canonicalRoot", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $canonicalRoot", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalSmokeUsesANarrowElevatedMachineTrustHelperAndPreservesTheBuildPrivateKey()
    {
        var script = ReadWrapper();
        var helper = ReadTrustHelper();

        Assert.Contains("StoreLocation]::LocalMachine", script, StringComparison.Ordinal);
        Assert.Contains("TrustedPeople", script, StringComparison.Ordinal);
        Assert.Contains("Set-LocalDevelopmentCertificateTrust.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-ElevatedTrustAction", script, StringComparison.Ordinal);
        Assert.Contains("-Verb RunAs", script, StringComparison.Ordinal);
        Assert.Contains("OwnershipToken", script, StringComparison.Ordinal);
        Assert.Contains("trust-add.json", script, StringComparison.Ordinal);
        Assert.Contains("trust-remove-{0}.json", script, StringComparison.Ordinal);
        Assert.Contains("machineTrustRemoved", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\CurrentUser\\My\\$expectedThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("buildCertificatePreserved", script, StringComparison.Ordinal);
        Assert.Contains("#Requires -RunAsAdministrator", helper, StringComparison.Ordinal);
        Assert.Contains("StoreLocation]::LocalMachine", helper, StringComparison.Ordinal);
        Assert.Contains("FixedTimeEquals", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-CurrentUserTrust", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-CurrentUserTrust", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-CurrentUserTrustedCertificates", script, StringComparison.Ordinal);
        Assert.DoesNotContain("currentUserTrustRemoved", script, StringComparison.Ordinal);
        Assert.DoesNotContain("TrustedRoot", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TrustedRoot", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Import-PfxCertificate", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Import-PfxCertificate", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath \"Cert:", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath \"Cert:", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalSmokeRunsOnlyPinnedLockedCopiesAndHasPersistentRecovery()
    {
        var script = ReadWrapper();
        var root = FindRepositoryRoot();
        var helperHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(
            root, "packaging", "windows", "Set-LocalDevelopmentCertificateTrust.ps1"))));
        var harnessHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(
            root, "packaging", "windows", "Test-WindowsReleaseLifecycle.ps1"))));

        Assert.Contains("4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F", script, StringComparison.Ordinal);
        Assert.Contains("0B370AE48B9099FDFC0EDA22C8CF523D547C8764AAA4070D0B14536242215185", script, StringComparison.Ordinal);
        Assert.Equal("0B370AE48B9099FDFC0EDA22C8CF523D547C8764AAA4070D0B14536242215185", helperHash);
        Assert.Equal("4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F", harnessHash);
        Assert.Contains("Copy-PinnedFile", script, StringComparison.Ordinal);
        Assert.Contains("Open-PinnedReadLease", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::Read", script, StringComparison.Ordinal);
        Assert.Contains("infra-005-local-machine-existing-profile-recovery-v1", script, StringComparison.Ordinal);
        Assert.Contains("infra-005-local-machine-trust-v1", script, StringComparison.Ordinal);
        Assert.Contains("machine_trust_add_started", script, StringComparison.Ordinal);
        Assert.Contains("RecoverOnly", script, StringComparison.Ordinal);
        Assert.Contains("Remove-ExactDevelopmentPackage", script, StringComparison.Ordinal);
        Assert.Contains("Refusing to remove a package outside", script, StringComparison.Ordinal);
        Assert.Contains("EvidencePath must be a file inside the owned", script, StringComparison.Ordinal);
        Assert.Contains("Evidence and user-data contours must remain disjoint", script, StringComparison.Ordinal);
        Assert.Contains("host_state_recovered_backup_retained", script, StringComparison.Ordinal);
        Assert.Contains("contentBackupRetained", script, StringComparison.Ordinal);
        Assert.Contains("backupContentDigest", script, StringComparison.Ordinal);
        Assert.Contains("backupFileCount", script, StringComparison.Ordinal);
        Assert.Contains("settingsReadable", script, StringComparison.Ordinal);
        Assert.Contains("secretsUnchanged", script, StringComparison.Ordinal);
        Assert.Contains("sqliteIntegrityPassed", script, StringComparison.Ordinal);
        Assert.Contains("sqliteBaselineRowsPreserved", script, StringComparison.Ordinal);
        Assert.Contains("Pooling=False", script, StringComparison.Ordinal);
        Assert.Contains("sqlite-probe-cache-", script, StringComparison.Ordinal);
        Assert.Contains("differs from the pinned package", script, StringComparison.Ordinal);
        Assert.Contains("exact file allowlist", script, StringComparison.Ordinal);
        Assert.Contains("$preparationPackageLease = Open-PinnedReadLease", script, StringComparison.Ordinal);
        Assert.Contains("$cacheLeases.Add((Open-PinnedReadLease", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedLifecycleSentinels", script, StringComparison.Ordinal);
        Assert.Contains("$preparationCommitted = $true", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedTransaction -Path $transactionRoot -Parent $transactionParent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $dataBackup", script, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(script, "Remove-OwnedTransaction -Path $transactionRoot -Parent $transactionParent"));
        Assert.Equal(1, CountOccurrences(script, "Invoke-ElevatedTrustAction -Action Add"));
        Assert.Equal(2, CountOccurrences(script, "Invoke-ElevatedTrustAction -Action Remove"));
        Assert.Equal(2, CountOccurrences(script, "-OwnershipToken $runId"));
        Assert.Contains("-OwnershipToken ([string]$stale.ownershipToken)", script, StringComparison.Ordinal);
        Assert.Contains("Open-ResultIdentityLease", script, StringComparison.Ordinal);
        Assert.Contains("trust-add.intent.json", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", script, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(script, "$resultLease = Open-ResultIdentityLease -Path $ResultPath -RequireNew"));
        Assert.Contains("[IO.FileAccess]::Read, [IO.FileShare]::Read", script, StringComparison.Ordinal);
        var journalPhase = script.IndexOf("$journal.phase = \"machine_trust_add_started\"", StringComparison.Ordinal);
        var journalWrite = script.IndexOf("Write-JsonAtomically -Value $journal", journalPhase, StringComparison.Ordinal);
        var trustAdd = script.IndexOf("Invoke-ElevatedTrustAction -Action Add", StringComparison.Ordinal);
        Assert.True(journalPhase >= 0 && journalWrite > journalPhase && trustAdd > journalWrite);
    }

    [Fact]
    public void ElevatedTrustHelperIsReceiptGatedAndMutationIsJournaledFirst()
    {
        var helper = ReadTrustHelper();

        Assert.Contains("[ValidateSet(\"Add\", \"Remove\")]", helper, StringComparison.Ordinal);
        Assert.Contains("25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC", helper, StringComparison.Ordinal);
        Assert.Contains("66075815637CA0D5059B64D57C5E22100B142024", helper, StringComparison.Ordinal);
        Assert.Contains("CN=isTranscribe Development", helper, StringComparison.Ordinal);
        Assert.Contains("infra-005-local-machine-trust-v1", helper, StringComparison.Ordinal);
        Assert.Contains("trust-add.json", helper, StringComparison.Ordinal);
        Assert.Contains("trust-add.intent.json", helper, StringComparison.Ordinal);
        Assert.Contains("trust-remove-", helper, StringComparison.Ordinal);
        Assert.Contains("$receipt.ownershipToken -cne $OwnershipToken", helper, StringComparison.Ordinal);
        Assert.Contains("must stay inside the exact transaction directory", helper, StringComparison.Ordinal);
        Assert.Contains("must not traverse reparse points", helper, StringComparison.Ordinal);
        Assert.Contains("exact action-specific transaction filename contract", helper, StringComparison.Ordinal);
        Assert.Contains("must be precreated by the medium-token orchestrator", helper, StringComparison.Ordinal);
        Assert.Contains("Write-JsonDurablyInPlace", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("File]::Replace", helper, StringComparison.Ordinal);
        Assert.True(
            helper.IndexOf("$result.phase = \"add_started\"", StringComparison.Ordinal) <
            helper.IndexOf("$store.Add($certificate)", StringComparison.Ordinal));
        Assert.True(
            helper.IndexOf("Write-JsonDurablyInPlace -Value $result -Path $intentPath", StringComparison.Ordinal) <
            helper.IndexOf("$store.Add($certificate)", StringComparison.Ordinal));
        Assert.True(
            helper.IndexOf("$receipt.ownershipToken -cne $OwnershipToken", StringComparison.Ordinal) <
            helper.IndexOf("$store.Remove($matches[0])", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalSmokeScriptsParseInPowerShell7()
    {
        var paths = new[]
        {
            Path.Combine(FindRepositoryRoot(), "packaging", "windows", "Invoke-LocalWindowsReleaseLifecycle.ps1"),
            Path.Combine(FindRepositoryRoot(), "packaging", "windows", "Set-LocalDevelopmentCertificateTrust.ps1"),
            Path.Combine(FindRepositoryRoot(), "packaging", "windows", "Test-WindowsReleaseLifecycle.ps1")
        };
        var quotedPaths = string.Join(",", paths.Select(path => "'" + path.Replace("'", "''") + "'"));
        var command = "$allErrors=@(); foreach($path in @(" + quotedPaths + ")) {" +
            "$errors=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$null,[ref]$errors);" +
            "$allErrors += $errors }; if($allErrors.Count -gt 0) { $allErrors | Out-String | Write-Error; exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("pwsh", "-NoLogo -NoProfile -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000), "PowerShell parser timed out.");
        Assert.True(process.ExitCode == 0, output);
    }

    private static string ReadWrapper() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "packaging", "windows", "Invoke-LocalWindowsReleaseLifecycle.ps1"));

    private static string ReadTrustHelper() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "packaging", "windows", "Set-LocalDevelopmentCertificateTrust.ps1"));

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

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
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
