using System.Reflection;
using System.Runtime.Versioning;
using IsTranscribe.Platform.Windows;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-014-transcription-extension-seam#verification
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#platform-parity
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReleaseV2CompositionTests
{
    [Fact]
    public void WindowsRuntimeActivatesSharedTranscriptionWithoutLegacyFireworksComposition()
    {
        var runtimeType = typeof(IsTranscribe.Application.ApplicationRuntime);
        var composedTypes = runtimeType
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static field => field.FieldType)
            .Concat(runtimeType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(static constructor => constructor
                    .GetParameters()
                    .Select(static parameter => parameter.ParameterType)))
            .SelectMany(FlattenType)
            .Distinct()
            .ToArray();

        Assert.Contains(
            composedTypes,
            static type => string.Equals(
                type.FullName,
                "IsTranscribe.Application.Transcription.TranscriptionEngineRegistry",
                StringComparison.Ordinal));
        Assert.DoesNotContain(composedTypes, IsLegacyTranscriptionDependency);

        var root = FindRepositoryRoot();
        var compositionSources = new[]
        {
            Path.Combine(root, "src", "IsTranscribe.Desktop", "App.axaml.cs"),
            Path.Combine(root, "src", "IsTranscribe.Application", "ApplicationRuntime.cs")
        };
        var forbiddenConstructionTokens = new[]
        {
            "TranscriptionBackgroundWorker",
            "FireworksTranscriptionProvider",
            "ITranscriptionProvider",
            "DpapiSecretStore",
            "DpapiSecretProtector",
            "AppSecrets",
            "ISecretStore"
        };

        foreach (var sourcePath in compositionSources)
        {
            var source = File.ReadAllText(sourcePath);
            Assert.All(
                forbiddenConstructionTokens,
                token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
        }
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in FlattenType(elementType))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in FlattenType(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsLegacyTranscriptionDependency(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Contains("Fireworks", StringComparison.OrdinalIgnoreCase)
               || name.Contains("TranscriptionBackgroundWorker", StringComparison.Ordinal)
               || name.EndsWith(".ITranscriptionProvider", StringComparison.Ordinal);
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
