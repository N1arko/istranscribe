using IsTranscribe.Application.Platform;
using IsTranscribe.Platform.MacOS;
using Xunit;

namespace IsTranscribe.Platform.ContractTests;

/// <summary>
/// One behavioral capability fixture executed against both platform adapters.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#build-and-test-contour
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#platform-contracts
/// </remarks>
public abstract class PlatformAdapterContract
{
    protected abstract string Platform { get; }

    protected abstract IPlatformCapabilityService CreateAdapter();

    [Fact]
    public void AdapterPublishesEveryRequiredCapabilityWithSafeExplanation()
    {
        var adapter = CreateAdapter();
        var capabilities = adapter.GetCapabilities();
        Assert.Equal(
            ["audio_capture", "audio_observation", "meeting_observation", "permissions"],
            capabilities.Select(static item => item.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.All(capabilities, capability =>
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.Explanation));
            Assert.True(Enum.IsDefined(capability.State));
        });

        if (Platform == "macos")
        {
            var states = capabilities.ToDictionary(static capability => capability.Id, static capability => capability.State);
            Assert.Equal(PlatformCapabilityState.Available, states["audio_observation"]);
            Assert.Equal(PlatformCapabilityState.PermissionRequired, states["audio_capture"]);
            Assert.Equal(PlatformCapabilityState.PermissionRequired, states["meeting_observation"]);
            Assert.Equal(PlatformCapabilityState.PermissionRequired, states["permissions"]);
        }
    }
}

public sealed class MacOSPlatformAdapterContractTests : PlatformAdapterContract
{
    protected override string Platform => "macos";

    protected override IPlatformCapabilityService CreateAdapter() =>
        new MacOSApplicationPlatformRuntimeAdapter();
}
