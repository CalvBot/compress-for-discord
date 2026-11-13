using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>
/// Lightweight preview playback: ffmpeg decodes raw frames into a pipe and the
/// chosen audio tracks are mixed to an in-memory WAV, both driven by one clock.
/// </summary>
static class Player
{
    /// <summary>Longest stretch played in one go (keeps the in-memory audio small)</summary>
    const double MaxSpan = 180;

    static string S(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Plays [from, to). onFrame gets each frame and its time; returns when done or cancelled.</summary>
    public static async Task Play(string path, double from, double to, List<TrackMix> tracks, Size box, double srcW, double srcH, double srcFps,
        Action<Bitmap, double> onFrame, CancellationToken ct)
    {
        to = Math.Min(to, from + MaxSpan);
        var span = to - from;
        if (span <= 0.02) return;

        // Fit the frame inside the preview box, even dimensions
        var scale = Math.Min(box.Width / srcW, box.Height / srcH);
        int w = Math.Max(2, (int)(srcW * scale) & ~1), h = Math.Max(2, (int)(srcH * scale) & ~1);
        var fps = Math.Min(srcFps > 0 ? srcFps : 30, 30);

        // Audio first: mixed exactly like the encode will mix it
        MemoryStream? wav = null;
        if (Ff.AudioGraph(tracks) is { } graph)
        {
            var (code, bytes, _) = await Ff.Run(Ff.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-ss", S(from), "-t", S(span), "-i", path,
                "-filter_complex", graph, "-map", "[aout]", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", "-"], ct);
            if (code == 0 && bytes.Length > 44) wav = new MemoryStream(bytes);
        }
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(Ff.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-ss", S(from), "-t", S(span), "-i", path, "-an", "-sn",
            "-vf", $"fps={S(fps)},scale={w}:{h}", "-pix_fmt", "bgra", "-f", "rawvideo", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        using var sound = wav != null ? new SoundPlayer(wav) : null;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });

        var stdout = p.StandardOutput.BaseStream;
        var frameBytes = w * h * 4;
        var buf = new byte[frameBytes];
        var clock = new Stopwatch();
        try
        {
            for (var i = 0; ; i++)
            {
                if (!await ReadFull(stdout, buf, ct)) break;
                if (i == 0)
                {
                    // First frame is decoded, so start sound and clock together
                    sound?.Load();
                    sound?.Play();
                    clock.Start();
                }
                var due = i / fps;
                var wait = due - clock.Elapsed.TotalSeconds;
                if (wait < -0.25) continue; // running behind, drop this frame
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                onFrame(ToBitmap(buf, w, h), from + due);
            }
            // Let the audio tail finish
            var rest = span - clock.Elapsed.TotalSeconds;
            if (rest > 0 && clock.IsRunning) await Task.Delay(TimeSpan.FromSeconds(Math.Min(rest, 1)), ct);
        }
        finally
        {
            sound?.Stop();
            try { if (!p.HasExited) p.Kill(true); } catch { }
        }
    }

    static async Task<bool> ReadFull(Stream s, byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            int n;
            try { n = await s.ReadAsync(buf.AsMemory(got), ct); }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (IOException) { return false; }
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    static Bitmap ToBitmap(byte[] buf, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        for (var y = 0; y < h; y++) Marshal.Copy(buf, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        bmp.UnlockBits(data);
        return bmp;
    }

    // ---------- done sound ----------

    /// <summary>How long the done sound lasts, so the app can wait for it before exiting</summary>
    public static readonly TimeSpan ChimeLength = TimeSpan.FromSeconds(1.5);

    /// <summary>Plays the embedded "complete" sound</summary>
    public static void Chime()
    {
        var wav = typeof(Player).Assembly.GetManifestResourceStream("complete.wav");
        if (wav != null) new SoundPlayer(wav).Play();
    }
}
