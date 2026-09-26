using Avalonia;
using Avalonia.Automation;

namespace IsTranscribe.Desktop.Views;

internal static class AccessibilityLiveRegion
{
    public static void ApplyPolite(StyledElement element) =>
        Apply(element, AutomationLiveSetting.Polite);

    public static void ApplyAssertive(StyledElement element) =>
        Apply(element, AutomationLiveSetting.Assertive);

    private static void Apply(StyledElement element, AutomationLiveSetting setting)
    {
        // Avalonia 12.1 can crash in AvnAccessibilityElement when a live region changes on macOS.
        // Keep the semantic announcement on Windows until the native macOS implementation is safe.
        if (!OperatingSystem.IsMacOS())
        {
            AutomationProperties.SetLiveSetting(element, setting);
        }
    }
}
