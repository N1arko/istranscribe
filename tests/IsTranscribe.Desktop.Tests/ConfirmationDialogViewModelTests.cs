using IsTranscribe.Desktop.Localization;
using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#switching
/// </remarks>
public sealed class ConfirmationDialogViewModelTests
{
    [Fact]
    public void Open_confirmation_rebuilds_every_visible_action_when_language_changes()
    {
        var strings = new FakeLocalizationService(includeLanguageInText: true);
        using var viewModel = new ConfirmationDialogViewModel(
            strings,
            "String.Confirmation.RemoveHistory.Title",
            "String.Confirmation.RemoveHistory.Description",
            "String.Confirmation.RemoveHistory.KeepFile",
            "String.Action.Cancel",
            "String.Confirmation.RemoveHistory.DeleteFile");

        Assert.Equal("ru:String.Confirmation.RemoveHistory.Title", viewModel.Title);
        Assert.Equal("ru:String.Confirmation.RemoveHistory.Description", viewModel.Description);
        Assert.Equal("ru:String.Confirmation.RemoveHistory.KeepFile", viewModel.PrimaryAction);
        Assert.Equal("ru:String.Action.Cancel", viewModel.CancelAction);
        Assert.Equal("ru:String.Confirmation.RemoveHistory.DeleteFile", viewModel.DestructiveAction);

        strings.SetLanguage(UiLanguage.English);

        Assert.Equal("en:String.Confirmation.RemoveHistory.Title", viewModel.Title);
        Assert.Equal("en:String.Confirmation.RemoveHistory.Description", viewModel.Description);
        Assert.Equal("en:String.Confirmation.RemoveHistory.KeepFile", viewModel.PrimaryAction);
        Assert.Equal("en:String.Action.Cancel", viewModel.CancelAction);
        Assert.Equal("en:String.Confirmation.RemoveHistory.DeleteFile", viewModel.DestructiveAction);
    }
}
