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
    bool muted;
    readonly List<(AudioTrack Track, CheckBox Box, TrackBar Volume)> trackUi = [];

    CancellationTokenSource? encodeCts, frameCts, playCts;
    readonly CancellationTokenSource lifetime = new();
    readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 50 };
    double pendingFrame;
    string? outPath;

    readonly Label subtitle, timeLabel, summary, status;
    readonly PictureBox preview;
    readonly Timeline timeline;
    readonly LinkLabel resetLink;
    readonly FlowLayoutPanel sizeChips, resChips, fpsChips, encChips, audioChips, audioPanel, buttons;
    readonly Label audioLabel;
    readonly TextBox customMb;
    readonly Bar bar;
    readonly Pill closeBtn, goBtn, moreBtn, downloadBtn, settingsBtn, playBtn;
    readonly DarkMenu goMenu;

    public MainForm(string file)
    {
        this.file = file;
        fileSize = new FileInfo(file).Length;
        targetMb = cfg.TargetMb;

        SuspendLayout();
        Text = $"{Path.GetFileName(file)} · Compress for Discord";

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(18, 14), Margin = Padding.Empty };

        // Title row: heading on the left; update link, Settings and Help on the right
        var titleRow = new Panel { Size = new Size(ContentWidth, 36), Margin = Padding.Empty };
        var title = Heading("Compress video");
        title.Dock = DockStyle.Left;
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right, Padding = new Padding(0, 9, 0, 0) };
        links.Controls.Add(UpdateLink(this));
        links.Controls.Add(Link("Settings", ShowSettings));
        var help = Link("Help", ShowHelp);
        help.Margin = Padding.Empty;
        links.Controls.Add(help);
        titleRow.Controls.Add(title);
        titleRow.Controls.Add(links);

        subtitle = new Label { Text = $"{Path.GetFileName(file)} · {Planner.FormatBytes(fileSize)}", AutoSize = true, ForeColor = Theme.Muted, AutoEllipsis = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 0, 0, 10) };

        preview = new PictureBox { Size = new Size(ContentWidth, 300), BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 10) };

        timeline = new Timeline { Size = new Size(ContentWidth, 54), Enabled = false, Margin = Padding.Empty };
        timeline.RangeChanged += () => { UpdateTimeLabel(); UpdateSummary(); };
        timeline.Seeked += t => { StopPlayback(); RequestFrame(t); };

        var timeRow = new Panel { Size = new Size(ContentWidth, 30), Margin = new Padding(0, 6, 0, 10) };
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
        var options = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg2, Padding = new Padding(12, 10, 12, 6), Margin = new Padding(0, 0, 0, 10), MinimumSize = new Size(ContentWidth, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        FlowLayoutPanel ChipRow() => new() { AutoSize = true, WrapContents = true, MaximumSize = new Size(ContentWidth - 92 - 24, 0), Margin = Padding.Empty, BackColor = Theme.Bg2 };
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
        // Dropping the audio frees its bitrate for the video, which can be what makes a clip fit
        audioChips = ChipRow();
        audioChips.Controls.Add(Chip("On", false, audioChips, v => SetMuted((bool)v!)));
        audioChips.Controls.Add(Chip("Off", true, audioChips, v => SetMuted((bool)v!)));
        SelectChip(audioChips, false);

        options.Controls.Add(OptLabel("Size"), 0, 0); options.Controls.Add(sizeChips, 1, 0);
        options.Controls.Add(OptLabel("Resolution"), 0, 1); options.Controls.Add(resChips, 1, 1);
        options.Controls.Add(OptLabel("Frame rate"), 0, 2); options.Controls.Add(fpsChips, 1, 2);
        options.Controls.Add(OptLabel("Encoder"), 0, 3); options.Controls.Add(encChips, 1, 3);
        options.Controls.Add(audioLabel, 0, 4); options.Controls.Add(audioChips, 1, 4);
        options.Controls.Add(audioPanel, 1, 5);
        audioLabel.Visible = audioChips.Visible = audioPanel.Visible = false;

        summary = new Label { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 8) };
        bar = new Bar { Size = new Size(ContentWidth, 8), Visible = false, Margin = new Padding(0, 0, 0, 8) };

        // Bottom row: status on the left, buttons on the right
        // Sized by its content (not a fixed height) so the buttons never get clipped at other display scalings
        var bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, MinimumSize = new Size(ContentWidth, 0), Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
        buttons = new FlowLayoutPanel { Anchor = AnchorStyles.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        // Split button: "Compress & copy" plus a ▾ with "Compress & save…"
        moreBtn = new Pill("▾", PillKind.Primary) { Enabled = false, MinWidth = 34, FlatLeft = true, Margin = Padding.Empty };
        goBtn = new Pill("Compress & copy", PillKind.Primary) { Enabled = false, FlatRight = true, Margin = new Padding(8, 0, 1, 0) };
        goMenu = new DarkMenu();
        goMenu.Add("Compress && copy", "Enter", () => Go(save: false));
        goMenu.Add("Compress && save…", "Ctrl+S", () => Go(save: true));
        moreBtn.Click += (_, _) => goMenu.Show(moreBtn, new Point(moreBtn.Width - goMenu.PreferredSize.Width, moreBtn.Height + 4));
        closeBtn = new Pill("Close", PillKind.Secondary) { Margin = new Padding(8, 0, 0, 0) };
        downloadBtn = new Pill("Download ffmpeg", PillKind.Primary) { Visible = false, Margin = new Padding(8, 0, 0, 0) };
        settingsBtn = new Pill("Settings…", PillKind.Secondary) { Visible = false, Margin = new Padding(8, 0, 0, 0) };
        goBtn.Click += (_, _) => Go(save: false);
        closeBtn.Click += (_, _) => CloseOrCancel();
        downloadBtn.Click += (_, _) => DownloadFfmpeg();
        settingsBtn.Click += (_, _) => ShowSettings();
        buttons.Controls.AddRange([moreBtn, goBtn, downloadBtn, closeBtn, settingsBtn]);
        bottom.Controls.Add(status, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        root.Controls.AddRange([titleRow, subtitle, preview, timeline, timeRow, options, summary, bar, bottom]);
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
        subtitle.Text = $"{Path.GetFileName(file)} · {Planner.FormatBytes(fileSize)} · {shortSide}p{fps} · {Planner.FormatTime(probe.Duration, false)}";

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

        audioLabel.Visible = audioChips.Visible = probe.AudioTracks.Count > 0;
        // Track pickers only when there's a choice to make; a single track is always kept
        if (probe.AudioTracks.Count > 1)
        {
            var def = probe.AudioTracks.FirstOrDefault(t => t.IsDefault) ?? probe.AudioTracks[0];
            foreach (var t in probe.AudioTracks)
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2), BackColor = Theme.Bg2 };
                // Added after the form scaled, so convert to device pixels here
                int D(int v) => LogicalToDeviceUnits(v);
                var box = new CheckBox { Text = t.Label, Checked = t == def, AutoSize = false, Width = D(200), Height = D(28), ForeColor = Theme.Text, AutoEllipsis = true };
                var vol = new TrackBar { Minimum = 0, Maximum = 200, Value = 100, TickStyle = TickStyle.None, AutoSize = false, Width = D(140), Height = D(28), BackColor = Theme.Bg2, SmallChange = 5, LargeChange = 25 };
                var pct = new Label { Text = "100%", AutoSize = false, Width = D(48), Height = D(28), TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted };
                box.CheckedChanged += (_, _) => { vol.Enabled = box.Checked; UpdateSummary(); RestartIfPlaying(); };
                // Hear volume changes once the slider is let go
                vol.MouseUp += (_, _) => RestartIfPlaying();
                vol.ValueChanged += (_, _) => pct.Text = $"{vol.Value}%";
                vol.Enabled = box.Checked;
                row.Controls.AddRange([box, vol, pct]);
                audioPanel.Controls.Add(row);
                trackUi.Add((t, box, vol));
            }
            audioPanel.Visible = true;
        }

        UpdateTimeLabel();
        UpdateSummary();
        ShowStatus("", Theme.Muted);
        RequestFrame(0);
        _ = ResolveAutoEncoder();
        _ = LoadThumbnails();
    }

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
        _ = PlayFrom(t);
    }

    async Task PlayFrom(double from)
    {
        var cts = playCts = new CancellationTokenSource();
        frameTimer.Stop();
        frameCts?.Cancel();
        playBtn.Text = "❚❚  Pause";
        try
        {
            await Player.Play(file, from, timeline.End, Mix(), preview.ClientSize, probe!.Width, probe.Height, probe.Fps, (bmp, t) =>
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

    void SetMuted(bool off)
    {
        muted = off;
        audioPanel.Visible = !off && trackUi.Count > 0;
        UpdateSummary();
        RestartIfPlaying();
    }

    List<TrackMix> Mix()
    {
        if (probe == null || probe.AudioTracks.Count == 0 || muted) return [];
        if (trackUi.Count == 0) return [new TrackMix(probe.AudioTracks[0].Index, 1)];
        return trackUi.Where(t => t.Box.Checked).Select(t => new TrackMix(t.Track.Index, t.Volume.Value / 100.0)).ToList();
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
    }

    void UpdateSummary()
    {
        if (outPath != null || probe == null) return;
        var plan = CurrentPlan();
        if (plan == null) return;
        var limit = Planner.FormatBytes(targetMb * Planner.MB);
        var mix = Mix();
        if (plan.Feasible)
        {
            var audio = mix.Count == 0 ? " · no audio" : mix.Count > 1 ? $" · {mix.Count} audio tracks mixed" : "";
            var low = plan.VideoKbps < 500 ? " · low quality, try a shorter clip" : "";
            summary.Text = $"→ ~{Planner.FormatBytes(plan.EstimateBytes)} of {limit} · {plan.OutputShortSide}p{plan.Fps ?? (int)Math.Round(probe.Fps)} · {(plan.VideoKbps + plan.AudioKbps) / 1000.0:0.0} Mbps{audio}{low}";
            summary.ForeColor = plan.VideoKbps < 500 ? Theme.Warn : Theme.Text;
        }
        else
        {
            summary.Text = $"Too long for {limit}. Trim to under {Planner.FormatTime(plan.MaxDuration)}.";
            summary.ForeColor = Theme.Danger;
        }
        goBtn.Enabled = moreBtn.Enabled = plan.Feasible && encodeCts == null;
    }

    void ShowStatus(string text, Color color)
    {
        status.Text = text;
        status.ForeColor = color;
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

    async void Go(bool save)
    {
        if (probe == null || encodeCts != null || !goBtn.Enabled || !goBtn.Visible) return;
        var plan = CurrentPlan();
        if (plan is not { Feasible: true }) return;

        StopPlayback();
        string? savePath = null;
        if (save && (savePath = AskSavePath()) == null) return;
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
                cfg.SaveFolder = Path.GetDirectoryName(savePath) ?? "";
                cfg.Save();
                Done($"✓ Saved {Path.GetFileName(savePath)} ({Planner.FormatBytes(size)})");
            }
            else
            {
                outPath = Ff.MoveToOut(temp, OutputName);
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
            if (outPath == null) UpdateSummary();
        }
    }

    void SetBusy(bool busy)
    {
        bar.Visible = busy;
        bar.Value = 0;
        timeline.Enabled = !busy;
        playBtn.Enabled = !busy;
        customMb.Enabled = !busy;
        foreach (var (_, box, vol) in trackUi) { box.Enabled = !busy; vol.Enabled = !busy && box.Checked; }
        foreach (var p in new[] { sizeChips, resChips, fpsChips, encChips, audioChips }.SelectMany(g => g.Controls.OfType<Pill>())) p.Enabled = !busy;
        goBtn.Enabled = moreBtn.Enabled = !busy;
        closeBtn.Text = busy ? "Cancel" : "Close";
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
        downloadBtn.Enabled = settingsBtn.Enabled = false;
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
        finally { downloadBtn.Enabled = settingsBtn.Enabled = true; }
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
