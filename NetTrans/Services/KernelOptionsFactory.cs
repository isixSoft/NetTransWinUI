using System.Globalization;
using NetTrans.Kernel;
using NetTrans.Models;

namespace NetTrans.Services;

/// <summary>
/// Maps the persisted 设置 sheet values (AppSettings) onto the download
/// kernel's options. Pure mapping logic -- no UI/Windows dependencies, so
/// headless test projects can link this file directly on any platform.
/// Shell-only fields (theme, boss key, island visibility, ...) are deliberately
/// not mapped; the kernel never sees them.
/// </summary>
public static class KernelOptionsFactory
{
    /// <summary>
    /// Builds kernel options from the given settings. Null settings (or any
    /// unparseable value) fall back to the kernel's own defaults.
    /// </summary>
    public static KernelOptions Create(AppSettings? settings)
    {
        if (settings is null) return new KernelOptions();

        var options = new KernelOptions
        {
            MaxSimultaneous = Math.Max(1, settings.MaxSimultaneousDownloads),
            GlobalSpeedLimit = ParseSpeedLimit(settings.GlobalSpeedLimit),
            DefaultSavePath = string.IsNullOrWhiteSpace(settings.DefaultSavePath)
                ? null
                : settings.DefaultSavePath.Trim(),
            FoldersByCategory = settings.FoldersByCategory,
            UncappedAtNight = settings.UncappedAtNight,
            NotifyOnComplete = settings.NotifyOnCompletion,
            OffPeakStart = settings.OffPeakStart,
            OffPeakEnd = settings.OffPeakEnd,
            // 本地签名库跟着下载根目录走（.nettrans-checksums.json）；
            // 未配置下载目录则不落盘，只保留进程内历史。
            HashDatabasePath = string.IsNullOrWhiteSpace(settings.DefaultSavePath)
                ? null
                : Path.Combine(settings.DefaultSavePath.Trim(), ".nettrans-checksums.json"),
        };
        options.RetryCount = ParseLeadingInt(settings.RetryPolicy, options.RetryCount);
        return options;
    }

    /// <summary>
    /// Parses the speed-cap dropdown text ("不限", "512 KB/s", "1.5 MB/s", ...)
    /// into bytes per second. Units are binary (KB = 1024, MB = 1024²,
    /// GB = 1024³). "不限", empty or unrecognizable text returns null
    /// (unlimited).
    /// </summary>
    private static long? ParseSpeedLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (string.Equals(trimmed, "不限", StringComparison.OrdinalIgnoreCase)) return null;

        // Leading number (integer or fractional), then the unit with optional spaces.
        int i = 0;
        while (i < trimmed.Length && (char.IsDigit(trimmed[i]) || trimmed[i] == '.')) i++;
        if (i == 0 || i == trimmed.Length) return null;

        if (!double.TryParse(trimmed[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        double multiplier = trimmed[i..].Trim().ToLowerInvariant() switch
        {
            "b/s" => 1,
            "kb/s" => 1024,
            "mb/s" => 1024 * 1024,
            "gb/s" => 1024 * 1024 * 1024,
            _ => 0,
        };
        if (multiplier == 0) return null;

        double bytes = value * multiplier;
        if (double.IsInfinity(bytes) || bytes >= long.MaxValue) return long.MaxValue;
        return (long)Math.Round(bytes);
    }

    /// <summary>Parses a leading integer ("3 次" → 3); unparseable → fallback.</summary>
    private static int ParseLeadingInt(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        var trimmed = text.TrimStart();
        int i = 0;
        while (i < trimmed.Length && char.IsDigit(trimmed[i])) i++;
        if (i == 0) return fallback;

        return int.TryParse(trimmed[..i], out var value) ? value : fallback;
    }
}
