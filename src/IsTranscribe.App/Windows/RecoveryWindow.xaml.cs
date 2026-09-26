using System.Windows;
using IsTranscribe.App.Strings;
using IsTranscribe.Application.Recovery;

namespace IsTranscribe.App.Windows;

public partial class RecoveryWindow : Wpf.Ui.Controls.FluentWindow
{
    public RecoveryWindow()
    {
        InitializeComponent();
    }

    public bool AllowClose { get; set; }

    public event EventHandler? OpenAppRequested;

    public event EventHandler? CloseRequested;

    public void ApplyRecovery(RecoveryLaunchDecision recovery)
    {
        SummaryText.Text = recovery.Summary;
        DetailText.Text = recovery.Detail ?? LocalizationManager.Instance["Dialog_Recovery_DefaultDetail"];
    }

    private void OnOpenAppClick(object sender, RoutedEventArgs e) => OpenAppRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuitClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
