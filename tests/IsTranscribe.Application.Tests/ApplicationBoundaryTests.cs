using IsTranscribe.Application.Platform;
using IsTranscribe.Application.Runtime;
using IsTranscribe.Host.Settings;
using Xunit;

namespace IsTranscribe.Application.Tests;

/// <summary>
/// Shared runtime ownership and dependency-direction contract.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#dependencies
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#application-runtime
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.projects
/// </remarks>
public sealed class ApplicationBoundaryTests
{
    private static readonly string[] CommonProjectDirectories =
    [
        "IsTranscribe.Core",
        "IsTranscribe.Application",
        "IsTranscribe.Persistence",
        "IsTranscribe.Desktop"
    ];

    [Fact]
    public void ApplicationOwnsTheConcreteRuntimeWithoutPlatformAssemblyReferences()
    {
        var assembly = typeof(ApplicationRuntime).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(static reference => reference.Name).ToArray();

        Assert.Equal("IsTranscribe.Application", assembly.GetName().Name);
        Assert.Equal("IsTranscribe.Application.Runtime", typeof(IApplicationRuntime).Namespace);
        Assert.DoesNotContain("IsTranscribe.Platform.Windows", references);
        Assert.DoesNotContain("IsTranscribe.Platform.MacOS", references);
        Assert.Null(assembly.GetType("IsTranscribe.Platform.Windows.WindowsReleaseV2Runtime"));
    }

    [Fact]
    public void ApplicationReferencesLocalTranscriptionLibraryWithoutWorkerExecutable()
    {
        var root = FindRepositoryRoot();
        var applicationProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Application",
            "IsTranscribe.Application.csproj"));
        var workerProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Transcription.Worker",
            "IsTranscribe.Transcription.Worker.csproj"));

        Assert.Contains("IsTranscribe.Transcription.Local", applicationProject, StringComparison.Ordinal);
        Assert.DoesNotContain("IsTranscribe.Transcription.Worker", applicationProject, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.Transcription.Local", workerProject, StringComparison.Ordinal);
    }

    [Fact]
    // @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#permissions
    public void CapabilityStateIncludesCanonicalPermissionLifecycle()
    {
        Assert.Equal(
            ["Available", "NotRequested", "PermissionRequired", "NeedsRestart", "Unsupported", "Failed"],
            Enum.GetNames<PlatformCapabilityState>());
    }

    [Fact]
    public void CommonProjectsContainNoWindowsOnlyDependenciesOrNativeCalls()
    {
        var root = FindRepositoryRoot();
        var forbiddenTokens = new[]
        {
            "using IsTranscribe.Platform.Windows",
            "<ProjectReference Include=\"..\\IsTranscribe.Platform.Windows",
            "System.Windows",
            "System.Windows.Forms",
            "Microsoft.Windows.SDK",
            "NAudio",
            "System.Security.Cryptography.ProtectedData",
            "DllImport(",
            "LibraryImport(",
            "OperatingSystem.IsWindows("
        };

        foreach (var projectDirectory in CommonProjectDirectories)
        {
            var sourceRoot = Path.Combine(root, "src", projectDirectory);
            var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(static path =>
                    (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                Assert.All(
                    forbiddenTokens,
                    token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void ReleaseSolutionHasTenProductProjectsIncludingLocalRuntimeAndNoLegacyHost()
    {
        var solution = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "isTranscribe.sln"));
        var productProjects = solution.Split('\n', StringSplitOptions.None)
            .Where(static line => line.StartsWith("Project(", StringComparison.Ordinal)
                                  && line.Contains("\"src\\", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(10, productProjects.Length);
        Assert.DoesNotContain("IsTranscribe.Host", solution, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.App.Windows", solution, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.App.MacOS", solution, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.Platform.Windows", solution, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.Platform.MacOS", solution, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.Transcription.Local", solution, StringComparison.Ordinal);
        Assert.Contains("IsTranscribe.Transcription.Worker", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalWindowsDataPathsRemainUnderTheExistingRoots()
    {
        var paths = new LocalAppPaths("isTranscribe");
        var expectedLocalRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "isTranscribe");
        var expectedDocumentsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "isTranscribe");

        Assert.Equal(expectedLocalRoot, paths.RootDirectory);
        Assert.Equal(Path.Combine(expectedLocalRoot, "config", "settings.json"), paths.SettingsFilePath);
        Assert.Equal(Path.Combine(expectedLocalRoot, "data", "app.db"), paths.DatabaseFilePath);
        Assert.Equal(Path.Combine(expectedDocumentsRoot, "Recordings"), paths.DefaultRecordingsDirectory);
        Assert.Equal(Path.Combine(expectedDocumentsRoot, "Transcripts"), paths.DefaultTranscriptsDirectory);
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
