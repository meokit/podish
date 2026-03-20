using System.Globalization;

namespace Podish.Core;

public static class ContainerMemoryLimits
{
    public const long MinimumMemoryQuotaBytes = 32L * 1024 * 1024;

    public static bool TryParseMemoryQuotaBytes(string raw, out long bytes, out string error)
    {
        bytes = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "memory limit is required";
            return false;
        }

        var trimmed = raw.Trim();
        var digitEnd = 0;
        while (digitEnd < trimmed.Length && char.IsDigit(trimmed[digitEnd]))
            digitEnd++;

        if (digitEnd == 0)
        {
            error = "memory limit must start with a positive integer";
            return false;
        }

        if (!long.TryParse(trimmed[..digitEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            error = "memory limit must be a positive integer";
            return false;
        }

        var suffix = trimmed[digitEnd..].Trim().ToLowerInvariant();
        var multiplier = suffix switch
        {
            "" or "b" => 1L,
            "k" or "kb" or "ki" or "kib" => 1024L,
            "m" or "mb" or "mi" or "mib" => 1024L * 1024,
            "g" or "gb" or "gi" or "gib" => 1024L * 1024 * 1024,
            _ => 0L
        };

        if (multiplier == 0)
        {
            error = $"unsupported memory size suffix: {suffix}";
            return false;
        }

        try
        {
            bytes = checked(value * multiplier);
        }
        catch (OverflowException)
        {
            error = "memory limit is too large";
            return false;
        }

        if (bytes < MinimumMemoryQuotaBytes)
        {
            bytes = 0;
            error = $"memory limit must be at least {FormatBytes(MinimumMemoryQuotaBytes)}";
            return false;
        }

        return true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes % (1024L * 1024 * 1024) == 0)
            return $"{bytes / (1024L * 1024 * 1024)}G";
        if (bytes % (1024L * 1024) == 0)
            return $"{bytes / (1024L * 1024)}M";
        if (bytes % 1024 == 0)
            return $"{bytes / 1024}K";
        return $"{bytes}B";
    }
}
