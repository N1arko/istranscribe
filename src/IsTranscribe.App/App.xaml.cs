using System.Windows;
using System.Windows.Media;
using IsTranscribe.App.Hosting;
using IsTranscribe.App.Strings;
using Wpf.Ui.Appearance;
using WpfApplication = System.Windows.Application;

namespace IsTranscribe.App;

public partial class App : WpfApplication
{
    private DesktopHostApplication? _desktopHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        ApplyTheme("system");

        LocalizationManager.Instance.SwitchLanguage("ru");

        _desktopHost = DesktopHostApplication.Create(this);
        var shouldExit = await _desktopHost.StartAsync(LaunchArguments.Parse(e.Args));
        if (shouldExit)
        {
            Shutdown();
        }
    }

    // @spec spec://modules/app/FEAT-008-modern-ui-design-system#behavior.theme
    public static void ApplyTheme(string themeName)
    {
        var isDark = string.Equals(themeName, "dark", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(themeName, "system", StringComparison.OrdinalIgnoreCase))
        {
            var systemTheme = ApplicationThemeManager.GetSystemTheme();
            isDark = systemTheme == SystemTheme.Dark
                  || systemTheme == SystemTheme.HC1
                  || systemTheme == SystemTheme.HC2
                  || systemTheme == SystemTheme.HCBlack;
        }

        ApplicationThemeManager.Apply(isDark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        ApplyCustomBrushes(isDark);
    }

    // @spec spec://modules/app/FEAT-008-modern-ui-design-system#behavior.resources.colors
    private static void ApplyCustomBrushes(bool isDark)
    {
        var resources = Current.Resources;

        static void Set(ResourceDictionary res, string key, string hex) =>
            res[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        if (isDark)
        {
            // Core semantic tokens — dark theme
            Set(resources, "AppBackgroundBrush", "#1C1C1E");
            Set(resources, "AppSurfaceBrush", "#2C2C2E");
            Set(resources, "AppBorderBrush", "#48484A");
            Set(resources, "AppTextPrimaryBrush", "#F2F2F7");
            Set(resources, "AppTextSecondaryBrush", "#AEAEB2");
            Set(resources, "AppTextTertiaryBrush", "#8E8E93");
            Set(resources, "AppAccentBrush", "#A5B4FC");
            Set(resources, "AppAccentBackgroundBrush", "#3730A3");
            Set(resources, "AppWarningBrush", "#FCD34D");
            Set(resources, "AppWarningBackgroundBrush", "#422006");
            Set(resources, "AppErrorBrush", "#FCA5A5");
            Set(resources, "AppErrorBackgroundBrush", "#450A0A");
            Set(resources, "AppSuccessBrush", "#86EFAC");
            // Supplementary tokens — dark theme
            Set(resources, "AppSurfaceAltBrush", "#3A3A3C");
            Set(resources, "AppSurfaceSubtleBrush", "#242426");
            Set(resources, "AppBorderSubtleBrush", "#545458");
            Set(resources, "AppBorderFadedBrush", "#3A3A3C");
            Set(resources, "AppWarningContrastBrush", "#FDE68A");
            Set(resources, "AppWarningDeepBrush", "#FCD34D");
            Set(resources, "AppWarningPanelBrush", "#3B2506");
            Set(resources, "AppWarningPanelBorderBrush", "#854D0E");
            Set(resources, "AppSidebarBrush", "#161618");
            Set(resources, "AppSidebarTextBrush", "#F2F2F7");
            Set(resources, "AppSidebarTextMutedBrush", "#8E8E93");
            Set(resources, "AppSidebarSurfaceBrush", "#1C1C1E");
            Set(resources, "AppTextLabelBrush", "#D1D1D6");
        }
        else
        {
            // Core semantic tokens — light theme (matching AppColors.xaml defaults)
            Set(resources, "AppBackgroundBrush", "#F3F4F6");
            Set(resources, "AppSurfaceBrush", "#FFFFFF");
            Set(resources, "AppBorderBrush", "#E5E7EB");
            Set(resources, "AppTextPrimaryBrush", "#1F2937");
            Set(resources, "AppTextSecondaryBrush", "#4B5563");
            Set(resources, "AppTextTertiaryBrush", "#6B7280");
            Set(resources, "AppAccentBrush", "#3730A3");
            Set(resources, "AppAccentBackgroundBrush", "#EEF2FF");
            Set(resources, "AppWarningBrush", "#B45309");
            Set(resources, "AppWarningBackgroundBrush", "#FEF3C7");
            Set(resources, "AppErrorBrush", "#B91C1C");
            Set(resources, "AppErrorBackgroundBrush", "#FEE2E2");
            Set(resources, "AppSuccessBrush", "#15803D");
            // Supplementary tokens — light theme
            Set(resources, "AppSurfaceAltBrush", "#F9FAFB");
            Set(resources, "AppSurfaceSubtleBrush", "#F8FAFC");
            Set(resources, "AppBorderSubtleBrush", "#D1D5DB");
            Set(resources, "AppBorderFadedBrush", "#DCE3EA");
            Set(resources, "AppWarningContrastBrush", "#92400E");
            Set(resources, "AppWarningDeepBrush", "#7C2D12");
            Set(resources, "AppWarningPanelBrush", "#FFF7ED");
            Set(resources, "AppWarningPanelBorderBrush", "#FED7AA");
            Set(resources, "AppSidebarBrush", "#111827");
            Set(resources, "AppSidebarTextBrush", "#E5E7EB");
            Set(resources, "AppSidebarTextMutedBrush", "#9CA3AF");
            Set(resources, "AppSidebarSurfaceBrush", "#1F2937");
            Set(resources, "AppTextLabelBrush", "#374151");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _desktopHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
