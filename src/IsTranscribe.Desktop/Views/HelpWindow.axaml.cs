using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Small local help surface for the core recording flow.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
/// </remarks>
public sealed partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();

    // @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
    private async void PrivacyPolicyButton_OnClick(object? sender, RoutedEventArgs args)
    {
        var window = new PrivacyPolicyWindow { Icon = Icon };
        await window.ShowDialog(this);
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs args) => Close();
}
