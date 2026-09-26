using System.Security.Cryptography;

namespace IsTranscribe.DetectionAcceptance;

/// <summary>
/// Hashes the complete runner output dependency closure used by live detection.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#verification
/// </remarks>
internal static class BuildFingerprint
{
    public static IReadOnlyDictionary<string, string> CreateDependencyManifest()
    {
        try
        {
            return Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
                .Where(static path =>
                    path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Name = Path.GetRelativePath(AppContext.BaseDirectory, path)
                        .Replace(Path.DirectorySeparatorChar, '/'),
                    Hash = TryHashFile(path)
                })
                .Where(static item => item.Hash is not null)
                .ToDictionary(
                    static item => item.Name,
                    static item => item.Hash!,
                    StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string? TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
