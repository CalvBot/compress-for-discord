using System.Drawing.Imaging;

namespace CompressForDiscord;

/// <summary>
/// Sixteen small frames spread across a recent project's trimmed part, for the list in Settings. Made once and saved
/// beside the project as one wide JPEG; a project saved again is a new file, so it gets new ones.
/// </summary>
static class ProjectThumbs
{
    public const int Count = 16;
    const int FrameH = 108;

    /// <summary>The saved frames, or else new ones made from the video (slow: call off the UI thread). Null if there's no video to read.</summary>
    public static Bitmap[]? LoadOrMake(string path, Project p, CancellationToken ct)
    {
        var file = Projects.ThumbPath(path);
        try
        {
            if (File.Exists(file))
            {
                // Read into memory so the file isn't held open
                using var strip = new Bitmap(new MemoryStream(File.ReadAllBytes(file)));
                return Split(strip);
            }
        }
        catch (Exception ex) { AppLog.Info($"Couldn't read project thumbnails {file}: {ex.Message}"); }
        if (!File.Exists(p.File)) return null;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frames = Make(p, ct);
        if (frames == null || ct.IsCancellationRequested) return frames;
        try
        {
            // Only if the project is still there (it may have been removed meanwhile)
            if (File.Exists(path))
            {
                using var strip = new Bitmap(frames[0].Width * Count, frames[0].Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(strip))
                    for (var i = 0; i < Count; i++) g.DrawImage(frames[i], i * frames[0].Width, 0, frames[0].Width, frames[0].Height);
                strip.Save(file, ImageFormat.Jpeg);
            }
        }
        catch (Exception ex) { AppLog.Info($"Couldn't save project thumbnails {file}: {ex.Message}"); }
        AppLog.Info($"Project thumbnails for {Path.GetFileName(p.File)} in {watch.ElapsedMilliseconds} ms");
        return frames;
    }

    static Bitmap[] Split(Bitmap strip)
    {
        var w = strip.Width / Count;
        var frames = new Bitmap[Count];
        for (var i = 0; i < Count; i++) frames[i] = strip.Clone(new Rectangle(i * w, 0, w, strip.Height), PixelFormat.Format24bppRgb);
        return frames;
    }

    static Bitmap[]? Make(Project p, CancellationToken ct)
    {
        var (from, to) = p.End > p.Start ? (p.Start, p.End) : (0, Math.Max(p.Start, 1));
        double At(int i) => from + (i + 0.5) / Count * (to - from);
        var got = new Bitmap?[Count];
        using (var reader = FrameReader.Open(p.File, 30, gpu: false))
        {
            if (reader != null)
            {
                var n = reader.Native;
                var w = n.Height > 0 ? Math.Clamp((int)Math.Round(FrameH * (double)n.Width / n.Height / 2) * 2, 16, FrameH * 4) : FrameH * 16 / 9;
                // Far apart, the keyframe before each point is near enough and much quicker
                var exact = (to - from) / Count < 2;
                try
                {
                    for (var i = 0; i < Count && !ct.IsCancellationRequested; i++) got[i] = reader.Get(At(i), new Size(w, FrameH), exact, ct);
                }
                catch (Exception ex) { AppLog.Info($"Windows' decoder failed on {p.File} ({ex.Message}); using ffmpeg for the thumbnails"); }
            }
        }
        // Videos Windows can't read: ffmpeg, one frame at a time
        for (var i = 0; i < Count && !ct.IsCancellationRequested; i++)
            if (got[i] == null)
                try { got[i] = Ff.Frame(p.File, At(i), FrameH, ct, keyOnly: true).GetAwaiter().GetResult() is { } img ? ToBitmap(img) : null; }
                catch { }
        if (ct.IsCancellationRequested || got.FirstOrDefault(b => b != null) is not { } first)
        {
            foreach (var b in got) b?.Dispose();
            return null;
        }
        // All the same size; a frame that couldn't be read repeats the one before
        var size = first.Size;
        var frames = new Bitmap[Count];
        Bitmap? prev = null;
        for (var i = 0; i < Count; i++)
        {
            var src = got[i] ?? prev ?? first;
            frames[i] = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(frames[i])) g.DrawImage(src, 0, 0, size.Width, size.Height);
            if (got[i] != null) prev = got[i];
        }
        foreach (var b in got) b?.Dispose();
        return frames;
    }

    static Bitmap ToBitmap(Image img)
    {
        if (img is Bitmap b) return b;
        var copy = new Bitmap(img);
        img.Dispose();
        return copy;
    }
}
