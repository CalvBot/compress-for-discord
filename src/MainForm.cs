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
    /// <summary>Output size as a percentage of the source (or of the crop); null = 100%</summary>
    int? scalePct;
    int? frameRate;

    /// <summary>A crop box at a moment: its centre and width as fractions of the frame. Its height follows from the shape.</summary>
    readonly record struct CropKey(double T, double Cx, double Cy, double W);
    /// <summary>Shape of the picture: src (the video's own), 16:9, 1:1, 9:16 or free</summary>
    string cropMode = "src";
    /// <summary>The preview shows the result, not the editing view</summary>
    bool showResult;
    /// <summary>Width / height of the crop box in pixels. Every key point shares it, so the output never changes size.</summary>
    double cropAspect;
    /// <summary>One point = a fixed crop. More = the box moves and zooms between them.</summary>
    readonly List<CropKey> cropKeys = [];
    /// <summary>There is sound and the master lane isn't muted</summary>
    bool AudioOn => trackUi.Count > 0 && masterLane.Active;
    /// <summary>The section under the timeline (crop key points, audio tracks) is open</summary>
    bool tracksOpen;
    /// <summary>" · size · resolution · bitrate" shown after the file name</summary>
    string details;
    readonly List<(AudioTrack Track, VolumeLane Lane)> trackUi = [];

    CancellationTokenSource? encodeCts, frameCts, playCts;
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 50 };
    double pendingFrame;
    /// <summary>Where the current playback stops when it isn't the end of the trim (double-click play)</summary>
    double? playTo;
    string? outPath;

    readonly Label subtitle, timeLabel, summary, status;
    readonly CropView preview;
    readonly KeyLane keyLane;
    readonly ZoomBar zoomBar;
    /// <summary>The part of the video the timeline and tracks are showing (zoom)</summary>
    double viewStart, viewEnd;
    CancellationTokenSource? thumbCts;
    /// <summary>Waits for zooming to settle before remaking the thumbnails</summary>
    readonly System.Windows.Forms.Timer thumbTimer = new() { Interval = 350 };
    readonly Timeline timeline;
    readonly FlowLayoutPanel sizeChips, resChips, cropChips, fpsChips, encChips, tracksPanel, buttons;
    readonly VolumeLane masterLane;
    readonly TextBox customMb, fpsBox, timeBox;
    readonly RoundBox fpsField;
    readonly Pill previewBtn, advBtn;
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

        preview = new CropView { Size = new Size(ContentWidth, 300), Margin = new Padding(0, 0, 0, 10) };
        preview.BoxChanged += EditCrop;
        preview.BoxCommitted += () => UpdateSummary();

        timeline = new Timeline { Size = new Size(ContentWidth, 68), Enabled = false, Margin = Padding.Empty };
        timeline.RangeChanged += () => { UpdateTimeLabel(); UpdateSummary(); };
        // Key points of the crop, shown only while cropping
        keyLane = new KeyLane { Size = new Size(ContentWidth, 44), Margin = new Padding(0, 4, 0, 0) };
        keyLane.AddRequested += AddCropKey;
        keyLane.RemoveRequested += i => { if (cropKeys.Count > 1) cropKeys.RemoveAt(i); ShowCrop(); UpdateSummary(); };
        keyLane.MoveRequested += MoveCropKey;
        keyLane.Picked += i => timeline.Seek(cropKeys[i].T);
        timeline.Seeked += t => { StopPlayback(); RequestFrame(t); };
        // Double-click plays from that spot to the end of the video, so you can watch what's outside the trim
        timeline.PlayRequested += t => { if (probe != null && encodeCts == null) { StopPlayback(); _ = PlayFrom(t, probe.Duration); } };

        var timelineMenu = new DarkMenu();
        timelineMenu.Add("Clear in and out", "", () => { timeline.Start = 0; timeline.End = timeline.Duration; timeline.Invalidate(); UpdateTimeLabel(); UpdateSummary(); });
        timeline.ContextMenuStrip = timelineMenu;

        // Play row: Play, Preview, the current time (click to type one), the trim range, and Advanced on the right
        timeRow = new Panel { Size = new Size(ContentWidth, 30), Margin = new Padding(0, 0, 0, 8) };
        // The whole video with a window on it: drag the window's ends to zoom the timeline, the middle to scroll
        zoomBar = new ZoomBar { Size = new Size(ContentWidth, 16), Margin = new Padding(0, 0, 0, 4) };
        zoomBar.ViewChanged += (a, b) => SetView(a, b);
        timeline.ZoomRequested += (factor, around) => { var span = (viewEnd - viewStart) * factor; SetView(around - (around - viewStart) * factor, around - (around - viewStart) * factor + span); };
        timeline.PanRequested += by => { var shift = (viewEnd - viewStart) * by; SetView(viewStart + shift, viewEnd + shift, keepSpan: true); };
        thumbTimer.Tick += (_, _) => { thumbTimer.Stop(); _ = LoadThumbnails(); };
        playBtn = new Pill("▶  Play") { Dock = DockStyle.Left, Enabled = false };
        playBtn.Click += (_, _) => TogglePlay();
        // Preview: show the result, with position and scale applied, instead of the editing view with its box
        previewBtn = new Pill("Preview") { Dock = DockStyle.Left };
        previewBtn.Click += (_, _) => SetPreview(!showResult);
        timeBox = new TextBox { BackColor = Theme.Bg, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), TextAlign = HorizontalAlignment.Center, Text = Planner.FormatTime(0) };
        var timeField = new RoundBox { LogicalWidth = 84, BackColor = Theme.Bg, Dock = DockStyle.Left, Cursor = Cursors.IBeam };
        timeField.Controls.Add(timeBox);
        timeField.Click += (_, _) => timeBox.Focus();
        timeBox.GotFocus += (_, _) => { timeField.Invalidate(); BeginInvoke(timeBox.SelectAll); };
        timeBox.LostFocus += (_, _) => { timeField.Invalidate(); timeBox.Text = Planner.FormatTime(timeline.Current); };
        timeLabel = new Label { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0), ForeColor = Theme.Muted, Font = new Font("Consolas", 9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        // Advanced: the tracks under the timeline (position and scale, audio)
        advBtn = new Pill("Advanced ▾") { Dock = DockStyle.Right };
        advBtn.Click += (_, _) => { tracksOpen = !tracksOpen; ShowTracks(); };
        Panel Gap() => new() { Dock = DockStyle.Left, Width = 6 };
        // Docked from the last one added inwards: Play ends up leftmost
        timeRow.Controls.AddRange([timeLabel, advBtn, timeField, Gap(), previewBtn, Gap(), playBtn]);

        // Options: label column + chip rows
        options = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg2, Padding = new Padding(12, 10, 12, 6), Margin = new Padding(0, 10, 0, 10), MinimumSize = new Size(ContentWidth, 0) };
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

        // Frame rate: the usual ones as chips, anything else typed in
        fpsBox = new TextBox { BackColor = Theme.Bg2, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, PlaceholderText = "Custom", TextAlign = HorizontalAlignment.Center };
        fpsField = new RoundBox { Margin = new Padding(0, 0, 6, 6), Cursor = Cursors.IBeam };
        fpsBox.GotFocus += (_, _) => fpsField.Invalidate();
        fpsBox.LostFocus += (_, _) => fpsField.Invalidate();
        fpsField.Controls.Add(fpsBox);
        fpsField.Click += (_, _) => fpsBox.Focus();
        fpsBox.TextChanged += (_, _) =>
        {
            if (probe == null || !int.TryParse(fpsBox.Text, out var n) || n < 1) return;
            // Never above the source's own rate
            frameRate = n < probe.Fps - 0.5 ? n : null;
            SelectChip(fpsChips!, frameRate);
            UpdateSummary();
        };

        resChips = ChipRow();
        fpsChips = ChipRow();
        encChips = ChipRow();
        cropChips = ChipRow();
        foreach (var (label, value) in new[] { ("Original", "src"), ("16:9", "16:9"), ("1:1", "1:1"), ("9:16", "9:16"), ("Free", "free") })
            cropChips.Controls.Add(Chip(label, value, cropChips, v => SetCrop((string)v!)));
        SelectChip(cropChips, "src");
        // "GPU" means NVIDIA or AMD hardware encoding when this PC has it (the default); the chip hides itself if it doesn't
        foreach (var (label, value) in new[] { ("GPU", "auto"), ("CPU", "x264") })
            encChips.Controls.Add(Chip(label, value, encChips, v => { cfg.Encoder = (string)v!; cfg.Save(); UpdateSummary(); }));
        SelectChip(encChips, cfg.Encoder == "x264" ? "x264" : "auto");

        // Between the timeline and the play row: the crop's key points and one lane per audio track, all as wide
        // as the timeline so every time lines up with the video above
        tracksPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Visible = false };
        tracksPanel.Controls.Add(keyLane);
        // The finished sound, always in view under the timeline: its mute button turns the audio off, and its
        // line is the overall volume. The individual tracks are under Advanced.
        masterLane = new VolumeLane { Label = "Audio", Margin = new Padding(0, 4, 0, 0), PixelWidth = ContentWidth, Visible = false };
        masterLane.MuteToggled += () => { UpdateSummary(); RestartIfPlaying(); };
        masterLane.Committed += RestartIfPlaying;

        options.Controls.Add(OptLabel("Size"), 0, 0); options.Controls.Add(sizeChips, 1, 0);
        options.Controls.Add(OptLabel("Scale"), 0, 1); options.Controls.Add(resChips, 1, 1);
        options.Controls.Add(OptLabel("Crop"), 0, 2); options.Controls.Add(cropChips, 1, 2);
        options.Controls.Add(OptLabel("Frame rate"), 0, 3); options.Controls.Add(fpsChips, 1, 3);
        options.Controls.Add(OptLabel("Encoder"), 0, 4); options.Controls.Add(encChips, 1, 4);

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

        root.Controls.AddRange([titleRow, preview, timeRow, zoomBar, timeline, masterLane, tracksPanel, options, bar, bottom]);
        Controls.Add(root);
        AcceptDrop(f => { if (encodeCts == null && outPath == null) Program.OpenVideo(f, this); });
        // Clicking anywhere else stops typing in the custom size box
        void HookClicks(Control c)
        {
            if (c == customMb || c == timeBox || c == fpsBox) return;
            c.MouseDown += (_, _) => { if (Typing) LeaveCustomBox(); };
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

        preview.FrameAspect = probe.Width / (double)probe.Height;
        timeline.Fps = probe.Fps;
        timeline.Reset(probe.Duration);
        zoomBar.Duration = probe.Duration;
        SetView(0, probe.Duration, remakeThumbs: false);
        timeline.Enabled = true;
        playBtn.Enabled = true;
        ActiveControl = timeline;

        // Scale: a percentage of the source, or of the crop when there is one
        foreach (var pct in Planner.Scales.Where(pct => ScaledSide(pct) >= 144))
            resChips.Controls.Add(Chip($"{pct}%", pct == 100 ? null : pct, resChips, v => { scalePct = (int?)v; UpdateSummary(); }));
        SelectChip(resChips, null);
        fpsChips.Controls.Add(Chip($"{fps}", null, fpsChips, v => { fpsBox.Text = ""; frameRate = (int?)v; UpdateSummary(); }));
        foreach (var f in Planner.FrameRates.Where(f => f < probe.Fps - 0.5))
            fpsChips.Controls.Add(Chip($"{f}", f, fpsChips, v => { fpsBox.Text = ""; frameRate = (int?)v; UpdateSummary(); }));
        fpsChips.Controls.Add(fpsField);
        SelectChip(fpsChips, null);
        // The picture starts as the whole frame in the video's own shape
        SetCrop(cropMode);

        // One lane per audio track: waveform, volume line, and a badge with its name that mutes it.
        // Only the first track plays to begin with (in an OBS recording that's the full mix).
        foreach (var t in probe.AudioTracks)
        {
            var lane = new VolumeLane { Label = t.Label, Margin = new Padding(0, 4, 0, 0), Duration = probe.Duration, End = probe.Duration, Active = t.Index == 0 };
            lane.MuteToggled += () => { UpdateMasterPeaks(); UpdateSummary(); RestartIfPlaying(); };
            lane.Committed += UpdateMasterPeaks;
            // Hear a volume change once the mouse is let go
            lane.Committed += RestartIfPlaying;
            (lane.ViewStart, lane.ViewEnd) = (viewStart, viewEnd);
            tracksPanel.Controls.Add(lane);
            trackUi.Add((t, lane));
        }
        (masterLane.Duration, masterLane.End, masterLane.Visible) = (probe.Duration, probe.Duration, trackUi.Count > 0);
        LayoutLanes();
        ShowTracks();
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
                case "audio": masterLane.Active = value != "off"; masterLane.Invalidate(); UpdateSummary(); break;
                case "adv": tracksOpen = true; ShowTracks(); break;
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
                case "res": scalePct = int.Parse(value); SelectChip(resChips, scalePct); UpdateSummary(); break;
                case "crop": SetCrop(value); break;
                case "zoom": SetView(double.Parse(value.Split('-')[0]), double.Parse(value.Split('-')[1])); break;
                case "preview": SetPreview(true); break;
                case "goto": timeBox.Text = value; if (ParseTime(value) is { } go) timeline.Seek(go); break;
                case "cropkeys":
                    // A zoom: the full box at 20% of the way in, half the width and off-centre by 60%
                    cropKeys.Clear();
                    cropKeys.Add(new CropKey(probe!.Duration * 0.2, 0.5, 0.5, MaxCropW));
                    cropKeys.Add(new CropKey(probe.Duration * 0.6, 0.62, 0.45, MaxCropW * 0.5));
                    timeline.Seek(probe.Duration * 0.4);
                    ShowCrop();
                    UpdateSummary();
                    break;
                case "size": targetMb = double.Parse(value); UpdateSummary(); break;
                case "trim": timeline.SetEnd(double.Parse(value)); break;
                case "keys":
                    foreach (var (_, lane) in trackUi.Take(3))
                    {
                        lane.Keys.AddRange([(probe!.Duration * 0.2, 1), (probe.Duration * 0.4, 0.3), (probe.Duration * 0.7, 1.6)]);
                        lane.Active = true;
                        lane.Invalidate();
                    }
                    break;
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

    /// <summary>
    /// Shows this stretch of the video on the timeline and every track.
    /// </summary>
    /// <param name="keepSpan">scrolling: keep the zoom and stop at the ends, rather than squeezing against them</param>
    void SetView(double start, double end, bool keepSpan = false, bool remakeThumbs = true)
    {
        var duration = timeline.Duration;
        var span = Math.Clamp(keepSpan ? viewEnd - viewStart : end - start, zoomBar.MinSpan, duration);
        start = Math.Clamp(start, 0, duration - span);
        if (start == viewStart && start + span == viewEnd) return;
        (viewStart, viewEnd) = (start, start + span);
        (timeline.ViewStart, timeline.ViewEnd) = (keyLane.ViewStart, keyLane.ViewEnd) = (zoomBar.ViewStart, zoomBar.ViewEnd) = (viewStart, viewEnd);
        foreach (var lane in trackUi.Select(t => t.Lane).Append(masterLane))
        {
            (lane.ViewStart, lane.ViewEnd) = (viewStart, viewEnd);
            lane.Invalidate();
        }
        timeline.Invalidate();
        keyLane.Invalidate();
        zoomBar.Invalidate();
        if (remakeThumbs) { thumbTimer.Stop(); thumbTimer.Start(); }
    }

    /// <summary>
    /// Thumbnails for the stretch the timeline is showing. The first set fills in as it arrives; after a zoom the
    /// old strip stays until the new one is complete, then they swap.
    /// </summary>
    async Task LoadThumbnails()
    {
        thumbCts?.Cancel();
        var cts = thumbCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        double start = viewStart, span = viewEnd - viewStart;
        var first = timeline.Thumbs.All(t => t == null);
        var fresh = first ? timeline.Thumbs : new Image?[timeline.Thumbs.Length];
        if (first) (timeline.ThumbStart, timeline.ThumbSpan) = (start, span);
        await Ff.Thumbnails(file, start, span, fresh.Length, timeline.Height, (i, img) =>
        {
            if (cts.IsCancellationRequested || IsDisposed) { img.Dispose(); return; }
            fresh[i] = img;
            if (first) BeginInvoke(timeline.Invalidate);
        }, cts.Token);
        if (first || IsDisposed) return;
        if (cts.IsCancellationRequested)
        {
            foreach (var img in fresh) img?.Dispose();
            return;
        }
        var old = timeline.Thumbs;
        (timeline.Thumbs, timeline.ThumbStart, timeline.ThumbSpan) = (fresh, start, span);
        timeline.Invalidate();
        foreach (var img in old) img?.Dispose();
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
        var img = await Ff.Frame(file, pendingFrame, DecodeSize().Height, cts.Token);
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
            await Player.Play(file, from, to ?? timeline.End, Mix(), Master, DecodeSize(), probe!.Width, probe.Height, probe.Fps, (bmp, t) =>
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
        masterLane.PixelWidth = timeline.Width;
        foreach (var (_, lane) in trackUi) lane.PixelWidth = timeline.Width;
    }

    /// <summary>The master lane's waveform: the loudest unmuted track at each moment, at its volume</summary>
    void UpdateMasterPeaks()
    {
        var live = trackUi.Where(t => t.Lane.Active && t.Lane.Peaks != null).Select(t => t.Lane).ToList();
        if (live.Count == 0) masterLane.Peaks = null;
        else
        {
            var mix = new float[live[0].Peaks!.Length];
            for (var i = 0; i < mix.Length; i++)
            {
                var t = (i + 0.5) / mix.Length * timeline.Duration;
                mix[i] = live.Max(l => i < l.Peaks!.Length ? (float)(l.Peaks[i] * l.GainAt(t)) : 0);
            }
            masterLane.Peaks = mix;
        }
        masterLane.Invalidate();
    }

    /// <summary>The Advanced section under the timeline: the position and scale track, then the audio tracks</summary>
    void ShowTracks()
    {
        advBtn.On = tracksOpen;
        advBtn.Text = tracksOpen ? "Advanced ▴" : "Advanced ▾";
        tracksPanel.Visible = tracksOpen && probe != null;
        ShowCrop();
    }

    void SetPreview(bool on)
    {
        showResult = on;
        previewBtn.On = on;
        preview.ShowResult = on;
        // Frames are decoded bigger for a zoomed-in result, so fetch again
        if (playCts != null) RestartIfPlaying();
        else if (probe != null) RequestFrame(timeline.Current);
    }

    /// <summary>
    /// Size to decode preview frames at. Showing the result zoomed in enlarges part of the frame, so it's
    /// decoded bigger then (up to the source's own size) to stay sharp.
    /// </summary>
    Size DecodeSize()
    {
        var size = preview.ClientSize;
        if (!showResult || !Reframed) return size;
        var closest = cropKeys.Min(k => CropRect(k).Height);
        var f = Math.Max(1, Math.Min(Math.Min(4, 1 / Math.Max(0.05, closest)), probe!.Height / (double)Math.Max(1, size.Height)));
        return new Size((int)(size.Width * f), (int)(size.Height * f));
    }

    /// <summary>"1:23.45", "83.5" or "1:02:03" as seconds; null if it isn't a time</summary>
    static double? ParseTime(string text)
    {
        double total = 0;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0) return null;
            total = total * 60 + n;
        }
        return total;
    }

    /// <summary>Measures each track for its waveform and marks the silent ones</summary>
    async Task LoadPeaks()
    {
        foreach (var (track, lane) in trackUi)
        {
            float[]? peaks;
            try { peaks = await Ff.Peaks(file, track.Index, 1200, lifetime.Token); } catch { return; }
            if (IsDisposed) return;
            lane.Peaks = peaks;
            lane.Silent = peaks != null && peaks.Max() < 0.01f;
            lane.Invalidate();
            UpdateMasterPeaks();
        }
    }

    /// <summary>Overall volume from the master lane, applied after the tracks are mixed</summary>
    TrackMix? Master => AudioOn ? new TrackMix(-1, masterLane.Volume, [.. masterLane.Keys]) : null;

    List<TrackMix> Mix() => probe == null || !AudioOn ? []
        : trackUi.Where(t => t.Lane.Active).Select(t => new TrackMix(t.Track.Index, t.Lane.Volume, [.. t.Lane.Keys])).ToList();

    // ---------- crop and zoom ----------

    double SrcAspect => probe!.Width / (double)probe.Height;
    /// <summary>Widest a box of the current shape can be, as a fraction of the frame's width, and still fit its height</summary>
    double MaxCropW => Math.Min(1, cropAspect / SrcAspect);
    bool Cropping => probe != null && cropKeys.Count > 0;
    /// <summary>The picture is anything other than the whole frame: another shape, zoomed in, or moving</summary>
    bool Reframed => Cropping && (Math.Abs(cropAspect - SrcAspect) > 1e-6 || cropKeys.Any(k => k.W < MaxCropW - 1e-6));

    /// <summary>The box at a time: the key points, moving in straight lines between them</summary>
    CropKey CropAt(double t)
    {
        if (cropKeys.Count == 1 || t <= cropKeys[0].T) return cropKeys[0];
        for (var i = 1; i < cropKeys.Count; i++)
            if (t < cropKeys[i].T)
            {
                var (a, b) = (cropKeys[i - 1], cropKeys[i]);
                var f = (t - a.T) / Math.Max(0.001, b.T - a.T);
                return new CropKey(t, a.Cx + (b.Cx - a.Cx) * f, a.Cy + (b.Cy - a.Cy) * f, a.W + (b.W - a.W) * f);
            }
        return cropKeys[^1];
    }

    /// <summary>A key point as a rectangle in fractions of the frame, kept inside it</summary>
    RectangleF CropRect(CropKey k)
    {
        double w = Math.Min(k.W, MaxCropW), h = w * SrcAspect / cropAspect;
        return new RectangleF((float)Math.Clamp(k.Cx - w / 2, 0, 1 - w), (float)Math.Clamp(k.Cy - h / 2, 0, 1 - h), (float)w, (float)h);
    }

    void ClampCropWidths()
    {
        for (var i = 0; i < cropKeys.Count; i++) cropKeys[i] = cropKeys[i] with { W = Math.Min(cropKeys[i].W, MaxCropW) };
    }

    void SetCrop(string mode)
    {
        cropMode = mode;
        SelectChip(cropChips, mode);
        if (probe != null)
        {
            cropAspect = mode switch { "16:9" => 16 / 9.0, "1:1" => 1, "9:16" => 9 / 16.0, "free" when cropAspect > 0 => cropAspect, _ => SrcAspect };
            // Starts as the biggest box of that shape, in the middle
            if (cropKeys.Count == 0) cropKeys.Add(new CropKey(0, 0.5, 0.5, MaxCropW));
            ClampCropWidths();
        }
        ShowCrop();
        UpdateSummary();
    }

    /// <summary>Puts the box for the current time on the preview, and the key points on their track</summary>
    void ShowCrop()
    {
        if (!Cropping) return;
        preview.BoxAspect = cropMode == "free" ? 0 : cropAspect / SrcAspect;
        // The box stays out of the way until it's wanted: Advanced is open, or the picture has been reframed
        preview.Box = tracksOpen || Reframed ? CropRect(CropAt(timeline.Current)) : null;
        keyLane.Levels = cropKeys.Select(k => 1 - Math.Min(k.W, MaxCropW) / MaxCropW).ToList();
        (keyLane.Duration, keyLane.Current, keyLane.Keys) = (timeline.Duration, timeline.Current, cropKeys.Select(k => k.T).ToList());
        keyLane.Invalidate();
    }

    /// <summary>
    /// The box was dragged on the preview. With one key point that's a fixed crop, so it just changes. With
    /// several, it changes the point under the playhead, or adds one there if there isn't one.
    /// </summary>
    void EditCrop(RectangleF r)
    {
        if (!Cropping) return;
        if (cropMode == "free")
        {
            // Any shape goes, but every key point takes it on, or the output would change size mid-video
            cropAspect = r.Width / r.Height * SrcAspect;
            ClampCropWidths();
        }
        var key = new CropKey(timeline.Current, r.X + r.Width / 2, r.Y + r.Height / 2, r.Width);
        var i = cropKeys.Count == 1 ? 0 : cropKeys.FindIndex(k => Math.Abs(k.T - key.T) < 0.02);
        if (i >= 0) cropKeys[i] = key with { T = cropKeys[i].T };
        else
        {
            var after = cropKeys.FindIndex(k => k.T > key.T);
            cropKeys.Insert(after < 0 ? cropKeys.Count : after, key);
        }
        ShowCrop();
    }

    void AddCropKey(double t)
    {
        if (!Cropping || cropKeys.Any(k => Math.Abs(k.T - t) < 0.05)) return;
        var after = cropKeys.FindIndex(k => k.T > t);
        cropKeys.Insert(after < 0 ? cropKeys.Count : after, CropAt(t) with { T = t });
        timeline.Seek(t);
        UpdateSummary();
    }

    void MoveCropKey(int i, double t)
    {
        // A point can't pass its neighbours
        var lo = i > 0 ? cropKeys[i - 1].T + 0.05 : 0;
        var hi = i < cropKeys.Count - 1 ? cropKeys[i + 1].T - 0.05 : timeline.Duration;
        cropKeys[i] = cropKeys[i] with { T = Math.Clamp(t, lo, Math.Max(lo, hi)) };
        ShowCrop();
    }

    /// <summary>
    /// What gets planned as the source: the whole frame, or with a crop the smallest box. The output is that
    /// size (times the scale), so the most zoomed-in moment is pixel for pixel and nothing is ever enlarged.
    /// </summary>
    ProbeResult PlanSource
    {
        get
        {
            if (!Reframed) return probe!;
            var w = cropKeys.Min(k => Math.Min(k.W, MaxCropW));
            int pw = Math.Max(2, (int)Math.Round(w * probe!.Width) & ~1), ph = Math.Max(2, (int)Math.Round(pw / cropAspect) & ~1);
            return probe with { Width = pw, Height = Math.Min(ph, probe.Height & ~1) };
        }
    }

    /// <summary>The key points in source pixels (left, top, width) for the encoder</summary>
    List<(double T, double X, double Y, double W)>? CropForEncode() => !Reframed ? null
        : cropKeys.Select(k => { var r = CropRect(k); return (k.T, (double)r.X * probe!.Width, (double)r.Y * probe.Height, (double)r.Width * probe.Width); }).ToList();

    // ---------- window size ----------

    void CaptureBase() => (baseW, basePreviewH, baseChipMax) = (preview.Width, preview.Height, sizeChips.MaximumSize.Width);

    /// <summary>Makes every row as wide, and the preview as tall, as the window was dragged</summary>
    void ApplyStretch()
    {
        var w = baseW + extraW;
        root.SuspendLayout();
        foreach (var c in new Control[] { titleRow, preview, timeRow, zoomBar, timeline, keyLane, bar }) c.Width = w;
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

    /// <summary>Short side of the picture at a scale percentage (even, as H.264 needs)</summary>
    int ScaledSide(int percent) { var src = PlanSource; return (int)Math.Round(Math.Min(src.Width, src.Height) * percent / 100.0) & ~1; }

    Plan? CurrentPlan()
    {
        var duration = timeline.End - timeline.Start;
        if (probe == null || duration <= 0) return null;
        var cal = calibration.TryGetValue(EffectiveEncoder, out var c) ? c : 1;
        return Planner.Make(PlanSource, duration, (long)(targetMb * Planner.MB), Mix().Count > 0, cfg.SafetyMarginPercent, cal, scalePct is { } pct ? ScaledSide(pct) : null, frameRate);
    }

    bool Trimmed => probe != null && (timeline.Start > 0.05 || timeline.End < probe.Duration - 0.05);

    void UpdateTimeLabel()
    {
        if (!timeBox.Focused) timeBox.Text = Planner.FormatTime(timeline.Current);
        timeLabel.Text = $"{Planner.FormatTime(timeline.Start)} – {Planner.FormatTime(timeline.End)} ({Planner.FormatTime(timeline.End - timeline.Start, false)})";
        ShowCrop();
        // Keep the playhead in sight while playing: turn the page when it runs off the end
        if (playCts != null && (timeline.Current > viewEnd || timeline.Current < viewStart))
            SetView(timeline.Current, timeline.Current + (viewEnd - viewStart), keepSpan: true);
        (zoomBar.Current, zoomBar.TrimStart, zoomBar.TrimEnd) = (timeline.Current, timeline.Start, timeline.End);
        zoomBar.Invalidate();
        foreach (var lane in trackUi.Select(t => t.Lane).Append(masterLane))
        {
            (lane.Start, lane.End, lane.Current) = (timeline.Start, timeline.End, timeline.Current);
            if (lane.Visible) lane.Invalidate();
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
        var src = PlanSource;
        double shortSrc = Math.Min(src.Width, src.Height), aspect = Math.Max(src.Width, src.Height) / shortSrc;
        double Bits(int side, int f) => plan.VideoKbps * 1000.0 / (side * side * aspect * f);
        var bits = Bits(plan.OutputShortSide, fps);
        if (bits >= Good) return (Theme.Green, "");
        var better = Planner.Scales.Where(pct => ScaledSide(pct) < plan.OutputShortSide && Bits(ScaledSide(pct), fps) >= Good).Select(pct => (int?)pct).FirstOrDefault();
        var hint = better != null ? $" · try {better}%" : fps > 30 ? " · try 30 fps" : " · try a shorter clip";
        return (bits >= Good / 2 ? Theme.Warn : Theme.Danger, hint);
    }

    /// <param name="keepStatus">leave an error or "Cancelled" showing instead of going back to the result line</param>
    void UpdateSummary(bool keepStatus = false)
    {
        if (outPath != null || probe == null) return;
        if (!keepStatus && encodeCts == null && status.Visible) ShowStatus("", Theme.Muted);
        var limit = Planner.FormatBytes(targetMb * Planner.MB);
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

    string OutputName => Path.GetFileNameWithoutExtension(file) + (Trimmed ? "-trimmed" : "") + "-compressed.mp4";

    /// <summary>Asks where to save; starts in the last-used folder, else next to the source video</summary>
    string? AskSavePath()
    {
        var folder = Directory.Exists(cfg.SaveFolder) ? cfg.SaveFolder : Path.GetDirectoryName(file);
        using var dlg = new SaveFileDialog { Title = "Save compressed video", Filter = "MP4 video|*.mp4", FileName = OutputName, InitialDirectory = folder, OverwritePrompt = true };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    /// <param name="saveTo">skips the save dialog (tests)</param>
    async void Go(bool save, string? saveTo = null)
    {
        if (probe == null || encodeCts != null || !goBtn.Enabled || !goBtn.Visible) return;
        var plan = CurrentPlan();
        if (plan is not { Feasible: true }) return;
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
            var (temp, size) = await Ff.Encode(new EncodeRequest
            {
                Input = file,
                Start = timeline.Start,
                Duration = timeline.End - timeline.Start,
                TargetBytes = plan.TargetBytes,
                VideoKbps = plan.VideoKbps,
                AudioKbps = plan.AudioKbps,
                Tracks = Mix(),
                Master = Master,
                ScaleShortSide = plan.ScaleShortSide,
                Fps = plan.Fps,
                Portrait = probe.Height > probe.Width,
                Crop = CropForEncode(),
                CropW = PlanSource.Width,
                CropH = PlanSource.Height,
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
        customMb.Enabled = fpsBox.Enabled = timeBox.Enabled = !busy;
        foreach (var lane in trackUi.Select(t => t.Lane).Append(masterLane)) lane.Enabled = !busy;
        foreach (var p in new[] { sizeChips, resChips, cropChips, fpsChips, encChips }.SelectMany(g => g.Controls.OfType<Pill>())) p.Enabled = !busy;
        preview.Enabled = keyLane.Enabled = !busy;
        // The button keeps its size and says what's happening; hovering turns it into Cancel
        goBtn.AltText = busy ? "Compressing…" : null;
        goBtn.HoverText = busy ? "✕  Cancel" : null;
        goBtn.Enabled = busy;
        moreBtn.Enabled = false;
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

    /// <summary>The keyboard is being used to type into one of the boxes, so it isn't for shortcuts</summary>
    bool Typing => customMb.Focused || fpsBox.Focused || timeBox.Focused;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Typing a custom size: Enter, Esc or Tab just finish typing
        if (Typing && keyData is Keys.Enter or Keys.Escape or Keys.Tab)
        {
            // Enter in the time box jumps there; anything else just finishes typing
            var jump = timeBox.Focused && keyData == Keys.Enter ? ParseTime(timeBox.Text) : null;
            LeaveCustomBox();
            if (jump is { } t && probe != null) timeline.Seek(t);
            return true;
        }
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

    /// <summary>Space play/pause, ←/→ frame step (Shift = 5 s), Q/I in, E/O out, Home/End</summary>
    bool HandleEditKey(Keys keyData)
    {
        if (probe == null || encodeCts != null || outPath != null || Typing) return false;
        var key = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (mods != Keys.None && !(mods == Keys.Shift && key is Keys.Left or Keys.Right)) return false;
        var step = mods == Keys.Shift ? 5 : 1 / (probe.Fps > 0 ? probe.Fps : 30);
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
