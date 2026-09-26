using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the unsigned, Store-identity-bound submission contour.
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#decisions.identity
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#decisions.signing
/// </summary>
public sealed class WindowsStoreSubmissionToolingContractTests
{
    private static readonly string[] IdentityFields =
    [
        "applicationId",
        "configured",
        "identityName",
        "packageFamilyName",
        "publisher",
        "publisherDisplayName",
        "reservedProductName",
        "schemaVersion"
    ];

    [Fact]
    public void CheckedInStoreIdentityTemplateIsExplicitlyUnconfiguredAndFailClosed()
    {
        using var document = JsonDocument.Parse(Read("store-identity.template.json"));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            IdentityFields,
            root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("infra-005-store-identity-v1", RequiredString(root, "schemaVersion"));
        Assert.False(root.GetProperty("configured").GetBoolean());

        Assert.Equal("App", RequiredString(root, "applicationId"));
        foreach (var field in IdentityFields.Where(static field =>
                     field is not "schemaVersion" and not "configured" and not "applicationId"))
        {
            var value = RequiredString(root, field);
            Assert.StartsWith("<", value, StringComparison.Ordinal);
            Assert.EndsWith(">", value, StringComparison.Ordinal);
            Assert.DoesNotContain("isTranscribe Development", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BuildRequiresAConfiguredResolvedStoreIdentity()
    {
        var script = Read("Build-WindowsRelease.ps1");

        Assert.Contains("[ValidateSet(\"Development\", \"Production\", \"Store\")]", script, StringComparison.Ordinal);
        Assert.Contains("[string]$StoreIdentityPath", script, StringComparison.Ordinal);
        Assert.Contains("infra-005-store-identity-v1", script, StringComparison.Ordinal);
        Assert.Contains("configured", script, StringComparison.Ordinal);
        foreach (var field in IdentityFields.Where(static field => field is not "schemaVersion" and not "configured"))
        {
            Assert.Contains(field, script, StringComparison.Ordinal);
        }

        Assert.Contains("unresolved", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("placeholder", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$SigningMode -eq \"Store\"", script, StringComparison.Ordinal);
        Assert.Contains("$StoreIdentityPath", script, StringComparison.Ordinal);
        Assert.Contains("{{PACKAGE_DISPLAY_NAME}}", script, StringComparison.Ordinal);
        Assert.Contains(
            "{{PACKAGE_DISPLAY_NAME}}",
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "packaging", "windows", "msix", "AppxManifest.template.xml")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoreBuildIsUnsignedCertificateFreeAndIsolatedFromPublicOrDevelopmentOutput()
    {
        var script = Read("Build-WindowsRelease.ps1");

        Assert.Contains("$SigningMode -ne \"Store\"", script, StringComparison.Ordinal);
        Assert.Contains("Get-CodeSigningCertificate", script, StringComparison.Ordinal);
        Assert.Contains("STORE_SUBMISSION_ONLY.txt", script, StringComparison.Ordinal);
        Assert.Contains("store-identity.json", script, StringComparison.Ordinal);
        Assert.Contains("-store-win-x64.msix", script, StringComparison.Ordinal);
        Assert.Contains("StoreSubmissionUnsigned", script, StringComparison.Ordinal);
        Assert.Contains("storeSigningRequired", script, StringComparison.Ordinal);
        Assert.Contains("submissionEligible", script, StringComparison.Ordinal);
        Assert.Contains("distributionChannel", script, StringComparison.Ordinal);
        Assert.Contains("\"store\"", script, StringComparison.Ordinal);

        var storeBranch = script.IndexOf("if ($SigningMode -eq \"Store\")", StringComparison.Ordinal);
        var certificateCall = script.IndexOf("Get-CodeSigningCertificate", StringComparison.Ordinal);
        var packageSigning = script.IndexOf("Signing the MSIX package", StringComparison.Ordinal);
        Assert.True(storeBranch >= 0 && certificateCall >= 0 && packageSigning >= 0);
        Assert.Contains("AppxSignature.p7x", script, StringComparison.Ordinal);
        Assert.Contains("Get-PackagePublisherId", script, StringComparison.Ordinal);
        Assert.Contains("0123456789abcdefghjkmnpqrstvwxyz", script, StringComparison.Ordinal);
        Assert.Contains("packageFamilyName does not match the configured identityName and publisher", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreSubmissionVerifierRequiresOnlyTheUnsignedStoreHandoffFiles()
    {
        var verifier = Read("Test-WindowsStoreSubmissionArtifact.ps1");

        Assert.Contains("STORE_SUBMISSION_ONLY.txt", verifier, StringComparison.Ordinal);
        Assert.Contains("store-identity.json", verifier, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", verifier, StringComparison.Ordinal);
        Assert.Contains("dependency-license-inventory.json", verifier, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", verifier, StringComparison.Ordinal);
        Assert.Contains("-store-win-x64.msix", verifier, StringComparison.Ordinal);
        Assert.Contains("StoreSubmissionUnsigned", verifier, StringComparison.Ordinal);
        Assert.Contains("storeSigningRequired", verifier, StringComparison.Ordinal);
        Assert.Contains("submissionEligible", verifier, StringComparison.Ordinal);
        Assert.Contains("exact", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowlist", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reparse", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AppxSignature.p7x", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-PackagePublisherId", verifier, StringComparison.Ordinal);
        Assert.Contains("capability exact allowlist requires two declarations", verifier, StringComparison.Ordinal);
        Assert.Contains("extension exact allowlist requires one application extension", verifier, StringComparison.Ordinal);
        Assert.Contains("permits no package-level Extensions", verifier, StringComparison.Ordinal);
        Assert.Contains("MPEG-1 Layer III", verifier, StringComparison.Ordinal);
        Assert.Contains("bundledCodecBinaryCount", verifier, StringComparison.Ordinal);
        Assert.Contains("ffmpeg", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("libmp3lame", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WindowsMediaFoundationMp3Encoder", verifier, StringComparison.Ordinal);
        Assert.Contains("WindowsMediaFoundationAacEncoder", verifier, StringComparison.Ordinal);
        Assert.Contains("forbidden AAC composition symbol", verifier, StringComparison.Ordinal);
        Assert.Contains("infra-005-dependencies-v2", verifier, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", verifier, StringComparison.Ordinal);
        Assert.Contains("third-party-notice-policy.json", verifier, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.SDK.NET.Ref/10.0.26100.84", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("aacLicensingReviewReference", verifier, StringComparison.OrdinalIgnoreCase);

        foreach (var forbiddenPayload in new[]
                 {
                     "DEVELOPMENT_ONLY.txt",
                     "isTranscribe-development-certificate.cer",
                     ".pfx",
                     ".p12",
                     ".pem",
                     ".key",
                     ".zip",
                     ".7z",
                     ".rar"
                 })
        {
            Assert.Contains(forbiddenPayload, verifier, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var forbiddenMutation in new[]
                 {
                     "Add-AppxPackage",
                     "Remove-AppxPackage",
                     "Import-Certificate",
                     "Import-PfxCertificate",
                     "New-SelfSignedCertificate",
                     "Set-ExecutionPolicy",
                     "Start-Process"
                 })
        {
            Assert.DoesNotContain(forbiddenMutation, verifier, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StoreSubmissionScriptsParseInPowerShell7()
    {
        var scripts = new[]
        {
            ToolPath("Build-WindowsRelease.ps1"),
            ToolPath("Test-WindowsStoreSubmissionArtifact.ps1"),
            ToolPath("Test-WindowsStoreSubmissionMetadata.ps1")
        };
        var quoted = string.Join(",", scripts.Select(path => $"'{path.Replace("'", "''")}'"));
        var command = $"$failed=$false; foreach($path in @({quoted})) " +
            "{ $errors=$null; [void][System.Management.Automation.Language.Parser]::ParseFile(" +
            "$path,[ref]$null,[ref]$errors); if($errors.Count -gt 0) { $failed=$true; " +
            "$errors | ForEach-Object { Write-Error $_.Message } } }; if($failed) { exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-EncodedCommand", encoded })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000), "PowerShell parser timed out.");
        Assert.True(process.ExitCode == 0, output);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }

    private static string Read(string name) => File.ReadAllText(ToolPath(name));

    private static string ToolPath(string name) => Path.Combine(
        FindRepositoryRoot(), "packaging", "windows", name);

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
