using System.Security.Cryptography;

namespace IsTranscribe.Transcription.Local.Models;

// @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization
public sealed class DiarizationAssets(string root)
{
    public const string RuntimeVersion = "sherpa-onnx/1.12.14";
    public const string ParametersVersion = "speaker-clustering/v1";
    public static IReadOnlyList<DiarizationAsset> Manifest { get; } =
    [
        new("segmentation.onnx", 5992913,
            "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
            "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx", "MIT"),
        new("embedding.onnx", 26534365,
            "5ef208a9da1453335308a6b6f4e6dfbd7e183a38b604de0a57664f45d257fe94",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34.onnx", "Apache-2.0")
    ];
    public string Root { get; } = Path.GetFullPath(root);
    public string SegmentationPath => Path.Combine(Root, Manifest[0].FileName);
    public string EmbeddingPath => Path.Combine(Root, Manifest[1].FileName);
    public long DownloadBytes => Manifest.Sum(static asset => asset.SizeBytes);

    public async ValueTask<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        foreach (var asset in Manifest)
            if (!await VerifyFileAsync(Path.Combine(Root, asset.FileName), asset, cancellationToken).ConfigureAwait(false))
                return false;
        return true;
    }

    // Acquisition is invoked only by an explicit setup action. Inference never downloads.
    public async ValueTask InstallAsync(HttpClient http, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        foreach (var asset in Manifest)
        {
            var path = Path.Combine(Root, asset.FileName);
            if (await VerifyFileAsync(path, asset, cancellationToken).ConfigureAwait(false)) continue;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > asset.SizeBytes) throw new InvalidDataException("diarization_asset_size_invalid");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                if (!await VerifyFileAsync(temporary, asset, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("diarization_asset_hash_invalid");
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static async ValueTask<bool> VerifyFileAsync(string path, DiarizationAsset asset, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length != asset.SizeBytes) return false;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == asset.Sha256;
    }
}

public sealed record DiarizationAsset(string FileName, long SizeBytes, string Sha256, string Url, string License);
