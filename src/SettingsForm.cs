namespace CompressForDiscord;

/// <summary>Recent projects, ffmpeg location, desktop shortcut, done sound and updates. Everything applies as soon as it's changed.</summary>
sealed class SettingsForm : DarkForm
{
    const int W = 580;
    readonly AppConfig cfg = AppConfig.Current;
    readonly Label ffmpegStatus, updateStatus;
    readonly TextBox pathBox;
    readonly Pill browseBtn, openBtn, installBtn;
    readonly LinkLabel downloadLink, checkLink;
    readonly Bar bar;
    CancellationTokenSource? download;
    Release? release;
    /// <summary>The path box is being filled in by code, not typed in</summary>
    bool settingPath;

    /// <summary>ffmpeg was found, changed or downloaded</summary>
    public event Action? FfmpegChanged;
    /// <summary>A recent project was picked: the window that opened Settings opens it once Settings has closed</summary>
    public Project? OpenProject { get; private set; }
    readonly RecentList recentList;
    readonly DarkMenu recentMenu = new();
    readonly LinkLabel clearLink;

    public SettingsForm()
    {
        Text = "Settings · Compress for Discord";
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(20, 6), Margin = Padding.Empty };

        // Recent projects: every compress is saved, so a closed window can be picked up again
        root.Controls.Add(Section("Recent projects"));
        recentList = new RecentList { Width = W, Margin = new Padding(0, 0, 0, 8) };
        recentList.RowClicked += OpenRecent;
        recentList.RowMenu += ShowRecentMenu;
        root.Controls.Add(recentList);
        var keepLabel = Muted("Keep the last");
        keepLabel.Margin = new Padding(0, 5, 8, 0);
        var keep = new List<Pill>();
        foreach (var n in new[] { 10, 25, 50, 0 })
        {
            var chip = new Pill(n == 0 ? "Off" : $"{n}") { Margin = new Padding(0, 0, 6, 0), On = cfg.RecentProjects == n };
            chip.Click += (_, _) =>
            {
                cfg.RecentProjects = n;
                cfg.Save();
                foreach (var c in keep) c.On = c == chip;
                Projects.Trim();
                FillRecent();
            };
            keep.Add(chip);
        }
        clearLink = Link("Clear", () => { Projects.Clear(); FillRecent(); });
        clearLink.Margin = new Padding(8, 5, 0, 0);
        root.Controls.Add(Row([keepLabel, .. keep, clearLink]));
        FillRecent();

        // ffmpeg: a folder you can type or paste, Browse, and Open folder. Empty = found automatically.
        root.Controls.Add(Section("ffmpeg"));
        pathBox = new TextBox { BackColor = Theme.Bg3, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Text = cfg.FfmpegPath };
        var pathField = new RoundBox { LogicalWidth = 0, LogicalHeight = 38, Radius = 8, Fill = Theme.Bg3, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.IBeam };
        pathField.Controls.Add(pathBox);
        pathField.Click += (_, _) => pathBox.Focus();
        pathBox.GotFocus += (_, _) => pathField.Invalidate();
        pathBox.LostFocus += (_, _) => pathField.Invalidate();
        pathBox.TextChanged += (_, _) =>
        {
            if (settingPath) return;
            // Pasted paths often come quoted, or point at ffmpeg.exe itself; both are fine
            cfg.FfmpegPath = pathBox.Text.Trim().Trim('"');
            cfg.Save();
            RefreshFfmpeg();
        };
        browseBtn = new Pill("Browse…", PillKind.Secondary) { MinWidth = 80, Margin = new Padding(0, 0, 8, 0) };
        openBtn = new Pill("Open folder", PillKind.Secondary) { MinWidth = 80, Margin = Padding.Empty };
        browseBtn.Click += (_, _) => ChooseFfmpeg();
        openBtn.Click += (_, _) => { if (Ff.Ffmpeg != "") Updater.Open(Path.GetDirectoryName(Ff.Ffmpeg)!); };
        var pathRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, MinimumSize = new Size(W, 0), MaximumSize = new Size(W, 0), Margin = new Padding(0, 0, 0, 6) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(pathField, 0, 0);
        pathRow.Controls.Add(browseBtn, 1, 0);
        pathRow.Controls.Add(openBtn, 2, 0);
        root.Controls.Add(pathRow);

        ffmpegStatus = Muted("");
        ffmpegStatus.Margin = new Padding(0, 0, 10, 0);
        downloadLink = Link("Download ffmpeg", DownloadFfmpeg);
        root.Controls.Add(Row(ffmpegStatus, downloadLink));
        bar = new Bar { Size = new Size(W, 8), Visible = false, Margin = new Padding(0, 8, 0, 0) };
        root.Controls.Add(bar);

        // Encoder
        root.Controls.Add(Section("Encoder"));
        var gpu = new Pill("GPU") { Margin = new Padding(0, 0, 6, 0) };
        var cpu = new Pill("CPU") { Margin = Padding.Empty };
        void PickEncoder(string value)
        {
            cfg.Encoder = value;
            cfg.Save();
            (gpu.On, cpu.On) = (value != "x264", value == "x264");
        }
        gpu.Click += (_, _) => PickEncoder("auto");
        cpu.Click += (_, _) => PickEncoder("x264");
        (gpu.On, cpu.On) = (cfg.Encoder != "x264", cfg.Encoder == "x264");
        root.Controls.Add(Row(gpu, cpu));
        // "GPU" means NVIDIA or AMD hardware encoding; say so when this PC has neither
        Shown += async (_, _) =>
        {
            if (Ff.Ffmpeg == "") return;
            string found;
            try { found = await Ff.PickEncoder("auto"); } catch { return; }
            if (IsDisposed || found != "x264") return;
            gpu.Enabled = false;
            (gpu.On, cpu.On) = (false, true);
        };

        // General
        root.Controls.Add(Section("General"));
        var desktop = Check("Desktop shortcut", Shortcut.OnDesktop);
        desktop.CheckedChanged += (_, _) =>
        {
            try { Shortcut.SetOnDesktop(desktop.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        root.Controls.Add(desktop);
        var sound = Check("Play a sound when done", cfg.PlaySound);
        sound.CheckedChanged += (_, _) => { cfg.PlaySound = sound.Checked; cfg.Save(); };
        root.Controls.Add(sound);

        // Updates
        root.Controls.Add(Section("Updates"));
        var auto = Check("Check for updates automatically", cfg.CheckForUpdates);
        auto.CheckedChanged += (_, _) => { cfg.CheckForUpdates = auto.Checked; cfg.Save(); };
        root.Controls.Add(auto);
        updateStatus = Muted($"Version {Updater.Current}");
        updateStatus.Margin = new Padding(0, 0, 10, 0);
        checkLink = Link("Check now", CheckNow);
        root.Controls.Add(Row(updateStatus, checkLink));
        installBtn = new Pill("Install update", PillKind.Primary) { Visible = false, Margin = new Padding(0, 10, 0, 0) };
        installBtn.Click += (_, _) => InstallUpdate();
        root.Controls.Add(installBtn);

        // No Done button: every change is saved as it's made. Esc or the X closes.
        root.Controls.Add(new Panel { Size = new Size(W, 12), Margin = Padding.Empty });
        Controls.Add(root);
        RefreshFfmpeg();
    }

    // Don't start with the cursor in the path box: an empty box shows where ffmpeg was found, but only while it isn't focused
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = null;
#if DEBUG
        // Test screenshots: recenthover=N shows a row hovered, recentmenu=N opens its right-click menu
        foreach (var part in (Environment.GetEnvironmentVariable("CFD_TEST") ?? "").Split(';'))
        {
            if (part.StartsWith("recenthover=")) recentList.Hover(int.Parse(part[12..]));
            if (part.StartsWith("recentmenu=")) BeginInvoke(() => ShowRecentMenu(int.Parse(part[11..]), new Point(recentList.Width / 2, 20)));
        }
#endif
    }

    /// <summary>One row per recent project, newest first; click to open it, right-click for more. The list scrolls after six.</summary>
    void FillRecent()
    {
        var recent = Projects.Recent();
        recentList.SetItems([.. recent.Select(r => new RecentList.Item(r.Path, r.Project, !File.Exists(r.Project.File)))]);
        recentList.Visible = clearLink.Visible = recent.Count > 0;
    }

    /// <summary>Opens a recent project; one whose video has gone asks where it is first</summary>
    void OpenRecent(int i)
    {
        var item = recentList.Items[i];
        if (item.Missing && !PickVideoFor(item, "Locate")) return;
        OpenProject = item.Project;
        Close();
    }

    /// <summary>Asks for the video a project should use: where the old one went, or a different one to set up the same way</summary>
    bool PickVideoFor(RecentList.Item item, string verb)
    {
        var old = item.Project.File;
        using var dlg = new OpenFileDialog
        {
            Title = $"{verb} {Path.GetFileName(old)}",
            Filter = Program.VideoFilter,
            FileName = Path.GetFileName(old),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(old)) ? Path.GetDirectoryName(old) : "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        Projects.Relink(item.Path, item.Project, dlg.FileName);
        return true;
    }

    void ShowRecentMenu(int i, Point at)
    {
        var item = recentList.Items[i];
        recentMenu.Items.Clear();
        if (!item.Missing) recentMenu.Add("Open", "", () => OpenRecent(i));
        recentMenu.Add(item.Missing ? "Locate video…" : "Swap video…", "", () => { if (PickVideoFor(item, item.Missing ? "Locate" : "Swap for")) FillRecent(); });
        if (!item.Missing) recentMenu.Add("Show in folder", "", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Project.File}\""));
        recentMenu.Items.Add(new ToolStripSeparator());
        recentMenu.Add("Remove from list", "", () => { Projects.Remove(item.Path); FillRecent(); });
        recentMenu.Show(recentList, at);
    }

    static Label Section(string text) =>
        new() { Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, 14, 0, 8) };

    static FlowLayoutPanel Row(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(items);
        return row;
    }

    static DarkCheck Check(string text, bool on) => new() { Text = text, Checked = on, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };

    void RefreshFfmpeg()
    {
        var custom = cfg.FfmpegPath != "";
        var found = Ff.Locate(cfg.FfmpegPath) == null;
        // With nothing typed, the box shows where it was found
        pathBox.PlaceholderText = found && !custom ? Path.GetDirectoryName(Ff.Ffmpeg) ?? "" : "Folder with ffmpeg.exe";
        // A folder that doesn't have it still falls back to an automatic find, so check it's really the typed one
        var usingTyped = found && custom && Ff.Ffmpeg.StartsWith(cfg.FfmpegPath.TrimEnd('\\', '/').Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        // Only problems get a line; a working ffmpeg needs nothing said
        ffmpegStatus.Text = custom && !usingTyped ? "ffmpeg.exe isn't in that folder." : found ? "" : "Not found. Download it, or paste its folder above.";
        ffmpegStatus.ForeColor = Theme.Danger;
        ffmpegStatus.Parent!.Visible = ffmpegStatus.Text != "";
        openBtn.Enabled = found;
        // The download is only offered when there's no ffmpeg to use. (It isn't checked for updates: the
        // builds it comes from are remade daily, so there would always be a "newer" one.)
        downloadLink.Visible = !found;
        if (found) FfmpegChanged?.Invoke();
    }

    void ChooseFfmpeg()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder containing ffmpeg.exe and ffprobe.exe", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) pathBox.Text = dlg.SelectedPath;
    }

    void SetPath(string path)
    {
        settingPath = true;
        pathBox.Text = path;
        settingPath = false;
        cfg.FfmpegPath = path;
        cfg.Save();
        RefreshFfmpeg();
    }

    async void DownloadFfmpeg()
    {
        if (download != null) return;
        download = new CancellationTokenSource();
        pathBox.Enabled = browseBtn.Enabled = downloadLink.Enabled = false;
        bar.Value = 0;
        bar.Visible = true;
        ffmpegStatus.Parent!.Visible = true;
        var progress = new Progress<double>(p => { ffmpegStatus.Text = $"Downloading ffmpeg · {p * 100:0}%"; ffmpegStatus.ForeColor = Theme.Text; bar.Value = p; });
        try
        {
            await Task.Run(() => Ff.DownloadFfmpeg(progress, download.Token));
            SetPath(""); // the download is found automatically
        }
        catch (OperationCanceledException) { RefreshFfmpeg(); }
        catch (Exception ex) { ffmpegStatus.Text = "Download failed: " + ex.Message; ffmpegStatus.ForeColor = Theme.Danger; }
        finally
        {
            download = null;
            bar.Visible = false;
            pathBox.Enabled = browseBtn.Enabled = downloadLink.Enabled = true;
        }
    }

    async void CheckNow()
    {
        checkLink.Enabled = false;
        updateStatus.Text = "Checking…";
        updateStatus.ForeColor = Theme.Muted;
        try
        {
            release = await Updater.Check();
            cfg.LastUpdateCheck = DateTime.UtcNow;
            cfg.Save();
            updateStatus.Text = release == null ? $"Version {Updater.Current} is the latest." : $"Version {release.Version} is available (you have {Updater.Current}).";
            updateStatus.ForeColor = release == null ? Theme.Muted : Theme.Green;
            installBtn.Visible = release != null;
        }
        catch
        {
            updateStatus.Text = "Couldn't check for updates. Are you online?";
            updateStatus.ForeColor = Theme.Danger;
        }
        checkLink.Enabled = true;
    }

    async void InstallUpdate()
    {
        if (release == null) return;
        installBtn.Enabled = checkLink.Enabled = false;
        updateStatus.Text = "Downloading the update…";
        try
        {
            await Updater.Install(release);
            Application.Exit();
        }
        catch (Exception ex)
        {
            updateStatus.Text = "Update failed: " + ex.Message;
            updateStatus.ForeColor = Theme.Danger;
            installBtn.Enabled = checkLink.Enabled = true;
        }
    }

    /// <summary>
    /// The recent projects in one rounded box, like the text fields: each row is the video's name and when it was
    /// saved, a gone video in red. Scrolls (wheel or the thin bar) after six rows.
    /// </summary>
    sealed class RecentList : Control
    {
        public sealed record Item(string Path, Project Project, bool Missing);
        public IReadOnlyList<Item> Items { get; private set; } = [];
        public event Action<int>? RowClicked;
        public event Action<int, Point>? RowMenu;

        const int RowH = 34, Shown = 6, Pad = 4, BarW = 6;
        int hover = -1, scroll;
        bool overBar;
        int? dragFrom;
        readonly ToolTip tips = new();

        public RecentList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Fit();
        }

        int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);
        int Row => Px(RowH);
        int View => Math.Min(Items.Count, Shown) * Row;
        int MaxScroll => Math.Max(0, Items.Count * Row - View);
        bool Scrolls => MaxScroll > 0;
        Rectangle Track => new(Width - Px(Pad) - Px(BarW), Px(Pad), Px(BarW), View);
        Rectangle Thumb
        {
            get
            {
                var t = Track;
                var h = Math.Max(Px(24), t.Height * View / Math.Max(1, Items.Count * Row));
                return new Rectangle(t.X, t.Y + (MaxScroll == 0 ? 0 : (t.Height - h) * scroll / MaxScroll), t.Width, h);
            }
        }

        void Fit() => Height = View + 2 * Px(Pad);
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

        public void SetItems(IReadOnlyList<Item> items)
        {
            Items = items;
            (hover, scroll) = (-1, 0);
            Fit();
            Invalidate();
        }

        int RowAt(Point p)
        {
            if (p.Y < Px(Pad) || p.Y >= Px(Pad) + View || (Scrolls && p.X >= Track.X - Px(2))) return -1;
            var i = (p.Y - Px(Pad) + scroll) / Row;
            return i < Items.Count ? i : -1;
        }

        void ScrollTo(int to)
        {
            to = Math.Clamp(to, 0, MaxScroll);
            if (to == scroll) return;
            scroll = to;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragFrom is { } from)
            {
                // Dragging the thumb: its travel maps onto the whole list
                var travel = Math.Max(1, Track.Height - Thumb.Height);
                ScrollTo(from + (e.Y - dragStartY) * MaxScroll / travel);
                return;
            }
            var bar = Scrolls && e.X >= Track.X - Px(2) && Thumb.Contains(Track.X, e.Y);
            var row = RowAt(e.Location);
            if (row != hover || bar != overBar)
            {
                (hover, overBar) = (row, bar);
                Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
                tips.SetToolTip(this, row >= 0 ? Items[row].Project.File : null);
                Invalidate();
            }
        }

        int dragStartY;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Scrolls || e.X < Track.X - Px(2)) return;
            var thumb = Thumb;
            // A click on the track jumps a page towards it; on the thumb, drags it
            if (e.Y < thumb.Y) ScrollTo(scroll - View);
            else if (e.Y >= thumb.Bottom) ScrollTo(scroll + View);
            else (dragFrom, dragStartY) = (scroll, e.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragFrom != null) { dragFrom = null; return; }
            var row = RowAt(e.Location);
            if (row < 0) return;
            if (e.Button == MouseButtons.Left) RowClicked?.Invoke(row);
            else if (e.Button == MouseButtons.Right) RowMenu?.Invoke(row, e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scroll - e.Delta / 120 * Row);
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, PointToClient(MousePosition).X, PointToClient(MousePosition).Y, 0));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            (hover, overBar) = (-1, false);
            Invalidate();
        }

        /// <summary>Shows a row as hovered (test screenshots)</summary>
        public void Hover(int i) { hover = i; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var box = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Px(8)))
            using (var fill = new SolidBrush(Theme.Bg3)) g.FillPath(fill, box);

            var pad = Px(Pad);
            var right = Scrolls ? Track.X - Px(4) : Width - pad;
            g.SetClip(new Rectangle(pad, pad, Width - 2 * pad, View));
            var first = scroll / Row;
            for (var i = first; i < Items.Count && i * Row - scroll < View; i++)
            {
                var item = Items[i];
                var r = new Rectangle(pad, pad + i * Row - scroll, right - pad, Row);
                if (i == hover)
                    using (var shape = Theme.Round(new RectangleF(r.X, r.Y + 1, r.Width - 1, r.Height - 2), Px(6)))
                    using (var fill = new SolidBrush(Theme.Bg)) g.FillPath(fill, shape);
                var text = Px(10);
                var when = item.Missing ? "moved or deleted · click to locate" : Projects.When(item.Project.Saved);
                var whenW = TextRenderer.MeasureText(when, Font).Width;
                TextRenderer.DrawText(g, when, Font, new Rectangle(r.Right - text - whenW, r.Y, whenW, r.Height), item.Missing ? Theme.Danger : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, System.IO.Path.GetFileName(item.Project.File), Font, new Rectangle(r.X + text, r.Y, r.Width - 3 * text - whenW, r.Height), item.Missing ? Theme.Muted : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            g.ResetClip();

            // Thin rounded scrollbar, as on the timeline's tracks
            if (Scrolls)
            {
                var thumb = Thumb;
                using var shape = Theme.Round(new RectangleF(thumb.X, thumb.Y, thumb.Width, thumb.Height), thumb.Width / 2f);
                using var fill = new SolidBrush(overBar || dragFrom != null ? Theme.SecondaryHover : Theme.Secondary);
                g.FillPath(fill, shape);
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        download?.Cancel();
        base.OnFormClosing(e);
    }
}
