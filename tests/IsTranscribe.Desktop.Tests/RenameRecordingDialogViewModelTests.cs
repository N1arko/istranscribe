using IsTranscribe.Desktop.ViewModels;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </remarks>
public sealed class RenameRecordingDialogViewModelTests
{
    [Fact]
    public void Save_requires_a_changed_valid_name_and_returns_the_trimmed_title()
    {
        using var viewModel = new RenameRecordingDialogViewModel(
            new FakeLocalizationService(),
            "Manual recording");
        RenameRecordingDialogResolvedEventArgs? result = null;
        viewModel.Resolved += (_, args) => result = args;

        Assert.False(viewModel.SaveCommand.CanExecute(null));
        viewModel.Name = "   ";
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        viewModel.Name = "  Weekly product sync  ";
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        viewModel.SaveCommand.Execute(null);

        Assert.NotNull(result);
        Assert.True(result.IsConfirmed);
        Assert.Equal("Weekly product sync", result.Name);
    }

    [Fact]
    public void Save_rejects_control_characters_and_titles_over_the_shared_limit()
    {
        using var viewModel = new RenameRecordingDialogViewModel(
            new FakeLocalizationService(),
            "Manual recording");

        viewModel.Name = "First\nSecond";
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        viewModel.Name = new string('x', viewModel.MaximumNameLength + 1);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }
}
