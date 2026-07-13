using System;
using System.Globalization;

namespace FileManager.Contracts.Primitives;

/// <summary>Formats a raw byte count as a short human-readable string (e.g. <c>1.5 GB</c>), matching
/// the binary-magnitude / decimal-label convention Windows Explorer uses (1 KB = 1024 B, labelled
/// KB/MB/GB/…). Negative counts — a net at-rest change can shrink a drive — keep a leading minus.
/// This is the one shared size formatter; nothing in the codebase formatted sizes before.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>Formats <paramref name="bytes"/> with an adaptive precision (fewer decimals as the
    /// number grows) so the string stays short: e.g. <c>0 B</c>, <c>512 B</c>, <c>1.50 KB</c>,
    /// <c>9.99 MB</c>, <c>15.3 GB</c>, <c>620 GB</c>, <c>2 TB</c>.</summary>
    public static string Format(long bytes)
    {
        if (bytes == 0)
            return "0 B";

        string sign = bytes < 0 ? "-" : "";
        // Use double for scaling; magnitudes are well within double's exact-integer range for the
        // unit boundaries that matter, and we only need display precision. Guard long.MinValue
        // (its magnitude overflows negation) by widening to double before the abs.
        double magnitude = Math.Abs((double)bytes);

        int unit = 0;
        while (magnitude >= 1024d && unit < Units.Length - 1)
        {
            magnitude /= 1024d;
            unit++;
        }

        // Bytes are whole; larger units get 0/1/2 decimals depending on size so we never show a
        // misleadingly precise "1.00 B" or a cramped "1024.00 KB".
        string number = unit == 0
            ? magnitude.ToString("0", CultureInfo.InvariantCulture)
            : magnitude switch
            {
                >= 100d => magnitude.ToString("0", CultureInfo.InvariantCulture),
                >= 10d => magnitude.ToString("0.#", CultureInfo.InvariantCulture),
                _ => magnitude.ToString("0.##", CultureInfo.InvariantCulture),
            };

        return $"{sign}{number} {Units[unit]}";
    }
}
