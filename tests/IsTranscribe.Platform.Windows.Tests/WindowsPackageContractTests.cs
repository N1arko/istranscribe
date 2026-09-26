using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Static release-package contract shared by the MSIX builder and packaged runtime smoke.
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#package-decision
/// @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#install
/// </summary>
public sealed class WindowsPackageContractTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Desktop6 =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";
    private static readonly XNamespace RestrictedCapabilities =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    public static TheoryData<string, int, int> PackageAssets => new()
    {
        { "StoreLogo.png", 50, 50 },
        { "Square44x44Logo.png", 44, 44 },
        { "Square150x150Logo.png", 150, 150 },
        { "Wide310x150Logo.png", 310, 150 },
        { "Square310x310Logo.png", 310, 310 }
    };

    [Fact]
    public void ManifestDeclaresTheStableFullTrustWindowsX64Contour()
    {
        var manifest = LoadManifest();
        var identity = RequiredElement(manifest.Root, Foundation + "Identity");
        Assert.Equal("{{PACKAGE_NAME}}", RequiredAttribute(identity, "Name"));
        Assert.Equal("{{PUBLISHER}}", RequiredAttribute(identity, "Publisher"));
        Assert.Equal("{{PACKAGE_VERSION}}", RequiredAttribute(identity, "Version"));
        Assert.Equal("x64", RequiredAttribute(identity, "ProcessorArchitecture"));

        var target = RequiredElement(manifest.Root, Foundation + "Dependencies")
            .Element(Foundation + "TargetDeviceFamily");
        Assert.NotNull(target);
        Assert.Equal("Windows.Desktop", RequiredAttribute(target, "Name"));
        Assert.Equal("10.0.19041.0", RequiredAttribute(target, "MinVersion"));

        var application = RequiredElement(
            RequiredElement(manifest.Root, Foundation + "Applications"),
            Foundation + "Application");
        Assert.Equal("App", RequiredAttribute(application, "Id"));
        Assert.Equal("IsTranscribe.Desktop.exe", RequiredAttribute(application, "Executable"));
        Assert.Equal("Windows.FullTrustApplication", RequiredAttribute(application, "EntryPoint"));
        Assert.Equal("packagedClassicApp", RequiredAttribute(application, Uap10 + "RuntimeBehavior"));
        Assert.Equal("mediumIL", RequiredAttribute(application, Uap10 + "TrustLevel"));
    }

    [Fact]
    public void ManifestKeepsCanonicalUserDataOutsidePackageRemovalBoundaries()
    {
        var properties = RequiredElement(LoadManifest().Root, Foundation + "Properties");

        Assert.Equal(
            "disabled",
            RequiredElement(properties, Desktop6 + "FileSystemWriteVirtualization").Value);
        Assert.Equal(
            "disabled",
            RequiredElement(properties, Desktop6 + "RegistryWriteVirtualization").Value);

        var capabilities = RequiredElement(LoadManifest().Root, Foundation + "Capabilities");
        Assert.Contains(
            capabilities.Elements(RestrictedCapabilities + "Capability"),
            element => RequiredAttribute(element, "Name") == "runFullTrust");
        Assert.Contains(
            capabilities.Elements(RestrictedCapabilities + "Capability"),
            element => RequiredAttribute(element, "Name") == "unvirtualizedResources");
        Assert.Contains(
            capabilities.Elements(Foundation + "DeviceCapability"),
            element => RequiredAttribute(element, "Name") == "microphone");
    }

    [Fact]
    public void ManifestRegistersDisabledByDefaultPackageAwareAutostart()
    {
        var manifest = LoadManifest();
        var extension = manifest
            .Descendants(Desktop + "Extension")
            .Single(element => RequiredAttribute(element, "Category") == "windows.startupTask");

        Assert.Equal("IsTranscribe.Desktop.exe", RequiredAttribute(extension, "Executable"));
        Assert.Equal("Windows.FullTrustApplication", RequiredAttribute(extension, "EntryPoint"));
        Assert.Equal("--autostart", RequiredAttribute(extension, Uap10 + "Parameters"));

        var startupTask = RequiredElement(extension, Desktop + "StartupTask");
        Assert.Equal("isTranscribeStartup", RequiredAttribute(startupTask, "TaskId"));
        Assert.Equal("false", RequiredAttribute(startupTask, "Enabled"));
        Assert.Equal("isTranscribe", RequiredAttribute(startupTask, "DisplayName"));
    }

    [Fact]
    public void ManifestVisualElementsReferenceEveryPackagedAsset()
    {
        var manifest = LoadManifest();
        var visualElements = manifest.Descendants(Uap + "VisualElements").Single();
        var defaultTile = RequiredElement(visualElements, Uap + "DefaultTile");

        var referencedAssets = new[]
        {
            RequiredElement(
                RequiredElement(manifest.Root, Foundation + "Properties"),
                Foundation + "Logo").Value,
            RequiredAttribute(visualElements, "Square44x44Logo"),
            RequiredAttribute(visualElements, "Square150x150Logo"),
            RequiredAttribute(defaultTile, "Wide310x150Logo"),
            RequiredAttribute(defaultTile, "Square310x310Logo")
        };

        Assert.All(referencedAssets, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        var expectedAssets = new[]
        {
            "Assets\\StoreLogo.png",
            "Assets\\Square44x44Logo.png",
            "Assets\\Square150x150Logo.png",
            "Assets\\Wide310x150Logo.png",
            "Assets\\Square310x310Logo.png"
        };
        Assert.Equal(
            expectedAssets.OrderBy(static value => value, StringComparer.Ordinal),
            referencedAssets.OrderBy(static value => value, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(PackageAssets))]
    public void PackageAssetHasTheDeclaredPixelDimensions(
        string fileName,
        int expectedWidth,
        int expectedHeight)
    {
        var path = Path.Combine(FindRepositoryRoot(), "packaging", "windows", "msix", "Assets", fileName);

        var (width, height) = ReadPngDimensions(path);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void ReleaseBuilderPinsLockedSignedAndAtomicProductionInputs()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "packaging", "windows", "Build-WindowsRelease.ps1"));

        Assert.Contains("src\\IsTranscribe.App.Windows\\IsTranscribe.App.Windows.csproj", script, StringComparison.Ordinal);
        Assert.DoesNotContain("src\\IsTranscribe.App\\IsTranscribe.App.csproj", script, StringComparison.Ordinal);
        Assert.Contains("$DotNetRuntimePackVersion = \"10.0.11\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--locked-mode\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--self-contained\", \"true\"", script, StringComparison.Ordinal);
        Assert.Contains("\"pack\", \"/d\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/h\", \"SHA256\"", script, StringComparison.Ordinal);
        Assert.Contains("\"sign\", \"/fd\", \"SHA256\"", script, StringComparison.Ordinal);
        Assert.Contains("\"verify\", \"/pa\", \"/all\", \"/tw\"", script, StringComparison.Ordinal);
        Assert.Contains("Production builds require -TimestampUrl.", script, StringComparison.Ordinal);
        Assert.Contains("Production and Store builds require -SourceRevision", script, StringComparison.Ordinal);
        Assert.DoesNotContain("AacLicensingReviewReference", script, StringComparison.Ordinal);
        Assert.Contains("infra-005-release-v3", script, StringComparison.Ordinal);
        Assert.Contains("MPEG-1 Layer III", script, StringComparison.Ordinal);
        Assert.Contains("bundledCodecBinaryCount = 0", script, StringComparison.Ordinal);
        Assert.Contains("New-ThirdPartyNotices.ps1", script, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", script, StringComparison.Ordinal);
        Assert.Contains("redistributedDependencyCount", script, StringComparison.Ordinal);
        Assert.Contains("Production packages cannot use a self-signed certificate.", script, StringComparison.Ordinal);
        Assert.Contains("release-identity-policy.json", script, StringComparison.Ordinal);
        Assert.Contains("checked-in stable release identity policy", script, StringComparison.Ordinal);
        Assert.Contains("checked-in release identity allowlist", script, StringComparison.Ordinal);
        Assert.Contains("identityPolicySchemaVersion", script, StringComparison.Ordinal);
        Assert.Contains("identityPolicySha256", script, StringComparison.Ordinal);
        Assert.Contains("locked NuGet content hash for runtime pack", script, StringComparison.Ordinal);
        Assert.Contains("DEVELOPMENT_ONLY.txt", script, StringComparison.Ordinal);
        Assert.Contains("\"Production\" { \"public\" }", script, StringComparison.Ordinal);
        Assert.Contains("\"Store\" { \"store\" }", script, StringComparison.Ordinal);
        Assert.Contains("default { \"development\" }", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $candidateRoot -Destination $releaseRoot", script, StringComparison.Ordinal);
        Assert.Contains("$safeVersion.release.lock", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileOptions]::DeleteOnClose", script, StringComparison.Ordinal);
        Assert.Contains("Another release build is already active", script, StringComparison.Ordinal);
        Assert.Contains("Remove-SafeDirectory -Path $stagingRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Build-InstallerBundle.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Install-IsTranscribe.cmd", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirdPartyNoticePolicyPinsExactOfflinePayloadAndLicenseResources()
    {
        var root = FindRepositoryRoot();
        var installerRoot = Path.Combine(root, "packaging", "windows");
        var generator = File.ReadAllText(Path.Combine(installerRoot, "New-ThirdPartyNotices.ps1"));
        Assert.Contains("infra-005-dependencies-v2", generator, StringComparison.Ordinal);
        Assert.Contains("Published asset mapping changed", generator, StringComparison.Ordinal);
        Assert.Contains("Unknown license expression", generator, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.SDK.NET.Ref/10.0.26100.84", generator, StringComparison.Ordinal);

        var noticesRoot = Path.Combine(installerRoot, "notices");
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(noticesRoot, "third-party-notice-policy.json")));
        var policy = document.RootElement;
        Assert.Equal("infra-005-third-party-notices-v1", policy.GetProperty("schemaVersion").GetString());
        Assert.Equal(43, policy.GetProperty("expectedPackageCount").GetInt32());
        Assert.Equal(33, policy.GetProperty("expectedRedistributedPackageCount").GetInt32());
        var packages = policy.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(43, packages.Length);
        Assert.Equal(33, packages.Count(package => package.GetProperty("redistributed").GetBoolean()));
        Assert.Contains(packages, package => package.GetProperty("id").GetString() == "Concentus");
        Assert.Contains(packages, package => package.GetProperty("id").GetString() == "Concentus.Oggfile");
        Assert.Contains(packages, package => package.GetProperty("id").GetString() == "NAudio.Core");
        Assert.Contains(packages, package => package.GetProperty("id").GetString() == "NAudio.Wasapi");
        Assert.DoesNotContain(packages, package => package.GetProperty("id").GetString() is
            "NAudio" or "NAudio.Asio" or "NAudio.Midi" or "NAudio.WinMM");
        Assert.Contains(
            policy.GetProperty("verbatimSources").EnumerateArray(),
            source => source.GetProperty("package").GetString() == "Concentus/2.2.2"
                      && source.GetProperty("path").GetString() == "LICENSE"
                      && source.GetProperty("sha256").GetString()
                      == "c17f3900e66526e46b23839b62e4b507b03a51cc262d925fb8ad5c753afc0026");

        foreach (var license in policy.GetProperty("canonicalLicenses").EnumerateObject())
        {
            var declaration = license.Value;
            var path = Path.Combine(noticesRoot, declaration.GetProperty("path").GetString()!);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Equal(declaration.GetProperty("sha256").GetString(), hash);
        }
    }

    [Fact]
    public void ReleaseIdentityPolicyPinsStablePackageAndReviewedPublisherRollover()
    {
        var policyPath = Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "windows",
            "release-identity-policy.json");
        using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));
        var root = policy.RootElement;

        Assert.Equal("infra-005-release-identity-policy-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("isTranscribe.Desktop", root.GetProperty("packageName").GetString());
        Assert.Equal("isTranscribe", root.GetProperty("publisherDisplayName").GetString());
        Assert.Equal("CN=isTranscribe Development", root.GetProperty("developmentPublisher").GetString());

        var productionPublishers = root.GetProperty("productionPublisherSubjects");
        Assert.Equal(JsonValueKind.Array, productionPublishers.ValueKind);
        Assert.DoesNotContain(
            productionPublishers.EnumerateArray(),
            element => element.GetString() == "CN=isTranscribe Development");
        var rollover = root.GetProperty("rollover");
        Assert.True(rollover.GetProperty("requiresReviewedPolicyChange").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(rollover.GetProperty("instructions").GetString()));
    }

    [Fact]
    public void ReleaseVerifierEnforcesCryptographicIdentityAndSupplyChainIntegrity()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "windows",
            "Test-WindowsReleaseArtifact.ps1"));

        Assert.StartsWith("#Requires -Version 7.0", script, StringComparison.Ordinal);
        Assert.Contains("ArtifactWinTrustVerifier", script, StringComparison.Ordinal);
        Assert.Contains("0x800B0109", script, StringComparison.Ordinal);
        Assert.Contains("HashMismatch", script, StringComparison.Ordinal);
        Assert.Contains("NotSigned", script, StringComparison.Ordinal);
        Assert.Contains("SignToolPath", script, StringComparison.Ordinal);
        Assert.Contains("\"/tw\"", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ExactReleaseFileAllowlist", script, StringComparison.Ordinal);
        Assert.Contains("forbidden private-key or archive file", script, StringComparison.Ordinal);
        Assert.Contains("Assert-JsonBoolean", script, StringComparison.Ordinal);
        Assert.Contains("Assert-JsonInteger", script, StringComparison.Ordinal);
        Assert.Contains("dotnet restore $projectPath --locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("*.nupkg.sha512", script, StringComparison.Ordinal);
        Assert.Contains("NuGet metadata/lock content hash", script, StringComparison.Ordinal);
        Assert.Contains("src\\IsTranscribe.App.Windows\\packages.lock.json", script, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.Desktop.deps.json", script, StringComparison.Ordinal);
        Assert.Contains("runtimepack.$ExpectedRuntimePackage", script, StringComparison.Ordinal);
        Assert.Contains("identity policy SHA-256 evidence", script, StringComparison.Ordinal);
        Assert.Contains("TemporaryCleanupFailure", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-AppxPackage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Import-Certificate", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Import-PfxCertificate", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeBuildSupportsFailClosedUserLocalNinjaToolchains()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "transcription",
            "Build-WhisperNative.ps1"));

        Assert.Contains("[string]$WindowsCCompiler", script, StringComparison.Ordinal);
        Assert.Contains("[string]$WindowsCxxCompiler", script, StringComparison.Ordinal);
        Assert.Contains("[string]$WindowsMakeProgram", script, StringComparison.Ordinal);
        Assert.Contains("$WindowsGenerator -cne \"Ninja\"", script, StringComparison.Ordinal);
        Assert.Contains("-DCMAKE_BUILD_TYPE=Release", script, StringComparison.Ordinal);
        Assert.Contains("-DCMAKE_C_COMPILER=$WindowsCCompiler", script, StringComparison.Ordinal);
        Assert.Contains("-DCMAKE_CXX_COMPILER=$WindowsCxxCompiler", script, StringComparison.Ordinal);
        Assert.Contains("-DCMAKE_MAKE_PROGRAM=$WindowsMakeProgram", script, StringComparison.Ordinal);
        Assert.Contains("Split-Path -Parent $WindowsMakeProgram", script, StringComparison.Ordinal);
        Assert.Contains("$env:CC = $WindowsCCompiler", script, StringComparison.Ordinal);
        Assert.Contains("$env:CXX = $WindowsCxxCompiler", script, StringComparison.Ordinal);
        Assert.Contains("-not [IO.Path]::IsPathRooted($tool.Path)", script, StringComparison.Ordinal);
        Assert.Contains("Windows build requires an absolute DumpbinPath", script, StringComparison.Ordinal);

        var supplyChainTest = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "transcription",
            "Test-WhisperNativeSupplyChain.ps1"));
        Assert.Contains("[string]$NativeImportLibraryPath", supplyChainTest, StringComparison.Ordinal);
        Assert.Contains("NativeImportLibraryPath is required for Windows", supplyChainTest, StringComparison.Ordinal);
        Assert.Contains("Windows native ABI execution requires cl.exe", supplyChainTest, StringComparison.Ordinal);
        Assert.Contains("ITW_SMOKE_VULKAN", supplyChainTest, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "execution is currently scoped to the macOS acceptance host",
            supplyChainTest,
            StringComparison.Ordinal);

        var cmake = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "native",
            "whisper",
            "CMakeLists.txt"));
        Assert.Contains("/experimental:deterministic", cmake, StringComparison.Ordinal);
        Assert.Contains("/pathmap:${ITW_WHISPER_SOURCE_REAL}", cmake, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseOwnedLockFilesPinTheDesktopAndPackagingToolGraphs()
    {
        var root = FindRepositoryRoot();
        var desktopLockPath = Path.Combine(root, "src", "IsTranscribe.App.Windows", "packages.lock.json");
        var buildToolsLockPath = Path.Combine(root, "packaging", "windows", "packages.lock.json");
        using var desktopLock = JsonDocument.Parse(File.ReadAllText(desktopLockPath));
        using var buildToolsLock = JsonDocument.Parse(File.ReadAllText(buildToolsLockPath));

        var dependencies = desktopLock.RootElement.GetProperty("dependencies");
        Assert.True(dependencies.TryGetProperty("net10.0", out var framework));
        var windowsSdk = framework.GetProperty("Microsoft.Windows.SDK.NET.Ref");
        Assert.Equal("10.0.26100.84", windowsSdk.GetProperty("resolved").GetString());
        Assert.False(string.IsNullOrWhiteSpace(windowsSdk.GetProperty("contentHash").GetString()));
        var runtimePack = framework.GetProperty("Microsoft.NETCore.App.Runtime.win-x64");
        Assert.Equal("[10.0.11, 10.0.11]", runtimePack.GetProperty("requested").GetString());
        Assert.Equal("10.0.11", runtimePack.GetProperty("resolved").GetString());
        Assert.False(string.IsNullOrWhiteSpace(runtimePack.GetProperty("contentHash").GetString()));
        var buildToolsFramework = buildToolsLock.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0");
        var buildTools = buildToolsFramework.GetProperty("Microsoft.Windows.SDK.BuildTools");
        Assert.Equal("10.0.28000.1839", buildTools.GetProperty("resolved").GetString());
        Assert.False(string.IsNullOrWhiteSpace(buildTools.GetProperty("contentHash").GetString()));
    }

    [Fact]
    public void LegacyZipAndPowerShellContourIsDocumentedAsDevelopmentOnly()
    {
        var readme = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "packaging", "windows", "README.md"));

        Assert.Contains("historical", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("development contour", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("excluded from the public", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/IsTranscribe.App.Windows/packages.lock.json", readme, StringComparison.Ordinal);
    }

    private static XDocument LoadManifest()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "windows",
            "msix",
            "AppxManifest.template.xml");
        return XDocument.Load(path, LoadOptions.PreserveWhitespace);
    }

    private static XElement RequiredElement(XContainer? parent, XName name) =>
        parent?.Element(name)
        ?? throw new InvalidDataException($"Required MSIX manifest element '{name}' is missing.");

    private static string RequiredAttribute(XElement? element, XName name) =>
        element?.Attribute(name)?.Value
        ?? throw new InvalidDataException($"Required MSIX manifest attribute '{name}' is missing.");

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        var header = new byte[24];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            header[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(header, 12, 4));
        return (
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
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
