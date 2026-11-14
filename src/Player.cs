using System.Diagnostics;
using System.Drawing.Imaging;
using System.Media;
using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>
/// Lightweight preview playback: ffmpeg decodes raw frames into a pipe, and another mixes the chosen audio
/// tracks into one that plays as it's made; both are driven by one clock.
/// </summary>
static class Player
{
    /// <summary>Plays [from, to) at this many frames a second. onFrame gets each frame and its time; returns when done or cancelled.</summary>
    public static async Task Play(string path, double from, double to, List<TrackMix> tracks, TrackMix? master, Size box, double srcW, double srcH, double fps,
        Action<Bitmap, double> onFrame, CancellationToken ct)
    {
        var span = to - from;
        if (span <= 0.02) return;

        // Fit the frame inside the preview box, even dimensions
        var scale = Math.Min(box.Width / srcW, box.Height / srcH);
        int w = Math.Max(2, (int)(srcW * scale) & ~1), h = Math.Max(2, (int)(srcH * scale) & ~1);
        if (fps <= 0) fps = 30;

        static Process Start(IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(Ff.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync();
            return p;
        }

        // The sound, mixed exactly like the encode will mix it, played as it comes rather than once it's all made
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? sound = null;
        AudioOut? output = null;
        Task? pump = null;
        Process? p = null;
        try
        {
            if (Ff.AudioGraph(tracks, from, master) is { } graph && AudioOut.Open() is { } opened)
            {
                output = opened;
                sound = Start(["-hide_banner", "-loglevel", "error", "-ss", Ff.F3(from), "-t", Ff.F3(span), "-i", path,
                    "-filter_complex", graph, "-map", "[aout]", "-ac", "2", "-ar", "48000", "-f", "s16le", "-"]);
                var pcm = sound.StandardOutput.BaseStream;
                pump = Task.Run(() => opened.Pump(pcm, heard, pumpCts.Token));
            }
            p = Start(["-hide_banner", "-loglevel", "error", "-ss", Ff.F3(from), "-t", Ff.F3(span), "-i", path, "-an", "-sn",
                "-vf", $"fps={Ff.F3(fps)},scale={w}:{h}", "-pix_fmt", "bgra", "-f", "rawvideo", "-"]);
            var video = p;
            using var reg = ct.Register(() => { try { video.Kill(true); } catch { } });

            var stdout = p.StandardOutput.BaseStream;
            var buf = new byte[w * h * 4];
            var clock = new Stopwatch();
            var shown = 0.0;
            for (var i = 0; ; i++)
            {
                if (!await ReadFull(stdout, buf, ct)) break;
                if (i == 0)
                {
                    // First frame is decoded: once the sound has a moment queued too, start them and the clock together
                    if (pump != null) await Task.WhenAny(heard.Task, Task.Delay(1500, ct));
                    ct.ThrowIfCancellationRequested();
                    output?.Start();
                    clock.Start();
                }
                var due = i / fps;
                var wait = due - clock.Elapsed.TotalSeconds;
                // Running behind (decoding slower than real time): drop this frame, but still show one every
                // tenth of a second so the picture keeps moving instead of freezing while the sound plays on
                if (wait < -0.25 && clock.Elapsed.TotalSeconds - shown < 0.1) continue;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                shown = clock.Elapsed.TotalSeconds;
                onFrame(ToBitmap(buf, w, h), from + due);
            }
            // Let the audio tail finish
            var rest = span - clock.Elapsed.TotalSeconds;
            if (rest > 0 && clock.IsRunning) await Task.Delay(TimeSpan.FromSeconds(Math.Min(rest, 1)), ct);
        }
        finally
        {
            // Stop the sound before letting its buffers go: the pump first, then the device
            pumpCts.Cancel();
            foreach (var proc in new[] { sound, p })
                try { if (proc is { HasExited: false }) proc.Kill(true); } catch { }
            if (pump != null) try { await pump; } catch { }
            output?.Dispose();
            sound?.Dispose();
            p?.Dispose();        }
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

    static SoundPlayer? chime;

    /// <summary>Plays the embedded "complete" sound</summary>
    public static void Chime()
    {
        // Loaded once and kept: Play returns at once while Windows still reads the sound from the player's memory
        if (chime == null)
        {
            using var wav = typeof(Player).Assembly.GetManifestResourceStream("complete.wav");
            if (wav == null) return;
            chime = new SoundPlayer(wav);
            chime.Load();
        }
        chime.Play();
    }
}
