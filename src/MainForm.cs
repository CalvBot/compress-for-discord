using System.Collections.Specialized;
using System.Runtime.InteropServices;

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

    /// <summary>Shape of the picture: src (the video's own), 16:9, 1:1, 9:16, or free once it's been reshaped by hand (Alt + drag a corner)</summary>
    string cropMode = "src";
    /// <summary>The preview shows the result, not the editing view</summary>
    bool showResult;
    /// <summary>Width / height of the crop box in pixels. Every key point shares it, so the output never changes size.</summary>
    double cropAspect;
    /// <summary>Where the picture's centre is (fractions of the frame), over time</summary>
    readonly Keyed<(double X, double Y)> pos = new((a, b, f) => (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f));
    /// <summary>How wide the picture is (fraction of the frame's width), over time: smaller = zoomed in</summary>
    readonly Keyed<double> scale = new((a, b, f) => a + (b - a) * f);
    /// <summary>Position and scale have a track each, with their own key points, instead of sharing one</summary>
    bool separate;
    /// <summary>There is sound and the master lane isn't muted</summary>
    bool AudioOn => trackUi.Count > 0 && masterLane.Active;
    /// <summary>The section under the timeline (crop key points, audio tracks) is open</summary>
    bool tracksOpen;
    /// <summary>" · size · resolution · bitrate" shown after the file name</summary>
    string details;
    readonly List<(AudioTrack Track, VolumeLane Lane)> trackUi = [];

    CancellationTokenSource? encodeCts, frameCts, playCts;
    readonly CancellationTokenSource lifetime = new();
    /// <summary>A preview frame is being fetched; the next one wanted goes straight after it</summary>
    bool frameBusy;
    /// <summary>When a click on the timeline last stopped playback, so the same click can't also count towards a double-click that starts it again</summary>
    long stoppedByClick;
    double pendingFrame;
    /// <summary>Where the current playback stops when it isn't the end of the trim (double-click play)</summary>
    double? playTo;
    string? outPath;

    readonly Label subtitle, timeLabel, summary, status;
    readonly CropView preview;
    readonly KeyLane keyLane, posLane, scaleLane;
    readonly TrackScroller scroller;
    readonly SizeGrip grip, previewGrip;
    int grabExtra;
    /// <summary>Above zero while several things are being changed at once: the window is fitted and painted once at the end</summary>
    int holdFit;
    // What position and scale's own key points were before they were combined, and what combining made of them,
    // so that separating again brings the originals back if nothing was changed in between
    List<(double T, (double X, double Y) Value)>? keptPos, bakedPos;
    List<(double T, double Value)>? keptScale, bakedScale;
    readonly DarkMenu trackMenu = new(), cropMenu = new();
    /// <summary>Height of every keyframable track, in 96-dpi pixels (Shift + wheel over a track changes it)</summary>
    int trackH = 56;
    /// <summary>The track the mouse is over: the arrow keys then jump between its key points</summary>
    Control? hoverTrack;
    /// <summary>Height of the track box while Advanced is open; the grip under it changes it</summary>
    int tracksHeight, grabHeight;
    readonly ZoomBar zoomBar;
    /// <summary>The part of the video the timeline and tracks are showing (zoom)</summary>
    double viewStart, viewEnd;
    CancellationTokenSource? thumbCts;
    /// <summary>Waits for zooming to settle before remaking the thumbnails</summary>
    readonly System.Windows.Forms.Timer thumbTimer = new() { Interval = 350 };
    readonly Timeline timeline;
    readonly FlowLayoutPanel sizeChips, resChips, fpsChips, buttons;
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
        titleRow = new Panel { Size = new Size(ContentWidth, 36), Margin = new Padding(0, 0, 0, 6) };
        details = $" · {Planner.FormatBytes(fileSize)}";
        subtitle = new Label { Text = Path.GetFileName(file) + details, Dock = DockStyle.Fill, UseMnemonic = false, ForeColor = Theme.Header, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Padding = new Padding(8, 0, 0, 0) };
        var update = UpdateLink(this);
        update.Margin = new Padding(0, 5, 10, 0);
        var settingsIcon = new Pill("Settings") { Icon = PillIcon.Gear, Margin = new Padding(0, 0, 6, 0) };
        var helpIcon = new Pill("Help") { Icon = PillIcon.Help, Margin = Padding.Empty };
        settingsIcon.Click += (_, _) => ShowSettings();
        helpIcon.Click += (_, _) => ShowHelp();
        tips.SetToolTip(settingsIcon, "Settings");
        tips.SetToolTip(helpIcon, "Help and shortcuts (F1)");
        links.Controls.AddRange([update, settingsIcon, helpIcon]);
        titleRow.Controls.Add(subtitle);
        titleRow.Controls.Add(links);
        // Deferred: changing the text in the middle of a layout pass leaves the label at its default width
        subtitle.SizeChanged += (_, _) => { if (IsHandleCreated) BeginInvoke(FitSubtitle); };

        preview = new CropView { Size = new Size(ContentWidth, 300), Margin = Padding.Empty };
        // Drag the bar under the preview to give it more (or less) of the window
        previewGrip = new SizeGrip { Size = new Size(ContentWidth, 10), Margin = Padding.Empty };
        previewGrip.DragStarted += () => grabExtra = extraH;
        previewGrip.Dragged += by =>
        {
            var room = Screen.FromControl(this).WorkingArea.Height - (Height - extraH);
            var to = Math.Clamp(grabExtra + by, 0, Math.Max(0, room));
            if (to == extraH || baseW == 0) return;
            extraH = to;
            Batch(ApplyStretch);
        };
        preview.BoxChanged += EditCrop;
        preview.BoxCommitted += () => UpdateSummary();
        preview.Stepped += by => { if (probe != null && encodeCts == null) timeline!.Seek(timeline.Current + by / (probe.Fps > 0 ? probe.Fps : 30)); };
        // Right-click: reset the picture, or change its shape
        cropMenu.Add("Reset crop", "", ResetCrop);
        cropMenu.Items.Add(new ToolStripSeparator());
        var shapes = new[] { ("Original", "src"), ("16:9", "16:9"), ("1:1", "1:1"), ("9:16", "9:16") }
            .Select(shape => (Item: cropMenu.Add(shape.Item1, "", () => SetCrop(shape.Item2)), Mode: shape.Item2)).ToList();
        cropMenu.Opening += (_, _) => { foreach (var (item, mode) in shapes) item.ShortcutKeyDisplayString = mode == cropMode ? "✓" : null; };
        preview.MenuRequested += at => { if (probe != null && encodeCts == null) cropMenu.Show(preview, at); };

        // Its strip is as tall as a track (the 14 is the row of tabs above it); unlike the tracks it never changes height
        timeline = new Timeline { Size = new Size(ContentWidth, 14 + 56), Enabled = false, Margin = Padding.Empty };
        timeline.RangeChanged += () => { UpdateTimeLabel(); UpdateSummary(); };
        // Key points of the crop, shown only while cropping
        // Position and scale: one track for both, or (right-click) one each
        var warm = Theme.Blend(Theme.Bg3, Theme.Warn, 0.13);
        KeyLane Lane(string label) => new() { Label = label, Back = warm, Size = new Size(ContentWidth, 56), Margin = new Padding(0, 4, 0, 0), Visible = false };
        keyLane = Lane("Position & scale");
        posLane = Lane("Position");
        scaleLane = Lane("Scale");
        Wire(keyLane, () => [pos, scale]);
        Wire(posLane, () => [pos]);
        Wire(scaleLane, () => [scale]);

        // Clicking or dragging on the strip: stop if it's playing, and show that frame
        timeline.Seeked += t => { if (playCts != null) stoppedByClick = Environment.TickCount64; StopPlayback(); RequestFrame(t); };
        // Double-click plays from that spot to the end of the video, so you can watch what's outside the trim.
        // Not when the first click of the two was what stopped it playing: then it just stays stopped there.
        timeline.PlayRequested += t => { if (probe != null && encodeCts == null && Environment.TickCount64 - stoppedByClick > 800) { StopPlayback(); _ = PlayFrom(t, probe.Duration); } };

        var timelineMenu = new DarkMenu();
        timelineMenu.Add("Clear in and out", "", () => { timeline.Start = 0; timeline.End = timeline.Duration; timeline.Invalidate(); UpdateTimeLabel(); UpdateSummary(); });
        timeline.ContextMenuStrip = timelineMenu;

        // Play row: Play, Preview, the current time (click to type one), the trim range, and Advanced on the right
        timeRow = new Panel { Size = new Size(ContentWidth, 30), Margin = new Padding(0, 0, 0, 8) };
        // The whole video with a window on it: drag the window's ends to zoom the timeline, the middle to scroll
        zoomBar = new ZoomBar { Size = new Size(ContentWidth, 16), Margin = new Padding(0, 0, 0, 4) };
        zoomBar.ViewChanged += (a, b) => SetView(a, b);
        timeline.ZoomRequested += ZoomBy;
        timeline.PanRequested += by => { var shift = (viewEnd - viewStart) * by; SetView(viewStart + shift, viewEnd + shift, keepSpan: true); };
        thumbTimer.Tick += (_, _) => { thumbTimer.Stop(); _ = LoadThumbnails(); };
        playBtn = new Pill("Play") { Icon = PillIcon.Play, Dock = DockStyle.Left, Enabled = false };
        tips.SetToolTip(playBtn, "Play / pause (Space)");
        playBtn.Click += (_, _) => TogglePlay();
        // Preview: show the result, with position and scale applied, instead of the editing view with its box
        previewBtn = new Pill("Preview") { Icon = PillIcon.Eye, Dock = DockStyle.Left };
        tips.SetToolTip(previewBtn, "Preview the result");
        previewBtn.Click += (_, _) => SetPreview(!showResult);
        timeBox = new TextBox { BackColor = Theme.Bg, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), TextAlign = HorizontalAlignment.Center, Text = Planner.FormatTime(0) };
        var timeField = new RoundBox { LogicalWidth = 84, BackColor = Theme.Bg, Dock = DockStyle.Left, Cursor = Cursors.IBeam };
        timeField.Controls.Add(timeBox);
        timeField.Click += (_, _) => timeBox.Focus();
        timeBox.GotFocus += (_, _) => { timeField.Invalidate(); BeginInvoke(timeBox.SelectAll); };
        timeBox.LostFocus += (_, _) => { timeField.Invalidate(); timeBox.Text = Planner.FormatTime(timeline.Current); };
        timeLabel = new Label { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0), ForeColor = Theme.Muted, Font = new Font("Consolas", 9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        // Advanced: the tracks under the timeline (position and scale, audio)
        advBtn = new Pill("Advanced") { Icon = PillIcon.Sliders, Dock = DockStyle.Right };
        tips.SetToolTip(advBtn, "Tracks: position, scale and audio");
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
        // Under the timeline: position and scale, the result audio, then every audio track, all as wide as the
        // timeline so times line up. Closed, only the result audio shows. Open, it's a box that scrolls, with a
        // grip under it to make it taller.
        scroller = new TrackScroller { Size = new Size(ContentWidth, 0), Margin = Padding.Empty };
        grip = new SizeGrip { Size = new Size(ContentWidth, 10), Margin = Padding.Empty, Visible = false };
        grip.DragStarted += () => grabHeight = scroller.Height;
        grip.Dragged += by => { tracksHeight = Math.Max(D(60), grabHeight + by); SizeTracks(); };
        // Tracks get their real height once they exist on screen, so measure again whenever the content changes
        scroller.Content.SizeChanged += (_, _) => { if (probe != null && holdFit == 0) SizeTracks(); };
        // The finished sound, always in view under the timeline: its mute button turns the audio off, and its
        // line is the overall volume. The individual tracks are under Advanced.
        masterLane = new VolumeLane { Label = "Audio", Back = Theme.Blend(Theme.Bg3, Theme.Brand, 0.2), Margin = new Padding(0, 4, 0, 0), PixelWidth = ContentWidth, Visible = false };
        // Top to bottom: position and scale (one track or two), then the result audio; the audio tracks follow
        foreach (var lane in new Control[] { keyLane, posLane, scaleLane, masterLane }) scroller.Content.Controls.Add(lane);
        HookTrack(masterLane);
        masterLane.MuteToggled += () => { UpdateSummary(); RestartIfPlaying(); };
        masterLane.Committed += RestartIfPlaying;

        options.Controls.Add(OptLabel("Size"), 0, 0); options.Controls.Add(sizeChips, 1, 0);
        options.Controls.Add(OptLabel("Scale"), 0, 1); options.Controls.Add(resChips, 1, 1);
        options.Controls.Add(OptLabel("Frame rate"), 0, 2); options.Controls.Add(fpsChips, 1, 2);

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

        root.Controls.AddRange([titleRow, preview, previewGrip, timeRow, zoomBar, timeline, scroller, grip, options, bar, bottom]);
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
        // Closest zoom: a dozen frames across the timeline
        zoomBar.MinSpan = Math.Min(probe.Duration, 12 / (probe.Fps > 0 ? probe.Fps : 30));
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
            var lane = new VolumeLane { Label = t.Label, RowHeight = trackH, Margin = new Padding(0, 4, 0, 0), Duration = probe.Duration, End = probe.Duration, Active = t.Index == 0 };
            HookTrack(lane);
            lane.MuteToggled += () => { UpdateMasterPeaks(); UpdateSummary(); RestartIfPlaying(); };
            lane.Committed += UpdateMasterPeaks;
            // Hear a volume change once the mouse is let go
            lane.Committed += RestartIfPlaying;
            (lane.ViewStart, lane.ViewEnd) = (viewStart, viewEnd);
            lane.Visible = false;
            scroller.Content.Controls.Add(lane);
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
                case "rate": fpsBox.Text = value; break;
                case "separate": SetSeparate(true); break;
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
                case "preview": SetPreview(true); break;
                case "goto": timeBox.Text = value; if (ParseTime(value) is { } go) timeline.Seek(go); break;
                case "cropkeys":
                    // A zoom: the full box at 20% of the way in, half the width and off-centre by 60%
                    pos.Keys.Clear();
                    scale.Keys.Clear();
                    pos.Keys.AddRange([(probe!.Duration * 0.2, (0.5, 0.5)), (probe.Duration * 0.6, (0.62, 0.45))]);
                    scale.Keys.AddRange([(probe.Duration * 0.2, MaxCropW), (probe.Duration * 0.6, MaxCropW * 0.5)]);
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
                case "settings": BeginInvoke(ShowSettings); break;
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
        BeginInvoke(() => UpdateSummary());
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
        (timeline.ViewStart, timeline.ViewEnd) = (zoomBar.ViewStart, zoomBar.ViewEnd) = (viewStart, viewEnd);
        foreach (var lane in new[] { keyLane, posLane, scaleLane })
        {
            (lane.ViewStart, lane.ViewEnd) = (viewStart, viewEnd);
            lane.Invalidate();
        }
        foreach (var lane in trackUi.Select(t => t.Lane).Append(masterLane))
        {
            (lane.ViewStart, lane.ViewEnd) = (viewStart, viewEnd);
            lane.Invalidate();
        }
        timeline.Invalidate();
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
        // Zoomed in until there are fewer frames than thumbnails: show the frames themselves, one each
        const int Usual = 16;
        var frame = FrameLength;
        var count = Usual;
        if (!first && frame > 0 && span / frame <= Usual)
        {
            start = Math.Floor(start / frame) * frame;
            count = Math.Max(1, (int)Math.Ceiling((viewEnd - start) / frame - 1e-6));
            span = count * frame;
        }
        var fresh = first ? timeline.Thumbs : new Image?[count];
        if (first) (timeline.ThumbStart, timeline.ThumbSpan) = (start, span);
        await Ff.Thumbnails(file, start, span, fresh.Length, timeline.Height, (i, img) =>
        {
            if (cts.IsCancellationRequested || IsDisposed) { img.Dispose(); return; }
            fresh[i] = img;
            if (first) BeginInvoke(timeline.StripChanged);
        }, cts.Token);
        if (first || IsDisposed) return;
        if (cts.IsCancellationRequested)
        {
            foreach (var img in fresh) img?.Dispose();
            return;
        }
        var old = timeline.Thumbs;
        (timeline.Thumbs, timeline.ThumbStart, timeline.ThumbSpan) = (fresh, start, span);
        timeline.StripChanged();
        foreach (var img in old) img?.Dispose();
    }

    // ---------- preview frame ----------

    void RequestFrame(double t)
    {
        pendingFrame = t;
        UpdateTimeLabel();
        if (!frameBusy) LoadFrames();
    }

    /// <summary>
    /// Fetches the frame that's wanted, then (if the playhead has moved on meanwhile) the one wanted now, and so on.
    /// So dragging along the timeline shows frames as fast as they can be decoded instead of only once you stop.
    /// </summary>
    async void LoadFrames()
    {
        frameBusy = true;
        try
        {
            while (!IsDisposed)
            {
                var wanted = pendingFrame;
                var cts = frameCts = new CancellationTokenSource();
                var img = await Ff.Frame(file, wanted, DecodeSize().Height, cts.Token);
                if (IsDisposed) { img?.Dispose(); return; }
                if (cts.IsCancellationRequested || img == null) img?.Dispose();
                else
                {
                    var old = preview.Image;
                    preview.Image = img;
                    old?.Dispose();
                }
                if (pendingFrame == wanted || playCts != null) break;
            }
        }
        finally { frameBusy = false; }
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
        playBtn.Icon = PillIcon.Play;
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
        frameCts?.Cancel();
        playBtn.Icon = PillIcon.Pause;
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

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);

    /// <summary>
    /// Runs a change that shows, hides or resizes several things, then lays the window out, fits it and paints it
    /// once. Done piece by piece, each step resized and repainted the window, which showed as flashing and made
    /// quick clicks lag behind.
    /// </summary>
    void Batch(Action change)
    {
        if (!IsHandleCreated || holdFit > 0) { change(); return; }
        holdFit++;
        SendMessage(Handle, 0x000B, 0, 0); // WM_SETREDRAW off
        root.SuspendLayout();
        scroller.Content.SuspendLayout();
        try { change(); }
        finally
        {
            scroller.Content.ResumeLayout(true);
            root.ResumeLayout(true);
            root.Size = root.GetPreferredSize(Size.Empty);
            holdFit--;
            FitToContent();
            SendMessage(Handle, 0x000B, 1, 0);
            Refresh();
        }
    }

    /// <summary>The Advanced section under the timeline: the position and scale track, then the audio tracks</summary>
    void ShowTracks() => Batch(ShowTracksNow);

    void ShowTracksNow()
    {
        advBtn.On = tracksOpen;
        // Back to the simple view is always back to the default window size
        if (!tracksOpen && baseW > 0 && (extraW | extraH) != 0)
        {
            (extraW, extraH) = (0, 0);
            ApplyStretch();
        }
        var open = tracksOpen && probe != null;
        keyLane.Visible = open && !separate;
        posLane.Visible = scaleLane.Visible = open && separate;
        // Showing a track can move it in the list, so put the first four back in order every time
        var fixedOrder = new Control[] { keyLane, posLane, scaleLane, masterLane };
        for (var i = 0; i < fixedOrder.Length; i++) scroller.Content.Controls.SetChildIndex(fixedOrder[i], i);
        foreach (var (_, lane) in trackUi) lane.Visible = open;
        masterLane.Visible = trackUi.Count > 0;
        SizeTracks();
        ShowCrop();
    }

    /// <summary>
    /// Closed, the box is exactly its one lane. Open, it's as tall as the grip was dragged (short to begin with)
    /// and scrolls for the rest.
    /// </summary>
    void SizeTracks()
    {
        var all = scroller.Content.GetPreferredSize(Size.Empty).Height;
        var open = tracksOpen && probe != null;
        // Short to begin with: the first two tracks and a sliver of the next, so it's clear there's more
        if (tracksHeight == 0) tracksHeight = D(132);
        scroller.Visible = all > 0;
        scroller.Height = open ? Math.Clamp(tracksHeight, Math.Min(all, D(60)), all) : all;
        scroller.Scrolls = open && scroller.Height < all;
        grip.Visible = open && all > D(60);
    }

    void SetSeparate(bool on)
    {
        if (on == separate) return;
        if (!on)
        {
            // Back on one track they share its key points: every time either had one, with the values each had
            // there. Their own points are kept aside in case this is undone.
            (keptPos, keptScale) = ([.. pos.Keys], [.. scale.Keys]);
            var times = KeyTimes.ToList();
            pos.Resample(times);
            scale.Resample(times);
            (bakedPos, bakedScale) = ([.. pos.Keys], [.. scale.Keys]);
        }
        else if (keptPos != null && pos.Keys.SequenceEqual(bakedPos!) && scale.Keys.SequenceEqual(bakedScale!))
        {
            // Nothing was changed while they were combined: give each track back exactly the points it had
            pos.Keys.Clear();
            pos.Keys.AddRange(keptPos);
            scale.Keys.Clear();
            scale.Keys.AddRange(keptScale!);
        }
        if (on) keptPos = null;
        separate = on;
        ShowTracks();
    }

    /// <summary>Connects a key-point track on screen to the values it edits (one, or both for the shared track)</summary>
    void Wire(KeyLane lane, Func<IKeyTrack[]> tracks)
    {
        lane.AddRequested += t =>
        {
            if (!Cropping) return;
            var added = false;
            foreach (var k in tracks()) added |= k.Add(t);
            if (added) { timeline.Seek(t); UpdateSummary(); }
        };
        lane.RemoveRequested += i => { foreach (var k in tracks()) if (i < k.Count) k.RemoveAt(i); ShowCrop(); UpdateSummary(); };
        lane.MoveRequested += (i, t) => { foreach (var k in tracks()) if (i < k.Count) k.Move(i, t, timeline.Duration); ShowCrop(); };
        lane.Picked += i => { if (i < tracks()[0].Count) timeline.Seek(tracks()[0].TimeAt(i)); };
        HookTrack(lane);
    }

    /// <summary>What every keyframable track shares: its right-click menu, the wheel shortcuts, and knowing the mouse is over it</summary>
    void HookTrack(Control track)
    {
        track.MouseEnter += (_, _) => hoverTrack = track;
        track.MouseLeave += (_, _) => { if (hoverTrack == track) hoverTrack = null; };
        void Taller(int by) => SetTrackHeight(trackH + by * 14);
        if (track is VolumeLane v)
        {
            v.HeightRequested += Taller;
            v.ZoomRequested += ZoomBy;
            v.MenuRequested += at => ShowTrackMenu(track, at);
        }
        else if (track is KeyLane k)
        {
            k.HeightRequested += Taller;
            k.ZoomRequested += ZoomBy;
            k.MenuRequested += at => ShowTrackMenu(track, at);
        }
    }

    void ShowTrackMenu(Control track, Point at)
    {
        if (probe == null || encodeCts != null) return;
        trackMenu.Items.Clear();
        trackMenu.Add("Clear keyframes", "", () => ClearKeys(track));
        if (track is KeyLane) trackMenu.Add(separate ? "Combine position and scale" : "Separate position and scale", "", () => SetSeparate(!separate));
        trackMenu.Show(track, at);
    }

    /// <summary>Back to no key points. For audio that's a level volume line; for position and scale, the whole frame.</summary>
    void ClearKeys(Control track)
    {
        if (track is VolumeLane v) { v.ClearKeys(); if (track != masterLane) UpdateMasterPeaks(); return; }
        if (track != scaleLane) pos.Reset((0.5, 0.5));
        if (track != posLane) scale.Reset(MaxCropW);
        ShowCrop();
        UpdateSummary();
    }

    IEnumerable<double> KeyTimesOf(Control track) => track is VolumeLane v ? v.KeyTimes : track is KeyLane k ? k.KeyTimes : [];

    void SetTrackHeight(int height)
    {
        height = Math.Clamp(height, 42, 240);
        if (height == trackH) return;
        trackH = height;
        Batch(() =>
        {
            foreach (var lane in trackUi.Select(t => t.Lane).Append(masterLane)) lane.RowHeight = trackH;
            foreach (var lane in new[] { keyLane, posLane, scaleLane }) lane.RowHeight = trackH;
            SizeTracks();
        });
    }

    /// <summary>Zooms the timeline in or out, keeping the moment under the mouse where it is</summary>
    void ZoomBy(double factor, double around)
    {
        var start = around - (around - viewStart) * factor;
        SetView(start, start + (viewEnd - viewStart) * factor);
    }

    /// <summary>The whole frame again, in its own shape. While the picture is animated, that's a key point at the playhead.</summary>
    void ResetCrop()
    {
        if (!Cropping) return;
        SetCrop("src");
        pos.Set(timeline.Current, (0.5, 0.5));
        scale.Set(timeline.Current, MaxCropW);
        ShowCrop();
        UpdateSummary();
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
        var closest = scale.Keys.Min(k => Math.Min(k.Value, MaxCropW)) * SrcAspect / cropAspect;
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

    // ---------- position and scale ----------

    double SrcAspect => probe!.Width / (double)probe.Height;
    /// <summary>Widest a box of the current shape can be, as a fraction of the frame's width, and still fit its height</summary>
    double MaxCropW => Math.Min(1, cropAspect / SrcAspect);
    bool Cropping => probe != null && pos.Count > 0 && scale.Count > 0;
    /// <summary>The picture is anything other than the whole frame: another shape, or zoomed in at some point</summary>
    bool Reframed => Cropping && (Math.Abs(cropAspect - SrcAspect) > 1e-6 || scale.Keys.Any(k => k.Value < MaxCropW - 1e-6)
        || pos.Keys.Any(k => Math.Abs(k.Value.X - 0.5) > 1e-6 || Math.Abs(k.Value.Y - 0.5) > 1e-6));
    /// <summary>Every time either track has a key point, in order</summary>
    IEnumerable<double> KeyTimes => pos.Keys.Select(k => k.T).Concat(scale.Keys.Select(k => k.T)).Distinct().Order();

    /// <summary>
    /// The picture at a time, as a rectangle in fractions of the frame. It may hang off the frame (that part is
    /// black in the result), as far as its centre reaching the edge.
    /// </summary>
    RectangleF CropRectAt(double t)
    {
        var (cx, cy) = pos.At(t);
        double w = Math.Min(scale.At(t), MaxCropW), h = w * SrcAspect / cropAspect;
        return new RectangleF((float)(Math.Clamp(cx, 0, 1) - w / 2), (float)(Math.Clamp(cy, 0, 1) - h / 2), (float)w, (float)h);
    }

    void ClampCropWidths()
    {
        for (var i = 0; i < scale.Keys.Count; i++) scale.Keys[i] = (scale.Keys[i].T, Math.Min(scale.Keys[i].Value, MaxCropW));
    }

    void SetCrop(string mode)
    {
        cropMode = mode;
        if (probe != null)
        {
            cropAspect = mode switch { "16:9" => 16 / 9.0, "1:1" => 1, "9:16" => 9 / 16.0, "free" when cropAspect > 0 => cropAspect, _ => SrcAspect };
            // Starts as the biggest box of that shape, in the middle
            if (pos.Count == 0)
            {
                pos.Reset((0.5, 0.5));
                scale.Reset(MaxCropW);
            }
            ClampCropWidths();
        }
        ShowCrop();
        UpdateSummary();
    }

    /// <summary>Puts the box for the current time on the preview, and the key points on their tracks</summary>
    void ShowCrop()
    {
        if (!Cropping) return;
        // The box keeps its shape while it's resized, unless Alt is held
        preview.BoxAspect = cropAspect / SrcAspect;
        preview.MaxBoxWidth = (float)MaxCropW;
        // The box stays out of the way until it's wanted: Advanced is open, or the picture has been reframed
        preview.Box = tracksOpen || Reframed ? CropRectAt(timeline.Current) : null;
        // How far zoomed in: 0 at the widest the shape allows
        double Zoom(double w) => 1 - Math.Min(w, MaxCropW) / MaxCropW;
        Fill(keyLane, pos, pos.Keys.Select(k => Zoom(scale.At(k.T))));
        Fill(scaleLane, scale, scale.Keys.Select(k => Zoom(k.Value)));
        // Across as the main line (right = up), down as the fainter one
        Fill(posLane, pos, pos.Keys.Select(k => k.Value.X), pos.Keys.Select(k => 1 - k.Value.Y));
    }

    void Fill(KeyLane lane, IKeyTrack track, IEnumerable<double> levels, IEnumerable<double>? levels2 = null)
    {
        (lane.Duration, lane.Current) = (timeline.Duration, timeline.Current);
        lane.Keys = Enumerable.Range(0, track.Count).Select(track.TimeAt).ToList();
        lane.Levels = levels.ToList();
        lane.Levels2 = levels2?.ToList() ?? [];
        if (lane.Visible) lane.Invalidate();
    }

    /// <summary>
    /// The box was dragged on the preview. A track with one key point holds a fixed value, so it just changes;
    /// with several, the point under the playhead changes, or one is added there. Sharing a track, position and
    /// scale always change together; on their own tracks, moving the box leaves the scale's points alone.
    /// </summary>
    void EditCrop(RectangleF r)
    {
        if (!Cropping) return;
        // Reshaped (Alt + drag): that's now the shape at every moment, or the output would change size mid-video
        var shape = r.Width / r.Height * SrcAspect;
        if (Math.Abs(shape - cropAspect) > cropAspect * 0.002)
        {
            (cropAspect, cropMode) = (shape, "free");
            ClampCropWidths();
        }
        var t = timeline.Current;
        var resized = Math.Abs(r.Width - CropRectAt(t).Width) > 1e-4;
        pos.Set(t, (r.X + r.Width / 2, r.Y + r.Height / 2));
        if (!separate || resized) scale.Set(t, r.Width);
        ShowCrop();
    }

    /// <summary>
    /// What gets planned as the source: the whole frame, or when reframed the picture at its most zoomed in. The
    /// output is that size (times the scale), so that moment is pixel for pixel and nothing is ever enlarged.
    /// </summary>
    ProbeResult PlanSource
    {
        get
        {
            if (!Reframed) return probe!;
            var w = scale.Keys.Min(k => Math.Min(k.Value, MaxCropW));
            int pw = Math.Max(2, (int)Math.Round(w * probe!.Width) & ~1), ph = Math.Max(2, (int)Math.Round(pw / cropAspect) & ~1);
            return probe with { Width = pw, Height = Math.Min(ph, probe.Height & ~1) };
        }
    }

    /// <summary>The picture in source pixels (left, top, width) at every key time, for the encoder</summary>
    List<(double T, double X, double Y, double W)>? CropForEncode() => !Reframed ? null
        : KeyTimes.Select(t => { var r = CropRectAt(t); return (t, (double)r.X * probe!.Width, (double)r.Y * probe.Height, (double)r.Width * probe.Width); }).ToList();

    // ---------- window size ----------

    void CaptureBase() => (baseW, basePreviewH, baseChipMax) = (preview.Width, preview.Height, sizeChips.MaximumSize.Width);

    /// <summary>Makes every row as wide, and the preview as tall, as the window was dragged</summary>
    void ApplyStretch()
    {
        var w = baseW + extraW;
        root.SuspendLayout();
        foreach (var c in new Control[] { titleRow, preview, previewGrip, timeRow, zoomBar, timeline, keyLane, posLane, scaleLane, scroller, grip, bar }) c.Width = w;
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
        if (fitting || holdFit > 0 || Controls.Count == 0 || WindowState != FormWindowState.Normal) return;
        fitting = true;
        try
        {
            if (userSizing && baseW > 0)
            {
                var c = Controls[0];
                int dw = ClientSize.Width - (c.Right + c.Left), dh = ClientSize.Height - (c.Bottom + c.Top);
                if (dw != 0 || dh != 0)
                {
                    extraW = Math.Max(0, extraW + dw);
                    // Height goes to the track box while it's open, so closing Advanced leaves the window as it was
                    if (tracksOpen) tracksHeight = Math.Max(D(60), scroller.Height + dh);
                    else extraH = Math.Max(0, extraH + dh);
                    ApplyStretch();
                    if (tracksOpen) SizeTracks();
                    root.Size = root.GetPreferredSize(Size.Empty);
                }
            }
            // The minimum is the size with nothing stretched; lift it first so the window can also shrink to fit
            MinimumSize = Size.Empty;
            base.FitToContent();
            // With the track box open, it can also be dragged down to its smallest
            var slack = tracksOpen ? Math.Max(0, scroller.Height - D(60)) : 0;
            if (baseW > 0) MinimumSize = new Size(Width - extraW, Height - extraH - slack);
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

    /// <summary>One frame at the chosen frame rate, in seconds: what's marked on the timeline and what moved key points land on</summary>
    double FrameLength => probe == null ? 0 : 1.0 / (frameRate ?? (probe.Fps > 0 ? probe.Fps : 30));

    void ApplyFrameGrid()
    {
        var frame = FrameLength;
        if (timeline.Grid == frame) return;
        timeline.Grid = keyLane.Grid = posLane.Grid = scaleLane.Grid = masterLane.Grid = frame;
        foreach (var (_, lane) in trackUi) lane.Grid = frame;
        timeline.Invalidate();
        foreach (Control c in scroller.Content.Controls) c.Invalidate();
        // Zoomed right in, the thumbnails are one per frame, so they follow the frame rate too
        if ((viewEnd - viewStart) / frame <= timeline.Thumbs.Length) { thumbTimer.Stop(); thumbTimer.Start(); }
    }

    /// <param name="keepStatus">leave an error or "Cancelled" showing instead of going back to the result line</param>
    void UpdateSummary(bool keepStatus = false)
    {
        if (outPath != null || probe == null) return;
        ApplyFrameGrid();
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
                SrcW = probe.Width,
                SrcH = probe.Height,
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
        foreach (var p in new[] { sizeChips, resChips, fpsChips }.SelectMany(g => g.Controls.OfType<Pill>())) p.Enabled = !busy;
        preview.Enabled = keyLane.Enabled = posLane.Enabled = scaleLane.Enabled = !busy;
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
            // Over a track with key points, the arrows go from one to the next
            case Keys.Left or Keys.Right when mods == Keys.None && hoverTrack is { Visible: true } && KeyTimesOf(hoverTrack).ToList() is { Count: > 0 } times:
                var back = key == Keys.Left;
                var to = back ? times.Where(t => t < timeline.Current - 0.01).Select(t => (double?)t).LastOrDefault() : times.Where(t => t > timeline.Current + 0.01).Select(t => (double?)t).FirstOrDefault();
                if (to is { } there) timeline.Seek(there);
                break;
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
        base.OnFormClosing(e);
    }
}
