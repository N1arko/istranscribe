using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Local, language-aware disclosure of the current release privacy boundary.
/// </summary>
/// <remarks>
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// </remarks>
public sealed partial class PrivacyPolicyWindow : Window
{
    public PrivacyPolicyWindow() => InitializeComponent();

    private void CloseButton_OnClick(object? sender, RoutedEventArgs args) => Close();
}
