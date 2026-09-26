using NAudio.Wave;

namespace IsTranscribe.Host.Audio.Capture;

public static class WaveArtifactConcatenator
{
    public static void Concatenate(IEnumerable<string> sourcePaths, string destinationPath)
    {
        var paths = sourcePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
        {
            throw new InvalidOperationException("At least one wave artifact path is required.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        WaveFormat? outputFormat = null;
        WaveFileWriter? writer = null;

        try
        {
            foreach (var path in paths)
            {
                // @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
                _ = WaveArtifactRepair.TryRepair(path);
                using var reader = new WaveFileReader(path);
                outputFormat ??= reader.WaveFormat;
                if (!reader.WaveFormat.Equals(outputFormat))
                {
                    throw new InvalidOperationException("Wave artifacts must have matching formats to be concatenated.");
                }

                writer ??= new WaveFileWriter(destinationPath, outputFormat);
                reader.CopyTo(writer);
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }
}
