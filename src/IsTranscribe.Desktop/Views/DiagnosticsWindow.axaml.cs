using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Native clipboard/export bridge for explicit diagnostic actions.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public sealed partial class DiagnosticsWindow : Window
{
    private DiagnosticsViewModel? _viewModel;

    public DiagnosticsWindow()
    {
        InitializeComponent();
        AccessibilityLiveRegion.ApplyPolite(DiagnosticsStatusLiveRegion);
        AccessibilityLiveRegion.ApplyAssertive(DiagnosticsErrorLiveRegion);
    }

    public void Bind(DiagnosticsViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            Unsubscribe(_viewModel);
        }

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CopyRequested += ViewModel_OnCopyRequested;
        _viewModel.ExportRequested += ViewModel_OnExportRequested;
        _viewModel.CloseRequested += ViewModel_OnCloseRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            Unsubscribe(_viewModel);
        }

        base.OnClosed(e);
    }

    private async void ViewModel_OnCopyRequested(object? sender, EventArgs args)
    {
        if (_viewModel is null || Clipboard is null)
        {
            _viewModel?.MarkActionFailed();
            return;
        }

        try
        {
            await Clipboard.SetTextAsync(_viewModel.SummaryText);
            _viewModel.MarkCopied();
        }
        catch
        {
            _viewModel.MarkActionFailed();
        }
    }

    private async void ViewModel_OnExportRequested(object? sender, EventArgs args)
    {
        if (_viewModel is null || !StorageProvider.CanSave)
        {
            _viewModel?.MarkActionFailed();
            return;
        }

        try
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"isTranscribe-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmm}.txt",
                FileTypeChoices =
                [
                    new FilePickerFileType(_viewModel.TextFileTypeLabel) { Patterns = ["*.txt"] }
                ]
            });
            if (destination is null)
            {
                return;
            }

            await using var stream = await destination.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(_viewModel.SummaryText);
            await writer.FlushAsync();
            _viewModel.MarkExported();
        }
        catch
        {
            _viewModel.MarkActionFailed();
        }
    }

    private void ViewModel_OnCloseRequested(object? sender, EventArgs args) => Close();

    private void Unsubscribe(DiagnosticsViewModel viewModel)
    {
        viewModel.CopyRequested -= ViewModel_OnCopyRequested;
        viewModel.ExportRequested -= ViewModel_OnExportRequested;
        viewModel.CloseRequested -= ViewModel_OnCloseRequested;
    }
}
