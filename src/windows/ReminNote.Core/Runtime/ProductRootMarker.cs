using System.Text.Json;

namespace ReminNote.Core.Runtime;

/// <summary>
/// Identifies a self-contained ReminNote package root. Development checkouts
/// continue to use the .git/ReminNote.sln markers; packaged roots use this
/// small, non-secret marker instead of shipping repository metadata.
/// </summary>
public static class ProductRootMarker
{
    public const string FileName = "ReminNote.runtime.json";

    public const int FormatVersion = 1;

    public static bool IsValid(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var markerPath = Path.Combine(Path.GetFullPath(root), FileName);
            if (!File.Exists(markerPath))
            {
                return false;
            }

            var markerInfo = new FileInfo(markerPath);
            if (markerInfo.Length is <= 0 or > 8 * 1024)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            var marker = document.RootElement;
            if (marker.ValueKind != JsonValueKind.Object ||
                !marker.TryGetProperty("product", out var product) ||
                product.ValueKind != JsonValueKind.String ||
                !string.Equals(product.GetString(), "ReminNote", StringComparison.Ordinal) ||
                !marker.TryGetProperty("formatVersion", out var formatVersion) ||
                formatVersion.ValueKind != JsonValueKind.Number ||
                !formatVersion.TryGetInt32(out var parsedFormatVersion))
            {
                return false;
            }

            return parsedFormatVersion == FormatVersion;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            JsonException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }
}
