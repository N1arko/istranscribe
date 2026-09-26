using Avalonia.Controls;
using Avalonia.Platform.Storage;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Views;

/// <summary>
/// Native first-run surface and folder-picker bridge.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#first-run
/// </remarks>
public sealed partial class SetupWindow : Window
{
    private SetupViewModel? _viewModel;

    public SetupWindow()
    {
        InitializeComponent();
        AccessibilityLiveRegion.ApplyPolite(CapabilityLiveRegion);
        AccessibilityLiveRegion.ApplyAssertive(SetupErrorLiveRegion);
    }

    public void Bind(SetupViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.FolderPickerRequested -= ViewModel_OnFolderPickerRequested;
        }

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.FolderPickerRequested += ViewModel_OnFolderPickerRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FolderPickerRequested -= ViewModel_OnFolderPickerRequested;
        }

        base.OnClosed(e);
    }

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
