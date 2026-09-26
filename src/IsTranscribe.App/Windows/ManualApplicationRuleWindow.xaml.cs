using System.Windows;
using IsTranscribe.App.Strings;
using IsTranscribe.Host.Persistence;
using MessageBox = System.Windows.MessageBox;

namespace IsTranscribe.App.Windows;

public partial class ManualApplicationRuleWindow : Wpf.Ui.Controls.FluentWindow
{
    public ManualApplicationRuleWindow()
    {
        InitializeComponent();
    }

    public AppRuleRecord? Result { get; private set; }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var displayName = DisplayNameTextBox.Text.Trim();
        var processName = ProcessNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(processName))
        {
            MessageBox.Show(this, LocalizationManager.Instance["Validation_AddApplication_ProcessNameRequired"], LocalizationManager.Instance["Validation_AddApplication_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = AppRuleRecord.Create(
            string.IsNullOrWhiteSpace(displayName) ? processName : displayName,
            processName);

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
