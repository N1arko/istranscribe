namespace IsTranscribe.Transcription.Local.Models;

/// <summary>
/// Physical file mutations used by crash-recoverable model removal.
/// </summary>
/// <remarks>@spec spec://modules/app/FEAT-016-local-whisper-transcription#models</remarks>
public sealed class PhysicalModelStoreFileMutator : IModelStoreFileMutator
{
    public bool FileExists(string path) => File.Exists(path);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite = false) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);
}
