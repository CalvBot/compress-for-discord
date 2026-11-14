using System.Diagnostics;
using System.Globalization;

namespace CompressForDiscord;

/// <summary>
/// One audio track's waveform, filled in as it's measured. The finest level holds the loudest sample in each
/// millisecond (so zoomed all the way in, where a frame is ~50 px wide, it still has detail); each level above holds
/// the loudest of two below it, so drawing any zoom reads only a sample or two per pixel (like a video editor's
/// waveform levels of detail). Levels are stored as one byte each, the square root of the level, which is the scale
/// it's drawn at: a 65-minute recording takes ~8 MB a track. Measured in 10-second chunks: the part on screen first,
/// then outwards from it, so a long recording's waveform pops in where you're looking.
/// The loader threads write while the window reads; a stale read only means a chunk shows a moment late.
/// </summary>
sealed class Waveform
{
    /// <summary>Finest slices a second (1 ms each)</summary>
    public const int Rate = 1000;
    const int SampleRate = 8000, PerSlice = SampleRate / Rate;
    const int ChunkSeconds = 10, ChunkSlices = ChunkSeconds * Rate;
    /// <summary>Longest stretch decoded in one go: shorter means the loader follows the view sooner</summary>
    const int MaxRunChunks = 30;
    /// <summary>ffmpegs measuring at once, each its own stretch: decoding the sound is what takes the time, at one core each</summary>
    static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    // levels[0] is the finest; values are 0..255 for the square root of 0..1
    readonly byte[][] levels;
    readonly bool[] known;
    /// <summary>Chunks a worker has taken on and not finished yet (kept on the lead wave, under its gate)</summary>
    readonly bool[] claimed;
    readonly object gate = new();
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

    // A rough sketch, read straight from the file at spots spread over the recording (see AudioSketch), shown where
    // the measuring hasn't got to yet. Spot i is the sound around SketchTime(i).
    readonly byte[] sketch;
    readonly bool[] sketched;
    int sketchedCount;

    /// <summary>How many spots the sketch has room for (a power of two)</summary>
    public int SketchPoints => sketch.Length;
    public double SketchTime(int i) => (i + 0.5) * Duration / sketch.Length;

    /// <summary>The time t is measured properly already</summary>
    public bool Measured(double t) => known[Math.Clamp((int)(t / ChunkSeconds), 0, known.Length - 1)];

    /// <summary>The loudest sample (0..32768) read around SketchTime(i)</summary>
    public void Sketch(int i, int sample)
    {
        sketch[i] = Encode(sample);
        Volatile.Write(ref sketched[i], true);
        Interlocked.Increment(ref sketchedCount);
        Interlocked.Increment(ref version);
    }

    public Waveform(double duration)
    {
        Duration = duration;
        // Room for a spot every ~1/8 s (up to 32768, a spot every ~8 s for a 72-hour recording): how fine it gets
        // depends on how far in you zoom, since the sketch stops at about two pixels between spots
        var points = 16;
        while (points < 1 << 15 && points < duration * 8) points *= 2;
        sketch = new byte[points];
        sketched = new bool[points];
        slices = Math.Max(1, (int)Math.Ceiling(duration * Rate));
        known = new bool[(slices + ChunkSlices - 1) / ChunkSlices];
        claimed = new bool[known.Length];
        var list = new List<byte[]>();
        for (var n = slices; ; n = (n + 1) / 2)
        {
            list.Add(new byte[n]);
            if (n == 1) break;
        }
        levels = [.. list];
    }

    /// <summary>
    /// The loudest sample from t0 to t1 (0..1), read from the level where that's a sample or two.
    /// Measured = false while none of it has been measured yet; until then it's the sketch's guess (rough = true)
    /// once there is one.
    /// </summary>
    public float Max(double t0, double t1, out bool measured, out bool rough)
    {
        var s0 = t0 * Rate;
        var s1 = Math.Max(t1 * Rate, s0 + 1e-9);
        var level = Math.Clamp((int)Math.Log2(Math.Max(1, s1 - s0)), 0, levels.Length - 1);
        var a = levels[level];
        var scale = 1 << level;
        var i0 = Math.Clamp((int)(s0 / scale), 0, a.Length - 1);
        var i1 = Math.Clamp((int)Math.Ceiling(s1 / scale), i0 + 1, a.Length);
        byte peak = 0;
        for (var i = i0; i < i1; i++) if (a[i] > peak) peak = a[i];
        int c0 = Math.Clamp((int)(s0 / ChunkSlices), 0, known.Length - 1), c1 = Math.Clamp((int)((s1 - 1e-6) / ChunkSlices), c0, known.Length - 1);
        measured = false;
        for (var c = c0; c <= c1 && !measured; c++) measured = known[c];
        rough = false;
        if (measured || Volatile.Read(ref sketchedCount) == 0) return Decode(peak);
        measured = rough = true;
        return Decode(SketchMax(t0, t1));
    }

    /// <summary>The loudest sketched spot from t0 to t1, or the nearest one if none is in there yet</summary>
    byte SketchMax(double t0, double t1)
    {
        var n = sketch.Length;
        var per = n / Duration;
        int i0 = Math.Max(0, (int)Math.Ceiling(t0 * per - 0.5)), i1 = Math.Min(n - 1, (int)Math.Floor(t1 * per - 0.5));
        byte peak = 0;
        var any = false;
        for (var i = i0; i <= i1; i++)
            if (sketched[i])
            {
                any = true;
                if (sketch[i] > peak) peak = sketch[i];
            }
        if (any) return peak;
        // None under this pixel yet: the nearest spot. Spots are read coarse to fine, so look at the two either side
        // on each pass's spacing, finest first (a few checks, where walking out one by one could take thousands)
        var tm = (t0 + t1) / 2 * per;
        var mid = Math.Clamp((int)tm, 0, n - 1);
        for (var stride = 1; stride <= n; stride *= 2)
        {
            int left = mid / stride * stride, right = left + stride;
            bool l = sketched[left], r = right < n && sketched[right];
            if (l && r) return Math.Abs(left + 0.5 - tm) <= Math.Abs(right + 0.5 - tm) ? sketch[left] : sketch[right];
            if (l) return sketch[left];
            if (r) return sketch[right];
        }
        return 0;
    }

    static float Decode(byte b) => b / 255f * (b / 255f);
    /// <summary>A peak sample (0..32768) as stored: the square root, rounded up so anything above silence shows</summary>
    static byte Encode(int sample) => (byte)Math.Min(255, Math.Ceiling(Math.Sqrt(sample / 32768.0) * 255));

    /// <summary>
    /// Measures these tracks' waveforms, the chunks in view first. One ffmpeg reads them all together, so a long
    /// recording's file is read once rather than once per track, and a few of those run at once on different
    /// stretches; if that can't work (one track won't decode), they're measured one at a time instead. View gives
    /// the part of the video on screen, and is checked again after every chunk so scrolling somewhere new is filled
    /// in next. While Paused says so (compressing), it waits.
    /// </summary>
    public static async Task LoadAll(string path, IReadOnlyList<(Waveform Wave, int Track)> tracks, Func<(double From, double To)> view,
        Func<bool> paused, CancellationToken ct)
    {
        if (tracks.Count == 0) return;
        if (await Load(path, tracks, view, paused, Workers, ct) || tracks.Count == 1) return;
        AppLog.Warn("Couldn't measure the audio tracks together; measuring them one at a time");
        await Task.WhenAll(tracks.Select(t => Load(path, [t], view, paused, 1, ct)));
    }

    /// <returns>false if not a single sample could be read (a lone track is then marked Failed)</returns>
    static async Task<bool> Load(string path, IReadOnlyList<(Waveform Wave, int Track)> tracks, Func<(double From, double To)> view,
        Func<bool> paused, int workers, CancellationToken ct)
    {
        // Every wave here is measured over the same chunks, so the first one says what's still to do
        var lead = tracks[0].Wave;
        var waves = tracks.Select(t => t.Wave).ToArray();
        var count = waves.Length;
        var frame = 2 * count; // one sample of every track
        var clock = Stopwatch.StartNew();
        var runs = 0;
        var names = string.Join(", ", tracks.Select(t => t.Track + 1));
        // Each track to mono (first, so there's half as much to resample) at 4 kHz; several are padded (so one
        // ending early doesn't end the others) and merged, one track per channel
        var graph = string.Join(";", tracks.Select((t, i) =>
            $"[0:a:{t.Track}]aformat=channel_layouts=mono,aresample={SampleRate},aformat=sample_fmts=s16:channel_layouts=mono:sample_rates={SampleRate}{(count > 1 ? ",apad" : "")}[w{i}]"));
        if (count > 1) graph += ";" + string.Concat(Enumerable.Range(0, count).Select(i => $"[w{i}]")) + $"amerge=inputs={count}[w]";
        var output = count > 1 ? "[w]" : "[w0]";

        async Task WhilePaused() { while (paused()) await Task.Delay(100, ct).ConfigureAwait(false); }

        var read = await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(Work, ct)));
        if (read.All(ok => ok))
        {
            AppLog.Info($"Waveforms of audio track(s) {names} measured in {clock.Elapsed.TotalSeconds:0.0}s ({runs} runs, {workers} at once)");
            return true;
        }
        if (count > 1) return false;
        lead.Failed = true;
        Interlocked.Increment(ref lead.version);
        return true;

        // Takes the next stretch no other worker has, measures it with one ffmpeg, and again until none is left.
        // False if a stretch read nothing at all before anything had been measured: the tracks can't be read like this.
        async Task<bool> Work()
        {
            var buf = new byte[frame * PerSlice * 64];
            var peak = new int[count];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await WhilePaused();
                int first, run;
                lock (lead.gate)
                {
                    if (lead.Next(view()) is not { } next) return true;
                    first = next;
                    run = 1;
                    // At most a fair share of what's left, so a short recording is split between the workers
                    // rather than the first one taking all of it
                    var free = 0;
                    for (var c = 0; c < lead.known.Length; c++) if (lead.Free(c)) free++;
                    var most = Math.Clamp((free + workers - 1) / workers, 1, MaxRunChunks);
                    while (run < most && lead.Free(first + run)) run++;
                    for (var c = first; c < first + run; c++) lead.claimed[c] = true;
                }
                Interlocked.Increment(ref runs);
                var done = first; // next chunk of the run to finish
                try
                {
                    var psi = new ProcessStartInfo(Ff.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-ss", (first * ChunkSeconds).ToString(CultureInfo.InvariantCulture), "-i", path,
                        "-filter_complex", graph, "-map", output, "-t", (run * ChunkSeconds).ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-" })
                        psi.ArgumentList.Add(arg);

                    using var p = Process.Start(psi)!;
                    var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
                    using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
                    var stdout = p.StandardOutput.BaseStream;
                    long got = 0; // bytes read in this run
                    var slice = first * ChunkSlices; // slice being filled
                    int inSlice = 0, leftover = 0;
                    Array.Clear(peak);
                    var moved = false;
                    void Store()
                    {
                        for (var c = 0; c < count; c++)
                            if (slice < waves[c].slices) waves[c].levels[0][slice] = Encode(peak[c]);
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
                                // This run has gone off screen while something on it still needs measuring (and no
                                // worker has it): go there instead
                                if (done < first + run)
                                {
                                    var (c0, c1) = lead.Chunks(view());
                                    moved = (done < c0 || done > c1) && lead.MissingInView(view()) != null;
                                }
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
                    if (got == 0 && p.ExitCode != 0 && Volatile.Read(ref lead.knownCount) == 0)
                    {
                        AppLog.Warn($"Waveform of audio track(s) {names} failed: {(await err).Trim()}");
                        return false;
                    }
                    // The stream ended: the rest of the run is past the end of the sound (silent), or the slice still
                    // being filled is the last
                    if (inSlice > 0) Store();
                    for (; done < first + run; done++) foreach (var w in waves) w.Finish(done);
                }
                finally
                {
                    // Whatever it didn't get to is free for the next worker (or this one) to take
                    lock (lead.gate) for (var c = done; c < first + run; c++) lead.claimed[c] = false;
                }
            }
        }
    }

    /// <summary>Not measured, and no worker has taken it on</summary>
    bool Free(int chunk) => chunk >= 0 && chunk < known.Length && !known[chunk] && !claimed[chunk];

    /// <summary>Which free chunk to measure from next: the first one in view, otherwise the nearest, ahead before behind</summary>
    int? Next((double From, double To) view)
    {
        if (MissingInView(view) is { } c) return c;
        var (c0, c1) = Chunks(view);
        for (int ahead = c1 + 1, behind = c0 - 1; ahead < known.Length || behind >= 0; ahead++, behind--)
        {
            if (Free(ahead)) return ahead;
            if (Free(behind))
            {
                // Measured forwards, so start a run's length back (or at the last measured chunk) and come up to it
                var start = behind;
                while (start > 0 && behind - start + 1 < MaxRunChunks && Free(start - 1)) start--;
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
        for (var c = c0; c <= c1; c++) if (Free(c)) return c;
        return null;
    }

    /// <summary>A chunk's finest slices are in: build the levels above them and mark it measured</summary>
    void Finish(int chunk)
    {
        // Locked: two workers finishing neighbouring chunks share the coarse levels' entries
        lock (gate)
        {
            if (chunk >= known.Length || known[chunk]) return;
            int lo = chunk * ChunkSlices, hi = Math.Min(slices, lo + ChunkSlices);
            byte loud = 0;
            for (var i = lo; i < hi; i++) if (levels[0][i] > loud) loud = levels[0][i];
            for (var l = 1; l < levels.Length; l++)
            {
                var (below, here) = (levels[l - 1], levels[l]);
                lo >>= 1;
                hi = Math.Min(here.Length, (hi + 1) >> 1);
                for (var i = lo; i < hi; i++)
                    here[i] = Math.Max(below[2 * i], 2 * i + 1 < below.Length ? below[2 * i + 1] : (byte)0);
            }
            Loudest = Math.Max(Loudest, Decode(loud));
            known[chunk] = true;
            Interlocked.Increment(ref knownCount);
            Interlocked.Increment(ref version);
        }
    }
}
