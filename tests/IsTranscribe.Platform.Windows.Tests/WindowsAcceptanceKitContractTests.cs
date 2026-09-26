using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the standalone development-only clean-machine installer handoff.
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing
/// </summary>
public sealed class WindowsAcceptanceKitContractTests
{
    private const string KitName =
        "TEST-ONLY-isTranscribe-installer-acceptance-2.0.0-to-2.0.1-win-x64";
    private const string BaseHash = "E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B";
    private const string UpgradeHash = "FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3";
    private const string CertificateHash = "25D68C4BFFFDE505F871F7EA2C0F88003A2833F695A474470A0A18004232A9CC";
    private const string HarnessHash = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F";

    [Fact]
    public void BuilderProducesOnlyADevelopmentAcceptanceArtifactOutsideRelease()
    {
        var builder = Read("packaging", "windows", "New-WindowsAcceptanceKit.ps1");

        Assert.Contains("artifacts\\acceptance\\INFRA-005\\windows-x64", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts\\release\\windows-x64\\public", builder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TEST-ONLY-isTranscribe-installer-acceptance", builder, StringComparison.Ordinal);
        Assert.Contains("Test-WindowsReleaseArtifact.ps1", builder, StringComparison.Ordinal);
        Assert.Contains("signing.mode -cne \"development\"", builder, StringComparison.Ordinal);
        Assert.Contains("signing.publicEligible", builder, StringComparison.Ordinal);
        Assert.Contains("containsPrivateKey = $false", builder, StringComparison.Ordinal);
        Assert.Contains("externalDigestFile", builder, StringComparison.Ordinal);
        Assert.Contains("ZipFile]::OpenRead", builder, StringComparison.Ordinal);
        Assert.Contains("Remove-SafeDirectory", builder, StringComparison.Ordinal);
        Assert.Contains(BaseHash, builder, StringComparison.Ordinal);
        Assert.Contains(UpgradeHash, builder, StringComparison.Ordinal);
        Assert.Contains(CertificateHash, builder, StringComparison.Ordinal);
        Assert.Contains(HarnessHash, builder, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneVerifierUsesAnExactLedgerAndHardenedMsixInspection()
    {
        var verifier = Read("packaging", "windows", "portable-kit", "Test-AcceptanceKit.ps1");

        Assert.Contains("Assert-ExactPathSet", verifier, StringComparison.Ordinal);
        Assert.Contains("Assert-SafeRelativePath", verifier, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointInPath", verifier, StringComparison.Ordinal);
        Assert.Contains("DtdProcessing]::Prohibit", verifier, StringComparison.Ordinal);
        Assert.Contains("XmlResolver = $null", verifier, StringComparison.Ordinal);
        Assert.Contains("DevelopmentUntrustedRoot", verifier, StringComparison.Ordinal);
        Assert.Contains("hasCodeSigning", verifier, StringComparison.Ordinal);
        Assert.Contains("hasDigitalSignature", verifier, StringComparison.Ordinal);
        Assert.Contains("CertificateAuthority", verifier, StringComparison.Ordinal);
        Assert.Contains(BaseHash, verifier, StringComparison.Ordinal);
        Assert.Contains(UpgradeHash, verifier, StringComparison.Ordinal);
        Assert.Contains(CertificateHash, verifier, StringComparison.Ordinal);
        Assert.Contains(HarnessHash, verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanVmRuntimeRequiresAStandardUserAndKeepsEvidenceOutsideTheKit()
    {
        var wrapper = Read("packaging", "windows", "portable-kit", "Invoke-AcceptanceKit.ps1");

        Assert.Contains("S-1-5-32-544", wrapper, StringComparison.Ordinal);
        Assert.Contains("S-1-16-", wrapper, StringComparison.Ordinal);
        Assert.Contains("tokenElevated", wrapper, StringComparison.Ordinal);
        Assert.Contains("not a member of local Administrators", wrapper, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\TrustedPeople", wrapper, StringComparison.Ordinal);
        Assert.Contains("HasPrivateKey", wrapper, StringComparison.Ordinal);
        Assert.Contains("FileShare]::Read", wrapper, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", wrapper, StringComparison.Ordinal);
        Assert.Contains("validated-staging", wrapper, StringComparison.Ordinal);
        Assert.Contains(".raw.json", wrapper, StringComparison.Ordinal);
        Assert.Contains(".passed.json", wrapper, StringComparison.Ordinal);
        Assert.Contains("evidenceIsStagingNotCanonical", wrapper, StringComparison.Ordinal);
        Assert.Contains("Assert-HarnessEvidence", wrapper, StringComparison.Ordinal);
        Assert.Contains("harnessPackageRegistrationRemoved", wrapper, StringComparison.Ordinal);
        Assert.Contains("ownedProcessCleanupFailed", wrapper, StringComparison.Ordinal);
        Assert.Contains("ownedProcessesRemaining", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaveInstalled", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void SandboxGeneratorMapsOnlyTheImmutableKitAndDedicatedEvidenceStaging()
    {
        var generator = Read("packaging", "windows", "portable-kit", "New-WindowsSandboxConfiguration.ps1");

        Assert.Contains("C:\\isTranscribeAcceptance\\kit", generator, StringComparison.Ordinal);
        Assert.Contains("C:\\isTranscribeAcceptance\\evidence", generator, StringComparison.Ordinal);
        Assert.Contains("-ReadOnly $true", generator, StringComparison.Ordinal);
        Assert.Contains("-ReadOnly $false", generator, StringComparison.Ordinal);
        foreach (var element in new[]
        {
            "vGPU", "Networking", "AudioInput", "VideoInput",
            "PrinterRedirection", "ClipboardRedirection"
        })
        {
            Assert.Contains($"-Name \"{element}\" -Value \"Disable\"", generator, StringComparison.Ordinal);
        }
        Assert.Contains("ProtectedClient\" -Value \"Enable", generator, StringComparison.Ordinal);
        Assert.DoesNotContain("LogonCommand", generator, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedArchiveDigestAndArtifactLedgerMatchTheExactArchive()
    {
        var outputRoot = PortableOutputRoot();
        var artifactPath = Path.Combine(outputRoot, "acceptance-kit-artifact.json");
        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var artifact = document.RootElement;
        var archive = artifact.GetProperty("archive");
        var archiveName = archive.GetProperty("file").GetString()!;
        var archivePath = Path.Combine(outputRoot, archiveName);
        var digestName = archive.GetProperty("externalDigestFile").GetString()!;
        var digestPath = Path.Combine(outputRoot, digestName);
        var extractedDirectory = artifact.GetProperty("extractedDirectory").GetString()!;
        var extractedManifest = Path.Combine(outputRoot, extractedDirectory, "acceptance-kit-manifest.json");

        Assert.Equal("infra-005-acceptance-kit-artifact-v1", artifact.GetProperty("schemaVersion").GetString());
        Assert.Equal("prepared", artifact.GetProperty("status").GetString());
        Assert.True(artifact.GetProperty("classification").GetProperty("testOnly").GetBoolean());
        Assert.False(artifact.GetProperty("classification").GetProperty("publicEligible").GetBoolean());
        Assert.Equal(KitName, extractedDirectory);
        Assert.True(File.Exists(archivePath));
        Assert.True(File.Exists(digestPath));
        Assert.True(File.Exists(extractedManifest));

        var actualArchiveHash = Sha256(archivePath);
        Assert.Equal(archive.GetProperty("sizeBytes").GetInt64(), new FileInfo(archivePath).Length);
        Assert.Equal(archive.GetProperty("sha256").GetString(), actualArchiveHash);
        Assert.Equal(
            $"{actualArchiveHash}  {archiveName}\n",
            NormalizeNewlines(File.ReadAllText(digestPath, Encoding.ASCII)));
        Assert.Equal(artifact.GetProperty("kitManifestSha256").GetString(), Sha256(extractedManifest));
    }

    [Fact]
    public void GeneratedZipExactlyMatchesTheExtractedKitAndBothHashLedgers()
    {
        var kitRoot = ExtractedKitRoot();
        var archivePath = Path.Combine(PortableOutputRoot(), $"{KitName}.zip");
        var extractedFiles = Directory.EnumerateFiles(kitRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => RelativePath(kitRoot, path), StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(archivePath);
        var archiveEntries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        var expectedPrefix = $"{KitName}/";
        Assert.All(
            archiveEntries,
            entry => Assert.StartsWith(expectedPrefix, entry.FullName, StringComparison.Ordinal));
        var entriesByRelativePath = archiveEntries.ToDictionary(
            entry => entry.FullName[expectedPrefix.Length..],
            StringComparer.Ordinal);

        Assert.Equal(
            extractedFiles.Keys.Order(StringComparer.Ordinal),
            entriesByRelativePath.Keys.Order(StringComparer.Ordinal));

        var extractedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relativePath, extractedPath) in extractedFiles)
        {
            var entry = entriesByRelativePath[relativePath];
            var extractedFile = new FileInfo(extractedPath);
            var extractedHash = Sha256(extractedPath);
            extractedHashes.Add(relativePath, extractedHash);
            Assert.Equal(extractedFile.Length, entry.Length);
            using var stream = entry.Open();
            Assert.Equal(extractedHash, Sha256(stream));
        }

        using var manifestDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(kitRoot, "acceptance-kit-manifest.json")));
        var manifest = manifestDocument.RootElement;
        var manifestLedger = manifest.GetProperty("files").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("relativePath").GetString()!,
            StringComparer.Ordinal);
        var filesOutsideManifestLedger = new[] { "acceptance-kit-manifest.json", "SHA256SUMS.txt" };
        Assert.Equal(
            extractedFiles.Keys.Except(filesOutsideManifestLedger).Order(StringComparer.Ordinal),
            manifestLedger.Keys.Order(StringComparer.Ordinal));
        foreach (var (relativePath, ledgerEntry) in manifestLedger)
        {
            Assert.Equal(new FileInfo(extractedFiles[relativePath]).Length, ledgerEntry.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(extractedHashes[relativePath], ledgerEntry.GetProperty("sha256").GetString());
        }

        var checksumLedger = File.ReadAllLines(Path.Combine(kitRoot, "SHA256SUMS.txt"), Encoding.ASCII)
            .ToDictionary(line => line[66..], line => line[..64], StringComparer.Ordinal);
        Assert.Equal(
            extractedFiles.Keys.Except(new[] { "SHA256SUMS.txt" }).Order(StringComparer.Ordinal),
            checksumLedger.Keys.Order(StringComparer.Ordinal));
        foreach (var (relativePath, expectedHash) in checksumLedger)
        {
            Assert.Equal(extractedHashes[relativePath], expectedHash);
        }
    }

    [Fact]
    public void GeneratedKitMatchesCurrentReviewedPackageAndRuntimeSources()
    {
        var repository = FindRepositoryRoot();
        var kit = ExtractedKitRoot();
        var mappings = new List<(string Source, string Destination)>
        {
            (Path.Combine(repository, "packaging", "windows", "Test-WindowsReleaseLifecycle.ps1"),
                Path.Combine(kit, "tools", "Test-WindowsReleaseLifecycle.ps1"))
        };
        foreach (var relativePath in new[]
        {
            "README-FIRST.txt",
            "TEST_ONLY_DO_NOT_DISTRIBUTE.txt",
            "Run-Clean-VM-Preflight.cmd",
            "Run-Clean-VM-Lifecycle.cmd",
            "Run-Windows-Sandbox.cmd",
            "docs/CLEAN-VM-CHECKLIST.md",
            "docs/COVERAGE.md"
        })
        {
            mappings.Add((
                Path.Combine(repository, "packaging", "windows", "portable-kit", FromSlash(relativePath)),
                Path.Combine(kit, FromSlash(relativePath))));
        }
        foreach (var relativePath in new[]
        {
            "Invoke-AcceptanceKit.ps1",
            "New-WindowsSandboxConfiguration.ps1",
            "Test-AcceptanceKit.ps1"
        })
        {
            mappings.Add((
                Path.Combine(repository, "packaging", "windows", "portable-kit", relativePath),
                Path.Combine(kit, "tools", relativePath)));
        }
        mappings.Add((
            Path.Combine(repository, "packaging", "windows", "portable-kit", "sandbox", "Run-In-Sandbox.cmd"),
            Path.Combine(kit, "tools", "sandbox", "Run-In-Sandbox.cmd")));

        foreach (var version in new[] { "2.0.0", "2.0.1" })
        {
            var sourceRelease = Path.Combine(
                repository, "artifacts", "release", "windows-x64", "development", version);
            var kitRelease = Path.Combine(kit, "packages", version);
            var sourceNames = Directory.EnumerateFiles(sourceRelease).Select(Path.GetFileName).Order(StringComparer.Ordinal);
            var kitNames = Directory.EnumerateFiles(kitRelease).Select(Path.GetFileName).Order(StringComparer.Ordinal);
            Assert.Equal(sourceNames, kitNames);
            mappings.AddRange(Directory.EnumerateFiles(sourceRelease).Select(source => (
                source,
                Path.Combine(kitRelease, Path.GetFileName(source)))));
        }

        foreach (var (source, destination) in mappings)
        {
            Assert.True(File.Exists(source), $"Reviewed source is missing: {source}");
            Assert.True(File.Exists(destination), $"Generated kit file is missing: {destination}");
            Assert.True(
                new FileInfo(source).Length == new FileInfo(destination).Length,
                $"Generated kit length drifted from reviewed source. Source: {source}; generated: {destination}");
            Assert.True(
                string.Equals(Sha256(source), Sha256(destination), StringComparison.Ordinal),
                $"Generated kit hash drifted from reviewed source. Source: {source}; generated: {destination}");
        }
    }

    [Fact]
    public void StandaloneVerifierRejectsAnExtraFileAndThenTamperedContent()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"isTranscribe-INFRA-005-kit-negative-{Guid.NewGuid():N}");
        var temporaryKit = Path.Combine(temporaryRoot, KitName);
        Directory.CreateDirectory(temporaryKit);
        try
        {
            CopyDirectory(ExtractedKitRoot(), temporaryKit);
            var verifier = Path.Combine(temporaryKit, "tools", "Test-AcceptanceKit.ps1");
            var unexpectedFile = Path.Combine(temporaryKit, "unexpected-file.txt");
            File.WriteAllText(unexpectedFile, "negative-regression", new UTF8Encoding(false));

            var unexpectedResult = RunWindowsPowerShell(
                "-NoLogo", "-NoProfile", "-File", verifier, "-KitDirectory", temporaryKit);
            Assert.NotEqual(0, unexpectedResult.ExitCode);
            Assert.Contains(
                "does not match the exact acceptance-kit allowlist",
                unexpectedResult.Output,
                StringComparison.OrdinalIgnoreCase);

            File.Delete(unexpectedFile);
            File.AppendAllText(
                Path.Combine(temporaryKit, "README-FIRST.txt"),
                "\r\nnegative-regression-tamper\r\n",
                new UTF8Encoding(false));

            var tamperedResult = RunWindowsPowerShell(
                "-NoLogo", "-NoProfile", "-File", verifier, "-KitDirectory", temporaryKit);
            Assert.NotEqual(0, tamperedResult.ExitCode);
            Assert.Contains("file hash mismatch", tamperedResult.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GeneratedPortableSandboxConfigurationHasOnlyTheExactIsolatedMappings()
    {
        var configurationPath = RepositoryPath(
            "artifacts", "acceptance", "INFRA-005", "windows-x64", "portable-kit-sandbox-preflight.wsb");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(configurationPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("Configuration", root.Name.LocalName);
        Assert.Empty(root.Descendants("LogonCommand"));
        Assert.Equal(
            new[]
            {
                "AudioInput", "ClipboardRedirection", "MappedFolders", "MemoryInMB", "Networking",
                "PrinterRedirection", "ProtectedClient", "VideoInput", "vGPU"
            },
            root.Elements().Select(element => element.Name.LocalName).Order(StringComparer.Ordinal));

        foreach (var name in new[]
        {
            "vGPU", "Networking", "AudioInput", "VideoInput", "PrinterRedirection", "ClipboardRedirection"
        })
        {
            Assert.Equal("Disable", Assert.Single(root.Elements(name)).Value);
        }
        Assert.Equal("Enable", Assert.Single(root.Elements("ProtectedClient")).Value);
        Assert.Equal("4096", Assert.Single(root.Elements("MemoryInMB")).Value);

        var mappings = Assert.Single(root.Elements("MappedFolders")).Elements("MappedFolder")
            .ToDictionary(mapping => RequiredValue(mapping, "SandboxFolder"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, mappings.Count);
        var kitMapping = mappings["C:\\isTranscribeAcceptance\\kit"];
        Assert.Equal(Path.GetFullPath(ExtractedKitRoot()), Path.GetFullPath(RequiredValue(kitMapping, "HostFolder")));
        Assert.Equal("true", RequiredValue(kitMapping, "ReadOnly"));
        var evidenceMapping = mappings["C:\\isTranscribeAcceptance\\evidence"];
        Assert.Equal(
            Path.GetFullPath(RepositoryPath(
                "artifacts", "acceptance", "INFRA-005", "windows-x64", "portable-kit-sandbox-staging")),
            Path.GetFullPath(RequiredValue(evidenceMapping, "HostFolder")));
        Assert.Equal("false", RequiredValue(evidenceMapping, "ReadOnly"));
    }

    [Fact]
    public void KitNeverAutomatesTrustElevationOrPersistentSecurityChanges()
    {
        var combined = string.Join('\n', SourceFiles().Select(path => File.ReadAllText(path)));
        foreach (var forbidden in new[]
        {
            "Import-Certificate", "Import-PfxCertificate", "certutil",
            "Set-ExecutionPolicy", "Enable-WindowsOptionalFeature", "-Verb RunAs",
            "New-SelfSignedCertificate", "-AllowUnsigned"
        })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("Build-WindowsRelease.ps1", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandEntrypointsAreLocationRelativeAndKeepScenarioLabelsSeparate()
    {
        var preflight = Read("packaging", "windows", "portable-kit", "Run-Clean-VM-Preflight.cmd");
        var lifecycle = Read("packaging", "windows", "portable-kit", "Run-Clean-VM-Lifecycle.cmd");
        var sandbox = Read("packaging", "windows", "portable-kit", "sandbox", "Run-In-Sandbox.cmd");

        Assert.Contains("%~dp0", preflight, StringComparison.Ordinal);
        Assert.Contains("CleanVmPreflight", preflight, StringComparison.Ordinal);
        Assert.Contains("%~dp0", lifecycle, StringComparison.Ordinal);
        Assert.Contains("CleanVmLifecycle", lifecycle, StringComparison.Ordinal);
        Assert.Contains("SandboxLifecycle", sandbox, StringComparison.Ordinal);
        Assert.Contains("windows-sandbox-administrator", Read("packaging", "windows", "portable-kit", "Invoke-AcceptanceKit.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedRuntimeScriptsParseInStockWindowsPowerShell51()
    {
        var scripts = SourceFiles()
            .Where(path => path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .Append(RepositoryPath("packaging", "windows", "Test-WindowsReleaseLifecycle.ps1"))
            .Concat(Directory.EnumerateFiles(ExtractedKitRoot(), "*.ps1", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var quotedScripts = string.Join(",", scripts.Select(path => $"'{path.Replace("'", "''")}'"));
        var command = $"$failed=$false; foreach($path in @({quotedScripts})) " +
            "{ $errors=$null; [void][System.Management.Automation.Language.Parser]::ParseFile(" +
            "$path,[ref]$null,[ref]$errors); if($errors.Count -gt 0) " +
            "{ $errors | ForEach-Object { Write-Error $_.Message }; $failed=$true } }; if($failed) { exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = RunWindowsPowerShell("-NoLogo", "-NoProfile", "-EncodedCommand", encodedCommand);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void InstructionsDeclareAuthenticityAndCoverageBoundaries()
    {
        var readme = Read("packaging", "windows", "portable-kit", "README-FIRST.txt");
        var coverage = Read("packaging", "windows", "portable-kit", "docs", "COVERAGE.md");

        Assert.Contains("separate .sha256", readme, StringComparison.Ordinal);
        Assert.Contains("not a member", readme, StringComparison.Ordinal);
        Assert.Contains("not eligible for a public release", readme, StringComparison.Ordinal);
        Assert.Contains("does not prove standard-user", readme, StringComparison.Ordinal);
        Assert.Contains("App Installer", coverage, StringComparison.Ordinal);
        Assert.Contains("process loopback", coverage, StringComparison.Ordinal);
        Assert.Contains("sourceRevision infra-008-local-lifecycle", coverage, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(FindRepositoryRoot(), "packaging", "windows", "portable-kit");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
    }

    private static string Read(params string[] relativePath) => File.ReadAllText(
        Path.Combine(new[] { FindRepositoryRoot() }.Concat(relativePath).ToArray()));

    private static string PortableOutputRoot() => RepositoryPath(
        "artifacts", "acceptance", "INFRA-005", "windows-x64", "portable-kit");

    private static string ExtractedKitRoot() => Path.Combine(PortableOutputRoot(), KitName);

    private static string RepositoryPath(params string[] relativePath) => Path.Combine(
        new[] { FindRepositoryRoot() }.Concat(relativePath).ToArray());

    private static string RelativePath(string root, string path) => Path
        .GetRelativePath(root, path)
        .Replace('\\', '/');

    private static string FromSlash(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256(stream);
    }

    private static string Sha256(Stream stream) => Convert
        .ToHexString(SHA256.HashData(stream))
        .ToLowerInvariant();

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RequiredValue(XElement parent, string name) => Assert.Single(parent.Elements(name)).Value;

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: false);
        }
    }

    private static (int ExitCode, string Output) RunWindowsPowerShell(params string[] arguments)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PSModulePath"] = string.Join(
            Path.PathSeparator,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"),
            Path.Combine(Path.GetDirectoryName(executable)!, "Modules"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000), "Windows PowerShell parser timed out.");
        return (process.ExitCode, output);
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
