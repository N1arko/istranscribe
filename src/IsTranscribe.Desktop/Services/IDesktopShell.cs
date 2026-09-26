using IsTranscribe.Application.Platform;

namespace IsTranscribe.Desktop.Services;

// Compatibility alias for presentation code and existing fakes.
// @spec spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#desktop
public interface IDesktopShell : IPlatformShell;
