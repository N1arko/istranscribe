using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IsTranscribe.VisualReview;

/// <summary>
/// Resolves the production semantic tokens for the active theme and calculates WCAG text contrast.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#visual-system
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// </remarks>
internal static class TokenContrastValidator
{
    private const double MinimumTextContrast = 4.5;

    private static readonly IReadOnlyList<string> TextRoles =
    [
        "Color.Text.Primary",
        "Color.Text.Secondary",
        "Color.Text.Tertiary"
    ];

    private static readonly IReadOnlyList<string> SurfaceRoles =
    [
        "Color.Window.Background",
        "Color.Surface.Primary",
        "Color.Surface.Secondary",
        "Color.Surface.Elevated"
    ];

    public static IReadOnlyList<TokenContrastCheck> Validate(Avalonia.Application application, string theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        var checks = new List<TokenContrastCheck>();
        foreach (var foregroundRole in TextRoles)
        {
            foreach (var backgroundRole in SurfaceRoles)
            {
                checks.Add(CreateCheck(application, theme, foregroundRole, backgroundRole));
            }
        }

        checks.Add(CreateCheck(
            application,
            theme,
            "Color.Text.OnAccent",
            "Color.Accent.Primary"));
        foreach (var recordingRole in new[]
                 {
                     "Color.Status.Recording",
                     "Color.Status.RecordingHover",
                     "Color.Status.RecordingPressed"
                 })
        {
            checks.Add(CreateCheck(
                application,
                theme,
                "Color.Text.OnAccent",
                recordingRole));
        }

        return checks;
    }

    private static TokenContrastCheck CreateCheck(
        Avalonia.Application application,
        string theme,
        string foregroundRole,
        string backgroundRole)
    {
        var foreground = ResolveColor(application, foregroundRole);
        var background = ResolveColor(application, backgroundRole);
        var ratio = ContrastRatio(foreground, background);
        return new TokenContrastCheck(
            theme,
            foregroundRole,
            foreground.ToString(),
            backgroundRole,
            background.ToString(),
            Math.Round(ratio, 3),
            MinimumTextContrast,
            ratio >= MinimumTextContrast);
    }

    private static Color ResolveColor(Avalonia.Application application, string resourceKey)
    {
        if (application.TryGetResource(
                resourceKey,
                application.RequestedThemeVariant,
                out var value)
            && value is Color color)
        {
            return color;
        }

        throw new KeyNotFoundException(
            $"Color token '{resourceKey}' could not be resolved for '{application.RequestedThemeVariant}'.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * Linearize(color.R))
        + (0.7152 * Linearize(color.G))
        + (0.0722 * Linearize(color.B));

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}

internal sealed record TokenContrastCheck(
    string Theme,
    string ForegroundRole,
    string ForegroundColor,
    string BackgroundRole,
    string BackgroundColor,
    double Ratio,
    double MinimumRatio,
    bool Passed);
