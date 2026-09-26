using System.Windows;
using IsTranscribe.Host.Persistence;

namespace IsTranscribe.App.Windows;

public partial class RunningProcessPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly EditableCollection<SelectableProcessItem> _items;

    public RunningProcessPickerWindow(IEnumerable<SelectableProcessItem> items)
    {
        InitializeComponent();
        _items = new EditableCollection<SelectableProcessItem>(items);
        ProcessItemsControl.ItemsSource = _items;
    }

    public IReadOnlyList<AppRuleRecord> SelectedRules { get; private set; } = Array.Empty<AppRuleRecord>();

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        SelectedRules = _items
            .Where(static item => item.IsSelected)
            .Select(static item => AppRuleRecord.Create(item.DisplayName, item.ProcessName))
            .ToArray();

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
