using Avalonia.Controls;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Compact modal editor for a recording display title.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </remarks>
public sealed partial class RenameRecordingDialog : Window
{
    private RenameRecordingDialogViewModel? _viewModel;
    private bool _resolved;

    public RenameRecordingDialog() => InitializeComponent();

    public Task<string?> ShowAsync(Window owner, RenameRecordingDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Resolved += ViewModel_OnResolved;
        return ShowDialog<string?>(owner);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _resolved = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Resolved -= ViewModel_OnResolved;
        }

        base.OnClosed(e);
    }

    private void ViewModel_OnResolved(object? sender, RenameRecordingDialogResolvedEventArgs args)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        Close(args.IsConfirmed ? args.Name : null);
    }
}
