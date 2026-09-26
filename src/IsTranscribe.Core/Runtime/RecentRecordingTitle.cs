namespace IsTranscribe.Core.Runtime;

/// <summary>
/// Shared validation contract for a user-owned recent-recording title.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#recent-recordings
/// </remarks>
public static class RecentRecordingTitle
{
    public const int MaxLength = 120;

    public static string Normalize(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = title.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A recording title is required.", nameof(title));
        }

        if (normalized.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                $"A recording title cannot exceed {MaxLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A recording title cannot contain control characters.",
                nameof(title));
        }

        return normalized;
    }
}
