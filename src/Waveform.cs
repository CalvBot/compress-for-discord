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
    /// Measures these tracks' waveforms, the chunks in view first. One ffmpeg reads them all together, so a long
    /// recording's file is read once rather than once per track; if that can't work (one track won't decode),
    /// they're measured one at a time instead. View gives the part of the video on screen, and is checked again
    /// after every chunk so scrolling somewhere new is filled in next. While Paused says so (compressing), it waits.
    /// </summary>
    public static async Task LoadAll(string path, IReadOnlyList<(Waveform Wave, int Track)> tracks, Func<(double From, double To)> view,
        Func<bool> paused, CancellationToken ct)
    {
        if (tracks.Count == 0) return;
        if (await Load(path, tracks, view, paused, ct) || tracks.Count == 1) return;
        AppLog.Warn("Couldn't measure the audio tracks together; measuring them one at a time");
        await Task.WhenAll(tracks.Select(t => Load(path, [t], view, paused, ct)));
    }

    /// <returns>false if not a single sample could be read (a lone track is then marked Failed)</returns>
    static async Task<bool> Load(string path, IReadOnlyList<(Waveform Wave, int Track)> tracks, Func<(double From, double To)> view,
        Func<bool> paused, CancellationToken ct)
    {
        // Every wave here is measured over the same chunks, so the first one says what's still to do
        var lead = tracks[0].Wave;
        var waves = tracks.Select(t => t.Wave).ToArray();
        var count = waves.Length;
        var frame = 2 * count; // one sample of every track
        var buf = new byte[frame * PerSlice * 64];
        var peak = new int[count];
        var clock = Stopwatch.StartNew();
        var runs = 0;
        var names = string.Join(", ", tracks.Select(t => t.Track + 1));
        // Each track to mono at 4 kHz; several are padded (so one ending early doesn't end the others) and merged,
        // one track per channel
        var graph = string.Join(";", tracks.Select((t, i) =>
            $"[0:a:{t.Track}]aresample={SampleRate},aformat=sample_fmts=s16:channel_layouts=mono{(count > 1 ? ",apad" : "")}[w{i}]"));
        if (count > 1) graph += ";" + string.Concat(Enumerable.Range(0, count).Select(i => $"[w{i}]")) + $"amerge=inputs={count}[w]";
        var output = count > 1 ? "[w]" : "[w0]";

        async Task WhilePaused() { while (paused()) await Task.Delay(250, ct).ConfigureAwait(false); }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await WhilePaused();
            if (lead.Next(view()) is not { } first)
            {
                AppLog.Info($"Waveforms of audio track(s) {names} measured in {clock.Elapsed.TotalSeconds:0.0}s ({runs} runs)");
                return true;
            }
            runs++;
            var run = 1;
            while (run < MaxRunChunks && first + run < lead.known.Length && !lead.known[first + run]) run++;
            var start = first * ChunkSeconds;
            var psi = new ProcessStartInfo(Ff.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-ss", start.ToString(CultureInfo.InvariantCulture), "-i", path,
                "-filter_complex", graph, "-map", output, "-t", (run * ChunkSeconds).ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-" })
                psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var stdout = p.StandardOutput.BaseStream;
            long got = 0; // bytes read in this run
            var slice = first * ChunkSlices; // slice being filled
            var done = first; // next chunk of the run to finish
            int inSlice = 0, leftover = 0;
            Array.Clear(peak);
            var moved = false;
            void Store()
            {
                for (var c = 0; c < count; c++)
                    if (slice < waves[c].slices) waves[c].levels[0][slice] = (ushort)Math.Min(65535, peak[c] * 2);
            }
            try
            {
                while (!moved)
                {
                    // Stop reading as soon as asked (not just between chunks): ffmpeg then stops too, once its output backs up
                    await WhilePaused();
                    int n;
                    try { n = await stdout.ReadAsync(buf.AsMemory(leftover), ct).ConfigureAwait(false); }
                    catch (Exception) when (!ct.IsCancellationRequested) { break; }
                    if (n == 0) break;
                    got += n;
                    n += leftover;
                    var i = 0;
                    for (; i + frame <= n && !moved; i += frame)
                    {
                        for (var c = 0; c < count; c++)
                        {
                            var v = Math.Abs((int)BitConverter.ToInt16(buf, i + 2 * c));
                            if (v > peak[c]) peak[c] = v;
                        }
                        if (++inSlice < PerSlice) continue;
                        // A slice's worth of samples: store it
                        Store();
                        Array.Clear(peak);
                        inSlice = 0;
                        slice++;
                        if (slice % ChunkSlices != 0 || done >= first + run) continue;
                        foreach (var w in waves) w.Finish(done);
                        done++;
                        // Something in view still needs measuring and this run won't reach it next: go there instead
                        moved = done < first + run && lead.MissingInView(view()) is { } want && want != done;
                    }
                    // Part of a sample waits for the rest
                    leftover = n - i;
                    Array.Copy(buf, i, buf, 0, leftover);
                }
            }
            finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
            ct.ThrowIfCancellationRequested();
            if (moved) continue;
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (got == 0 && p.ExitCode != 0 && lead.knownCount == 0)
            {
                // Nothing at all from the first try: can't be read like this
                AppLog.Warn($"Waveform of audio track(s) {names} failed: {(await err).Trim()}");
                if (count > 1) return false;
                lead.Failed = true;
                Interlocked.Increment(ref lead.version);
                return true;
            }
            // The stream ended: the rest of the run is past the end of the sound (silent), or the slice still
            // being filled is the last
            if (inSlice > 0) Store();
            for (; done < first + run; done++) foreach (var w in waves) w.Finish(done);
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
