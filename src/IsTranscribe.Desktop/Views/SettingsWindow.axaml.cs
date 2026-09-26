using Avalonia.Controls;
using Avalonia.Platform.Storage;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Compact settings sheet and native folder-picker bridge.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#settings
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private SettingsViewModel? _viewModel;
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
        AccessibilityLiveRegion.ApplyPolite(MicrophoneCapabilityLiveRegion);
        AccessibilityLiveRegion.ApplyAssertive(SettingsErrorLiveRegion);
        AccessibilityLiveRegion.ApplyPolite(SettingsStatusLiveRegion);
    }

    public void Bind(SettingsViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.FolderPickerRequested -= ViewModel_OnFolderPickerRequested;
            _viewModel.CloseRequested -= ViewModel_OnCloseRequested;
        }

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.FolderPickerRequested += ViewModel_OnFolderPickerRequested;
        _viewModel.CloseRequested += ViewModel_OnCloseRequested;
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FolderPickerRequested -= ViewModel_OnFolderPickerRequested;
            _viewModel.CloseRequested -= ViewModel_OnCloseRequested;
        }

        base.OnClosed(e);
    }

    private void ViewModel_OnCloseRequested(object? sender, EventArgs args) => Hide();

    private async void ViewModel_OnFolderPickerRequested(object? sender, EventArgs args)
    {
        if (_viewModel is null || !StorageProvider.CanPickFolder)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false
        });
        var localPath = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            _viewModel.SetRecordingsFolder(localPath);
        }
    }
}
