using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the host boundary of the disposable Windows Sandbox installer fixture.
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
/// </summary>
public sealed class WindowsSandboxAcceptanceContractTests
{
    [Fact]
    public void GeneratorCreatesTwoReadOnlyInputsAndOneDedicatedWritableStagingMapping()
    {
        var script = Read("packaging", "windows", "New-WindowsSandboxAcceptance.ps1");

        Assert.Contains("artifacts\\release\\windows-x64\\development", script, StringComparison.Ordinal);
        Assert.Contains("packaging\\windows", script, StringComparison.Ordinal);
        Assert.Contains("sandbox-staging", script, StringComparison.Ordinal);
        Assert.Contains("-ReadOnly $true", script, StringComparison.Ordinal);
        Assert.Contains("-ReadOnly $false", script, StringComparison.Ordinal);
        Assert.Contains("writableMappingCount = 1", script, StringComparison.Ordinal);
        Assert.Contains("must be empty before a new disposable run", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointInPath", script, StringComparison.Ordinal);
        Assert.Contains("Assert-PathsDoNotOverlap", script, StringComparison.Ordinal);
        Assert.Contains("must stay within their expected repository roots", script, StringComparison.Ordinal);
        Assert.Contains("prepared_host_launch_unsupported", script, StringComparison.Ordinal);
        Assert.Contains("hostLaunchPrerequisitesObserved", script, StringComparison.Ordinal);
        Assert.Contains("configurationIsHostPathSpecific = $true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LogonCommand", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedConfigurationHasTheExactStructuralHostBoundary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testParent = Path.Combine(repositoryRoot, "artifacts", ".sandbox-generator-tests");
        var fixtureRoot = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
        try
        {
            PrepareGeneratorFixture(repositoryRoot, fixtureRoot);
            var generator = Path.Combine(repositoryRoot, "packaging", "windows", "New-WindowsSandboxAcceptance.ps1");
            var result = RunWindowsPowerShell(
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                generator,
                "-RepositoryRoot",
                fixtureRoot);
            Assert.True(result.ExitCode == 0, result.CombinedOutput);

            var configurationPath = Path.Combine(
                fixtureRoot,
                "artifacts",
                "acceptance",
                "INFRA-005",
                "windows-x64",
                "isTranscribe-installer-acceptance.wsb");
            var document = XDocument.Load(configurationPath);
            var configuration = Assert.IsType<XElement>(document.Root);
            Assert.Equal("Configuration", configuration.Name.LocalName);

            var mappings = configuration
                .Element("MappedFolders")!
                .Elements("MappedFolder")
                .ToArray();
            Assert.Equal(3, mappings.Length);
            Assert.Equal(2, mappings.Count(mapping => mapping.Element("ReadOnly")?.Value == "true"));
            var writable = Assert.Single(mappings, mapping => mapping.Element("ReadOnly")?.Value == "false");

            var expectedHostFolders = new[]
            {
                Path.Combine(fixtureRoot, "artifacts", "release", "windows-x64", "development"),
                Path.Combine(fixtureRoot, "packaging", "windows"),
                Path.Combine(fixtureRoot, "artifacts", "acceptance", "INFRA-005", "windows-x64", "sandbox-staging")
            }.Select(Path.GetFullPath).ToArray();
            var actualHostFolders = mappings
                .Select(mapping => Path.GetFullPath(mapping.Element("HostFolder")!.Value))
                .ToArray();
            Assert.Equal(expectedHostFolders, actualHostFolders);
            Assert.Equal(expectedHostFolders[2], Path.GetFullPath(writable.Element("HostFolder")!.Value));
            Assert.All(actualHostFolders, path =>
            {
                Assert.True(Path.IsPathFullyQualified(path));
                Assert.True(Directory.Exists(path));
                Assert.Equal(0, (int)(File.GetAttributes(path) & FileAttributes.ReparsePoint));
            });
            for (var first = 0; first < actualHostFolders.Length; first++)
            {
                for (var second = first + 1; second < actualHostFolders.Length; second++)
                {
                    Assert.False(PathsOverlap(actualHostFolders[first], actualHostFolders[second]));
                }
            }

            Assert.Equal("Disable", configuration.Element("vGPU")?.Value);
            Assert.Equal("Disable", configuration.Element("Networking")?.Value);
            Assert.Equal("Disable", configuration.Element("AudioInput")?.Value);
            Assert.Equal("Disable", configuration.Element("VideoInput")?.Value);
            Assert.Equal("Enable", configuration.Element("ProtectedClient")?.Value);
            Assert.Equal("Disable", configuration.Element("PrinterRedirection")?.Value);
            Assert.Equal("Disable", configuration.Element("ClipboardRedirection")?.Value);
            Assert.Equal("4096", configuration.Element("MemoryInMB")?.Value);
            Assert.Null(configuration.Element("LogonCommand"));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
            if (Directory.Exists(testParent) && !Directory.EnumerateFileSystemEntries(testParent).Any())
            {
                Directory.Delete(testParent);
            }
        }
    }

    [Fact]
    public void SandboxConfigurationDisablesUnneededHostIntegration()
    {
        var script = Read("packaging", "windows", "New-WindowsSandboxAcceptance.ps1");

        foreach (var element in new[]
        {
            "vGPU", "Networking", "AudioInput", "VideoInput",
            "PrinterRedirection", "ClipboardRedirection"
        })
        {
            Assert.Contains($"-Name \"{element}\" -Value \"Disable\"", script, StringComparison.Ordinal);
        }
        Assert.Contains("-Name \"ProtectedClient\" -Value \"Enable\"", script, StringComparison.Ordinal);
        Assert.Contains("-Name \"MemoryInMB\" -Value \"4096\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FixtureNeverAutomatesCertificateTrustOrPersistentSecurityChanges()
    {
        var combined = string.Join('\n', new[]
        {
            Read("packaging", "windows", "New-WindowsSandboxAcceptance.ps1"),
            Read("packaging", "windows", "windows-sandbox", "Invoke-WindowsSandboxAcceptance.ps1"),
            Read("packaging", "windows", "windows-sandbox", "Run-IsTranscribe-Installer-Acceptance.cmd")
        });

        foreach (var forbidden in new[]
        {
            "Import-Certificate", "Import-PfxCertificate", "certutil",
            "Set-ExecutionPolicy", "Enable-WindowsOptionalFeature", "-Verb RunAs",
            "New-SelfSignedCertificate", "-AllowUnsigned"
        })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RunnerUsesStockWindowsPowerShellAndLocalCopiesForTheTwoPassLifecycle()
    {
        var command = Read("packaging", "windows", "windows-sandbox", "Run-IsTranscribe-Installer-Acceptance.cmd");
        var wrapper = Read("packaging", "windows", "windows-sandbox", "Invoke-WindowsSandboxAcceptance.ps1");

        Assert.Contains("powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass", command, StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:\\isTranscribeAcceptance\\local", wrapper, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $sourceBaseMsix", wrapper, StringComparison.Ordinal);
        Assert.Contains("E26083828271B48054F6D9B749DD94BE4C04D57D67EA9B94F2C973E91EF5227B", wrapper, StringComparison.Ordinal);
        Assert.Contains("FC906F0C370306BB7DECEE7C8B72531AE88EE9D8945251DF43ED6CB71A9153E3", wrapper, StringComparison.Ordinal);
        const string expectedHarnessHash = "4E13BAA5E92698954358B0C6DF5A5DA3A20C287CCE112662C9073DFB8757BB4F";
        Assert.Contains(expectedHarnessHash, wrapper, StringComparison.Ordinal);
        Assert.Contains(expectedHarnessHash, Read("packaging", "windows", "New-WindowsSandboxAcceptance.ps1"), StringComparison.Ordinal);
        Assert.Contains("OperationTimeoutSeconds = 120", wrapper, StringComparison.Ordinal);
        Assert.Contains("windows-sandbox-preflight.json", wrapper, StringComparison.Ordinal);
        Assert.Contains("windows-sandbox-lifecycle.json", wrapper, StringComparison.Ordinal);
        Assert.Contains("-PreflightOnly", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaveInstalled", wrapper, StringComparison.Ordinal);
        Assert.True(
            wrapper.IndexOf("$trustedCertificate = Get-ChildItem", StringComparison.Ordinal) <
            wrapper.IndexOf("[void][IO.Directory]::CreateDirectory($localRoot)", StringComparison.Ordinal));
    }

    [Fact]
    public void BothPowerShellScriptsParseInStockWindowsPowerShell51()
    {
        var scripts = new[]
        {
            Path.Combine(FindRepositoryRoot(), "packaging", "windows", "New-WindowsSandboxAcceptance.ps1"),
            Path.Combine(FindRepositoryRoot(), "packaging", "windows", "windows-sandbox", "Invoke-WindowsSandboxAcceptance.ps1")
        };
        var quotedScripts = string.Join(",", scripts.Select(path => $"'{path.Replace("'", "''")}'"));
        var command = $"$failed=$false; foreach($path in @({quotedScripts})) " +
            "{ $errors=$null; [void][System.Management.Automation.Language.Parser]::ParseFile(" +
            "$path,[ref]$null,[ref]$errors); if($errors.Count -gt 0) " +
            "{ $errors | ForEach-Object { Write-Error $_.Message }; $failed=$true } }; " +
            "if($failed) { exit 1 }";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = RunWindowsPowerShell("-NoLogo", "-NoProfile", "-EncodedCommand", encodedCommand);
        Assert.True(result.ExitCode == 0, $"Windows PowerShell 5.1 parser failed.{Environment.NewLine}{result.CombinedOutput}");
    }

    [Fact]
    public void InstructionsKeepTrustManualAndDeclareTheLiveCoverageBoundary()
    {
        var instructions = Read("packaging", "windows", "windows-sandbox", "README.txt");

        Assert.Contains("Install Certificate", instructions, StringComparison.Ordinal);
        Assert.Contains("Local Machine", instructions, StringComparison.Ordinal);
        Assert.Contains("Trusted People", instructions, StringComparison.Ordinal);
        Assert.Contains("explicit manual step", instructions, StringComparison.Ordinal);
        Assert.Contains("Close Windows Sandbox", instructions, StringComparison.Ordinal);
        Assert.Contains("does not validate App Installer UI", instructions, StringComparison.Ordinal);
        Assert.Contains("Zen/Zoom", instructions, StringComparison.Ordinal);
        Assert.Contains("build 19041", instructions, StringComparison.Ordinal);
        Assert.Contains("absolute paths", instructions, StringComparison.Ordinal);
        Assert.Contains("does not create a portable fixture", instructions, StringComparison.Ordinal);
    }

    private static string Read(params string[] relativePath) => File.ReadAllText(
        Path.Combine(new[] { FindRepositoryRoot() }.Concat(relativePath).ToArray()));

    private static bool PathsOverlap(string firstPath, string secondPath)
    {
        var first = Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var second = Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrepareGeneratorFixture(string repositoryRoot, string fixtureRoot)
    {
        var boardDirectory = Path.Combine(fixtureRoot, "specs");
        var baseDirectory = Path.Combine(fixtureRoot, "artifacts", "release", "windows-x64", "development", "2.0.0");
        var upgradeDirectory = Path.Combine(fixtureRoot, "artifacts", "release", "windows-x64", "development", "2.0.1");
        var fixtureToolsDirectory = Path.Combine(fixtureRoot, "packaging", "windows", "windows-sandbox");
        Directory.CreateDirectory(boardDirectory);
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(upgradeDirectory);
        Directory.CreateDirectory(fixtureToolsDirectory);
        File.WriteAllText(Path.Combine(boardDirectory, "BOARD.md"), "# Test BOARD");
        File.WriteAllBytes(Path.Combine(baseDirectory, "isTranscribe-2.0.0-dev-win-x64.msix"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(upgradeDirectory, "isTranscribe-2.0.1-dev-win-x64.msix"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(upgradeDirectory, "isTranscribe-development-certificate.cer"), Array.Empty<byte>());

        var sourceTools = Path.Combine(repositoryRoot, "packaging", "windows");
        File.Copy(
            Path.Combine(sourceTools, "Test-WindowsReleaseLifecycle.ps1"),
            Path.Combine(fixtureRoot, "packaging", "windows", "Test-WindowsReleaseLifecycle.ps1"));
        foreach (var fixtureFile in new[]
        {
            "Invoke-WindowsSandboxAcceptance.ps1",
            "Run-IsTranscribe-Installer-Acceptance.cmd",
            "README.txt"
        })
        {
            File.Copy(
                Path.Combine(sourceTools, "windows-sandbox", fixtureFile),
                Path.Combine(fixtureToolsDirectory, fixtureFile));
        }
    }

    private static (int ExitCode, string CombinedOutput) RunWindowsPowerShell(params string[] arguments)
    {
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(windowsPowerShell));

        var startInfo = new ProcessStartInfo(windowsPowerShell)
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
            Path.Combine(Path.GetDirectoryName(windowsPowerShell)!, "Modules"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000), "Windows PowerShell did not exit in time.");
        return (process.ExitCode, standardOutput + Environment.NewLine + standardError);
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

        throw new DirectoryNotFoundException("Could not find the repository root from the test output path.");
    }
}
