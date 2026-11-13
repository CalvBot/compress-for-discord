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
    bool CappedBySource,
    /// <summary>actual/predicted size ratio learned from past encodes; bitrate is divided by it</summary>
    double Calibration,
    double EstimateBytes);

static class Planner
{
    public const double MB = 1024 * 1024;
    /// <summary>Discord's upload limits: free, Nitro Basic, Nitro (1 GB)</summary>
    public static readonly double[] LimitPresets = [10, 50, 1024];
    public static readonly int[] Resolutions = [2160, 1440, 1080, 900, 720, 540];
    public static readonly int[] FrameRates = [60, 48, 30, 24];
    const int MinVideoKbps = 80;

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
        var videoKbps = (int)Math.Floor(totalKbps - audioKbps);

        // Never spend more bits than the source has; that just makes a bigger file with no quality gain
        var srcVideoKbps = (p.VideoBitrate > 0 ? p.VideoBitrate : Math.Max(0, p.Bitrate - audioKbps * 1000)) / 1000.0;
        var cappedBySource = srcVideoKbps > 0 && videoKbps > srcVideoKbps;
        if (cappedBySource) videoKbps = (int)Math.Ceiling(srcVideoKbps);

        // Manual choices only ever lower the resolution or frame rate
        var shortSide = Math.Min(p.Width, p.Height);
        int? scale = resolution < shortSide ? resolution : null;
        int? fps = frameRate < p.Fps - 0.5 ? frameRate : null;

        return new Plan(
            targetBytes,
            videoKbps,
            audioKbps,
            scale,
            fps,
            videoKbps >= MinVideoKbps,
            budgetKbits / cal / (MinVideoKbps + (hasAudio ? 64 : 0)),
            scale ?? shortSide,
            cappedBySource,
            cal,
            Math.Min(targetBytes, (videoKbps + audioKbps) * 125.0 * duration * (cappedBySource ? 1 : cal)));
    }

    public static string FormatBytes(double b) =>
        b >= 1024 * MB ? $"{Math.Round(b / 1024 / MB, 2):0.##} GB"
        : b >= MB ? $"{b / MB:0.#} MB"
        : $"{Math.Ceiling(b / 1024)} KB";

    public static string FormatTime(double sec, bool precise = true)
    {
        if (!double.IsFinite(sec) || sec < 0) sec = 0;
        var h = (int)(sec / 3600);
        var m = (int)(sec % 3600 / 60);
        var s = precise ? (sec % 60).ToString("00.0") : ((int)(sec % 60)).ToString("00");
        return h > 0 ? $"{h}:{m:00}:{s}" : $"{m}:{s}";
    }
}
