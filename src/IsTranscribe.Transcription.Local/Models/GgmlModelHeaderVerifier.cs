using System.Buffers.Binary;

namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Preliminary format check. Successful magic validation is never sufficient for model activation;
/// <see cref="IWhisperModelActivationVerifier"/> performs the mandatory full native load/ABI gate.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
/// </remarks>
public sealed class GgmlModelHeaderVerifier
{
    public async ValueTask VerifyAsync(
        string payloadPath,
        WhisperModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentNullException.ThrowIfNull(descriptor);

        var header = new byte[sizeof(uint)];
        try
        {
            await using var stream = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: header.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var read = await stream.ReadAtLeastAsync(
                header,
                header.Length,
                throwOnEndOfStream: false,
                cancellationToken).ConfigureAwait(false);
            if (read != header.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(header) != descriptor.Format.MagicLittleEndian)
            {
                throw new ModelStoreException(
                    ModelStoreError.PayloadFormatMismatch,
                    "The downloaded model does not match the app-owned Whisper GGML format.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ModelStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageWriteFailed,
                "The downloaded model could not be read for preliminary format validation.",
                exception);
        }
    }
}
