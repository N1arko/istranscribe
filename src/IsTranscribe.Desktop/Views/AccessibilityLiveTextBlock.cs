using Avalonia.Automation;
using Avalonia.Controls;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Platform-safe live-region text used for status changes that must remain visible on macOS.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </remarks>
public sealed class PoliteLiveTextBlock : TextBlock
{
    public PoliteLiveTextBlock() => AccessibilityLiveRegion.ApplyPolite(this);
}

/// <summary>
/// Platform-safe assertive live-region text used for recoverable errors.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
/// </remarks>
public sealed class AssertiveLiveTextBlock : TextBlock
{
    public AssertiveLiveTextBlock() => AccessibilityLiveRegion.ApplyAssertive(this);
}
