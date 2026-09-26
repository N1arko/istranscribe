using IsTranscribe.Transcription.Local.Models;
using Xunit;

namespace IsTranscribe.Transcription.Local.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// </summary>
public sealed class WhisperModelCatalogTests
{
    [Fact]
    public void EmbeddedCatalogPinsReviewedMultilingualPayloads()
    {
        var catalog = WhisperModelCatalog.LoadEmbedded();

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal("2", catalog.CatalogVersion);
        Assert.Equal("isTranscribe.whisper.multilingual", catalog.CatalogId);
        Assert.Equal("5359861c739e955e79d9a303bcbc70fb988958b1", catalog.UpstreamRevision);
        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp", catalog.SourceRepository.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://github.com/openai/whisper", catalog.ModelOriginRepository.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("MIT", catalog.LicenseSpdx);
        Assert.Equal("whisper.cpp.ggml-f16.v1", catalog.Format.Token);
        Assert.Equal(0x67676d6cU, catalog.Format.MagicLittleEndian);
        Assert.Equal(new Version(1, 9, 1), catalog.Format.MinimumRuntimeVersion);

        AssertModel(Assert.Single(catalog.Models), "large-v3-turbo", "accurate",
            "ggml-large-v3-turbo.bin", 1624555275,
            "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
            4000000000, recommended: true);
        Assert.Equal("large-v3-turbo", catalog.RecommendedModel.Id);
        Assert.Same(catalog.RecommendedModel, catalog.GetRequired("large-v3-turbo"));
    }

    [Fact]
    public void DefaultModelRootIsPersistentAndOutsideTheInstallDirectory()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(WhisperModelStorageLayout.GetDefaultRootPath);
            return;
        }

        var root = WhisperModelStorageLayout.GetDefaultRootPath();

        Assert.True(Path.IsPathFullyQualified(root));
        Assert.EndsWith(Path.Combine("isTranscribe", "models"), root, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                root,
                StringComparison.OrdinalIgnoreCase);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(
                Path.Combine("Library", "Application Support", "isTranscribe", "models"),
                root,
                StringComparison.Ordinal);
        }
    }

    private static void AssertModel(
        WhisperModelDescriptor model,
        string id,
        string preset,
        string fileName,
        long size,
        string sha256,
        long expectedMemory,
        bool recommended)
    {
        Assert.Equal(id, model.Id);
        Assert.Equal(preset, model.UiPreset);
        Assert.Equal(fileName, model.FileName);
        Assert.Equal(size, model.DownloadSizeBytes);
        Assert.Equal(sha256, model.Sha256);
        Assert.Equal(expectedMemory, model.ExpectedMemoryBytes);
        Assert.Equal(recommended, model.Recommended);
        Assert.True(model.Multilingual);
        Assert.Equal("MIT", model.LicenseSpdx);
        Assert.Contains(model.UpstreamRevision, model.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.StartsWith("https://", model.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
    }
}
