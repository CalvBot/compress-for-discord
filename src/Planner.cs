namespace CompressForDiscord;

sealed record Plan(
    long TargetBytes,
    int VideoKbps,
    int AudioKbps,
    int? ScaleShortSide,
    int? Fps,
    bool Feasible,
    /// <summary>longest clip that still fits at minimum quality</summary>
    double MaxDuration,
    int OutputShortSide,
    /// <summary>The picture was made smaller than asked because the bitrate is too low for that many pixels</summary>
    bool Shrunk,
    bool CappedBySource,
    /// <summary>actual/predicted size ratio learned from past encodes; bitrate is divided by it</summary>
    double Calibration,
    double EstimateBytes);

static class Planner
{
    public const double MB = 1024 * 1024;
    /// <summary>
    /// Discord's upload limits in MiB (what Discord means by MB): free (20 since Aug 2026, was 10), Nitro Basic
    /// (50), Nitro (1 GB since Sep 2026, was 500). The first is the default.
    /// </summary>
    public static readonly double[] LimitPresets = [20, 50, 1024];
    /// <summary>Output size as a percentage of the source, offered as chips; anything else is typed in</summary>
    public static readonly int[] ScaleChips = [100, 75, 50];
    public static readonly int[] FrameRates = [60, 48, 30, 24];
    const int MinVideoKbps = 80;
    /// <summary>
    /// The fewest bits per pixel (per frame) an encoder can be held to. Below it the encoder is already at its
    /// coarsest and ignores a lower bitrate, so the file comes out over the limit: measured on NVENC at 1440p60,
    /// 0.009 lands 2% over what's asked, 0.007 4% over, 0.0045 40% over and 0.003 130% over. Fewer pixels is the
    /// only way down from there (and looks better than the blocks it would otherwise be).
    /// </summary>
    const double MinBitsPerPixel = 0.009;
    /// <summary>The smallest picture the plan (or a retry) shrinks to</summary>
    public const int MinSide = 144;

    /// <param name="resolution">output short side, null = keep the source's</param>
    /// <param name="frameRate">null = keep the source's</param>
    /// <param name="calibration">median actual/predicted size ratio for this encoder (1 = no history), clamped so one odd encode can't swing the plan far</param>
    public static Plan Make(ProbeResult p, double duration, long limitBytes, bool hasAudio, double safetyMarginPercent, double calibration, int? resolution, int? frameRate)
    {
        var cal = Math.Clamp(calibration > 0 ? calibration : 1, 0.75, 1.3);
        var targetBytes = (long)Math.Floor(limitBytes * (1 - Math.Max(0, safetyMarginPercent) / 100));
        var budgetKbits = targetBytes * 8 * 0.98 / 1000; // ~2% mp4 container overhead
        var totalKbps = budgetKbits / Math.Max(duration, 0.1) / cal;

        var audioKbps = !hasAudio ? 0
            : totalKbps >= 1500 ? 160
            : totalKbps >= 600 ? 128
            : totalKbps >= 250 ? 96
            : 64;
        // Held to 200 Mbps: a short clip with a big limit and a source that doesn't say its bitrate would ask for gigabits
        var videoKbps = (int)Math.Floor(Math.Min(totalKbps - audioKbps, 200_000));

        // Never spend more bits than the source has; that just makes a bigger file with no quality gain
        var srcVideoKbps = (p.VideoBitrate > 0 ? p.VideoBitrate : Math.Max(0, p.Bitrate - audioKbps * 1000)) / 1000.0;
        var cappedBySource = srcVideoKbps > 0 && videoKbps > srcVideoKbps;
        if (cappedBySource) videoKbps = (int)Math.Ceiling(srcVideoKbps);

        // Manual choices only ever lower the resolution or frame rate
        var shortSide = Math.Min(p.Width, p.Height);
        int? scale = resolution < shortSide ? resolution : null;
        int? fps = frameRate < p.Fps - 0.5 ? frameRate : null;

        // Too few bits for that many pixels: a smaller picture, worked out now rather than found out by an attempt
        // that comes out over the limit. (Not when held to the source's own bitrate: that it can clearly be made at.)
        var shrunk = false;
        var side = scale ?? shortSide;
        double aspect = Math.Max(p.Width, p.Height) / (double)Math.Max(1, shortSide), rate = fps ?? (p.Fps > 0 ? p.Fps : 30);
        if (!cappedBySource && shortSide > 0 && videoKbps * 1000.0 / (side * side * aspect * rate) < MinBitsPerPixel)
        {
            var fits = Math.Max(MinSide, (int)Math.Sqrt(videoKbps * 1000.0 / (MinBitsPerPixel * aspect * rate)) & ~1);
            if (fits < side) (scale, shrunk) = (fits, true);
        }

        return new Plan(
            targetBytes,
            videoKbps,
            audioKbps,
            scale,
            fps,
            // A source that's already below the minimum (capped to its own rate) fits fine
            cappedBySource || videoKbps >= MinVideoKbps,
            budgetKbits / cal / (MinVideoKbps + (hasAudio ? 64 : 0)),
            scale ?? shortSide,
            shrunk,
            cappedBySource,
            cal,
            Math.Min(targetBytes, (videoKbps + audioKbps) * 125.0 * duration * (cappedBySource ? 1 : cal)));
    }

    public static string FormatBytes(double b) =>
        b >= 1024 * MB ? $"{Math.Round(b / 1024 / MB, 2):0.##} GB"
        : b >= MB ? $"{b / MB:0.#} MB"
        : $"{Math.Ceiling(b / 1024)} KB";

    public static string FormatRate(double kbps) => kbps >= 1000 ? $"{kbps / 1000:0.0} Mbps" : $"{kbps:0} kbps";

    public static string FormatTime(double sec, bool precise = true)
    {
        if (!double.IsFinite(sec) || sec < 0) sec = 0;
        var h = (int)(sec / 3600);
        var m = (int)(sec % 3600 / 60);
        // Hundredths, so a one-frame step shows up
        var s = precise ? (Math.Floor(sec % 60 * 100) / 100).ToString("00.00") : ((int)(sec % 60)).ToString("00");
        return h > 0 ? $"{h}:{m:00}:{s}" : $"{m}:{s}";
    }
}
