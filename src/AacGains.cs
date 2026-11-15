using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace CompressForDiscord;

/// <summary>
/// Roughly where an MP4's AAC tracks are loud, without decoding any of it. Every AAC frame (~21 ms) starts with a
/// "global gain", the scale its sound is coded at, which rises and falls with how loud the frame is. It's too rough to
/// draw (often 10 dB out), but good at saying where in a stretch the loud part is, so the waveform sketch can listen
/// there instead of at a fixed spot, and a short bang between spots still shows. The gains are read from the file's
/// audio alone (found through the MP4's index, a few percent of the file), start to end, which takes a few seconds on
/// a long recording; Loudest answers for whatever's been read so far. Other files (MKV, fragmented MP4) get nothing.
/// </summary>
sealed class AacGains
{
    sealed class Track
    {
        /// <summary>Per frame: 0 not read (or unreadable), 1 silent, 2+ the gain plus one</summary>
        public required byte[] Gain;
        public required int[] Sizes;
        public required (long Offset, int First, int Count, int Bytes)[] Chunks;
        /// <summary>Seconds a frame, and the time frame 0 starts (before 0: the encoder's lead-in)</summary>
        public required double Frame, Start;
        /// <summary>Frames read so far, from the first</summary>
        public int Read;
    }

    readonly string path;
    readonly Track?[] tracks;

    AacGains(string path, Track?[] tracks) => (this.path, this.tracks) = (path, tracks);

    /// <param name="wanted">Audio tracks (ffmpeg's numbering: the nth audio stream) in the order they'll be asked about</param>
    /// <returns>null if none of them can be read like this</returns>
    public static AacGains? Open(string path, IReadOnlyList<int> wanted)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Moov(fs) is not { } moov)
            {
                AppLog.Info("No loudness scan: not an MP4 with an index");
                return null;
            }
            var audio = new List<Track?>();
            foreach (var (type, a, b) in Boxes(moov, 0, moov.Length))
                if (type == "trak" && Trak(moov, a, b, out var track)) audio.Add(track);
            var list = wanted.Select(w => w < audio.Count ? audio[w] : null).ToArray();
            if (list.Any(t => t != null)) return new AacGains(path, list);
            AppLog.Info($"No loudness scan: no AAC tracks in an MP4 index ({audio.Count} audio tracks)");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Info($"No loudness scan ({ex.Message})");
            return null;
        }
    }

    /// <summary>Reads every frame's gain, in file order (one read per chunk of frames). Waits while paused says so.</summary>
    public void Scan(Func<bool> paused, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var frames = 0;
        try
        {
            var order = tracks.SelectMany((t, ti) => t == null ? [] : t.Chunks.Select((c, ci) => (c.Offset, Track: ti, Chunk: ci)))
                .OrderBy(x => x.Offset).ToArray();
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            var buf = new byte[64 * 1024];
            foreach (var (offset, ti, ci) in order)
            {
                while (paused())
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(100);
                }
                ct.ThrowIfCancellationRequested();
                var t = tracks[ti]!;
                var c = t.Chunks[ci];
                if (c.Bytes > buf.Length) buf = new byte[c.Bytes];
                var n = RandomAccess.Read(file, buf.AsSpan(0, c.Bytes), offset);
                for (int k = c.First, at = 0; k < c.First + c.Count && at + t.Sizes[k] <= n; at += t.Sizes[k], k++)
                {
                    t.Gain[k] = GainOf(buf.AsSpan(at, t.Sizes[k]));
                    frames++;
                }
                if (c.First + c.Count > t.Read) Volatile.Write(ref t.Read, c.First + c.Count);
            }
            AppLog.Info($"Loudness scan: {frames} AAC frames in {clock.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Info($"Loudness scan stopped after {frames} frames ({ex.Message})"); }
    }

    /// <summary>Every track that can be read has been read up to t</summary>
    public bool ReadTo(double t)
    {
        foreach (var tr in tracks)
            if (tr != null && Volatile.Read(ref tr.Read) < Math.Min(tr.Gain.Length, (int)Math.Ceiling((t - tr.Start) / tr.Frame))) return false;
        return true;
    }

    /// <summary>
    /// The loudest-looking frame starting from t0 to t1 on the ith track asked for: when it starts and its gain.
    /// null if that stretch isn't read yet, or this track can't be.
    /// </summary>
    public (double At, int Gain)? Loudest(int i, double t0, double t1)
    {
        if (i >= tracks.Length || tracks[i] is not { } t) return null;
        var k0 = Math.Max(0, (int)Math.Ceiling((t0 - t.Start) / t.Frame));
        var k1 = Math.Min(t.Gain.Length, (int)Math.Ceiling((t1 - t.Start) / t.Frame)) - 1;
        if (k1 < k0 || k1 >= Volatile.Read(ref t.Read)) return null;
        int best = -1, gain = 0;
        for (var k = k0; k <= k1; k++)
            if (t.Gain[k] > gain) (best, gain) = (k, t.Gain[k]);
        return best < 0 ? null : (t.Start + best * t.Frame, gain);
    }

    /// <summary>The start of an AAC frame (raw, as an MP4 holds it): its first channel's global gain</summary>
    static byte GainOf(ReadOnlySpan<byte> frame)
    {
        var b = new Bits(frame);
        var element = b.Take(3);
        // Mono (or LFE) or a stereo pair; anything else first isn't worth working through
        if (element is not (0 or 1 or 3)) return 0;
        b.Take(4);
        int gain, bands;
        if (element == 1 && b.Take(1) == 1)
        {
            // A pair sharing its window: the window info and the mid/side flags come before the gain
            if (!Ics(ref b, out bands, out var groups)) return 0;
            if (b.Take(2) == 1) b.Skip(groups * bands);
            gain = b.Take(8);
        }
        else
        {
            gain = b.Take(8);
            if (!Ics(ref b, out bands, out _)) return 0;
        }
        if (b.Over) return 0;
        // No bands coded: silence
        return bands == 0 ? (byte)1 : (byte)Math.Clamp(gain + 1, 2, 255);
    }

    /// <summary>ics_info: how many bands are coded, and in how many window groups</summary>
    static bool Ics(ref Bits b, out int bands, out int groups)
    {
        b.Take(1);
        var sequence = b.Take(2);
        b.Take(1);
        groups = 1;
        if (sequence == 2)
        {
            // Eight short windows, grouped: a 0 bit starts a new group
            bands = b.Take(4);
            var grouping = b.Take(7);
            for (var i = 0; i < 7; i++) if ((grouping >> i & 1) == 0) groups++;
            return true;
        }
        bands = b.Take(6);
        // Prediction is never on in AAC-LC
        return b.Take(1) == 0;
    }

    ref struct Bits(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> data = data;
        int at;
        public readonly bool Over => at > data.Length * 8;
        public void Skip(int n) => at += n;
        public int Take(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++, at++)
                v = v << 1 | (at < data.Length * 8 ? data[at >> 3] >> (7 - (at & 7)) & 1 : 0);
            return v;
        }
    }

    /// <summary>The file's moov box (its index), or null if it hasn't one or it's absurdly big</summary>
    static byte[]? Moov(FileStream fs)
    {
        var head = new byte[16];
        for (long pos = 0, end = fs.Length; pos + 8 <= end;)
        {
            fs.Position = pos;
            fs.ReadExactly(head, 0, 16 <= end - pos ? 16 : 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var headSize = 8;
            if (size == 1) (size, headSize) = ((long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8)), 16);
            else if (size == 0) size = end - pos;
            if (size < headSize) return null;
            if (Encoding.ASCII.GetString(head, 4, 4) == "moov")
            {
                if (size > 256 << 20) return null;
                // Its contents (the boxes in it)
                var moov = new byte[size - headSize];
                fs.Position = pos + headSize;
                fs.ReadExactly(moov);
                return moov;
            }
            pos += size;
        }
        return null;
    }

    /// <summary>The boxes from a to b: their type and contents</summary>
    static IEnumerable<(string Type, int From, int To)> Boxes(byte[] buf, int a, int b)
    {
        for (var pos = a; pos + 8 <= b;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos));
            var head = 8;
            if (size == 1 && pos + 16 <= b) (size, head) = ((long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(pos + 8)), 16);
            else if (size == 0) size = b - pos;
            if (size < head || pos + size > b) yield break;
            yield return (Encoding.ASCII.GetString(buf, pos + 4, 4), pos + head, pos + (int)size);
            pos += (int)size;
        }
    }

    static (int From, int To)? Child(byte[] buf, (int From, int To)? box, string type)
    {
        if (box is not { } x) return null;
        foreach (var (t, a, b) in Boxes(buf, x.From, x.To)) if (t == type) return (a, b);
        return null;
    }

    static uint U32(byte[] buf, int at) => BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(at));

    /// <returns>Whether it's an audio track; track is null when it is but isn't AAC in a plain (unfragmented) MP4</returns>
    static bool Trak(byte[] m, int a, int b, out Track? track)
    {
        track = null;
        var mdia = Child(m, (a, b), "mdia");
        if (Child(m, mdia, "hdlr") is not { } hdlr || hdlr.To - hdlr.From < 12 || Encoding.ASCII.GetString(m, hdlr.From + 8, 4) != "soun") return false;
        try
        {
            if (Child(m, mdia, "mdhd") is not { } mdhd) return true;
            var timescale = (double)U32(m, mdhd.From + (m[mdhd.From] == 1 ? 20 : 12));
            var stbl = Child(m, Child(m, mdia, "minf"), "stbl");
            if (Child(m, stbl, "stsd") is not { } stsd || Encoding.ASCII.GetString(m, stsd.From + 12, 4) != "mp4a") return true;
            if (Child(m, stbl, "stts") is not { } stts || Child(m, stbl, "stsz") is not { } stsz || Child(m, stbl, "stsc") is not { } stsc) return true;
            var delta = U32(m, stts.From + 12);
            var fixedSize = U32(m, stsz.From + 4);
            var count = (int)U32(m, stsz.From + 8);
            if (timescale <= 0 || delta == 0 || count <= 0) return true;
            // Counts from the file must fit in their own box, or the reads below wander into the next one
            static bool Fits((int From, int To) box, int head, long n, int size) => n >= 0 && head + n * size <= box.To - box.From;
            if (fixedSize == 0 && !Fits(stsz, 12, count, 4) || !Fits(stsc, 8, U32(m, stsc.From + 4), 12)) return true;
            var sizes = new int[count];
            for (var i = 0; i < count; i++) sizes[i] = (int)(fixedSize != 0 ? fixedSize : U32(m, stsz.From + 12 + 4 * i));
            // A size too big to be a frame (it reads as negative) would stop the scan for every track
            if (Array.Exists(sizes, size => size < 0)) return true;
            var offsets = new List<long>();
            if (Child(m, stbl, "stco") is { } stco)
            {
                if (!Fits(stco, 8, U32(m, stco.From + 4), 4)) return true;
                for (int i = 0, n = (int)U32(m, stco.From + 4); i < n; i++) offsets.Add(U32(m, stco.From + 8 + 4 * i));
            }
            else if (Child(m, stbl, "co64") is { } co64)
            {
                if (!Fits(co64, 8, U32(m, co64.From + 4), 8)) return true;
                for (int i = 0, n = (int)U32(m, co64.From + 4); i < n; i++) offsets.Add((long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(co64.From + 8 + 8 * i)));
            }
            var runs = (int)U32(m, stsc.From + 4);
            var chunks = new List<(long, int, int, int)>();
            for (int c = 0, run = 0, first = 0; c < offsets.Count && first < count; c++)
            {
                while (run + 1 < runs && U32(m, stsc.From + 8 + 12 * (run + 1)) <= c + 1) run++;
                var n = (int)Math.Min(U32(m, stsc.From + 8 + 12 * run + 4), (uint)(count - first));
                var bytes = 0L;
                for (var k = first; k < first + n; k++) bytes += sizes[k];
                if (bytes > 64 << 20) return true;
                chunks.Add((offsets[c], first, n, (int)bytes));
                first += n;
            }
            // The edit list's first entry says where the sound starts in the track (after the encoder's lead-in)
            var lead = 0.0;
            if (Child(m, Child(m, (a, b), "edts"), "elst") is { } elst && U32(m, elst.From + 4) > 0)
            {
                var start = m[elst.From] == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(elst.From + 16)) : (int)U32(m, elst.From + 12);
                if (start > 0) lead = start / timescale;
            }
            track = new Track { Gain = new byte[count], Sizes = sizes, Chunks = [.. chunks], Frame = delta / timescale, Start = -lead };
        }
        // A damaged track loses only its own scan, not the other tracks'
        catch (Exception) { track = null; }
        return true;
    }
}
