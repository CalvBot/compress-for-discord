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
    List<(double T, (double X, double Y) Value, Ease E)>? keptPos, bakedPos;
    List<(double T, double Value, Ease E)>? keptScale, bakedScale;
    /// <summary>The values behind each position / scale track on screen</summary>
    readonly Dictionary<KeyLane, Func<IKeyTrack[]>> keyTracks = [];
    readonly DarkMenu trackMenu = new(), cropMenu = new();
    /// <summary>Height of every keyframable track, in 96-dpi pixels (Shift + wheel over a track changes it)</summary>
    int trackH = 56;
    /// <summary>The track the mouse is over: the arrow keys then jump between its key points</summary>
    KeyTrackLane? hoverTrack;
    /// <summary>Height of the track box while Advanced is open; the grip under it changes it</summary>
    int tracksHeight, grabHeight;
    readonly ZoomBar zoomBar;
    /// <summary>The part of the video the timeline and tracks are showing (zoom)</summary>
    double viewStart, viewEnd;
    CancellationTokenSource? thumbCts;
    /// <summary>Waits for zooming to settle before remaking the thumbnails</summary>
    readonly System.Windows.Forms.Timer thumbTimer = new() { Interval = 150 };
    readonly Timeline timeline;
    readonly FlowLayoutPanel sizeChips, resChips, fpsChips, buttons;
    readonly VolumeLane masterLane;
    readonly TextBox customMb, fpsBox, resBox, timeBox;
    readonly RoundBox fpsField, resField;
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

    /// <summary>A saved project to set the window up as, once the video is read</summary>
    readonly Project? restore;
    /// <summary>The settings as they were once the video was read, to tell whether anything was changed since</summary>
    string? baseline;
    /// <summary>Saved to the recent projects already (after a compress), so closing doesn't save it again</summary>
    bool projectSaved;

    public MainForm(string file, Project? project = null)
    {
        this.file = file;
        restore = project;
        fileSize = new FileInfo(file).Length;
        targetMb = cfg.TargetMb;

        SuspendLayout();
        Text = $"{Path.GetFileName(file)} · Compress for Discord";
        // Opens at its smallest size; drag an edge for a longer timeline and bigger preview
        FormBorderStyle = FormBorderStyle.Sizable;

        // GrowAndShrink: it must get smaller again when something inside does, or the window is left too tall
        root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(18, 14), Margin = Padding.Empty };

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
        preview.Stepped += by => { if (probe != null && encodeCts == null) timeline!.Seek(timeline.Current + by * StepLength); };
        // Right-click: reset the picture, or change its shape
        cropMenu.Add("Reset crop", "", ResetCrop);
        cropMenu.Items.Add(new ToolStripSeparator());
        var shapes = new[] { ("Original", "src"), ("16:9", "16:9"), ("1:1", "1:1"), ("9:16", "9:16") }
            .Select(shape => (Item: cropMenu.Add(shape.Item1, "", () => SetCrop(shape.Item2)), Mode: shape.Item2)).ToList();
        cropMenu.Opening += (_, _) => { foreach (var (item, mode) in shapes) item.ShortcutKeyDisplayString = mode == cropMode ? "✓" : null; };
        preview.MenuRequested += at => { if (probe != null && encodeCts == null) cropMenu.Show(preview, at); };

        // Its strip is as tall as a track; unlike the tracks it never changes height
        timeline = new Timeline { Width = ContentWidth, RowHeight = trackH, Enabled = false, Margin = Padding.Empty };
        timeline.RangeChanged += () => { UpdateTimeLabel(); UpdateSummary(); };
        // Key points of the picture's position and scale, shown while Advanced is open: one track for both, or
        // (right-click) one each
        var warm = Theme.Blend(Theme.Bg3, Theme.Warn, 0.13);
        KeyLane Lane(string label) => new() { Label = label, Back = warm, Size = new Size(ContentWidth, 56), Margin = new Padding(0, 4, 0, 0), Visible = false };
        keyLane = Lane("Position & scale");
        posLane = Lane("Position");
        scaleLane = Lane("Scale");
        Wire(keyLane, () => [pos, scale]);
        Wire(posLane, () => [pos]);
        Wire(scaleLane, () => [scale]);

        // Clicking or dragging on the strip: stop if it's playing, and show that frame
        timeline.FrameLock = cfg.FrameLock;
        timeline.FrameLockToggled += () =>
        {
            cfg.FrameLock = timeline.FrameLock;
            cfg.Save();
            if (timeline.FrameLock && probe != null) timeline.Seek(timeline.Current);
        };
        timeline.Seeked += t => { if (playCts != null) stoppedByClick = Environment.TickCount64; StopPlayback(); RequestFrame(t); };
        // Double-click plays from that spot to the end of the video, so you can watch what's outside the trim.
        // Not when the first click of the two was what stopped it playing: then it just stays stopped there.
        timeline.PlayRequested += t => { if (probe != null && encodeCts == null && Environment.TickCount64 - stoppedByClick > 800) { StopPlayback(); _ = PlayFrom(t, probe.Duration); } };

        var timelineMenu = new DarkMenu();
        timelineMenu.Add("Clear in and out", "", () => timeline.SetRange(0, timeline.Duration));
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

        // A "Custom" box at the end of a row of chips; anything it can't read is cleared when it loses focus
        (TextBox Box, RoundBox Field) CustomField(Func<string, bool> valid)
        {
            var box = new TextBox { BackColor = Theme.Bg2, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, PlaceholderText = "Custom", TextAlign = HorizontalAlignment.Center };
            // Just wide enough for "Custom": about chip-sized, so it doesn't stand out from the row
            var field = new RoundBox { LogicalWidth = 62, Inset = 5, Margin = new Padding(0, 0, 6, 6), Cursor = Cursors.IBeam };
            box.GotFocus += (_, _) => field.Invalidate();
            box.LostFocus += (_, _) => field.Invalidate();
            box.Leave += (_, _) => { if (box.Text != "" && !valid(box.Text)) box.Text = ""; };
            field.Controls.Add(box);
            field.Click += (_, _) => box.Focus();
            return (box, field);
        }

        sizeChips = ChipRow();
        // A row's custom box emptied with none of its chips picked: back to the first chip, so the box never
        // keeps acting on a value that's no longer shown
        bool Emptied(TextBox box, FlowLayoutPanel? row) =>
            box.Text.Trim() == "" && row != null && !AnyChipOn(row);

        (customMb, var customBox) = CustomField(t => ParseMb(t) > 0);
        foreach (var mb in Planner.LimitPresets) sizeChips.Controls.Add(Chip(Planner.FormatBytes(mb * Planner.MB), mb, sizeChips, v => { customMb.Text = ""; SetTarget((double)v!, remember: true); }));
        sizeChips.Controls.Add(customBox);
        if (!Planner.LimitPresets.Contains(targetMb)) customMb.Text = targetMb.ToString("0.##");
        SelectChip(sizeChips, targetMb);
        customMb.TextChanged += (_, _) =>
        {
            if (Emptied(customMb, sizeChips))
            {
                var mb = Planner.LimitPresets[0];
                SelectChip(sizeChips, mb);
                SetTarget(mb, remember: false);
                return;
            }
            var n = ParseMb(customMb.Text);
            if (n <= 0) return;
            SelectChip(sizeChips, null);
            SetTarget(n, remember: false);
            typedSize |= customMb.Focused;
        };
        // Typed into and left: that size is the one for next time (not one opened with a project)
        customMb.Leave += (_, _) => { if (typedSize) RememberTarget(); typedSize = false; };

        // Each custom box shows the value really in use once left: cleared when that's a chip's, else the number
        // after it was held to the source ("60" on a 30 fps video is the source rate, a too-small scale goes up)
        void ShowUsed(TextBox box, FlowLayoutPanel row, int? used)
        {
            if (box.Text == "") return;
            box.Text = AnyChipOn(row) ? "" : used?.ToString() ?? "";
        }

        // Frame rate: the usual ones as chips, anything else typed in
        (fpsBox, fpsField) = CustomField(t => int.TryParse(t, out var n) && n > 0);
        fpsBox.TextChanged += (_, _) =>
        {
            if (probe != null && Emptied(fpsBox, fpsChips)) { frameRate = null; SelectChip(fpsChips!, null); UpdateSummary(); return; }
            if (probe == null || !int.TryParse(fpsBox.Text, out var n) || n < 1) return;
            // Never above the source's own rate
            frameRate = n < probe.Fps - 0.5 ? n : null;
            SelectChip(fpsChips!, frameRate);
            UpdateSummary();
        };
        fpsBox.Leave += (_, _) => { if (probe != null) ShowUsed(fpsBox, fpsChips!, frameRate); };

        // Scale: 100, 75 and 50% as chips, any other percentage typed in
        (resBox, resField) = CustomField(t => int.TryParse(t.Trim().TrimEnd('%'), out var n) && n > 0);
        resBox.TextChanged += (_, _) =>
        {
            if (probe != null && Emptied(resBox, resChips)) { scalePct = null; SelectChip(resChips!, null); UpdateSummary(); return; }
            if (probe == null || !int.TryParse(resBox.Text.Trim().TrimEnd('%'), out var n) || n < 1) return;
            // Never above the source, and never below a watchable size
            n = Math.Min(n, 100);
            while (n < 100 && ScaledSide(n) < 144) n++;
            scalePct = n < 100 ? n : null;
            SelectChip(resChips!, scalePct);
            UpdateSummary();
        };
        resBox.Leave += (_, _) => { if (probe != null) ShowUsed(resBox, resChips!, scalePct); };

        resChips = ChipRow();
        fpsChips = ChipRow();
        // Under the timeline: position and scale, the result audio, then every audio track, all as wide as the
        // timeline so times line up. Closed, only the result audio shows. Open, it's a box that scrolls, with a
        // grip under it to make it taller.
        scroller = new TrackScroller { Size = new Size(ContentWidth, 0), Margin = Padding.Empty };
        grip = new SizeGrip { Size = new Size(ContentWidth, 10), Margin = Padding.Empty, Visible = false };
        grip.DragStarted += () => grabHeight = scroller.Height;
        grip.Dragged += by => DragTracks(grabHeight + by);
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
            if (c == customMb || c == timeBox || c == fpsBox || c == resBox) return;
            c.MouseDown += (_, _) => { if (Typing) LeaveCustomBox(); };
            // Chips and audio lanes are added once the video is read, so catch those too
            c.ControlAdded += (_, e) => { if (e.Control != null) HookClicks(e.Control); };
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
        _ = Task.Run(Log.Calibration).ContinueWith(t =>
        {
            // The window may have been closed while the log was read
            try { BeginInvoke(() => { calibration = t.Result; UpdateSummary(); }); } catch (InvalidOperationException) { }
        }, TaskScheduler.Default);
        await LoadVideo();
    }


    async Task LoadVideo()
    {
        // Already loaded, or being read (Settings re-reports ffmpeg, maybe while the first read is still going).
        // A request during a read is kept: if that read fails (likely, with the ffmpeg just replaced), try again.
        if (probe != null) return;
        if (loading) { reloadWanted = true; return; }
        loading = true;
        try
        {
            do { reloadWanted = false; await ReadVideo(); }
            while (reloadWanted && probe == null && !IsDisposed);
        }
        finally { loading = false; }
    }

    bool loading, reloadWanted;
    /// <summary>A size was typed into the custom box since it was last left</summary>
    bool typedSize;

    /// <summary>A size in MB as typed: "8", "8.5" or "8,5" (a comma as the decimal point); 0 if it isn't one</summary>
    static double ParseMb(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : 0;

    async Task ReadVideo()
    {
        var err = Ff.Locate(cfg.FfmpegPath);
        var missing = err != null;
        settingsBtn.Visible = downloadBtn.Visible = missing;
        goBtn.Visible = moreBtn.Visible = !missing;
        if (err != null) { ShowStatus(err, Theme.Danger); return; }
        ShowStatus("Reading video…", Theme.Muted);
        try { probe = await Ff.Probe(file); }
        catch (Exception ex) { AppLog.Error($"Couldn't read {file}", ex); ShowStatus(ex.Message, Theme.Danger); goBtn.Enabled = false; return; }
        if (IsDisposed) return;
        AppLog.Info($"Loaded {file}: {probe.Width}x{probe.Height} {probe.Fps:0.##} fps, {probe.Duration:0.00} s, {probe.AudioTracks.Count} audio track(s)");
        // Ready in the background for scrubbing; until it is (or if Windows can't read it), frames come from ffmpeg
        var (path, srcFps) = (file, probe.Fps);
        frameReader = Task.Run(() => FrameReader.Open(path, srcFps));

        var shortSide = Math.Min(probe.Width, probe.Height);
        var fps = (int)Math.Round(probe.Fps);
        var srcKbps = (probe.Bitrate > 0 ? probe.Bitrate : fileSize * 8 / Math.Max(probe.Duration, 0.1)) / 1000.0;
        details = $" · {Planner.FormatBytes(fileSize)} · {shortSide}p{fps} · {Planner.FormatRate(srcKbps)}";
        FitSubtitle();
        // Hovering the top line: the source in full
        var lines = new List<string>
        {
            Path.GetFileName(file),
            $"Folder:  {Path.GetDirectoryName(file)}",
            $"Size:  {Planner.FormatBytes(fileSize)}",
            $"Length:  {Planner.FormatTime(probe.Duration)}",
            $"Picture:  {probe.Width}×{probe.Height} at {probe.Fps:0.##} fps",
            $"Video:  {probe.VideoCodec.ToUpperInvariant()}{(probe.VideoBitrate > 0 ? $", {Planner.FormatRate(probe.VideoBitrate / 1000.0)}" : "")}",
            $"Overall:  {Planner.FormatRate(srcKbps)}",
        };
        if (probe.AudioTracks.Count == 0) lines.Add("Audio:  none");
        foreach (var a in probe.AudioTracks)
            lines.Add($"Audio {a.Index + 1}:  {a.Label} · {a.Codec.ToUpperInvariant()}, {(a.Layout != "" ? a.Layout : $"{a.Channels} ch")}{(a.Language == "" ? "" : $", {a.Language}")}");
        tips.SetToolTip(subtitle, string.Join("\n", lines));
        // Chips, tracks and the saved project all go in, then the window is laid out and drawn once
        Batch(() => SetUp(probe, fps));
        baseline = StateKey();
        RequestFrame(timeline.Current);
        _ = ResolveAutoEncoder();
        _ = LoadThumbnails();
#if DEBUG
        ApplyTestState();
#endif
    }

    void SetUp(ProbeResult probe, int fps)
    {
        preview.FrameAspect = probe.Width / (double)probe.Height;
        timeline.Reset(probe.Duration);
        zoomBar.Duration = probe.Duration;
        // Closest zoom: a dozen frames across the timeline
        zoomBar.MinSpan = Math.Min(probe.Duration, 12 / probe.Fps);
        SetView(0, probe.Duration, remakeThumbs: false);
        timeline.Enabled = true;
        playBtn.Enabled = true;
        ActiveControl = timeline;

        // Scale: a percentage of the source, or of the crop when there is one
        // 100% always (even for a source under 144p), the others only when they'd still be watchable
        foreach (var pct in Planner.ScaleChips.Where(pct => pct == 100 || ScaledSide(pct) >= 144))
            resChips.Controls.Add(Chip($"{pct}%", pct == 100 ? null : pct, resChips, v => { resBox.Text = ""; scalePct = (int?)v; UpdateSummary(); }));
        resChips.Controls.Add(resField);
        SelectChip(resChips, null);
        void PickFps(object? v) { fpsBox.Text = ""; frameRate = (int?)v; UpdateSummary(); }
        fpsChips.Controls.Add(Chip($"{fps}", null, fpsChips, PickFps));
        foreach (var f in Planner.FrameRates.Where(f => f < probe.Fps - 0.5))
            fpsChips.Controls.Add(Chip($"{f}", f, fpsChips, PickFps));
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
        (masterLane.Duration, masterLane.End) = (probe.Duration, probe.Duration);
        LayoutLanes();
        ShowTracks();
        LoadPeaks();

        UpdateTimeLabel();
        UpdateSummary();
        ShowStatus("", Theme.Muted);
        if (restore != null) Restore(restore);
    }

    // ---------- recent projects ----------

    /// <summary>Everything set up in the window, to save as a recent project</summary>
    Project Snapshot() => new()
    {
        File = file,
        Start = timeline.Start,
        End = timeline.End,
        Current = timeline.Current,
        TargetMb = targetMb,
        ScalePct = scalePct,
        FrameRate = frameRate,
        CropMode = cropMode,
        CropAspect = cropAspect,
        Separate = separate,
        TracksOpen = tracksOpen,
        Pos = [.. pos.Keys.Select(k => new PosKey(k.T, k.Value.X, k.Value.Y, k.E))],
        Scale = [.. scale.Keys.Select(k => new ValueKey(k.T, k.Value, k.E))],
        Tracks = [.. trackUi.Select(t => State(t.Track.Index, t.Lane))],
        Master = trackUi.Count > 0 ? State(-1, masterLane) : null,
    };

    static TrackState State(int index, VolumeLane lane) => new(index, lane.Active, lane.Volume, [.. lane.Keys.Select(k => new ValueKey(k.T, k.V, k.E))]);

    /// <summary>The settings that make a project worth saving: not where the playhead is or whether Advanced is open</summary>
    string StateKey()
    {
        var p = Snapshot();
        (p.Current, p.TracksOpen) = (0, false);
        return System.Text.Json.JsonSerializer.Serialize(p);
    }

    void SaveProject()
    {
        if (probe == null || projectSaved) return;
        projectSaved = true;
        Projects.Save(Snapshot());
    }

    /// <summary>Sets the window up as a saved project</summary>
    void Restore(Project p)
    {
        // A project file from an older version, or edited by hand, mustn't stop the video opening
        try { RestoreNow(p); }
        catch (Exception ex) { AppLog.Error("Couldn't restore the project; opening with defaults", ex); }
    }

    void RestoreNow(Project p)
    {
        // Missing lists come back as null from a hand-edited or older file; points are expected in time order
        static List<T> Sorted<T>(List<T>? keys, Func<T, double> time) => keys?.OrderBy(time).ToList() ?? [];
        var posKeys = Sorted(p.Pos, k => k.T);
        var scaleKeys = Sorted(p.Scale, k => k.T);
        // Opening a project uses its size without changing the size remembered for new videos (remember: false)
        Batch(() =>
        {
            timeline.SetEnd(p.End);
            timeline.SetStart(p.Start);
            if (p.TargetMb > 0)
            {
                if (Planner.LimitPresets.Contains(p.TargetMb))
                {
                    customMb.Text = "";
                    SelectChip(sizeChips, p.TargetMb);
                    SetTarget(p.TargetMb, remember: false);
                }
                else customMb.Text = p.TargetMb.ToString("0.##");
            }
            scalePct = p.ScalePct;
            SelectChip(resChips, scalePct);
            if (scalePct is { } pctNow && !AnyChipOn(resChips)) resBox.Text = pctNow.ToString();
            frameRate = p.FrameRate;
            SelectChip(fpsChips, frameRate);
            if (frameRate is { } rate && !AnyChipOn(fpsChips)) fpsBox.Text = rate.ToString();

            // The picture: its shape, then the position and scale key points
            if (posKeys.Count > 0 && scaleKeys.Count > 0)
            {
                cropAspect = p.CropAspect;
                pos.Keys.Clear();
                pos.Keys.AddRange(posKeys.Select(k => (k.T, (k.X, k.Y), k.E)));
                scale.Keys.Clear();
                scale.Keys.AddRange(scaleKeys.Select(k => (k.T, k.V, k.E)));
            }
            SetCrop(p.CropMode);
            separate = p.Separate;
            keptPos = null;

            // Audio: each track that's still there, by its number
            void Apply(TrackState s, VolumeLane lane)
            {
                // A lone track has no lane of its own on screen to unmute it with (the Audio lane mutes instead)
                (lane.Active, lane.Volume) = (s.Active || (lane != masterLane && trackUi.Count == 1), s.Volume);
                lane.Selection.Clear();
                lane.Keys.Clear();
                lane.Keys.AddRange(Sorted(s.Keys, k => k.T).Select(k => (k.T, k.V, k.E)));
                lane.Invalidate();
            }
            foreach (var s in p.Tracks ?? [])
                if (trackUi.FirstOrDefault(t => t.Track.Index == s.Index).Lane is { } lane) Apply(s, lane);
            if (p.Master != null && trackUi.Count > 0) Apply(p.Master, masterLane);
            UpdateMasterPeaks();

            tracksOpen = p.TracksOpen;
            ShowTracksNow();
            timeline.Seek(Math.Clamp(p.Current, timeline.Start, timeline.End));
            ShowCrop();
            UpdateSummary();
        });
    }

#if DEBUG
    /// <summary>
    /// Debug builds only: puts the window into a state for screenshots and end-to-end tests without clicking.
    /// CFD_TEST="size=20;res=50;rate=30;trim=20;cropkeys;keys;save=C:\\out.mp4" (res is a percentage, trim the end time)
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
                case "res": resBox.Text = value; break;
                case "crop": SetCrop(value); break;
                case "rate": fpsBox.Text = value; break;
                case "separate": SetSeparate(true); break;
                case "select": keyLane.Select(int.Parse(value)); break;
                case "del": DeleteSelectedKey(); break;
                // selmulti=lane:0,1 (lane: key, pos, scale, or an audio track number)
                case "selmulti":
                    var (which, ids) = (value.Split(':')[0], value.Split(':')[1].Split(',').Select(int.Parse));
                    if (which is "key" or "pos" or "scale") (which == "key" ? keyLane : which == "pos" ? posLane : scaleLane).Selection.SetMany(ids);
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
                    var (moves0, paints0, ms0) = (timeline.DebugMoves, timeline.DebugPaints, timeline.DebugMoveMs);
                    _ = Task.Run(() =>
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var posted = 0;
                        while (watch.Elapsed.TotalSeconds < seconds)
                        {
                            var spin = System.Diagnostics.Stopwatch.StartNew();
                            while (spin.Elapsed.TotalMilliseconds < 1) Thread.SpinWait(50);
                            if (timeline.DebugMoves - moves0 < posted) continue;
                            // Across and back once every 2 s
                            var phase = watch.Elapsed.TotalSeconds % 2 / 2;
                            var x = 40 + (int)((w - 60) * (phase < 0.5 ? phase * 2 : 2 - phase * 2));
                            PostMessage(handle, 0x200, 1, (y << 16) | x);
                            posted++;
                        }
                        BeginInvoke(() =>
                        {
                            timeline.DebugEndDrag();
                            int moves = timeline.DebugMoves - moves0, paints = timeline.DebugPaints - paints0;
                            AppLog.Info($"Drag: {moves / seconds:0} moves/s, {paints / seconds:0} timeline paints/s, {(timeline.DebugMoveMs - ms0) / Math.Max(1, moves):0.00} ms per move");
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
                case "play": TogglePlay(); break;
                case "goto": timeBox.Text = value; if (ParseTime(value) is { } go) timeline.Seek(go); break;
                case "cropkeys":
                    // A zoom: the full box at 20% of the way in, half the width and off-centre by 60%
                    pos.Keys.Clear();
                    scale.Keys.Clear();
                    pos.Keys.AddRange([(probe!.Duration * 0.2, (0.5, 0.5), Ease.Linear), (probe.Duration * 0.6, (0.62, 0.45), Ease.Linear)]);
                    scale.Keys.AddRange([(probe.Duration * 0.2, MaxCropW, Ease.Linear), (probe.Duration * 0.6, MaxCropW * 0.5, Ease.Linear)]);
                    timeline.Seek(probe.Duration * 0.4);
                    ShowCrop();
                    UpdateSummary();
                    break;
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
        // Nothing to zoom into before the video is read (and no thumbnails to make yet)
        if (probe == null) return;
        var duration = timeline.Duration;
        var span = Math.Clamp(keepSpan ? viewEnd - viewStart : end - start, zoomBar.MinSpan, duration);
        start = Math.Clamp(start, 0, duration - span);
        if (start == viewStart && start + span == viewEnd) return;
        (viewStart, viewEnd) = (start, start + span);
        (timeline.ViewStart, timeline.ViewEnd) = (zoomBar.ViewStart, zoomBar.ViewEnd) = (viewStart, viewEnd);
        foreach (var lane in AllTracks)
        {
            (lane.ViewStart, lane.ViewEnd) = (viewStart, viewEnd);
            lane.Invalidate();
        }
        timeline.Invalidate();
        zoomBar.Invalidate();
        if (remakeThumbs) { thumbTimer.Stop(); thumbTimer.Start(); }
    }

    /// <summary>Every thumbnail made so far, by the moment it shows, so zooming back out (or in again) reuses them</summary>
    readonly SortedList<double, Image> thumbCache = [];
    /// <summary>Times in the cache whose picture is the keyframe before them, not the exact frame</summary>
    readonly HashSet<double> roughThumbs = [];
    const int ThumbCacheMax = 400;

    /// <summary>The cached thumbnail closest in time to t, and its time</summary>
    (Image? Img, double At) NearestThumb(double t)
    {
        var keys = thumbCache.Keys;
        if (keys.Count == 0) return (null, 0);
        int lo = 0, hi = keys.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid] < t) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && t - keys[lo - 1] < keys[lo] - t) lo--;
        return (thumbCache.Values[lo], keys[lo]);
    }

    /// <summary>
    /// Thumbnails for the stretch the timeline is showing. Each cell shows the nearest one already made straight
    /// away, then the ones it still needs fill in as they arrive.
    /// </summary>
    async Task LoadThumbnails()
    {
        thumbCts?.Cancel();
        var cts = thumbCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        double start = viewStart, span = viewEnd - viewStart;
        // Zoomed in until there are fewer frames than thumbnails: show the frames themselves, one each
        const int Usual = 16;
        var frame = FrameLength;
        var count = Usual;
        if (frame > 0 && span / frame <= Usual)
        {
            start = Math.Floor(start / frame) * frame;
            count = Math.Max(1, (int)Math.Ceiling((viewEnd - start) / frame - 1e-6));
            span = count * frame;
        }
        var cell = span / count;
        var strip = new Image?[count];
        var missing = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var mid = start + (i + 0.5) * cell;
            var (img, at) = NearestThumb(mid);
            strip[i] = img;
            // Only a thumbnail from inside this cell is the right picture; anything else is a stand-in
            // (and one from a keyframe is only near its time: not good enough once zoomed in)
            if (img == null || Math.Abs(at - mid) > cell / 2 || (cell < 20 && roughThumbs.Contains(at))) missing.Add(i);
        }
        (timeline.Thumbs, timeline.ThumbStart, timeline.ThumbSpan) = (strip, start, span);
        timeline.StripChanged();
        if (missing.Count == 0) return;

        var times = missing.Select(i => start + (i + 0.5) * cell).ToArray();
        // Each picture standing for 20 s or more: the keyframe just before its time is close enough, and quicker
        var rough = cell >= 20;
        await Ff.Thumbnails(file, times, timeline.Height, keyOnly: rough, (j, img) =>
        {
            void Keep()
            {
                if (IsDisposed) { img.Dispose(); return; }
                // Kept even if the view has moved on: it's likely wanted again
                var t = times[j];
                if (thumbCache.TryGetValue(t, out var had))
                {
                    if (rough || !roughThumbs.Contains(t)) { img.Dispose(); return; }
                    // The exact picture for a time that only had its keyframe's: it takes over, wherever that shows
                    var shown = timeline.Thumbs;
                    for (var i = 0; i < shown.Length; i++) if (shown[i] == had) shown[i] = img;
                    had.Dispose();
                    thumbCache[t] = img;
                    roughThumbs.Remove(t);
                }
                else
                {
                    thumbCache.Add(t, img);
                    if (rough) roughThumbs.Add(t);
                }
                if (timeline.Thumbs == strip) { strip[missing[j]] = img; timeline.StripChanged(); }
                TrimThumbCache();
            }
            try { BeginInvoke(Keep); } catch { img.Dispose(); }
        }, cts.Token);
    }

    /// <summary>Drops the thumbnails furthest from what's on screen once there are too many (never one on screen)</summary>
    void TrimThumbCache()
    {
        if (thumbCache.Count <= ThumbCacheMax) return;
        var centre = (viewStart + viewEnd) / 2;
        var showing = timeline.Thumbs.Where(t => t != null).ToHashSet();
        foreach (var (t, img) in thumbCache.OrderByDescending(kv => Math.Abs(kv.Key - centre)).Take(thumbCache.Count - ThumbCacheMax * 3 / 4).ToList())
        {
            if (showing.Contains(img)) continue;
            thumbCache.Remove(t);
            roughThumbs.Remove(t);
            img.Dispose();
        }
    }

    // ---------- preview frame ----------

    /// <summary>When the last preview frame was asked for (Environment.TickCount64), so the waveforms can make way while scrubbing</summary>
    long lastFrameRequest;

    void RequestFrame(double t)
    {
        pendingFrame = t;
        lastFrameRequest = Environment.TickCount64;
        UpdateTimeLabel();
        if (!frameBusy) LoadFrames();
    }

    /// <summary>
    /// Fetches the frame that's wanted, then (if the playhead has moved on meanwhile) the one wanted now, and so on.
    /// So dragging along the timeline shows frames as fast as they can be decoded instead of only once you stop.
    /// While it's being dragged the nearest keyframe will do (much quicker); once it stops, the exact frame.
    /// </summary>
    async void LoadFrames()
    {
        frameBusy = true;
        var dragging = false;
        try
        {
            while (!IsDisposed)
            {
                var wanted = pendingFrame;
                using var cts = frameCts = new CancellationTokenSource();
                var img = await GetFrame(wanted, exact: !dragging, cts.Token);
                if (frameCts == cts) frameCts = null;
                if (IsDisposed) { img?.Dispose(); return; }
                if (cts.IsCancellationRequested || img == null) img?.Dispose();
                else
                {
                    ShowImage(img);
                    // Show it now: while the mouse is dragging, Windows would put the repaint off
                    preview.Update();
                }
                if (playCts != null) break;
                if (pendingFrame != wanted) dragging = true;
                else if (dragging) dragging = false; // stopped: now the exact frame
                else break;
            }
        }
        finally { frameBusy = false; }
    }

    static bool AnyChipOn(Control row) => row.Controls.OfType<Pill>().Any(c => c.On);

    /// <summary>Puts a new frame on the preview and lets go of the one it replaces</summary>
    void ShowImage(Image img)
    {
        var old = preview.Image;
        preview.Image = img;
        old?.Dispose();
    }

    /// <summary>Windows' decoder with the video kept open (opened in the background once the video is read); null until then or if it can't</summary>
    Task<FrameReader?>? frameReader;

    async Task<Image?> GetFrame(double t, bool exact, CancellationToken ct)
    {
        var height = DecodeSize().Height;
        if (frameReader is { IsCompletedSuccessfully: true, Result: { } reader } && probe != null)
        {
            // Never larger than the source, and in its shape
            height = Math.Max(2, Math.Min(height, probe.Height)) & ~1;
            var size = new Size(Math.Max(2, (int)Math.Round(height * probe.Width / (double)probe.Height)) & ~1, height);
            try
            {
                var bmp = await Task.Run(() => reader.Get(t, size, exact, ct));
                if (bmp != null || ct.IsCancellationRequested) return bmp;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Windows' decoder failed at {t:0.00} s, using ffmpeg for the preview: {ex.Message}");
                frameReader = null;
                _ = Task.Run(reader.Dispose);
            }
        }
        return await Ff.Frame(file, t, height, ct, keyOnly: !exact);
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
        AppLog.Info($"Playing from {from:0.00} s at {PlaybackFps:0.##} fps{(showResult ? " (preview)" : "")}");
        try
        {
            await Player.Play(file, from, to ?? timeline.End, Mix(), Master, DecodeSize(), probe!.Width, probe.Height, PlaybackFps, (bmp, t) =>
            {
                if (cts.IsCancellationRequested || IsDisposed) { bmp.Dispose(); return; }
                // Playing to the out point, and it was moved in while playing: stop there now
                if (to == null && t >= timeline.End) { bmp.Dispose(); StopPlayback(); return; }
                ShowImage(bmp);
                timeline.ShowTime(t);
                UpdateTimeLabel();
            }, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("Playback failed", ex); if (!IsDisposed) ShowStatus(ex.Message, Theme.Danger); }
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

    /// <param name="remember">kept as the size for new videos: a chip clicked or a typed size finished, not each keystroke</param>
    void SetTarget(double mb, bool remember)
    {
        targetMb = mb;
        if (remember) RememberTarget();
        UpdateSummary();
    }

    void RememberTarget()
    {
        if (cfg.TargetMb == targetMb) return;
        cfg.TargetMb = targetMb;
        cfg.Save();
    }

    int D(int v) => (int)Math.Round(v * timeline.DeviceDpi / 96f);

    /// <summary>File name, size, resolution and bitrate on one line; a long name is cut short, never the details</summary>
    void FitSubtitle()
    {
        var name = Path.GetFileName(file);
        var room = subtitle.Width - D(12);
        bool Fits(string s) => TextRenderer.MeasureText(s, subtitle.Font).Width <= room;
        string Cut(int n) => name[..n].TrimEnd() + "…" + details;
        var text = name + details;
        if (name.Length > 8 && !Fits(text))
        {
            // The longest cut that fits, found by halving (it runs on every step of a window resize)
            int lo = 8, hi = name.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Fits(Cut(mid))) lo = mid; else hi = mid - 1;
            }
            text = Cut(lo);
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
        // A new list each time: the lane redraws when it's handed a different one
        masterLane.Sources = [.. trackUi.Where(t => t.Lane.Active && t.Lane.Sources.Count > 0)
            .Select(t => (t.Lane.Sources[0].Wave, (Func<double, double>?)t.Lane.GainAt))];
        masterLane.Invalidate();
    }

#if DEBUG
    /// <summary>For the drag test: times every message a control handles, by control and message</summary>
    sealed class MessageSpy : NativeWindow
    {
        public static readonly Dictionary<string, (int Count, double Ms)> Totals = [];
        readonly string name;
        public MessageSpy(Control c) { name = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name; AssignHandle(c.Handle); }
        protected override void WndProc(ref Message m)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            base.WndProc(ref m);
            var key = $"{name} 0x{m.Msg:x}";
            var (n, ms) = Totals.GetValueOrDefault(key);
            Totals[key] = (n + 1, ms + watch.Elapsed.TotalMilliseconds);
        }
    }

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int msg, nint wparam, nint lparam);
#endif

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
        // Hidden tracks let go of their picked points, so Del or Ctrl+C later can't act on something out of sight
        if (!open)
        {
            foreach (var lane in KeyLanes) { lane.Selection.Clear(); lane.Invalidate(); }
            // (not the result audio's: that one stays on screen)
            foreach (var (_, lane) in trackUi) { lane.Selection.Clear(); lane.Invalidate(); }
        }
        keyLane.Visible = open && !separate;
        posLane.Visible = scaleLane.Visible = open && separate;
        // Showing a track can move it in the list, so put the first four back in order every time
        var fixedOrder = new Control[] { keyLane, posLane, scaleLane, masterLane };
        for (var i = 0; i < fixedOrder.Length; i++) scroller.Content.Controls.SetChildIndex(fixedOrder[i], i);
        // A single audio track is the same sound as the master lane, so only show tracks when there are several
        foreach (var (_, lane) in trackUi) lane.Visible = open && trackUi.Count > 1;
        masterLane.Visible = trackUi.Count > 0;
        SizeTracks();
        ShowCrop();
    }

    /// <summary>How short and how tall the open track box can be: from a sliver down to every track showing</summary>
    (int Min, int Max) TrackBoxRange()
    {
        var all = scroller.Content.GetPreferredSize(Size.Empty).Height;
        return (Math.Min(all, D(60)), all);
    }

    /// <summary>
    /// Closed, the box is exactly its one lane. Open, it's as tall as the grip was dragged (short to begin with)
    /// and scrolls for the rest.
    /// </summary>
    void SizeTracks()
    {
        var (lo, all) = TrackBoxRange();
        var open = tracksOpen && probe != null;
        // Short to begin with: the first two tracks and a sliver of the next, so it's clear there's more
        if (tracksHeight == 0) tracksHeight = D(132);
        scroller.Visible = all > 0;
        scroller.Height = open ? Math.Clamp(tracksHeight, lo, all) : all;
        scroller.Scrolls = open && scroller.Height < all;
        grip.Visible = open && all > D(60);
    }

    /// <summary>The grip under the track box was dragged: make the box this tall (within what it holds)</summary>
    void DragTracks(int height)
    {
        var (lo, hi) = TrackBoxRange();
        height = Math.Clamp(height, lo, hi);
        if (height == scroller.Height) return;
        tracksHeight = height;
        // One layout, one fit, one paint per step: a fast drag otherwise piles up resizes and leaves the window torn
        Batch(SizeTracks);
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
        keyTracks[lane] = tracks;
        lane.AddRequested += t =>
        {
            if (!Cropping) return;
            var added = false;
            foreach (var k in tracks()) added |= k.Add(t);
            if (added) { timeline.Seek(t); UpdateSummary(); }
        };
        lane.RemoveRequested += gone =>
        {
            foreach (var k in tracks())
                foreach (var i in gone.OrderDescending()) if (i < k.Count) k.RemoveAt(i);
            ShowCrop();
            UpdateSummary();
        };
        // The held point and the rest of the selection move together, short of any point not selected
        lane.ShiftRequested += (group, delta) =>
        {
            var all = tracks();
            var times = Enumerable.Range(0, all[0].Count).Select(all[0].TimeAt).ToList();
            delta = Interp.ClampShift(times, group, delta, timeline.Duration);
            if (delta == 0) return;
            foreach (var k in all) k.Shift(group, delta);
            ShowCrop();
        };
        lane.Picked += i => { if (i < tracks()[0].Count) timeline.Seek(tracks()[0].TimeAt(i)); };
        HookTrack(lane);
    }

    /// <summary>What every keyframable track shares: its right-click menu, the wheel shortcuts, and knowing the mouse is over it</summary>
    void HookTrack(KeyTrackLane track)
    {
        track.MouseEnter += (_, _) => hoverTrack = track;
        track.MouseLeave += (_, _) => { if (hoverTrack == track) hoverTrack = null; };
        // One track's selected key points at a time
        track.KeySelected += () => { foreach (var other in AllTracks.Where(t => t != track)) other.Select(-1); };
        track.HeightRequested += by => SetTrackHeight(trackH + by * 14);
        track.ZoomRequested += ZoomBy;
        track.MenuRequested += at => ShowTrackMenu(track, at);
    }

    void ShowTrackMenu(KeyTrackLane track, Point at)
    {
        if (probe == null || encodeCts != null) return;
        trackMenu.Items.Clear();
        // A selected key point: how the value moves next to it
        var (selected, current) = track switch
        {
            VolumeLane { SelectedKey: >= 0 and var i } a when i < a.Keys.Count => (i, a.Keys[i].E),
            KeyLane { SelectedKey: >= 0 and var i } b when i < b.Keys.Count => (i, i < b.Eases.Count ? b.Eases[i] : Ease.Linear),
            _ => (-1, Ease.Linear),
        };
        if (selected >= 0)
        {
            foreach (var (ease, name) in Interp.Kinds)
                trackMenu.Add("       " + name, ease == current ? "current" : "", () => SetEase(track, selected, ease)).Tag = ease;
            trackMenu.Items.Add(new ToolStripSeparator());
        }
        trackMenu.Add("Clear keyframes", "", () => ClearKeys(track));
        if (track is VolumeLane audio) trackMenu.Add("Reset audio level", "", audio.ResetLevel);
        if (track is KeyLane) trackMenu.Add(separate ? "Combine position and scale" : "Separate position and scale", "", () => SetSeparate(!separate));
        trackMenu.Show(track, at);
    }

    /// <summary>Sets how the value moves next to a point; when it's one of several selected, next to all of them</summary>
    void SetEase(KeyTrackLane track, int index, Ease ease)
    {
        if (track is VolumeLane v)
        {
            // Committed (wired to UpdateMasterPeaks and the playback restart) fires once for the lot
            v.SetEase(v.Selection.Contains(index) ? v.Selection.Sorted : [index], ease);
        }
        else if (track is KeyLane k && keyTracks.TryGetValue(k, out var tracks))
        {
            foreach (var i in k.Selection.Contains(index) ? k.Selection.Sorted : [index])
                foreach (var t in tracks()) if (i < t.Count) t.SetEase(i, ease);
            ShowCrop();
        }
    }

    // ---------- copy and paste key points ----------

    /// <summary>Copied key points, timed from the first: audio levels, or position and/or scale</summary>
    sealed record KeyClip(bool Audio, List<(double Dt, double V, (double X, double Y)? P, double? S, Ease E)> Points);
    KeyClip? keyClip;
    KeyTrackLane? clipFrom;

    /// <summary>Ctrl+C: the selected key points, on whichever track they are. False if none are selected.</summary>
    bool CopyKeys()
    {
        foreach (var track in ShownTracks)
        {
            if (track is VolumeLane { Selection.Count: > 0 } a)
            {
                var pts = a.Selection.Sorted.Where(i => i < a.Keys.Count).Select(i => a.Keys[i]).ToList();
                if (pts.Count == 0) continue;
                keyClip = new(true, [.. pts.Select(k => (k.T - pts[0].T, k.V, ((double, double)?)null, (double?)null, k.E))]);
            }
            else if (track is KeyLane { Selection.Count: > 0 } b)
            {
                // The shared track's points are both position and scale; a separate track's are just its own
                var withPos = b != scaleLane;
                var withScale = b != posLane;
                var idx = b.Selection.Sorted.Where(i => (!withPos || i < pos.Count) && (!withScale || i < scale.Count)).ToList();
                if (idx.Count == 0) continue;
                var first = (withPos ? pos : (IKeyTrack)scale).TimeAt(idx[0]);
                keyClip = new(false, [.. idx.Select(i => (
                    (withPos ? pos.Keys[i].T : scale.Keys[i].T) - first,
                    0.0,
                    withPos ? pos.Keys[i].Value : ((double, double)?)null,
                    withScale ? scale.Keys[i].Value : (double?)null,
                    withPos ? pos.Keys[i].E : scale.Keys[i].E))]);
            }
            else continue;
            clipFrom = track;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ctrl+V: the copied points, the first at the playhead and the rest spaced as they were. They go on the track
    /// under the mouse if it takes them, otherwise back on the one they came from, and are left selected.
    /// </summary>
    bool PasteKeys()
    {
        if (keyClip is not { } clip || probe == null) return false;
        bool Takes(KeyTrackLane? t) => t is { Visible: true } && (clip.Audio
            ? t is VolumeLane
            : t == keyLane || (t == posLane && clip.Points.Any(p => p.P != null)) || (t == scaleLane && clip.Points.Any(p => p.S != null)));
        var target = Takes(hoverTrack) ? hoverTrack : Takes(clipFrom) ? clipFrom : !clip.Audio && Takes(keyLane) ? keyLane : null;
        if (target == null) return false;
        var at = timeline.Current;
        var points = clip.Points.Select(p => (T: at + p.Dt, p)).Where(x => x.T <= timeline.Duration + 1e-6).ToList();
        if (target is VolumeLane lane)
        {
            lane.Paste(points.Select(x => (x.T, x.p.V, x.p.E)));
            return true;
        }
        if (!Cropping) return false;
        foreach (var (t, p) in points)
        {
            // On the shared track both values get a point (one copied without the other keeps what it has there),
            // so position and scale stay at the same times
            var (x, s) = (p.P ?? pos.At(t), p.S ?? scale.At(t));
            if (target != scaleLane && (p.P != null || target == keyLane)) pos.Put(t, x, p.E);
            if (target != posLane && (p.S != null || target == keyLane)) scale.Put(t, Math.Min(s, MaxCropW), p.E);
        }
        ShowCrop();
        ((KeyLane)target).SelectTimes(points.Select(x => x.T));
        UpdateSummary();
        return true;
    }

    /// <summary>Back to no key points. For audio that's a level volume line; for position and scale, the whole frame.</summary>
    void ClearKeys(KeyTrackLane track)
    {
        if (track is VolumeLane v) { v.ClearKeys(); return; }
        if (track != scaleLane) pos.Reset((0.5, 0.5));
        if (track != posLane) scale.Reset(MaxCropW);
        ShowCrop();
        UpdateSummary();
    }

    /// <summary>Position and scale: the combined track and the two separate ones</summary>
    KeyLane[] KeyLanes => [keyLane, posLane, scaleLane];
    /// <summary>Every audio track's lane, then the result audio's</summary>
    IEnumerable<VolumeLane> AudioLanes => trackUi.Select(t => t.Lane).Append(masterLane);
    IEnumerable<KeyTrackLane> AllTracks => new KeyTrackLane[] { keyLane, posLane, scaleLane, masterLane }.Concat(trackUi.Select(t => t.Lane));
    /// <summary>The tracks on screen: Del and Ctrl+C never act on a hidden one's leftover selection</summary>
    IEnumerable<KeyTrackLane> ShownTracks => AllTracks.Where(t => t.Visible);

    /// <summary>Del: removes the selected key point, whichever track it's on. False if none is selected.</summary>
    bool DeleteSelectedKey()
    {
        if (ShownTracks.FirstOrDefault(t => t.SelectedKey >= 0) is not { } track) return false;
        track.DeleteSelected();
        return true;
    }

    void SetTrackHeight(int height)
    {
        height = Math.Clamp(height, 42, 240);
        if (height == trackH) return;
        trackH = height;
        Batch(() =>
        {
            foreach (var lane in AudioLanes) lane.RowHeight = trackH;
            foreach (var lane in KeyLanes) lane.RowHeight = trackH;
            // The timeline keeps its own height: only the tracks under it grow
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

    /// <summary>
    /// Frames a second to play at. Preview shows the result, so it plays at the frame rate picked for it (up to
    /// 60); otherwise the source, capped at 30 to keep playback light.
    /// </summary>
    double PlaybackFps
    {
        get
        {
            var src = probe is { Fps: > 0 } ? probe.Fps : 30;
            return showResult ? Math.Min(frameRate ?? src, 60) : Math.Min(src, 30);
        }
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
            // "NaN" and "Infinity" parse as numbers, but aren't times
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0) return null;
            total = total * 60 + n;
        }
        return total;
    }

    /// <summary>
    /// Measures every track's waveform at once, in the background, the part on screen first. A timer redraws the
    /// lanes as chunks come in and marks the silent tracks once a track is done.
    /// </summary>
    void LoadPeaks()
    {
#if DEBUG
        // Timing tests that mustn't share the disk with the waveforms
        if (Environment.GetEnvironmentVariable("CFD_TEST")?.Contains("nowaves") == true) return;
#endif
        var tracks = new List<(Waveform, int)>();
        foreach (var (track, lane) in trackUi)
        {
            var wave = new Waveform(timeline.Duration);
            lane.Sources = [(wave, null)];
            tracks.Add((wave, track.Index));
        }
        var path = file;
        // All tracks from one read of the file. Held back while compressing, so the encode has the disk to itself,
        // and while scrubbing: reading a huge recording alongside the preview cut the frames shown from ~29 a second to ~8.
        bool Paused() => encodeCts != null || frameBusy || Environment.TickCount64 - lastFrameRequest < 400;
        _ = Task.Run(async () =>
        {
            try { await Waveform.LoadAll(path, tracks, () => (viewStart, viewEnd), Paused, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Waveforms", ex); }
        });
        UpdateMasterPeaks();
        if (trackUi.Count == 0) return;
        var seen = -1;
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var lanes = trackUi.Select(t => t.Lane).ToList();
            var version = lanes.Sum(l => l.Sources[0].Wave.Version);
            if (version == seen) return;
            seen = version;
            foreach (var lane in lanes)
            {
                var wave = lane.Sources[0].Wave;
                lane.Silent = wave.Complete && !wave.Failed && wave.Loudest < 0.01f;
                lane.Invalidate();
            }
            masterLane.Invalidate();
            if (lanes.All(l => l.Sources[0].Wave.Complete)) timer.Dispose();
        };
        timer.Start();
        Disposed += (_, _) => timer.Dispose();
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
        for (var i = 0; i < scale.Keys.Count; i++) scale.Keys[i] = (scale.Keys[i].T, Math.Min(scale.Keys[i].Value, MaxCropW), scale.Keys[i].E);
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
        // What the lanes show; only when it changes are they handed it again (this runs on every frame)
        var h = new HashCode();
        foreach (var k in pos.Keys) h.Add(k);
        foreach (var k in scale.Keys) h.Add(k);
        h.Add(MaxCropW);
        var data = h.ToHashCode();
        Fill(keyLane, pos, data, () => pos.Keys.Select(k => Zoom(scale.At(k.T))), t => Zoom(scale.At(t)));
        Fill(scaleLane, scale, data, () => scale.Keys.Select(k => Zoom(k.Value)), t => Zoom(scale.At(t)));
        // Across as the main line (right = up), down as the fainter one
        Fill(posLane, pos, data, () => pos.Keys.Select(k => k.Value.X), t => pos.At(t).X, t => 1 - pos.At(t).Y);
    }

    void Fill(KeyLane lane, IKeyTrack track, int data, Func<IEnumerable<double>> levels, Func<double, double> levelAt, Func<double, double>? level2At = null)
    {
        (lane.Duration, lane.Current) = (timeline.Duration, timeline.Current);
        if (lane.DataVersion != data || lane.Keys.Count != track.Count)
        {
            // Points added or taken away shift the numbers of the rest, so a selection no longer means the same points
            if (lane.Keys.Count != track.Count) lane.Selection.Clear();
            lane.Keys = Enumerable.Range(0, track.Count).Select(track.TimeAt).ToList();
            lane.Eases = Enumerable.Range(0, track.Count).Select(track.EaseAt).ToList();
            (lane.LevelAt, lane.Level2At) = (levelAt, level2At);
            lane.Levels = levels().ToList();
            lane.DataVersion = data;
        }
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

    /// <summary>The picture's centre and width in source pixels, with their key points and eases, for the encoder</summary>
    CropPath? CropForEncode()
    {
        if (!Reframed) return null;
        double w = probe!.Width, h = probe.Height;
        return new CropPath(
            [.. pos.Keys.Select(k => (k.T, Math.Clamp(k.Value.X, 0, 1) * w, k.E))],
            [.. pos.Keys.Select(k => (k.T, Math.Clamp(k.Value.Y, 0, 1) * h, k.E))],
            [.. scale.Keys.Select(k => (k.T, Math.Min(k.Value, MaxCropW) * w, k.E))]);
    }

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
                    // Nothing inside is drawn until it's all resized, then it's drawn once. Otherwise each panel
                    // that changes size has Windows redraw everything in it (every chip, frame and all) on its own,
                    // as it goes, and the options flash as you drag the window wider or narrower.
                    SendMessage(c.Handle, 0x000B, 0, 0); // WM_SETREDRAW off
                    redrawHeld = c;
                    extraW = Math.Max(0, extraW + dw);
                    // Height goes to the track box while it's open, so closing Advanced leaves the window as it was.
                    // What the box can't take (it's at its smallest, or already shows every track) goes to the
                    // preview, or the window would be held at a size it can't fit and flick back and forth.
                    if (tracksOpen)
                    {
                        var (lo, hi) = TrackBoxRange();
                        var want = scroller.Height + dh;
                        tracksHeight = Math.Clamp(want, lo, hi);
                        extraH = Math.Max(0, extraH + want - tracksHeight);
                    }
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
            var slack = tracksOpen ? Math.Max(0, scroller.Height - TrackBoxRange().Min) : 0;
            if (baseW > 0) MinimumSize = new Size(Width - extraW, Height - extraH - slack);
        }
        finally
        {
            fitting = false;
            if (redrawHeld is { } held)
            {
                redrawHeld = null;
                SendMessage(held.Handle, 0x000B, 1, 0);
                // The whole window, at once: not just the content, or whatever the window showed around it before
                // the step (where Windows copied the old picture as it resized) is left behind as trails
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
        }
    }

    Control? redrawHeld;
    const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

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
        // A percentage picked before zooming the crop in can now be tiny, so hold it to a watchable size here too
        int? side = scalePct is { } pct ? Math.Max(ScaledSide(pct), Math.Min(144, ScaledSide(100))) : null;
        return Planner.Make(PlanSource, duration, (long)(targetMb * Planner.MB), Mix().Count > 0, cfg.SafetyMarginPercent, cal, side, frameRate);
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
        foreach (var lane in AudioLanes)
        {
            (lane.Start, lane.End, lane.Current) = (timeline.Start, timeline.End, timeline.Current);
            if (lane.Visible) lane.Invalidate();
        }
    }

    /// <summary>
    /// Colour for the result line: green when the bitrate suits the resolution and frame rate, yellow when it's
    /// on the low side, red when it will look bad.
    /// </summary>
    Color Quality(Plan plan, int fps)
    {
        // Already as good as the source: nothing to gain
        if (plan.CappedBySource) return Theme.Green;
        const double Good = 0.05; // bits per pixel per frame that H.264 needs to look clean
        var src = PlanSource;
        double shortSrc = Math.Min(src.Width, src.Height), aspect = Math.Max(src.Width, src.Height) / shortSrc;
        var side = plan.OutputShortSide;
        var bits = plan.VideoKbps * 1000.0 / (side * side * aspect * fps);
        return bits >= Good ? Theme.Green : bits >= Good / 2 ? Theme.Warn : Theme.Danger;
    }

    /// <summary>One frame at the chosen frame rate, in seconds: what's marked on the timeline and what moved key points land on</summary>
    double FrameLength => probe == null ? 0 : 1.0 / (frameRate ?? probe.Fps);
    /// <summary>One step of the arrow keys or the wheel over the preview: a frame at the chosen rate with frame lock on, else a source frame</summary>
    double StepLength => timeline.FrameLock ? FrameLength : 1.0 / probe!.Fps;

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
        // Preview plays at the chosen frame rate, so pick up a new one straight away
        if (showResult) RestartIfPlaying();
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
            var color = Quality(plan, fps);
            // Just the basics on the line; hovering it shows the whole breakdown. The size is the limit picked
            // above, so the line leads with what that buys: the video's bitrate (its colour says how it'll look)
            summary.Text = $"{Planner.FormatRate(plan.VideoKbps)} · {plan.OutputShortSide}p{fps}";
            summary.ForeColor = color;
            var quality = plan.CappedBySource ? "Same as the source"
                : color == Theme.Green ? "Good"
                : color == Theme.Warn ? "OK, a little soft"
                : "Low, will look blocky";
            var encoder = EffectiveEncoder switch { "nvenc" => "GPU (NVIDIA)", "amf" => "GPU (AMD)", _ => "CPU (2 passes)" };
            SummaryTip(string.Join("\n",
                $"Size:  ~{Planner.FormatBytes(plan.EstimateBytes)} of {limit}",
                $"Picture:  {plan.OutputShortSide}p at {fps} fps",
                // The video's own rate, so turning the audio off shows the room it hands to the picture
                $"Video:  {Planner.FormatRate(plan.VideoKbps)}",
                $"Audio:  {(plan.AudioKbps > 0 ? Planner.FormatRate(plan.AudioKbps) : "off")}",
                $"Quality:  {quality}",
                $"Encoder:  {encoder}"));
        }
        else
        {
            summary.Text = $"Too long for {limit}. Trim to under {Planner.FormatTime(plan.MaxDuration)}.";
            summary.ForeColor = Theme.Danger;
            SummaryTip(null);
        }
        CanGo(plan.Feasible);
    }

    /// <summary>Only when it changes: setting it again closes a tip that's open (it updates while trimming)</summary>
    void SummaryTip(string? text)
    {
        if (tips.GetToolTip(summary) != (text ?? "")) tips.SetToolTip(summary, text);
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
        AppLog.Info($"Compress {(save ? "& save" : "& copy")}: {timeline.Start:0.00}-{timeline.End:0.00} s, limit {plan.TargetBytes / Planner.MB:0.00} MB, video {plan.VideoKbps} kbps, audio {plan.AudioKbps} kbps, " +
            $"short side {plan.ScaleShortSide?.ToString() ?? "source"}, fps {plan.Fps?.ToString() ?? "source"}, crop {(CropForEncode() != null ? "yes" : "no")}, tracks {Mix().Count}, encoder {cfg.Encoder}");
        SetBusy(true);
        var progress = new Progress<(string Stage, double Progress)>(p =>
        {
            ShowStatus($"{p.Stage} · {p.Progress * 100:0}%", Theme.Text);
            bar.Value = p.Progress;
        });
        try
        {
            var request = new EncodeRequest
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
            };
            var (temp, size) = await Ff.Encode(request, progress, encodeCts.Token);
            // The encoder couldn't get small enough at the chosen size, so the picture was made smaller
            var shrunk = request.ScaleShortSide != plan.ScaleShortSide ? $", made {request.ScaleShortSide}p to fit" : "";

            if (savePath != null)
            {
                File.Move(temp, savePath, true);
                outPath = savePath;
                if (saveTo == null)
                {
                    cfg.SaveFolder = Path.GetDirectoryName(savePath) ?? "";
                    cfg.Save();
                }
                Done($"✓ Saved {Path.GetFileName(savePath)} ({Planner.FormatBytes(size)}{shrunk})");
            }
            else
            {
                outPath = Ff.MoveToOut(temp, name);
                // Same as Explorer's Copy, so Ctrl+V in Discord attaches the file. Another app can be holding the
                // clipboard; the file is made either way, so show it in Explorer to drag in instead of failing
                try { Clipboard.SetFileDropList(new StringCollection { outPath }); }
                catch (Exception ex)
                {
                    AppLog.Error("Couldn't copy to the clipboard; showing the file instead", ex);
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outPath}\""); } catch { }
                }
                Done($"✓ Copied ({Planner.FormatBytes(size)}{shrunk}). Press Ctrl+V in Discord.");
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Compress cancelled");
            ShowStatus("Cancelled", Theme.Muted);
            SetBusy(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress failed", ex);
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
        customMb.Enabled = fpsBox.Enabled = resBox.Enabled = timeBox.Enabled = !busy;
        foreach (var lane in AudioLanes) lane.Enabled = !busy;
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
        AppLog.Info($"Compress done: {outPath}");
        SaveProject();
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
        catch (Exception ex) { AppLog.Error("ffmpeg download failed", ex); encodeCts = null; bar.Visible = false; ShowStatus("Download failed: " + ex.Message, Theme.Danger); }
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
        if (s.OpenProject is { } project) { Program.OpenVideo(project.File, this, project); return; }
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
    bool Typing => customMb.Focused || fpsBox.Focused || resBox.Focused || timeBox.Focused;

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
        if (keyData == (Keys.Control | Keys.C)) return CopyKeys();
        if (keyData == (Keys.Control | Keys.V)) return PasteKeys();
        if (mods != Keys.None && !(mods == Keys.Shift && key is Keys.Left or Keys.Right)) return false;
        var step = mods == Keys.Shift ? 5 : StepLength;
        // Close enough to count as being on a key point (with frame lock, the playhead can only get within half a frame)
        var near = Math.Max(0.01, timeline.FrameLock ? FrameLength / 2 : 0);
        switch (key)
        {
            case Keys.Space: TogglePlay(); break;
            // Over a track with key points, the arrows go from one to the next
            case Keys.Left or Keys.Right when mods == Keys.None && hoverTrack is { Visible: true } && hoverTrack.KeyTimes.ToList() is { Count: > 0 } times:
                var back = key == Keys.Left;
                var to = back ? times.Where(t => t < timeline.Current - near).Select(t => (double?)t).LastOrDefault() : times.Where(t => t > timeline.Current + near).Select(t => (double?)t).FirstOrDefault();
                if (to is { } there) timeline.Seek(there);
                break;
            case Keys.Left: timeline.Seek(timeline.Current - step); break;
            case Keys.Right: timeline.Seek(timeline.Current + step); break;
            case Keys.Q or Keys.I: timeline.SetStart(timeline.Current); break;
            case Keys.E or Keys.O: timeline.SetEnd(timeline.Current); break;
            case Keys.Delete: if (!DeleteSelectedKey()) return false; break;
            case Keys.Home: timeline.Seek(timeline.Start); break;
            case Keys.End: timeline.Seek(timeline.End); break;
            default: return false;
        }
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (probe != null && encodeCts == null && baseline != null && StateKey() != baseline) SaveProject();
        playCts?.Cancel();
        lifetime.Cancel();
        encodeCts?.Cancel();
        frameCts?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        thumbTimer.Dispose();
        tips.Dispose();
        thumbCts?.Dispose();
        timeline.Thumbs = [];
        foreach (var img in thumbCache.Values) img.Dispose();
        thumbCache.Clear();
        if (frameReader is { } open) _ = open.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
    }
}
