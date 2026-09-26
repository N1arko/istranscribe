using Avalonia.Controls;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Modal confirmation boundary for recording deletion choices.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.hierarchy
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </remarks>
public sealed partial class ConfirmationDialog : Window
{
    private ConfirmationDialogViewModel? _viewModel;
    private bool _resolved;

    public ConfirmationDialog() => InitializeComponent();

    public Task<ConfirmationDialogResult> ShowAsync(Window owner, ConfirmationDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Resolved += ViewModel_OnResolved;
        return ShowDialog<ConfirmationDialogResult>(owner);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_resolved)
        {
            _resolved = true;
        }

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

    private void ViewModel_OnResolved(object? sender, ConfirmationDialogResult result)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        Close(result);
    }
}
