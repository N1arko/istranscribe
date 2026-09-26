using System.Windows;
using IsTranscribe.App.Strings;
using IsTranscribe.Host.Capabilities;

namespace IsTranscribe.App.Windows;

public partial class BlockedStartupWindow : Wpf.Ui.Controls.FluentWindow
{
    public BlockedStartupWindow()
    {
        InitializeComponent();
    }

    public void ApplyCapability(HostCapabilitySnapshot capability)
    {
        SummaryText.Text = capability.Summary;
        DetailText.Text = capability.BlockingReason ?? LocalizationManager.Instance["Dialog_BlockedStartup_DefaultDetail"];
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
