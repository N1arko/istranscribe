using IsTranscribe.Application.Platform;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.MacOS.Tests;

/// <summary>
/// Provider-neutral pre-parity composition and unsupported-state contract.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#structure
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#verification
/// </remarks>
public sealed class MacOSBoundaryTests
{
    [Fact]
    public void AdapterGraphDoesNotReferenceWindows()
    {
        var references = typeof(MacOSApplicationPlatformRuntimeAdapter)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("IsTranscribe.Platform.Windows", references);
        Assert.DoesNotContain("IsTranscribe.Host", references);
    }

    [Fact]
    public void AudioCapabilitiesExposeWI007Readiness()
    {
        var adapter = (IPlatformCapabilityService)new MacOSApplicationPlatformRuntimeAdapter();
        var capabilities = adapter.GetCapabilities().ToDictionary(static capability => capability.Id);

        Assert.Equal(PlatformCapabilityState.Available, capabilities["audio_observation"].State);
        Assert.Equal(PlatformCapabilityState.PermissionRequired, capabilities["audio_capture"].State);
        Assert.Equal(PlatformCapabilityState.PermissionRequired, capabilities["meeting_observation"].State);
    }
}
