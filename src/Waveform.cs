using System.Diagnostics;
using System.Globalization;

namespace CompressForDiscord;

/// <summary>
/// One audio track's waveform, filled in as it's measured. The finest level holds the loudest sample in each
/// hundredth of a second; each level above holds the loudest of two below it, so drawing any zoom reads only a
/// sample or two per pixel (like a video editor's waveform levels of detail). Measured in 30-second chunks: the part
/// on screen first, then outwards from it, so a long recording's waveform pops in where you're looking.
/// The loader thread writes while the window reads; a stale read only means a chunk shows a moment late.
/// </summary>
sealed class Waveform
{
    /// <summary>Finest slices a second (10 ms each)</summary>
    public const int Rate = 100;
    const int SampleRate = 4000, PerSlice = SampleRate / Rate;
    const int ChunkSeconds = 30, ChunkSlices = ChunkSeconds * Rate;
    /// <summary>Longest stretch decoded in one go: shorter means the loader follows the view sooner</summary>
    const int MaxRunChunks = 20;

    // levels[0] is the finest; values are 0..65535 for 0..1
    readonly ushort[][] levels;
    readonly bool[] known;
    readonly int slices;
    int version, knownCount;

    public double Duration { get; }
    /// <summary>Goes up each time a chunk is filled in, so the window knows to redraw</summary>
    public int Version => Volatile.Read(ref version);
    /// <summary>Loudest sample measured so far, 0..1</summary>
    public float Loudest { get; private set; }
    /// <summary>Every chunk is measured (or the track couldn't be read)</summary>
    public bool Complete => Failed || Volatile.Read(ref knownCount) == known.Length;
    /// <summary>ffmpeg couldn't read this track at all</summary>
    public bool Failed { get; private set; }

    public Waveform(double duration)
    {
        Duration = duration;
        slices = Math.Max(1, (int)Math.Ceiling(duration * Rate));
        known = new bool[(slices + ChunkSlices - 1) / ChunkSlices];
        var list = new List<ushort[]>();
        for (var n = slices; ; n = (n + 1) / 2)
        {
            list.Add(new ushort[n]);
            if (n == 1) break;
        }
        levels = [.. list];
    }

    /// <summary>
    /// The loudest sample from t0 to t1 (0..1), read from the level where that's a sample or two.
    /// Measured = false while none of it has been measured yet.
    /// </summary>
    public float Max(double t0, double t1, out bool measured)
    {
        var s0 = t0 * Rate;
        var s1 = Math.Max(t1 * Rate, s0 + 1e-9);
        var level = Math.Clamp((int)Math.Log2(Math.Max(1, s1 - s0)), 0, levels.Length - 1);
        var a = levels[level];
        var scale = 1 << level;
        var i0 = Math.Clamp((int)(s0 / scale), 0, a.Length - 1);
        var i1 = Math.Clamp((int)Math.Ceiling(s1 / scale), i0 + 1, a.Length);
        ushort peak = 0;
        for (var i = i0; i < i1; i++) if (a[i] > peak) peak = a[i];
        int c0 = Math.Clamp((int)(s0 / ChunkSlices), 0, known.Length - 1), c1 = Math.Clamp((int)((s1 - 1e-6) / ChunkSlices), c0, known.Length - 1);
        measured = false;
        for (var c = c0; c <= c1 && !measured; c++) measured = known[c];
        return peak / 65535f;
    }

    /// <summary>
    /// Measures the whole track, the chunks in view first. View gives the part of the video on screen;
    /// it's checked again after every chunk, so scrolling somewhere new is filled in next.
    /// </summary>
    public async Task Load(string path, int track, Func<(double From, double To)> view, CancellationToken ct)
    {
        var buf = new byte[PerSlice * 2 * 256];
        var clock = Stopwatch.StartNew();
        var runs = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Next(view()) is not { } first)
            {
                AppLog.Info($"Waveform of audio track {track + 1} measured in {clock.Elapsed.TotalSeconds:0.0}s ({runs} runs)");
                return;
            }
            runs++;
            var run = 1;
            while (run < MaxRunChunks && first + run < known.Length && !known[first + run]) run++;
            var start = first * ChunkSeconds;
            var psi = new ProcessStartInfo(Ff.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-ss", start.ToString(CultureInfo.InvariantCulture), "-i", path,
                "-map", $"0:a:{track}", "-t", (run * ChunkSeconds).ToString(CultureInfo.InvariantCulture), "-vn", "-sn",
                "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-" }) psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var stdout = p.StandardOutput.BaseStream;
            long got = 0; // bytes read in this run
            var slice = first * ChunkSlices; // slice being filled
            var done = first; // next chunk of the run to finish
            int peak = 0, inSlice = 0, leftover = 0;
            var moved = false;
            try
            {
                while (!moved)
                {
                    int n;
                    try { n = await stdout.ReadAsync(buf.AsMemory(leftover), ct).ConfigureAwait(false); }
                    catch (Exception) when (!ct.IsCancellationRequested) { break; }
                    if (n == 0) break;
                    got += n;
                    n += leftover;
                    var i = 0;
                    for (; i + 1 < n && !moved; i += 2)
                    {
                        var v = Math.Abs((int)BitConverter.ToInt16(buf, i));
                        if (v > peak) peak = v;
                        if (++inSlice < PerSlice) continue;
                        // A slice's worth of samples: store it
                        if (slice < slices) levels[0][slice] = (ushort)Math.Min(65535, peak * 2);
                        (peak, inSlice) = (0, 0);
                        slice++;
                        if (slice % ChunkSlices != 0 || done >= first + run) continue;
                        Finish(done++);
                        // Something in view still needs measuring and this run won't reach it next: go there instead
                        moved = done < first + run && MissingInView(view()) is { } want && want != done;
                    }
                    // An odd byte waits for its other half
                    leftover = n - i;
                    if (leftover > 0) buf[0] = buf[i];
                }
            }
            finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
            ct.ThrowIfCancellationRequested();
            if (moved) continue;
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (got == 0 && p.ExitCode != 0 && knownCount == 0)
            {
                // Nothing at all from the first try: this track can't be read. Mark it so the window stops waiting.
                AppLog.Warn($"Waveform of audio track {track + 1} failed: {(await err).Trim()}");
                Failed = true;
                Interlocked.Increment(ref version);
                return;
            }
            // The stream ended: the rest of the run is past the end of this track's sound (silent), or the slice
            // still being filled is its last
            if (peak > 0 && slice < slices) levels[0][slice] = (ushort)Math.Min(65535, peak * 2);
            while (done < first + run) Finish(done++);
        }
    }

    /// <summary>Which chunk to measure from next: the first one missing in view, otherwise the nearest, ahead before behind</summary>
    int? Next((double From, double To) view)
    {
        if (MissingInView(view) is { } c) return c;
        var (c0, c1) = Chunks(view);
        for (int ahead = c1 + 1, behind = c0 - 1; ahead < known.Length || behind >= 0; ahead++, behind--)
        {
            if (ahead < known.Length && !known[ahead]) return ahead;
            if (behind >= 0 && !known[behind])
            {
                // Measured forwards, so start a run's length back (or at the last measured chunk) and come up to it
                var start = behind;
                while (start > 0 && behind - start + 1 < MaxRunChunks && !known[start - 1]) start--;
                return start;
            }
        }
        return null;
    }

    /// <summary>The first and last chunk on screen</summary>
    (int, int) Chunks((double From, double To) view)
    {
        var (from, to) = view.To > view.From ? view : (0, Duration);
        var c0 = Math.Clamp((int)(from / ChunkSeconds), 0, known.Length - 1);
        return (c0, Math.Clamp((int)(to / ChunkSeconds), c0, known.Length - 1));
    }

    int? MissingInView((double From, double To) view)
    {
        var (c0, c1) = Chunks(view);
        for (var c = c0; c <= c1; c++) if (!known[c]) return c;
        return null;
    }

    /// <summary>A chunk's finest slices are in: build the levels above them and mark it measured</summary>
    void Finish(int chunk)
    {
        if (chunk >= known.Length || known[chunk]) return;
        int lo = chunk * ChunkSlices, hi = Math.Min(slices, lo + ChunkSlices);
        ushort loud = 0;
        for (var i = lo; i < hi; i++) if (levels[0][i] > loud) loud = levels[0][i];
        for (var l = 1; l < levels.Length; l++)
        {
            var (below, here) = (levels[l - 1], levels[l]);
            lo >>= 1;
            hi = Math.Min(here.Length, (hi + 1) >> 1);
            for (var i = lo; i < hi; i++)
                here[i] = Math.Max(below[2 * i], 2 * i + 1 < below.Length ? below[2 * i + 1] : (ushort)0);
        }
        Loudest = Math.Max(Loudest, loud / 65535f);
        known[chunk] = true;
        Interlocked.Increment(ref knownCount);
        Interlocked.Increment(ref version);
    }
}
