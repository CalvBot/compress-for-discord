using System.Runtime.InteropServices;

namespace CompressForDiscord;

// Test code: only debug builds compile this folder (see the project file). It drives the window from the
// CFD_TEST environment variable for screenshots and end-to-end checks, through the Test... hooks in MainForm.
sealed partial class MainForm
{
    /// <summary>
    /// Debug builds only: puts the window into a state for screenshots and end-to-end tests without clicking.
    /// CFD_TEST="size=20;res=50;rate=30;trim=20;cropkeys;keys;save=C:\\out.mp4" (res is a percentage, trim the end time)
    /// </summary>
    partial void TestSetUp()
    {
        foreach (var part in (Environment.GetEnvironmentVariable("CFD_TEST") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = (part.Split('=')[0], part[(part.IndexOf('=') + 1)..]);
            switch (key)
            {
                case "audio": masterLane.Active = value != "off"; masterLane.Invalidate(); UpdateSummary(); break;
                case "adv": tracksOpen = true; ShowTracks(); break;
                // The waveform style for this run only (not saved)
                case "mirrored": cfg.MirroredWaves = true; break;
                case "grow":
                    // Same path as dragging the window edge
                    userSizing = true;
                    ClientSize += new Size(int.Parse(value.Split('x')[0]), int.Parse(value.Split('x')[1]));
                    userSizing = false;
                    FitToContent();
                    break;
                case "step": for (var i = 0; i < int.Parse(value); i++) HandleEditKey(Keys.Right); break;
                case "busy": SetBusy(true); ShowStatus("Compressing on GPU · 42%", Theme.Text); bar.Value = 0.42; break;
                case "fps": frameRate = int.Parse(value); SelectChip(fpsChips, frameRate); UpdateSummary(); break;
                case "res": resBox.Text = value; break;
                case "crop": SetCrop(value); break;
                case "rate": fpsBox.Text = value; break;
                case "separate": SetSeparate(true); break;
                case "select": keyLane.Select(int.Parse(value)); break;
                case "del": DeleteSelectedKey(); break;
                // presskey=D2: once the window's up, as if that key was pressed; then logs each audio track's key point eases
                case "presskey":
                    var pressed = Enum.Parse<Keys>(value);
                    _ = Task.Delay(1500).ContinueWith(_ => BeginInvoke(() =>
                        AppLog.Info($"Presskey {value}: handled {HandleEditKey(pressed)}; eases {string.Join(" | ", trackUi.Select(t => t.Lane).Prepend(masterLane).Select(l => string.Join(",", l.Keys.Select(k => k.E))))}")));
                    break;
                // selmulti=lane:0,1 (lane: key, pos, scale, or an audio track number)
                case "selmulti":
                    var (which, ids) = (value.Split(':')[0], value.Split(':')[1].Split(',').Select(int.Parse));
                    if (which is "key" or "pos" or "scale" or "master") (which == "key" ? keyLane : which == "pos" ? posLane : which == "scale" ? scaleLane : (KeyTrackLane)masterLane).Selection.SetMany(ids);
                    else trackUi[int.Parse(which)].Lane.Selection.SetMany(ids);
                    foreach (var t in AllTracks) t.Invalidate();
                    break;
                case "copy": CopyKeys(); break;
                case "paste": PasteKeys(); break;
                case "shiftsel":
                    // Moves the shared track's selection by this many seconds, the way a drag does
                    var all = new IKeyTrack[] { pos, scale };
                    var d = Interp.ClampShift(Enumerable.Range(0, pos.Count).Select(pos.TimeAt).ToList(), keyLane.Selection.Set, double.Parse(value), timeline.Duration);
                    foreach (var k in all) k.Shift(keyLane.Selection.Set, d);
                    ShowCrop();
                    break;
                case "restore": if (Projects.Recent().FirstOrDefault(r => r.Project.File == file).Project is { } saved) Restore(saved); break;
                case "saveproject": projectSaved = false; SaveProject(); break;
                case "ease":
                    // Every key point on every track
                    var ease = Enum.Parse<Ease>(value);
                    foreach (var (_, lane) in trackUi) lane.SetEase(Enumerable.Range(0, lane.Keys.Count), ease);
                    for (var i = 0; i < pos.Count; i++) pos.SetEase(i, ease);
                    for (var i = 0; i < scale.Count; i++) scale.SetEase(i, ease);
                    ShowCrop();
                    UpdateMasterPeaks();
                    break;
                case "keymenu": ShowTrackMenu(keyLane, new Point(keyLane.Width / 2, keyLane.Height / 2)); break;
                case "lock": timeline.FrameLock = value == "1"; timeline.Invalidate(); break;
                case "gripspam":
                    // Drag the track box's grip up and down fast, as a quick hand would
                    BeginInvoke(() =>
                    {
                        var start = scroller.Height;
                        for (var i = 0; i < int.Parse(value); i++)
                        {
                            DragTracks(start + (int)(Math.Sin(i * 0.9) * 260) + 200);
                            Application.DoEvents();
                        }
                        DragTracks(start);
                        var c = Controls[0];
                        Text = $"GRIP client {ClientSize.Width}x{ClientSize.Height} content {c.Right + c.Left}x{c.Bottom + c.Top} box {scroller.Height}";
                    });
                    break;
                case "spam":
                    // Toggle Advanced over and over and report how long each took
                    BeginInvoke(() =>
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var n = int.Parse(value);
                        for (var i = 0; i < n; i++) { tracksOpen = !tracksOpen; ShowTracks(); }
                        Text = $"SPAM {watch.ElapsedMilliseconds / (double)n:0.0} ms per toggle, window {Width}x{Height}";
                    });
                    break;
                case "combine": SetSeparate(false); break;
                case "trackh": SetTrackHeight(int.Parse(value)); break;
                case "pos": pos.Set(timeline.Current, (double.Parse(value.Split(',')[0]), double.Parse(value.Split(',')[1]))); ShowCrop(); UpdateSummary(); break;
                case "resetcrop": ResetCrop(); break;
                case "zoom": SetView(double.Parse(value.Split('-')[0]), double.Parse(value.Split('-')[1])); break;
                // Zooms back out to the whole video this many ms later
                case "unzoom": { var t = new System.Windows.Forms.Timer { Interval = int.Parse(value) }; t.Tick += (_, _) => { t.Dispose(); SetView(0, timeline.Duration); }; t.Start(); break; }
                case "preview": SetPreview(true); break;
                case "sizesim":
                    // Drags the bottom edge up well past the smallest the window can be, then down well past the
                    // tallest the tracks fill, then the right edge in; logs any step where it went the wrong way
                    BeginInvoke(() =>
                    {
                        var log = new List<string>();
                        void Drag(int dx, int dy, int steps)
                        {
                            OnResizeBegin(EventArgs.Empty);
                            for (var i = 0; i < steps; i++)
                            {
                                var before = Size;
                                var asked = new Size(Width + dx, Height + dy);
                                Size = asked;
                                Application.DoEvents();
                                // Moving the edge one way must never move the window the other way, and a size the
                                // minimum allows must be kept (else Windows and the fit fight: it flickers)
                                if ((dy != 0 && Math.Sign(Height - before.Height) == -Math.Sign(dy)) || (dx != 0 && Math.Sign(Width - before.Width) == -Math.Sign(dx))
                                    || (asked.Height >= MinimumSize.Height && asked.Width >= MinimumSize.Width && Size != asked && dy <= 0 && dx <= 0))
                                    log.Add($"step {i} ({dx},{dy}): {before.Width}x{before.Height} asked {asked.Width}x{asked.Height} -> {Width}x{Height}, min {MinimumSize.Width}x{MinimumSize.Height}");
                            }
                            OnResizeEnd(EventArgs.Empty);
                            Application.DoEvents();
                        }
                        // As if the window was made bigger before Advanced was opened
                        if (int.TryParse(value, out var pre)) { extraH = pre; ApplyStretch(); FitToContent(); Application.DoEvents(); }
                        var start = Size;
                        if (value == "wide")
                        {
                            // Sideways only: wider then narrower, logging each step
                            OnResizeBegin(EventArgs.Empty);
                            foreach (var dx in new[] { 30, 30, 30, -20, -20, -20, -20, -20, -20 })
                            {
                                var asked = Width + dx;
                                Size = new Size(asked, Height);
                                Application.DoEvents();
                                AppLog.Info($"Size: wide step asked {asked} got {Width}x{Height}, extraW {extraW}, min {MinimumSize.Width}");
                            }
                            OnResizeEnd(EventArgs.Empty);
                            return;
                        }
                        Drag(0, -40, 40);
                        var small = Size;
                        Drag(0, 40, 60);
                        var tall = Size;
                        // Widen then narrow, counting what repaints
                        Drag(40, 0, 10);
                        var spies = new List<MessageSpy>();
                        void Spy(Control c) { spies.Add(new MessageSpy(c)); foreach (Control child in c.Controls) Spy(child); }
                        Spy(this);
                        Drag(-20, 0, 10);
                        foreach (var s in spies) s.ReleaseHandle();
                        foreach (var (what, (count, ms)) in MessageSpy.Totals.Where(kv => kv.Key.EndsWith(" 0xf") || kv.Key.EndsWith(" 0x14") || kv.Key.EndsWith(" 0x85")).OrderByDescending(kv => kv.Value.Count).Take(30))
                            AppLog.Info($"Size:   10 steps: {what} x{count}, {ms:0.0} ms");
                        Drag(-40, 0, 40);
                        AppLog.Info($"Size: start {start.Width}x{start.Height}, shrunk {small.Width}x{small.Height} (min {MinimumSize.Height}), grown {tall.Width}x{tall.Height}, narrowed {Width}x{Height} (min {MinimumSize.Width}), {log.Count} wrong-way steps");
                        foreach (var l in log.Take(6)) AppLog.Info("Size:   " + l);
                    });
                    break;
                case "dragsim":
                {
                    // dragsim=<seconds>: drags the playhead back and forth like a fast mouse would (a move arrives
                    // whenever the last one has been handled, 1 ms on), then logs how often the timeline repainted
                    var seconds = double.Parse(value);
                    var handle = timeline.Handle;
                    var (w, y) = (timeline.Width, timeline.Height - 10);
                    timeline.DebugBeginSeekDrag();
                    var spies = new List<MessageSpy>();
                    void Spy(Control c) { spies.Add(new MessageSpy(c)); foreach (Control child in c.Controls) Spy(child); }
                    Spy(this);
                    // The timeline's own mouse moves (0x200) and paints (0xf), as the spies count them
                    const string Moves = "Timeline 0x200", Paints = "Timeline 0xf";
                    var (moves0, paints0, ms0) = (MessageSpy.Of(Moves).Count, MessageSpy.Of(Paints).Count, MessageSpy.Of(Moves).Ms);
                    _ = Task.Run(() =>
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var posted = 0;
                        while (watch.Elapsed.TotalSeconds < seconds)
                        {
                            var spin = System.Diagnostics.Stopwatch.StartNew();
                            while (spin.Elapsed.TotalMilliseconds < 1) Thread.SpinWait(50);
                            if (MessageSpy.Of(Moves).Count - moves0 < posted) continue;
                            // Across and back once every 2 s
                            var phase = watch.Elapsed.TotalSeconds % 2 / 2;
                            var x = 40 + (int)((w - 60) * (phase < 0.5 ? phase * 2 : 2 - phase * 2));
                            PostMessage(handle, 0x200, 1, (y << 16) | x);
                            posted++;
                        }
                        BeginInvoke(() =>
                        {
                            timeline.DebugEndDrag();
                            int moves = MessageSpy.Of(Moves).Count - moves0, paints = MessageSpy.Of(Paints).Count - paints0;
                            AppLog.Info($"Drag: {moves / seconds:0} moves/s, {paints / seconds:0} timeline paints/s, {(MessageSpy.Of(Moves).Ms - ms0) / Math.Max(1, moves):0.00} ms per move");
                            foreach (var (what, (count, ms)) in MessageSpy.Totals.OrderByDescending(kv => kv.Value.Ms).Take(12))
                                AppLog.Info($"Drag:   {what}: {count / seconds:0}/s, {ms / seconds:0.0} ms/s");
                            foreach (var s in spies) s.ReleaseHandle();
                        });
                    });
                    break;
                }
                case "scrubbench":
                {
                    // scrubbench=<png to save a frame to>: times Windows' decoder against ffmpeg on this video
                    var (path, p, png) = (file, probe!, value);
                    _ = Task.Run(async () =>
                    {
                        try { await Bench(); } catch (Exception ex) { AppLog.Error("Bench", ex); }
                    });
                    async Task Bench()
                    {
                        var d = p.Duration;
                        double[] spots = [d * 0.5 + 0.95, d * 0.2 + 0.4, d * 0.8 + 0.7, d * 0.33 + 0.99, d * 0.61 + 0.2];
                        foreach (var gpu in new[] { true, false })
                        {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using var r = FrameReader.Open(path, p.Fps, gpu);
                        AppLog.Info($"Bench: open {sw.ElapsedMilliseconds} ms, {(r == null ? "unsupported" : r.Gpu ? "GPU" : "CPU")}");
                        var size = new Size(Math.Max(2, (int)Math.Round(540.0 * p.Width / p.Height)) & ~1, 540);
                        double Ms(Func<object?> f) { var s = System.Diagnostics.Stopwatch.StartNew(); (f() as IDisposable)?.Dispose(); return s.Elapsed.TotalMilliseconds; }
                        if (r != null)
                        {
                            AppLog.Info("Bench: MF exact " + string.Join(", ", spots.Select(t => $"{Ms(() => r.Get(t, size, true, default)):0}")));
                            AppLog.Info("Bench: MF keyframe " + string.Join(", ", spots.Select(t => $"{Ms(() => r.Get(t + 3, size, false, default)):0}")));
                            var steps = Enumerable.Range(1, 30).Select(i => Ms(() => r.Get(d * 0.5 + 10 + i / p.Fps, size, true, default))).ToList();
                            AppLog.Info($"Bench: MF step forward avg {steps.Skip(1).Average():0.0} ms, first {steps[0]:0}");
                            var drag = Enumerable.Range(1, 30).Select(i => Ms(() => r.Get(d * 0.5 + 20 + i * 0.37, size, false, default))).ToList();
                            AppLog.Info($"Bench: MF drag forward 0.37 s steps avg {drag.Average():0.0} ms");
                            var back = Enumerable.Range(1, 20).Select(i => Ms(() => r.Get(d * 0.5 + 40 - i * 0.37, size, false, default))).ToList();
                            AppLog.Info($"Bench: MF drag backward avg {back.Average():0.0} ms");
                            using var shot = r.Get(d * 0.5, size, true, default);
                            shot?.Save(png.Replace(".png", gpu ? "-gpu.png" : "-cpu.png"));
                        }
                        }
                        var ff = new List<double>();
                        foreach (var t in spots) { var s = System.Diagnostics.Stopwatch.StartNew(); (await Ff.Frame(path, t, 540))?.Dispose(); ff.Add(s.Elapsed.TotalMilliseconds); }
                        AppLog.Info("Bench: ffmpeg exact " + string.Join(", ", ff.Select(v => $"{v:0}")));
                        AppLog.Info("Bench: done");
                    }
                    break;
                }
                // play, or play=ms to wait that long first (once opening's work has settled)
                case "play":
                    if (int.TryParse(value, out var playIn)) _ = Task.Delay(playIn).ContinueWith(_ => BeginInvoke(TogglePlay));
                    else TogglePlay();
                    break;
                // playzoom: plays, then zooms in and out a notch every 80 ms, and logs how evenly the frames came
                // before and during the zooming
                case "playzoom":
                    BeginInvoke(new Action(async () =>
                    {
                        string Gaps(int from)
                        {
                            var g = debugFrames.Skip(from).Zip(debugFrames.Skip(from + 1), (a, b) => (double)(b - a)).ToList();
                            return g.Count == 0 ? "no frames" : $"{g.Count} frames, gap avg {g.Average():0.0} ms, worst {g.Max():0} ms, over 100 ms: {g.Count(x => x > 100)}";
                        }
                        await Task.Delay(int.TryParse(value, out var wait) ? wait : 2000);
                        timeline.Seek(timeline.Duration * 0.3);
                        TogglePlay();
                        await Task.Delay(3000);
                        var mark = debugFrames.Count;
                        await Task.Delay(4000);
                        AppLog.Info($"Playzoom: still: {Gaps(mark)}");
                        mark = debugFrames.Count;
                        var ui = System.Diagnostics.Stopwatch.StartNew();
                        var busy = 0.0;
                        for (var n = 0; n < 50; n++)
                        {
                            var w = System.Diagnostics.Stopwatch.StartNew();
                            ZoomBy(n / 10 % 2 == 0 ? 0.75 : 1 / 0.75, timeline.Current);
                            Update();
                            busy += w.Elapsed.TotalMilliseconds;
                            await Task.Delay(80);
                        }
                        AppLog.Info($"Playzoom: zooming: {Gaps(mark)}; zoom steps took {busy / 50:0.0} ms each");
                        mark = debugFrames.Count;
                        await Task.Delay(3000);
                        AppLog.Info($"Playzoom: after: {Gaps(mark)}");
                    }));
                    break;
                // thumbsim: zooms in 6 notches, out 6, in 6 and scrolls, pausing for the thumbnails each time; the
                // "Thumbs:" log lines show how many had to be made
                case "thumbsim":
                    BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(4000);
                        timeline.Seek(timeline.Duration * 0.3);
                        foreach (var factor in new[] { 0.75, 1 / 0.75, 0.75 })
                            for (var n = 0; n < 6; n++) { ZoomBy(factor, timeline.Current); await Task.Delay(1500); }
                        for (var n = 0; n < 3; n++) { var by = (viewEnd - viewStart) * 0.15; SetView(viewStart + by, viewEnd + by, keepSpan: true); await Task.Delay(1500); }
                        AppLog.Info("Thumbs: sim done");
                    }));
                    break;
                // zoomsim: Alt + wheel in, 40 notches past the limit, then out; logs where the view and playhead sit
                case "zoomsim":
                    timeline.Seek(timeline.Duration * 0.3);
                    for (var n = 0; n < 40; n++)
                    {
                        ZoomBy(0.75, timeline.Duration * 0.9);
                        if (n % 8 == 7 || n >= 36) AppLog.Info($"Zoomsim in {n + 1}: {viewStart:0.000}-{viewEnd:0.000}, playhead {(timeline.Current - viewStart) / (viewEnd - viewStart):P1} across");
                    }
                    for (var n = 0; n < 40; n++) ZoomBy(1 / 0.75, timeline.Duration * 0.9);
                    AppLog.Info($"Zoomsim out: {viewStart:0.000}-{viewEnd:0.000} of {timeline.Duration:0.000}");
                    break;
                case "goto": timeBox.Text = value; if (ParseTime(value) is { } go) timeline.Seek(go); break;
                case "cropkeys":
                {
                    // A zoom: the full box at 20% of the way in, half the width (cropkeys=0.1: a tenth) and
                    // off-centre by 60%. cropkeys=still,0.5: zoomed in to half the width throughout.
                    var still = value.StartsWith("still");
                    var to = double.TryParse(value.Split(',')[^1], System.Globalization.CultureInfo.InvariantCulture, out var given) ? given : 0.5;
                    pos.Keys.Clear();
                    scale.Keys.Clear();
                    pos.Keys.AddRange([(probe!.Duration * 0.2, still ? (0.62, 0.45) : (0.5, 0.5), Ease.Linear), (probe.Duration * 0.6, (0.62, 0.45), Ease.Linear)]);
                    scale.Keys.AddRange([(probe.Duration * 0.2, MaxCropW * (still ? to : 1), Ease.Linear), (probe.Duration * 0.6, MaxCropW * to, Ease.Linear)]);
                    timeline.Seek(probe.Duration * 0.4);
                    ShowCrop();
                    UpdateSummary();
                    break;
                }
                // manykeys=N: a crop that wanders through N points, for a filter graph too long for a command line
                case "manykeys":
                    pos.Keys.Clear();
                    scale.Keys.Clear();
                    for (var i = 0; i < int.Parse(value); i++)
                    {
                        var t = probe!.Duration * i / int.Parse(value);
                        pos.Keys.Add((t, (0.4 + 0.2 * (i % 3) / 2, 0.4 + 0.2 * (i % 2)), Ease.Smooth));
                        scale.Keys.Add((t, MaxCropW * (0.5 + 0.1 * (i % 4)), Ease.Smooth));
                    }
                    ShowCrop();
                    UpdateSummary();
                    break;
                // Opens the summary's hover details shortly after, for screenshots
                case "tip": { var t = new System.Windows.Forms.Timer { Interval = 800 }; t.Tick += (_, _) => { t.Dispose(); AppLog.Info("Summary tip: " + tips.GetToolTip(summary)?.Replace("\n", " | ")); AppLog.Info("File tip: " + tips.GetToolTip(subtitle)?.Replace("\n", " | ")); tips.Show(tips.GetToolTip(summary), summary, 0, -D(110), 8000); }; t.Start(); break; }
                // Not SetTarget: a test mustn't change the size remembered in the real settings
                case "size": targetMb = double.Parse(value); SelectChip(sizeChips, targetMb); UpdateSummary(); break;
                case "trim": timeline.SetEnd(double.Parse(value)); break;
                case "keys":
                    foreach (var (_, lane) in trackUi.Take(3))
                    {
                        lane.Keys.AddRange([(probe!.Duration * 0.2, 1, Ease.Linear), (probe.Duration * 0.4, 0.3, Ease.Linear), (probe.Duration * 0.7, 1.6, Ease.Linear)]);
                        lane.Active = true;
                        lane.Invalidate();
                    }
                    break;
                // dumpwaves=S: S seconds on, each track's waveform as drawn over the whole video in 600 columns
                // (sketch or measured, 0..1), written to waves.csv beside CFD_PROJECTS
                case "dumpwaves":
                    _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(value))).ContinueWith(task =>
                    {
                        var rows = trackUi.Select(u => u.Lane.Sources[0].Wave).Select(w => string.Join(",", Enumerable.Range(0, 600).Select(c =>
                        {
                            var v = w.Max(w.Duration * c / 600, w.Duration * (c + 1) / 600, out var measured, out _);
                            return measured ? v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) : "";
                        })));
                        File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("CFD_PROJECTS")!, "..", "waves.csv"), rows);
                        AppLog.Info("Dumped waveforms");
                    });
                    break;
                // keysim: A / Shift+A, dragging points on two tracks at once (and with Shift, to the playhead), Del on
                // both, and undo / redo, with posted mouse messages and this thread's key state (not the real
                // keyboard). Wants masterkeys and cropkeys first.
                case "keysim":
                    _ = Task.Delay(1500).ContinueWith(_ => BeginInvoke(new Action(async () =>
                    {
                        static nint L(PointF p) => ((int)Math.Round(p.Y) << 16) | ((int)Math.Round(p.X) & 0xFFFF);
                        void Check(string what, bool ok, string detail) => AppLog.Info($"Keysim: {(ok ? "ok  " : "FAIL")} {what}: {detail}");
                        var state = new byte[256];
                        // The left button too, as the undo check reads it
                        void Hold(bool shift, bool alt, bool button = false)
                        {
                            GetKeyboardState(state);
                            state[0x10] = (byte)(shift ? 0x80 : 0);
                            state[0x12] = (byte)(alt ? 0x80 : 0);
                            state[0x01] = (byte)(button ? 0x80 : 0);
                            SetKeyboardState(state);
                        }
                        async Task Drag(Control c, PointF from, float dx, bool shift = false, bool alt = false)
                        {
                            Hold(shift, alt);
                            PostMessage(c.Handle, 0x200, 0, L(from)); await Task.Delay(40);
                            Hold(shift, alt, true);
                            PostMessage(c.Handle, 0x201, 1, L(from)); await Task.Delay(40);
                            for (var i = 1; i <= 6; i++) { PostMessage(c.Handle, 0x200, 1, L(new PointF(from.X + dx * i / 6, from.Y))); await Task.Delay(40); }
                            Hold(shift, alt);
                            PostMessage(c.Handle, 0x202, 0, L(new PointF(from.X + dx, from.Y))); await Task.Delay(100);
                            Hold(false, false);
                            await Task.Delay(400); // the undo check sees it
                        }
                        string M() => string.Join(",", masterLane.Keys.Select(k => k.T.ToString("0.000")));
                        string P() => string.Join(",", pos.Keys.Select(k => k.T.ToString("0.000")));
                        try
                        {
                            Check("lanes shown", masterLane.Visible && keyLane.Visible, $"master {masterLane.Visible}, key {keyLane.Visible}");
                            hoverTrack = masterLane;
                            HandleEditKey(Keys.A);
                            Check("A", masterLane.Selection.Count == 3 && keyLane.Selection.Count == 0, $"master {masterLane.Selection.Count}, key {keyLane.Selection.Count}");
                            HandleEditKey(Keys.Shift | Keys.A);
                            Check("Shift+A", masterLane.Selection.Count == 3 && keyLane.Selection.Count == 2, $"master {masterLane.Selection.Count}, key {keyLane.Selection.Count}");
                            var (m0, p0) = (masterLane.Keys.Select(k => k.T).ToList(), pos.Keys.Select(k => k.T).ToList());
                            await Drag(masterLane, masterLane.DebugKey(0), 40, shift: true, alt: true);
                            var dm = masterLane.Keys[0].T - m0[0];
                            var dp = pos.Keys[0].T - p0[0];
                            Check("drag moves both tracks", dm > 0 && Math.Abs(dm - dp) < 1e-9 && Math.Abs(masterLane.Keys[2].T - m0[2] - dm) < 1e-9, $"master {dm:0.0000}, pos {dp:0.0000}");
                            Check("the held point lands on a frame", Math.Abs(masterLane.Keys[0].T / FrameLength - Math.Round(masterLane.Keys[0].T / FrameLength)) < 1e-6, $"{masterLane.Keys[0].T:0.00000} s, frame {FrameLength:0.00000}");
                            // Shift: the playhead a little way right of the second pos point; a drag ending near it snaps
                            var target = pos.Keys[1].T + 0.5;
                            timeline.Seek(target);
                            await Task.Delay(100);
                            var key0 = masterLane.DebugKey(0);
                            var lane0 = masterLane.Keys[0].T;
                            // Pixels per second on the lane, from two points
                            var pps = (masterLane.DebugKey(2).X - key0.X) / (masterLane.Keys[2].T - lane0);
                            await Drag(masterLane, key0, (float)(0.5 * pps) + 3, shift: true);
                            var onIt = pos.Keys.Any(k => Math.Abs(k.T - timeline.Current) < 1e-9) || masterLane.Keys.Any(k => Math.Abs(k.T - timeline.Current) < 1e-9);
                            Check("Shift snaps a selected point to the playhead", onIt, $"playhead {timeline.Current:0.000}; master {M()}; pos {P()}");
                            var snapped = (M(), P());
                            HandleEditKey(Keys.Delete);
                            // The crop keeps its last point, as its fixed value
                            Check("Del on both tracks", masterLane.Keys.Count == 0 && keyLane.Keys.Count == 1, $"master {masterLane.Keys.Count}, key lane {keyLane.Keys.Count}");
                            await Task.Delay(400);
                            HandleEditKey(Keys.Control | Keys.Z);
                            Check("undo the delete", (M(), P()) == snapped, $"master {M()}; pos {P()}");
                            HandleEditKey(Keys.Control | Keys.Z);
                            Check("undo the snap drag", M() != snapped.Item1, $"master {M()}");
                            HandleEditKey(Keys.Control | Keys.Shift | Keys.Z);
                            Check("redo", (M(), P()) == snapped, $"master {M()}; pos {P()}");
                            // Back past the snap drag and the first drag
                            for (var i = 0; i < 2; i++) HandleEditKey(Keys.Control | Keys.Z);
                            Check("undo both drags", M() == string.Join(",", m0.Select(t => t.ToString("0.000"))) && P() == string.Join(",", p0.Select(t => t.ToString("0.000"))), $"master {M()}; pos {P()}; {undoStack.Count} left, {redoStack.Count} to redo");
                        }
                        catch (Exception ex) { AppLog.Error("Keysim", ex); }
                        finally { Hold(false, false); }
                    })));
                    break;
                // The same three on the main audio lane (the only one a single-track video shows)
                case "masterkeys":
                    masterLane.Keys.AddRange([(probe!.Duration * 0.2, 1, Ease.Linear), (probe.Duration * 0.4, 0.3, Ease.Linear), (probe.Duration * 0.7, 1.6, Ease.Linear)]);
                    masterLane.Invalidate();
                    break;
                case "mousesim":
                    // Drags the crop box, key points and the playhead with posted mouse messages (the real mouse
                    // handlers, without moving the cursor), and logs whether each did what it should
                    BeginInvoke(new Action(async () =>
                    {
                        static nint L(PointF p) => ((int)Math.Round(p.Y) << 16) | ((int)Math.Round(p.X) & 0xFFFF);
                        async Task Drag(Control c, PointF from, PointF to, int steps = 6)
                        {
                            PostMessage(c.Handle, 0x200, 0, L(from)); await Task.Delay(40);
                            PostMessage(c.Handle, 0x201, 1, L(from)); await Task.Delay(40);
                            for (var i = 1; i <= steps; i++)
                            {
                                PostMessage(c.Handle, 0x200, 1, L(new PointF(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps)));
                                await Task.Delay(40);
                            }
                            PostMessage(c.Handle, 0x202, 0, L(to)); await Task.Delay(200);
                        }
                        void Check(string what, bool ok, string detail) => AppLog.Info($"Mouse: {(ok ? "ok  " : "FAIL")} {what}: {detail}");
                        string R(RectangleF r) => $"{r.X:0.0},{r.Y:0.0} {r.Width:0.0}x{r.Height:0.0}";
                        try
                        {
                            await Task.Delay(500);
                            // The crop box: moved, a corner pulled in, an edge pulled in
                            var r0 = preview.DebugBoxOnScreen;
                            var mid = new PointF(r0.X + r0.Width / 2, r0.Y + r0.Height / 2);
                            await Drag(preview, mid, new PointF(mid.X - 30, mid.Y + 15));
                            var r1 = preview.DebugBoxOnScreen;
                            Check("crop move", Math.Abs(r1.X - r0.X + 30) < 1.5 && Math.Abs(r1.Y - r0.Y - 15) < 1.5 && Math.Abs(r1.Width - r0.Width) < 1, $"{R(r0)} -> {R(r1)}");
                            await Drag(preview, new PointF(r1.Right, r1.Bottom), new PointF(r1.Right - 40, r1.Bottom - 40));
                            var r2 = preview.DebugBoxOnScreen;
                            Check("crop corner", r2.Width < r1.Width - 20 && Math.Abs(r2.Width / r2.Height - r1.Width / r1.Height) < 0.03 && Math.Abs(r2.X - r1.X) < 1.5 && Math.Abs(r2.Y - r1.Y) < 1.5,
                                $"{R(r1)} -> {R(r2)} (shape {r1.Width / r1.Height:0.000} -> {r2.Width / r2.Height:0.000}, top left stays)");
                            var edge = new PointF(r2.X, r2.Y + r2.Height / 2);
                            await Drag(preview, edge, new PointF(edge.X + 20, edge.Y));
                            var r3 = preview.DebugBoxOnScreen;
                            Check("crop edge", r3.Width < r2.Width - 10 && Math.Abs(r3.Right - r2.Right) < 1.5, $"{R(r2)} -> {R(r3)} (right side stays)");

                            // A crop key point dragged later
                            var g = (int)Math.Round(Tracks.Gutter * keyLane.DeviceDpi / 96f);
                            var t0 = keyLane.KeyTimes.First();
                            var k = keyLane.DebugKey(0);
                            await Drag(keyLane, k, new PointF(k.X + 25, k.Y));
                            var t1 = keyLane.KeyTimes.First();
                            var want = 25 * timeline.Duration / (keyLane.Width - g);
                            Check("crop key drag", Math.Abs(t1 - t0 - want) < want * 0.15 + 0.05 && keyLane.SelectedKey == 0, $"{t0:0.00} -> {t1:0.00} s (want +{want:0.00}), selected {keyLane.SelectedKey}");

                            // A volume key point dragged up
                            if (trackUi.Count > 0 && trackUi[0].Lane is { Keys.Count: > 1 } vol)
                            {
                                var (vt, vv, _) = vol.Keys[1];
                                var p = vol.DebugKey(1);
                                await Drag(vol, p, new PointF(p.X, p.Y - 12));
                                var (nt, nv, _) = vol.Keys[1];
                                Check("volume key drag", nv > vv + 0.05 && Math.Abs(nt - vt) < timeline.Duration * 0.01, $"{vv:0.00} at {vt:0.0}s -> {nv:0.00} at {nt:0.0}s");
                            }

                            // A press on the timeline moves the playhead there
                            var tx = timeline.Width * 3 / 4;
                            await Drag(timeline, new PointF(tx, timeline.Height - 10), new PointF(tx, timeline.Height - 10), 1);
                            var ratio = timeline.Current / timeline.Duration;
                            Check("timeline click", Math.Abs(timeline.Current - timeline.Duration * (tx - g) / (timeline.Width - g)) < timeline.Duration * 0.02 || ratio is > 0.7 and < 0.8,
                                $"playhead at {timeline.Current:0.0}s of {timeline.Duration:0.0}s ({ratio:P0} along)");
                        }
                        catch (Exception ex) { AppLog.Error("Mouse sim", ex); }
                        AppLog.Info("Mouse: done");
                    }));
                    break;
                case "settings": BeginInvoke(ShowSettings); break;
                case "help": BeginInvoke(ShowHelp); break;
                case "menu": BeginInvoke(() => moreBtn.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(moreBtn, [EventArgs.Empty])); break;
                case "save": BeginInvoke(() => Compress(value)); break;
            }
        }
    }

    /// <summary>For the drag test: times every message a control handles, by control and message</summary>
    sealed class MessageSpy : NativeWindow
    {
        public static readonly Dictionary<string, (int Count, double Ms)> Totals = [];
        /// <summary>The count and time so far for one control and message ("Timeline 0x200"); safe from any thread</summary>
        public static (int Count, double Ms) Of(string key) { lock (Totals) return Totals.GetValueOrDefault(key); }
        readonly string name;
        public MessageSpy(Control c) { name = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name; AssignHandle(c.Handle); }
        protected override void WndProc(ref Message m)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            base.WndProc(ref m);
            var key = $"{name} 0x{m.Msg:x}";
            lock (Totals)
            {
                var (n, ms) = Totals.GetValueOrDefault(key);
                Totals[key] = (n + 1, ms + watch.Elapsed.TotalMilliseconds);
            }
        }
    }

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int msg, nint wparam, nint lparam);
    /// <summary>When each playback frame was shown (Environment.TickCount64), for the playzoom test</summary>
    readonly List<long> debugFrames = [];
    partial void TestFrameShown() => debugFrames.Add(Environment.TickCount64);
    partial void TestNote(string message) => AppLog.Info(message);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
}
