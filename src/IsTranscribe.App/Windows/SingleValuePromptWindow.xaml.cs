using System.Windows;

namespace IsTranscribe.App.Windows;

public partial class SingleValuePromptWindow : Wpf.Ui.Controls.FluentWindow
{
    public SingleValuePromptWindow(string title, string prompt, string? initialValue = null)
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        ValueTextBox.Text = initialValue ?? string.Empty;
        ValueTextBox.SelectAll();
        ValueTextBox.Focus();
    }

    public string? Result { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = ValueTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
