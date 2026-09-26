using IsTranscribe.Application.Platform;
using IsTranscribe.Platform.ContractTests;
using System.Runtime.Versioning;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Runs the shared platform capability fixture against the Windows adapter.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#build-and-test-contour
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformAdapterContractTests : PlatformAdapterContract
{
    protected override string Platform => "windows";

    protected override IPlatformCapabilityService CreateAdapter() =>
        new WindowsApplicationPlatformRuntimeAdapter();
}
