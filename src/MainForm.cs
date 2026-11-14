using System.Collections.Specialized;

namespace CompressForDiscord;

sealed class MainForm : DarkForm
{
    const int ContentWidth = 640;

    readonly string file;
    readonly long fileSize;
    readonly AppConfig cfg = AppConfig.Current;

    ProbeResult? probe;
    Dictionary<string, double> calibration = [];
    string? autoEncoder;
    double targetMb;
    int? resolution, frameRate;
    /// <summary>Keep the sound (the ticked tracks, mixed) or drop it</summary>
    bool audioOn;
    /// <summary>The per-track panel under Audio is open</summary>
    bool advOpen;
    /// <summary>The user picked tracks themselves, so don't pick for them</summary>
    bool audioTouched;
    bool[] silent = [];
    /// <summary>mp4, or with the audio off an animated image: webp, gif, apng</summary>
    string format = "mp4";
    /// <summary>" · size · resolution · bitrate" shown after the file name</summary>
    string details;
    readonly List<(AudioTrack Track, CheckBox Box, VolumeLane Lane, Control Row)> trackUi = [];

    CancellationTokenSource? encodeCts, frameCts, playCts;
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 50 };
    double pendingFrame;
    /// <summary>Where the current playback stops when it isn't the end of the trim (double-click play)</summary>
    double? playTo;
    string? outPath;

    readonly Label subtitle, timeLabel, summary, status;
    readonly PictureBox preview;
    readonly Timeline timeline;
    readonly LinkLabel resetLink;
    readonly FlowLayoutPanel sizeChips, resChips, fpsChips, encChips, audioChips, audioPanel, buttons;
    readonly Label audioLabel, encLabel, formatLabel;
    readonly LinkLabel advLink;
    readonly Dropdown formatDrop = new();
    readonly TextBox customMb;
    readonly Bar bar;
    readonly Pill goBtn, moreBtn, downloadBtn, settingsBtn, playBtn;
    readonly DarkMenu goMenu;
    readonly ToolTip tips = new();

    // Rows that stretch when the window is dragged wider
    readonly TableLayoutPanel root, options, bottom;
    readonly Panel titleRow, timeRow;
    readonly List<FlowLayoutPanel> chipRows = [];
    /// <summary>Sizes at the smallest window, and how much the user has dragged it bigger</summary>
    int baseW, basePreviewH, baseChipMax, extraW, extraH;
    bool userSizing, fitting;

    public MainForm(string file)
    {
        this.file = file;
        fileSize = new FileInfo(file).Length;
        targetMb = cfg.TargetMb;

        SuspendLayout();
        Text = $"{Path.GetFileName(file)} · Compress for Discord";
        // Opens at its smallest size; drag an edge for a longer timeline and bigger preview
        FormBorderStyle = FormBorderStyle.Sizable;

        root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(18, 14), Margin = Padding.Empty };

        // Top row: the file's details on the left; update link, Settings and Help on the right
        titleRow = new Panel { Size = new Size(ContentWidth, 26), Margin = new Padding(0, 0, 0, 8) };
        details = $" · {Planner.FormatBytes(fileSize)}";
        subtitle = new Label { Text = Path.GetFileName(file) + details, Dock = DockStyle.Fill, UseMnemonic = false, ForeColor = Theme.Header, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Padding = new Padding(8, 3, 0, 0) };
        links.Controls.Add(UpdateLink(this));
        links.Controls.Add(Link("Settings", ShowSettings));
        var help = Link("Help", ShowHelp);
        help.Margin = Padding.Empty;
        links.Controls.Add(help);
        titleRow.Controls.Add(subtitle);
        titleRow.Controls.Add(links);
        // Deferred: changing the text in the middle of a layout pass leaves the label at its default width
        subtitle.SizeChanged += (_, _) => { if (IsHandleCreated) BeginInvoke(FitSubtitle); };

        preview = new PictureBox { Size = new Size(ContentWidth, 300), BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 10) };

        timeline = new Timeline { Size = new Size(ContentWidth, 68), Enabled = false, Margin = Padding.Empty };
        timeline.RangeChanged += () => { UpdateTimeLabel(); UpdateSummary(); };
        timeline.Seeked += t => { StopPlayback(); RequestFrame(t); };
        // Double-click plays from that spot to the end of the video, so you can watch what's outside the trim
        timeline.PlayRequested += t => { if (probe != null && encodeCts == null) { StopPlayback(); _ = PlayFrom(t, probe.Duration); } };

        timeRow = new Panel { Size = new Size(ContentWidth, 30), Margin = new Padding(0, 6, 0, 10) };
        playBtn = new Pill("▶  Play") { Dock = DockStyle.Left, Enabled = false };
        playBtn.Click += (_, _) => TogglePlay();
        timeLabel = new Label { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 0, 0), ForeColor = Theme.Muted, Font = new Font("Consolas", 9.5f), TextAlign = ContentAlignment.MiddleLeft };
        resetLink = Link("Use whole video", () => { timeline.Start = 0; timeline.End = timeline.Duration; timeline.Invalidate(); UpdateTimeLabel(); UpdateSummary(); });
        resetLink.Dock = DockStyle.Right;
        resetLink.Visible = false;
        resetLink.Margin = Padding.Empty;
        resetLink.Padding = new Padding(0, 6, 0, 0);
        timeRow.Controls.Add(timeLabel);
        timeRow.Controls.Add(resetLink);
        timeRow.Controls.Add(playBtn);

        // Options: label column + chip rows
        options = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg2, Padding = new Padding(12, 10, 12, 6), Margin = new Padding(0, 0, 0, 10), MinimumSize = new Size(ContentWidth, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        FlowLayoutPanel ChipRow()
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(ContentWidth - 92 - 24, 0), Margin = Padding.Empty, BackColor = Theme.Bg2 };
            chipRows.Add(row);
            return row;
        }
        Label OptLabel(string t) => new() { Text = t, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 5, 16, 0) };

        sizeChips = ChipRow();
        customMb = new TextBox { BackColor = Theme.Bg2, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, PlaceholderText = "Custom", TextAlign = HorizontalAlignment.Center };
        var customBox = new RoundBox { Margin = new Padding(0, 0, 6, 6), Cursor = Cursors.IBeam };
        customMb.GotFocus += (_, _) => customBox.Invalidate();
        customMb.LostFocus += (_, _) => customBox.Invalidate();
        customBox.Controls.Add(customMb);
        customBox.Click += (_, _) => customMb.Focus();
        customMb.Leave += (_, _) => { if (customMb.Text != "" && !double.TryParse(customMb.Text, out _)) customMb.Text = ""; };
        foreach (var mb in Planner.LimitPresets) sizeChips.Controls.Add(Chip(Planner.FormatBytes(mb * Planner.MB), mb, sizeChips, v => { customMb.Text = ""; SetTarget((double)v!); }));
        sizeChips.Controls.Add(customBox);
        if (!Planner.LimitPresets.Contains(targetMb)) customMb.Text = targetMb.ToString("0.##");
        SelectChip(sizeChips, targetMb);
        customMb.TextChanged += (_, _) =>
        {
            if (!double.TryParse(customMb.Text, out var n) || n <= 0) return;
            SelectChip(sizeChips, null);
            SetTarget(n);
        };

        resChips = ChipRow();
        fpsChips = ChipRow();
        encChips = ChipRow();
        // "GPU" means NVIDIA or AMD hardware encoding when this PC has it (the default); the chip hides itself if it doesn't
        foreach (var (label, value) in new[] { ("GPU", "auto"), ("CPU", "x264") })
            encChips.Controls.Add(Chip(label, value, encChips, v => { cfg.Encoder = (string)v!; cfg.Save(); UpdateSummary(); }));
        SelectChip(encChips, cfg.Encoder == "x264" ? "x264" : "auto");

        audioPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Bg2 };
        audioLabel = OptLabel("Audio");
        encLabel = OptLabel("Encoder");
        formatLabel = OptLabel("Format");
        // Dropping the audio frees its bitrate for the video, which can be what makes a clip fit
        audioChips = ChipRow();
        audioChips.Controls.Add(Chip("On", true, audioChips, v => SetAudio((bool)v!)));
        audioChips.Controls.Add(Chip("Off", false, audioChips, v => SetAudio((bool)v!)));
        advLink = Link("", () => { advOpen = !advOpen; ShowAudioRows(); });
        advLink.Margin = new Padding(0, 0, 0, 8);
        // With the audio off it can also be an animated image
        formatDrop.SetItems([("MP4 video", "mp4"), ("Animated WebP", "webp"), ("GIF", "gif"), ("APNG", "apng")], "mp4");
        formatDrop.Picked += v => SetFormat((string)v);

        options.Controls.Add(OptLabel("Size"), 0, 0); options.Controls.Add(sizeChips, 1, 0);
        options.Controls.Add(OptLabel("Resolution"), 0, 1); options.Controls.Add(resChips, 1, 1);
        options.Controls.Add(OptLabel("Frame rate"), 0, 2); options.Controls.Add(fpsChips, 1, 2);
        options.Controls.Add(encLabel, 0, 3); options.Controls.Add(encChips, 1, 3);
        options.Controls.Add(audioLabel, 0, 4); options.Controls.Add(audioChips, 1, 4);
        options.Controls.Add(advLink, 1, 5);
        options.Controls.Add(audioPanel, 1, 6);
        options.Controls.Add(formatLabel, 0, 7); options.Controls.Add(formatDrop.Button, 1, 7);
        audioLabel.Visible = audioChips.Visible = advLink.Visible = audioPanel.Visible = formatLabel.Visible = formatDrop.Button.Visible = false;

        bar = new Bar { Size = new Size(ContentWidth, 8), Visible = false, Margin = new Padding(0, 0, 0, 8) };

        // Bottom row: the result (or what's happening) on the left, the buttons on the right.
        // Sized by its content (not a fixed height) so the buttons never get clipped at other display scalings
        bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, MinimumSize = new Size(ContentWidth, 0), Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // One spot, two labels: the result line normally, the status line while something is going on
        summary = new Label { Dock = DockStyle.Fill, UseMnemonic = false, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        status = new Label { Dock = DockStyle.Fill, UseMnemonic = false, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Visible = false };
        var info = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Height = 10 };
        info.Controls.Add(summary);
        info.Controls.Add(status);
        buttons = new FlowLayoutPanel { Anchor = AnchorStyles.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        // Split button: "Compress & copy", and a ▾ holding the one extra action, "Compress & save…".
        // There's no Close or Cancel button: while it's compressing, the button itself cancels.
        moreBtn = new Pill("▾", PillKind.Primary) { Enabled = false, MinWidth = 34, FlatLeft = true, Margin = Padding.Empty };
        goBtn = new Pill("Compress & copy", PillKind.Primary) { Enabled = false, FlatRight = true, Margin = new Padding(8, 0, 1, 0) };
        goMenu = new DarkMenu();
        goMenu.Add("Compress && save…", "Ctrl+S", () => Go(save: true));
        moreBtn.Click += (_, _) => goMenu.ShowUnder(goBtn, moreBtn.Right - goBtn.Left, alignRight: true);
        downloadBtn = new Pill("Download ffmpeg", PillKind.Primary) { Visible = false, Margin = new Padding(8, 0, 0, 0) };
        settingsBtn = new Pill("Settings…", PillKind.Secondary) { Visible = false, Margin = new Padding(8, 0, 0, 0) };
        goBtn.Click += (_, _) => { if (encodeCts != null) encodeCts.Cancel(); else Go(save: false); };
        downloadBtn.Click += (_, _) => { if (encodeCts != null) encodeCts.Cancel(); else DownloadFfmpeg(); };
        settingsBtn.Click += (_, _) => ShowSettings();
        buttons.Controls.AddRange([moreBtn, goBtn, downloadBtn, settingsBtn]);
        bottom.Controls.Add(info, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        root.Controls.AddRange([titleRow, preview, timeline, timeRow, options, bar, bottom]);
        Controls.Add(root);
        AcceptDrop(f => { if (encodeCts == null && outPath == null) Program.OpenVideo(f, this); });
        // Clicking anywhere else stops typing in the custom size box
        void HookClicks(Control c)
        {
            if (c == customMb) return;
            c.MouseDown += (_, _) => { if (customMb.Focused) LeaveCustomBox(); };
            foreach (Control child in c.Controls) HookClicks(child);
        }
        HookClicks(this);

        frameTimer.Tick += (_, _) => LoadFrame();
        ResumeLayout(false);
        PerformLayout();
    }

    // ---------- startup ----------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CaptureBase();
        _ = Task.Run(Paths.CleanupStale);
        _ = Task.Run(Log.Calibration).ContinueWith(t => BeginInvoke(() => { calibration = t.Result; UpdateSummary(); }), TaskScheduler.Default);
        await LoadVideo();
    }


    async Task LoadVideo()
    {
        if (probe != null) return; // already loaded (Settings re-reports ffmpeg)
        var err = Ff.Locate(cfg.FfmpegPath);
        var missing = err != null;
        settingsBtn.Visible = downloadBtn.Visible = missing;
        goBtn.Visible = moreBtn.Visible = !missing;
        if (err != null) { ShowStatus(err, Theme.Danger); return; }
        ShowStatus("Reading video…", Theme.Muted);
        try { probe = await Ff.Probe(file); }
        catch (Exception ex) { ShowStatus(ex.Message, Theme.Danger); return; }
        if (IsDisposed) return;

        var shortSide = Math.Min(probe.Width, probe.Height);
        var fps = (int)Math.Round(probe.Fps);
        var srcKbps = (probe.Bitrate > 0 ? probe.Bitrate : fileSize * 8 / Math.Max(probe.Duration, 0.1)) / 1000.0;
        details = $" · {Planner.FormatBytes(fileSize)} · {shortSide}p{fps} · {Planner.FormatRate(srcKbps)}";
        FitSubtitle();

        timeline.Fps = probe.Fps;
        timeline.Reset(probe.Duration);
        timeline.Enabled = true;
        playBtn.Enabled = true;
        ActiveControl = timeline;

        resChips.Controls.Add(Chip($"{shortSide}p", null, resChips, v => { resolution = (int?)v; UpdateSummary(); }));
        foreach (var r in Planner.Resolutions.Where(r => r < shortSide))
            resChips.Controls.Add(Chip($"{r}p", r, resChips, v => { resolution = (int?)v; UpdateSummary(); }));
        SelectChip(resChips, null);
        fpsChips.Controls.Add(Chip($"{fps}", null, fpsChips, v => { frameRate = (int?)v; UpdateSummary(); }));
        foreach (var f in Planner.FrameRates.Where(f => f < probe.Fps - 0.5))
            fpsChips.Controls.Add(Chip($"{f}", f, fpsChips, v => { frameRate = (int?)v; UpdateSummary(); }));
        SelectChip(fpsChips, null);

        // One row per audio track (tick to include it, waveform with its volume line), shown under "Advanced"
        silent = new bool[probe.AudioTracks.Count];
        var def = probe.AudioTracks.FirstOrDefault(t => t.IsDefault) ?? probe.AudioTracks.FirstOrDefault();
        foreach (var t in probe.AudioTracks)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4), BackColor = Theme.Bg2 };
            // Added after the form scaled, so sizes are in real pixels here
            var box = new CheckBox { Text = t.Label, UseMnemonic = false, Checked = t == def, AutoSize = false, Height = D(34), Margin = new Padding(0, 0, 6, 0), ForeColor = Theme.Text, AutoEllipsis = true };
            var lane = new VolumeLane { Margin = Padding.Empty, Duration = probe.Duration, End = probe.Duration, Active = box.Checked };
            box.Click += (_, _) => audioTouched = true;
            box.CheckedChanged += (_, _) => { lane.Active = box.Checked; lane.Invalidate(); UpdateSummary(); RestartIfPlaying(); };
            // Hear a volume change once the mouse is let go
            lane.Committed += RestartIfPlaying;
            row.Controls.AddRange([box, lane]);
            audioPanel.Controls.Add(row);
            trackUi.Add((t, box, lane, row));
        }
        LayoutLanes();
        audioLabel.Visible = audioChips.Visible = true;
        SetAudio(def != null);
        _ = LoadPeaks();

        UpdateTimeLabel();
        UpdateSummary();
        ShowStatus("", Theme.Muted);
        RequestFrame(0);
        _ = ResolveAutoEncoder();
        _ = LoadThumbnails();
#if DEBUG
        ApplyTestState();
#endif
    }

#if DEBUG
    /// <summary>
    /// Debug builds only: puts the window into a state for screenshots and end-to-end tests without clicking.
    /// CFD_TEST="audio=mix;format=webp;fps=15;res=540;keys=1;menu=1;save=C:\\out.webp"
    /// </summary>
    void ApplyTestState()
    {
        foreach (var part in (Environment.GetEnvironmentVariable("CFD_TEST") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = (part.Split('=')[0], part[(part.IndexOf('=') + 1)..]);
            switch (key)
            {
                case "audio": SetAudio(value != "off"); break;
                case "adv": advOpen = true; ShowAudioRows(); break;
                case "grow":
                    // Same path as dragging the window edge
                    userSizing = true;
                    ClientSize += new Size(int.Parse(value.Split('x')[0]), int.Parse(value.Split('x')[1]));
                    userSizing = false;
                    FitToContent();
                    break;
                case "step": for (var i = 0; i < int.Parse(value); i++) HandleEditKey(Keys.Right); break;
                case "busy": SetBusy(true); ShowStatus("Compressing on GPU · 42%", Theme.Text); bar.Value = 0.42; break;
                case "format": formatDrop.Select(value); SetFormat(value); break;
                case "fps": frameRate = int.Parse(value); SelectChip(fpsChips, frameRate); UpdateSummary(); break;
                case "res": resolution = int.Parse(value); SelectChip(resChips, resolution); UpdateSummary(); break;
                case "size": targetMb = double.Parse(value); UpdateSummary(); break;
                case "trim": timeline.SetEnd(double.Parse(value)); break;
                case "keys":
                    foreach (var (_, box, lane, _) in trackUi.Take(3))
                    {
                        box.Checked = true;
                        lane.Keys.AddRange([(probe!.Duration * 0.2, 1), (probe.Duration * 0.4, 0.3), (probe.Duration * 0.7, 1.6)]);
                        lane.Active = true;
                        lane.Invalidate();
                    }
                    break;
                case "fmtmenu": BeginInvoke(() => formatDrop.Button.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(formatDrop.Button, [EventArgs.Empty])); break;
                case "menu": BeginInvoke(() => moreBtn.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(moreBtn, [EventArgs.Empty])); break;
                case "save": BeginInvoke(() => Go(save: true, value)); break;
            }
        }
    }
#endif

    async Task ResolveAutoEncoder()
    {
        try { autoEncoder = await Ff.PickEncoder("auto"); } catch { return; }
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (autoEncoder == "x264")
            {
                encChips.Controls.OfType<Pill>().First(p => (string?)p.Tag == "auto").Visible = false;
                SelectChip(encChips, "x264");
            }
            UpdateSummary();
        });
    }

    async Task LoadThumbnails()
    {
        var height = timeline.Height;
        await Ff.Thumbnails(file, probe!.Duration, timeline.Thumbs.Length, height, (i, img) =>
        {
            if (IsDisposed) { img.Dispose(); return; }
            BeginInvoke(() => { timeline.Thumbs[i] = img; timeline.Invalidate(); });
        }, lifetime.Token);
    }

    // ---------- preview frame ----------

    void RequestFrame(double t)
    {
        pendingFrame = t;
        UpdateTimeLabel();
        frameTimer.Stop();
        frameTimer.Start();
    }

    async void LoadFrame()
    {
        frameTimer.Stop();
        frameCts?.Cancel();
        var cts = frameCts = new CancellationTokenSource();
        var img = await Ff.Frame(file, pendingFrame, preview.Height, cts.Token);
        if (cts.IsCancellationRequested || IsDisposed) { img?.Dispose(); return; }
        if (img == null) return;
        var old = preview.Image;
        preview.Image = img;
        old?.Dispose();
    }

    // ---------- playback ----------

    void TogglePlay()
    {
        if (playCts != null) { StopPlayback(); return; }
        if (probe == null || encodeCts != null) return;
        // Resume from the playhead if it's inside the range, else from the start
        var from = timeline.Current >= timeline.Start && timeline.Current < timeline.End - 0.1 ? timeline.Current : timeline.Start;
        _ = PlayFrom(from);
    }

    void StopPlayback()
    {
        if (playCts == null) return;
        playCts.Cancel();
        playCts = null;
        playBtn.Text = "▶  Play";
    }

    void RestartIfPlaying()
    {
        if (playCts == null) return;
        var t = timeline.Current;
        StopPlayback();
        _ = PlayFrom(t, playTo);
    }

    /// <param name="to">where to stop; the end of the trim unless given</param>
    async Task PlayFrom(double from, double? to = null)
    {
        playTo = to;
        var cts = playCts = new CancellationTokenSource();
        frameTimer.Stop();
        frameCts?.Cancel();
        playBtn.Text = "❚❚  Pause";
        try
        {
            await Player.Play(file, from, to ?? timeline.End, Mix(), preview.ClientSize, probe!.Width, probe.Height, probe.Fps, (bmp, t) =>
            {
                if (cts.IsCancellationRequested || IsDisposed) { bmp.Dispose(); return; }
                var old = preview.Image;
                preview.Image = bmp;
                old?.Dispose();
                timeline.ShowTime(t);
                UpdateTimeLabel();
            }, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) ShowStatus(ex.Message, Theme.Danger); }
        if (playCts == cts) StopPlayback();
        cts.Dispose();
    }

    // ---------- choices ----------

    Pill Chip(string text, object? value, FlowLayoutPanel group, Action<object?> onPick)
    {
        var chip = new Pill(text) { Tag = value };
        chip.Click += (_, _) =>
        {
            if (encodeCts != null) return;
            SelectChip(group, value);
            onPick(value);
        };
        return chip;
    }

    static void SelectChip(FlowLayoutPanel group, object? value)
    {
        foreach (var p in group.Controls.OfType<Pill>()) p.On = Equals(p.Tag, value);
    }

    void SetTarget(double mb)
    {
        targetMb = mb;
        // Remembered for next time
        cfg.TargetMb = mb;
        cfg.Save();
        UpdateSummary();
    }

    // Lane widths in real pixels, from the room the chip rows have: a lane alone, or next to a track's checkbox when mixing
    int LaneRoom => sizeChips.MaximumSize.Width - 16;
    int LaneMix => LaneRoom * 6 / 10;
    int D(int v) => (int)Math.Round(v * timeline.DeviceDpi / 96f);

    /// <summary>File name, size, resolution and bitrate on one line; a long name is cut short, never the details</summary>
    void FitSubtitle()
    {
        var name = Path.GetFileName(file);
        var text = name + details;
        while (name.Length > 8 && TextRenderer.MeasureText(text, subtitle.Font).Width > subtitle.Width - D(12))
        {
            name = name[..^1];
            text = name.TrimEnd() + "…" + details;
        }
        if (subtitle.Text != text) subtitle.Text = text;
    }

    void LayoutLanes()
    {
        foreach (var (_, box, lane, _) in trackUi)
        {
            box.Width = LaneRoom - LaneMix - 6;
            lane.PixelWidth = LaneMix;
        }
    }

    void SetAudio(bool on)
    {
        audioOn = on && trackUi.Count > 0;
        SelectChip(audioChips, audioOn);
        ShowAudioRows();
        ShowEncoderRow();
        UpdateSummary();
        RestartIfPlaying();
    }

    void ShowAudioRows()
    {
        advLink.Text = advOpen ? "Advanced ▾" : "Advanced ▸";
        advLink.Visible = audioOn;
        audioPanel.Visible = audioOn && advOpen;
        formatLabel.Visible = formatDrop.Button.Visible = !audioOn;
    }

    /// <summary>Measures each track for its waveform, marks the silent ones and starts on one you can hear</summary>
    async Task LoadPeaks()
    {
        foreach (var (track, box, lane, _) in trackUi)
        {
            float[]? peaks;
            try { peaks = await Ff.Peaks(file, track.Index, 800, lifetime.Token); } catch { return; }
            if (IsDisposed) return;
            lane.Peaks = peaks;
            lane.Invalidate();
            silent[track.Index] = peaks != null && peaks.Max() < 0.01f;
            if (silent[track.Index]) box.Text = track.Label + " · silent";
        }
        var ticked = trackUi.Where(t => t.Box.Checked).ToList();
        if (!audioTouched && ticked.All(t => silent[t.Track.Index]) && Array.FindIndex(silent, s => !s) is >= 0 and var heard)
            foreach (var (track, box, _, _) in trackUi) box.Checked = track.Index == heard;
    }

    bool Animated => !audioOn && format != "mp4";
    string FormatName => format switch { "webp" => "Animated WebP", "gif" => "GIF", "apng" => "APNG", _ => "MP4" };
    string OutputExt => !Animated ? "mp4" : format == "apng" ? "png" : format;
    /// <summary>Frame rate of an animated image: the chosen one, never above 30</summary>
    int AnimFps => Math.Min(frameRate ?? (int)Math.Round(probe!.Fps), 30);

    void SetFormat(string f)
    {
        format = f;
        // Animated images get big fast, so start them at 15 fps unless a low rate is already chosen
        if (Animated && (frameRate ?? probe!.Fps) > 30 && fpsChips.Controls.OfType<Pill>().Any(p => Equals(p.Tag, 15)))
        {
            frameRate = 15;
            SelectChip(fpsChips, 15);
        }
        ShowEncoderRow();
        UpdateSummary();
    }

    // GPU/CPU only applies to video
    void ShowEncoderRow() => encLabel.Visible = encChips.Visible = !Animated;

    List<TrackMix> Mix() => probe == null || !audioOn ? []
        : trackUi.Where(t => t.Box.Checked).Select(t => new TrackMix(t.Track.Index, t.Lane.Volume, [.. t.Lane.Keys])).ToList();

    // ---------- window size ----------

    void CaptureBase() => (baseW, basePreviewH, baseChipMax) = (preview.Width, preview.Height, sizeChips.MaximumSize.Width);

    /// <summary>Makes every row as wide, and the preview as tall, as the window was dragged</summary>
    void ApplyStretch()
    {
        var w = baseW + extraW;
        root.SuspendLayout();
        foreach (var c in new Control[] { titleRow, preview, timeline, timeRow, bar }) c.Width = w;
        preview.Height = basePreviewH + extraH;
        options.MinimumSize = bottom.MinimumSize = new Size(w, 0);
        foreach (var row in chipRows) row.MaximumSize = new Size(baseChipMax + extraW, 0);
        LayoutLanes();
        root.ResumeLayout(true);
        // The window is fitted to the root panel right after this, so the panel takes its new size now
        root.Size = root.GetPreferredSize(Size.Empty);
    }

    protected override void OnResizeBegin(EventArgs e) { base.OnResizeBegin(e); userSizing = true; }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); userSizing = false; FitToContent(); }

    /// <summary>
    /// The window always exactly fits its content (DarkForm). Dragging an edge therefore works the other way
    /// round: the difference is given to the content (wider rows, taller preview), which the window then fits.
    /// </summary>
    protected override void FitToContent()
    {
        if (fitting || Controls.Count == 0 || WindowState != FormWindowState.Normal) return;
        fitting = true;
        try
        {
            if (userSizing && baseW > 0)
            {
                var c = Controls[0];
                int dw = ClientSize.Width - (c.Right + c.Left), dh = ClientSize.Height - (c.Bottom + c.Top);
                if (dw != 0 || dh != 0)
                {
                    (extraW, extraH) = (Math.Max(0, extraW + dw), Math.Max(0, extraH + dh));
                    ApplyStretch();
                }
            }
            // The minimum is the size with nothing stretched; lift it first so the window can also shrink to fit
            MinimumSize = Size.Empty;
            base.FitToContent();
            if (baseW > 0) MinimumSize = new Size(Width - extraW, Height - extraH);
        }
        finally { fitting = false; }
    }

    // Moving to a screen with other scaling: go back to the smallest size and measure again
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (baseW > 0 && (extraW | extraH) != 0) { (extraW, extraH) = (0, 0); ApplyStretch(); }
        base.OnDpiChanged(e);
        BeginInvoke(CaptureBase);
    }

    string EffectiveEncoder => cfg.Encoder == "auto" ? autoEncoder ?? "nvenc" : cfg.Encoder;

    Plan? CurrentPlan()
    {
        var duration = timeline.End - timeline.Start;
        if (probe == null || duration <= 0) return null;
        var cal = calibration.TryGetValue(EffectiveEncoder, out var c) ? c : 1;
        return Planner.Make(probe, duration, (long)(targetMb * Planner.MB), Mix().Count > 0, cfg.SafetyMarginPercent, cal, resolution, frameRate);
    }

    bool Trimmed => probe != null && (timeline.Start > 0.05 || timeline.End < probe.Duration - 0.05);

    void UpdateTimeLabel()
    {
        timeLabel.Text = $"{Planner.FormatTime(timeline.Current)}    {Planner.FormatTime(timeline.Start)} – {Planner.FormatTime(timeline.End)}  ({Planner.FormatTime(timeline.End - timeline.Start)})";
        resetLink.Visible = Trimmed && encodeCts == null;
        foreach (var (_, _, lane, row) in trackUi)
        {
            (lane.Start, lane.End, lane.Current) = (timeline.Start, timeline.End, timeline.Current);
            if (row.Visible) lane.Invalidate();
        }
    }

    /// <summary>
    /// Colour for the result line: green when the bitrate suits the resolution and frame rate, yellow when it's
    /// on the low side, red when it will look bad. With a suggestion of what to lower.
    /// </summary>
    (Color Color, string Hint) Quality(Plan plan, int fps)
    {
        // Already as good as the source: nothing to gain
        if (plan.CappedBySource) return (Theme.Green, "");
        const double Good = 0.05; // bits per pixel per frame that H.264 needs to look clean
        double shortSrc = Math.Min(probe!.Width, probe.Height), aspect = Math.Max(probe.Width, probe.Height) / shortSrc;
        double Bits(int side, int f) => plan.VideoKbps * 1000.0 / (side * side * aspect * f);
        var bits = Bits(plan.OutputShortSide, fps);
        if (bits >= Good) return (Theme.Green, "");
        var better = Planner.Resolutions.Where(r => r < plan.OutputShortSide && Bits(r, fps) >= Good).Select(r => (int?)r).FirstOrDefault();
        var hint = better != null ? $" · try {better}p" : fps > 30 ? " · try 30 fps" : " · try a shorter clip";
        return (bits >= Good / 2 ? Theme.Warn : Theme.Danger, hint);
    }

    /// <param name="keepStatus">leave an error or "Cancelled" showing instead of going back to the result line</param>
    void UpdateSummary(bool keepStatus = false)
    {
        if (outPath != null || probe == null) return;
        if (!keepStatus && encodeCts == null && status.Visible) ShowStatus("", Theme.Muted);
        var limit = Planner.FormatBytes(targetMb * Planner.MB);
        if (Animated)
        {
            // The size is only known once it's made: it's shrunk until it fits
            summary.Text = $"{FormatName} · up to {resolution ?? Math.Min(probe.Width, probe.Height)}p{AnimFps} · under {limit}";
            summary.ForeColor = Theme.Text;
            CanGo(timeline.End > timeline.Start);
            return;
        }
        var plan = CurrentPlan();
        if (plan == null) return;
        if (plan.Feasible)
        {
            var fps = plan.Fps ?? (int)Math.Round(probe.Fps);
            var (color, hint) = Quality(plan, fps);
            summary.Text = $"~{Planner.FormatBytes(plan.EstimateBytes)} · {plan.OutputShortSide}p{fps} · {Planner.FormatRate(plan.VideoKbps + plan.AudioKbps)}{hint}";
            summary.ForeColor = color;
        }
        else
        {
            summary.Text = $"Too long for {limit}. Trim to under {Planner.FormatTime(plan.MaxDuration)}.";
            summary.ForeColor = Theme.Danger;
        }
        CanGo(plan.Feasible);
    }

    // While compressing, the main button stays live: it's the cancel button
    void CanGo(bool ok)
    {
        goBtn.Enabled = ok || encodeCts != null;
        moreBtn.Enabled = ok && encodeCts == null;
    }

    void ShowStatus(string text, Color color)
    {
        status.Text = text;
        status.ForeColor = color;
        status.Visible = text != "";
        summary.Visible = text == "";
        // Long errors get cut off in one line; hovering shows all of it
        tips.SetToolTip(status, text.Length > 50 ? text : null);
    }

    // ---------- compress ----------

    string OutputName => Path.GetFileNameWithoutExtension(file) + (Trimmed ? "-trimmed" : "") + "-compressed." + OutputExt;

    /// <summary>Asks where to save; starts in the last-used folder, else next to the source video</summary>
    string? AskSavePath()
    {
        var folder = Directory.Exists(cfg.SaveFolder) ? cfg.SaveFolder : Path.GetDirectoryName(file);
        using var dlg = new SaveFileDialog { Title = "Save compressed video", Filter = $"{FormatName}|*.{OutputExt}", FileName = OutputName, InitialDirectory = folder, OverwritePrompt = true };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    /// <param name="saveTo">skips the save dialog (tests)</param>
    async void Go(bool save, string? saveTo = null)
    {
        if (probe == null || encodeCts != null || !goBtn.Enabled || !goBtn.Visible) return;
        var animated = Animated;
        var plan = animated ? null : CurrentPlan();
        if (!animated && plan is not { Feasible: true }) return;
        var name = OutputName;

        StopPlayback();
        string? savePath = null;
        if (save && (savePath = saveTo ?? AskSavePath()) == null) return;
        encodeCts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<(string Stage, double Progress)>(p =>
        {
            ShowStatus($"{p.Stage} · {p.Progress * 100:0}%", Theme.Text);
            bar.Value = p.Progress;
        });
        try
        {
            var (temp, size) = animated ? await Ff.EncodeAnimated(new AnimRequest
            {
                Input = file,
                Format = format,
                Start = timeline.Start,
                Duration = timeline.End - timeline.Start,
                TargetBytes = (long)(targetMb * Planner.MB * (1 - Math.Max(0, cfg.SafetyMarginPercent) / 100)),
                MaxShortSide = Math.Min(resolution ?? int.MaxValue, Math.Min(probe.Width, probe.Height)),
                Fps = AnimFps,
                Portrait = probe.Height > probe.Width,
            }, progress, encodeCts.Token) : await Ff.Encode(new EncodeRequest
            {
                Input = file,
                Start = timeline.Start,
                Duration = timeline.End - timeline.Start,
                TargetBytes = plan!.TargetBytes,
                VideoKbps = plan.VideoKbps,
                AudioKbps = plan.AudioKbps,
                Tracks = Mix(),
                ScaleShortSide = plan.ScaleShortSide,
                Fps = plan.Fps,
                Portrait = probe.Height > probe.Width,
                Encoder = cfg.Encoder,
                CappedBySource = plan.CappedBySource,
                Calibration = plan.Calibration,
            }, progress, encodeCts.Token);

            if (savePath != null)
            {
                File.Move(temp, savePath, true);
                outPath = savePath;
                if (saveTo == null)
                {
                    cfg.SaveFolder = Path.GetDirectoryName(savePath) ?? "";
                    cfg.Save();
                }
                Done($"✓ Saved {Path.GetFileName(savePath)} ({Planner.FormatBytes(size)})");
            }
            else
            {
                outPath = Ff.MoveToOut(temp, name);
                // Same as Explorer's Copy, so Ctrl+V in Discord attaches the file
                Clipboard.SetFileDropList(new StringCollection { outPath });
                Done($"✓ Copied ({Planner.FormatBytes(size)}). Press Ctrl+V in Discord.");
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Cancelled", Theme.Muted);
            SetBusy(false);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, Theme.Danger);
            SetBusy(false);
        }
        finally
        {
            encodeCts?.Dispose();
            encodeCts = null;
            if (outPath == null) UpdateSummary(keepStatus: true);
        }
    }

    void SetBusy(bool busy)
    {
        bar.Visible = busy;
        bar.Value = 0;
        timeline.Enabled = !busy;
        playBtn.Enabled = !busy;
        customMb.Enabled = !busy;
        foreach (var (_, box, lane, _) in trackUi) box.Enabled = lane.Enabled = !busy;
        formatDrop.Button.Enabled = advLink.Enabled = !busy;
        foreach (var p in new[] { sizeChips, resChips, fpsChips, encChips, audioChips }.SelectMany(g => g.Controls.OfType<Pill>())) p.Enabled = !busy;
        // The button keeps its size and says what's happening; hovering turns it into Cancel
        goBtn.AltText = busy ? "Compressing…" : null;
        goBtn.HoverText = busy ? "✕  Cancel" : null;
        goBtn.Enabled = busy;
        moreBtn.Enabled = false;
        resetLink.Visible = !busy && Trimmed;
    }

    async void Done(string message)
    {
        // Chime, then get out of the way; the file is already on the clipboard or saved
        bar.Value = 1;
        ShowStatus(message, Theme.Green);
        var chime = AppConfig.Current.PlaySound;
        if (chime) try { Player.Chime(); } catch { }
        Hide();
        if (chime) await Task.Delay(Player.ChimeLength);
        Close();
    }

    void CloseOrCancel()
    {
        if (encodeCts != null) encodeCts.Cancel();
        else Close();
    }

    /// <summary>One-time ~90 MB download of ffmpeg into the app's own folder</summary>
    async void DownloadFfmpeg()
    {
        if (encodeCts != null) return;
        encodeCts = new CancellationTokenSource();
        settingsBtn.Enabled = false;
        (downloadBtn.AltText, downloadBtn.HoverText) = ("Downloading…", "✕  Cancel");
        bar.Value = 0;
        bar.Visible = true;
        var progress = new Progress<double>(p => { ShowStatus($"Downloading ffmpeg · {p * 100:0}%", Theme.Text); bar.Value = p; });
        try
        {
            await Task.Run(() => Ff.DownloadFfmpeg(progress, encodeCts.Token));
            encodeCts = null;
            bar.Visible = false;
            await LoadVideo();
        }
        catch (OperationCanceledException) { encodeCts = null; bar.Visible = false; ShowStatus("Download cancelled.", Theme.Muted); }
        catch (Exception ex) { encodeCts = null; bar.Visible = false; ShowStatus("Download failed: " + ex.Message, Theme.Danger); }
        finally { settingsBtn.Enabled = true; (downloadBtn.AltText, downloadBtn.HoverText) = (null, null); }
    }

    // ---------- menu ----------

    void PickVideo()
    {
        if (encodeCts != null) return;
        using var dlg = new OpenFileDialog { Title = "Choose a video to compress", Filter = Program.VideoFilter, InitialDirectory = Path.GetDirectoryName(file) };
        if (dlg.ShowDialog(this) == DialogResult.OK) Program.OpenVideo(dlg.FileName, this);
    }

    void ShowSettings()
    {
        if (encodeCts != null) return;
        using var s = new SettingsForm();
        s.FfmpegChanged += () => _ = LoadVideo();
        s.ShowDialog(this);
        UpdateSummary(); // encoder or margin may have changed
    }

    void ShowHelp()
    {
        using var h = new HelpForm();
        h.ShowDialog(this);
    }

    // ---------- keys & teardown ----------

    /// <summary>Hands the keyboard back to the timeline so Space, arrows etc. work again</summary>
    void LeaveCustomBox() => ActiveControl = timeline;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Typing a custom size: Enter, Esc or Tab just finish typing
        if (customMb.Focused && keyData is Keys.Enter or Keys.Escape or Keys.Tab) { LeaveCustomBox(); return true; }
        if (keyData == Keys.Escape) { CloseOrCancel(); return true; }
        if (HandleEditKey(keyData)) return true;
        switch (keyData)
        {
            case Keys.Enter: if (outPath != null) Close(); else Go(save: false); return true;
            case Keys.Control | Keys.S: Go(save: true); return true;
            case Keys.Control | Keys.O: PickVideo(); return true;
            case Keys.F1: ShowHelp(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Space play/pause, ←/→ frame step (Shift = 1 s), Q/I in, E/O out, Home/End</summary>
    bool HandleEditKey(Keys keyData)
    {
        if (probe == null || encodeCts != null || outPath != null || customMb.Focused) return false;
        var key = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (mods != Keys.None && !(mods == Keys.Shift && key is Keys.Left or Keys.Right)) return false;
        var step = mods == Keys.Shift ? 1 : 1 / (probe.Fps > 0 ? probe.Fps : 30);
        switch (key)
        {
            case Keys.Space: TogglePlay(); break;
            case Keys.Left: timeline.Seek(timeline.Current - step); break;
            case Keys.Right: timeline.Seek(timeline.Current + step); break;
            case Keys.Q or Keys.I: timeline.SetStart(timeline.Current); break;
            case Keys.E or Keys.O: timeline.SetEnd(timeline.Current); break;
            case Keys.Home: timeline.Seek(timeline.Start); break;
            case Keys.End: timeline.Seek(timeline.End); break;
            default: return false;
        }
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        playCts?.Cancel();
        lifetime.Cancel();
        encodeCts?.Cancel();
        frameCts?.Cancel();
        frameTimer.Stop();
        base.OnFormClosing(e);
    }
}
