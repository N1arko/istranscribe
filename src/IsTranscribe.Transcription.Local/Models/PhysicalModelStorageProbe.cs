namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Physical write/free-space probe for app-owned model storage.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download</remarks>
public sealed class PhysicalModelStorageProbe : IModelStorageProbe
{
    public async ValueTask EnsureWritableAsync(string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Directory.CreateDirectory(directoryPath);
            var probePath = Path.Combine(directoryPath, $".write-probe-{Guid.NewGuid():N}");
            try
            {
                await using var stream = new FileStream(
                    probePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(new byte[] { 0x01 }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(probePath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageNotWritable,
                "The model storage directory is not writable.",
                exception);
        }
    }

    public ValueTask<long> GetAvailableFreeSpaceAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("The storage volume could not be resolved.");
            }

            return ValueTask.FromResult(new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelStoreException(
                ModelStoreError.StorageNotWritable,
                "The model storage volume could not be inspected.",
                exception);
        }
    }
}
