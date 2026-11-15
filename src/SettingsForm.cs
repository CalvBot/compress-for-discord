namespace CompressForDiscord;

/// <summary>
/// Recent projects, where ffmpeg is, the encoder, the general choices (desktop shortcut, done sound, waveform
/// style, undo steps) and updates. Everything applies as soon as it's changed.
/// </summary>
sealed partial class SettingsForm : DarkForm
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
    /// <summary>The waveform style was changed: redraw them</summary>
    public event Action? WavesChanged;
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
        recentList.RowRemoved += i => { Projects.Delete(recentList.Items[i].Path); FillRecent(); };
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
        // ffmpeg found first: the list's thumbnails are made with it (opened from the home window, nothing has looked yet)
        Ff.Locate(cfg.FfmpegPath);
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
            if (desktop.Checked == Shortcut.OnDesktop) return;
            try { Shortcut.SetOnDesktop(desktop.Checked); }
            catch (Exception ex)
            {
                AppLog.Error("Desktop shortcut", ex);
                MessageBox.Show(this, "Couldn't change the desktop shortcut.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                // Show what's really there (the check above stops this asking again)
                desktop.Checked = Shortcut.OnDesktop;
            }
        };
        root.Controls.Add(desktop);
        // Waveforms: up from the bottom of the lane, or mirrored about its middle
        var wavesLabel = Muted("Waveforms");
        wavesLabel.Margin = new Padding(0, 5, 8, 0);
        var bottom = new Pill("From the bottom") { Margin = new Padding(0, 0, 6, 0) };
        var mirrored = new Pill("Mirrored") { Margin = Padding.Empty };
        void PickWaves(bool mirror)
        {
            cfg.MirroredWaves = mirror;
            cfg.Save();
            (bottom.On, mirrored.On) = (!mirror, mirror);
            WavesChanged?.Invoke();
        }
        bottom.Click += (_, _) => PickWaves(false);
        mirrored.Click += (_, _) => PickWaves(true);
        (bottom.On, mirrored.On) = (!cfg.MirroredWaves, cfg.MirroredWaves);
        var wavesRow = Row(wavesLabel, bottom, mirrored);
        wavesRow.Margin = new Padding(0, 6, 0, 0);
        var sound = Check("Play a sound when done", cfg.PlaySound);
        sound.CheckedChanged += (_, _) => { cfg.PlaySound = sound.Checked; cfg.Save(); };
        root.Controls.Add(sound);
        root.Controls.Add(wavesRow);
        // How far Ctrl+Z goes back
        var undoLabel = Muted("Undo steps");
        undoLabel.Margin = new Padding(0, 5, 8, 0);
        var undo = new List<Pill>();
        foreach (var n in new[] { 10, 25, 50, 100 })
        {
            var chip = new Pill($"{n}") { Margin = new Padding(0, 0, 6, 0), On = cfg.UndoSteps == n };
            chip.Click += (_, _) =>
            {
                cfg.UndoSteps = n;
                cfg.Save();
                foreach (var c in undo) c.On = c == chip;
            };
            undo.Add(chip);
        }
        var undoRow = Row([undoLabel, .. undo]);
        undoRow.Margin = new Padding(0, 6, 0, 0);
        root.Controls.Add(undoRow);

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
        TestShown();
    }

    /// <summary>For the test code in src\Debug (debug builds only); in a release build the call is dropped</summary>
    partial void TestShown();

    /// <summary>One row per recent project, newest first; click to open it, right-click for more. Pages of five.</summary>
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
        recentMenu.Add("Remove from list", "", () => { Projects.Delete(item.Path); FillRecent(); });
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
        ffmpegStatus.Text = custom && !usingTyped ? "ffmpeg.exe isn't in that folder." : found ? "" : "ffmpeg wasn't found. Download it, or paste its folder above.";
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
            var token = download.Token;
            await Task.Run(() => Ff.DownloadFfmpeg(progress, token));
            // Settings was closed meanwhile: the download is found automatically next time
            if (!IsDisposed) SetPath("");
        }
        catch (OperationCanceledException) { if (!IsDisposed) RefreshFfmpeg(); }
        catch (Exception ex)
        {
            AppLog.Error("ffmpeg download failed", ex);
            if (!IsDisposed) { ffmpegStatus.Text = "Download failed: " + ex.Message; ffmpegStatus.ForeColor = Theme.Danger; }
        }
        finally
        {
            download?.Dispose();
            download = null;
            if (!IsDisposed)
            {
                bar.Visible = false;
                pathBox.Enabled = browseBtn.Enabled = downloadLink.Enabled = true;
            }
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
        catch (Exception ex)
        {
            AppLog.Warn($"Update check failed: {ex.Message}");
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
            // False = the release has no installer and its page was opened instead: nothing to quit for
            if (await Updater.Install(release)) { Application.Exit(); return; }
            if (IsDisposed) return;
            updateStatus.Text = "The release page is open. Download the update from there.";
            installBtn.Enabled = checkLink.Enabled = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Update install failed", ex);
            updateStatus.Text = "Update failed: " + ex.Message;
            updateStatus.ForeColor = Theme.Danger;
            installBtn.Enabled = checkLink.Enabled = true;
        }
    }

    /// <summary>
    /// The recent projects in one rounded box, like the text fields: each row is the video's name and when it was
    /// saved, a gone video in red. Pages of five, with ‹ › at the bottom right once there's more than one page. The box
    /// is always five rows tall (a short page leaves blank rows), so it and the arrows stay put while paging.
    /// </summary>
    sealed partial class RecentList : Control
    {
        public sealed record Item(string Path, Project Project, bool Missing);
        public IReadOnlyList<Item> Items { get; private set; } = [];
        public event Action<int>? RowClicked;
        public event Action<int, Point>? RowMenu;
        /// <summary>The ✕ at the end of a row was clicked: take that project off the list</summary>
        public event Action<int>? RowRemoved;

        const int RowH = 34, PerPage = 5, Pad = 4, FootH = 30, ArrowW = 30;
        int hover = -1, page;
        /// <summary>The mouse is on the hovered row's ✕</summary>
        bool overRemove;
        /// <summary>When a project was last taken off with its ✕ (Environment.TickCount64)</summary>
        long lastRemove;
        /// <summary>The arrow under the mouse: -1 back, 1 on, 0 neither</summary>
        int overArrow;
        /// <summary>The thumbnail shown for the hovered row: moving across the row scrubs through them</summary>
        int hoverFrame = -1;
        readonly ToolTip tips = new();
        /// <summary>Each project's thumbnails, by project file and video (made in the background, one at a time)</summary>
        readonly Dictionary<string, Bitmap[]?> thumbs = [];
        readonly CancellationTokenSource life = new();
        bool filling;
        Peek? peek;

        public RecentList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Fit();
        }

        static string Key(Item item) => item.Path + "|" + item.Project.File;
        Bitmap[]? Thumbs(Item item) => thumbs.GetValueOrDefault(Key(item));

        /// <summary>Makes the shown page's missing thumbnails (not every project's: keeping 50 would be ~50 MB and a minute of decoding)</summary>
        async void FillThumbs()
        {
            if (filling) return;
            filling = true;
            try
            {
                while (!life.IsCancellationRequested)
                {
                    var next = Items.Skip(page * PerPage).Take(PerPage).FirstOrDefault(i => !i.Missing && !thumbs.ContainsKey(Key(i)));
                    if (next == null) break;
                    thumbs[Key(next)] = null;
                    var frames = await Task.Run(() => ProjectThumbs.LoadOrMake(next.Path, next.Project, life.Token));
                    if (life.IsCancellationRequested) { foreach (var b in frames ?? []) b.Dispose(); break; }
                    thumbs[Key(next)] = frames;
                    Invalidate();
                    if (hover >= 0 && hover < Items.Count && Items[hover] == next && frames != null)
                    {
                        // The preview shows the path now
                        tips.SetToolTip(this, null);
                        ShowPeek();
                    }
                }
            }
            catch (Exception ex) { AppLog.Info($"Project thumbnails stopped: {ex.Message}"); }
            finally { filling = false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                life.Cancel();
                life.Dispose();
                tips.Dispose();
                peek?.Dispose();
                foreach (var frames in thumbs.Values) foreach (var b in frames ?? []) b.Dispose();
                thumbs.Clear();
            }
            base.Dispose(disposing);
        }

        /// <summary>Where the thumbnail goes in a row</summary>
        Rectangle ThumbBox(Rectangle row)
        {
            var h = row.Height - Px(10);
            return new Rectangle(row.X + Px(8), row.Y + (row.Height - h) / 2, h * 16 / 9, h);
        }

        /// <summary>A row's place in the list</summary>
        Rectangle RowBox(int i) => new(Px(Pad), Px(Pad) + (i - page * PerPage) * Row, Width - 2 * Px(Pad), Row);

        /// <summary>The ✕ at the right end of a row (shown on the row under the mouse)</summary>
        Rectangle RemoveBox(Rectangle row)
        {
            var s = Px(22);
            return new Rectangle(row.Right - s - Px(6), row.Y + (row.Height - s) / 2, s, s);
        }

        /// <summary>The larger frame above the hovered row (below it if there's no room), or hidden</summary>
        void ShowPeek()
        {
            // (not while the mouse is on the ✕: that's about removing the project, not looking at it)
            if (hover < 0 || hoverFrame < 0 || overRemove || Thumbs(Items[hover]) is not { } frames || !IsHandleCreated || !Visible)
            {
                peek?.Hide();
                return;
            }
            var frame = frames[Math.Clamp(hoverFrame, 0, frames.Length - 1)];
            var w = Px(288);
            var h = (int)Math.Round(w * (double)frame.Height / frame.Width);
            var size = new Size(w + 2 * Px(6), h + Px(6) + Px(24));
            var pad = Px(Pad);
            var rowTop = PointToScreen(new Point(0, pad + (hover - page * PerPage) * Row));
            var mouse = MousePosition;
            var screen = Screen.FromControl(this).WorkingArea;
            var x = Math.Clamp(mouse.X - size.Width / 2, screen.Left, screen.Right - size.Width);
            var y = rowTop.Y - size.Height - Px(4);
            if (y < screen.Top) y = rowTop.Y + Row + Px(4);
            peek ??= new Peek();
            peek.Set(frame, Items[hover].Project.File);
            peek.Bounds = new Rectangle(new Point(x, y), size);
            if (!peek.Visible) peek.Show(FindForm());
        }

        /// <summary>A borderless window with one frame and the video's path. It never takes focus or the mouse.</summary>
        sealed class Peek : Form
        {
            Image? image;
            string caption = "";

            public Peek()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.Bg2;
                DoubleBuffered = true;
            }

            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x80; // no activate, tool window
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Rounded corners on Windows 11
                var round = 2;
                try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            }

            [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
            static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

            // The mouse goes through to the list underneath
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x84) { m.Result = -1; return; } // WM_NCHITTEST: HTTRANSPARENT
                base.WndProc(ref m);
            }

            public void Set(Image frame, string path)
            {
                if (ReferenceEquals(frame, image) && path == caption) return;
                (image, caption) = (frame, path);
                Invalidate();
            }

            int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var border = new Pen(Theme.Bg3)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                if (image == null) return;
                var pad = Px(6);
                var pic = new Rectangle(pad, pad, Width - 2 * pad, Height - pad - Px(24));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(image, pic);
                TextRenderer.DrawText(g, caption, Font, new Rectangle(pad, pic.Bottom, Width - 2 * pad, Px(24)), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);
        int Row => Px(RowH);
        int Pages => Math.Max(1, (Items.Count + PerPage - 1) / PerPage);
        bool Paged => Pages > 1;
        int Rows => PerPage * Row;
        Rectangle Foot => new(Px(Pad), Px(Pad) + Rows, Width - 2 * Px(Pad), Px(FootH));
        Rectangle Arrow(int dir)
        {
            var f = Foot;
            var w = Px(ArrowW);
            return new Rectangle(dir < 0 ? f.Right - 2 * w : f.Right - w, f.Y, w, f.Height);
        }

        void Fit() => Height = Rows + (Paged ? Px(FootH) : 0) + 2 * Px(Pad);
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

        /// <summary>New items; stays on the same page (a removed row shouldn't jump back to the first), if it still has any</summary>
        public void SetItems(IReadOnlyList<Item> items)
        {
            Items = items;
            (hover, overRemove) = (-1, false);
            page = Math.Clamp(page, 0, Pages - 1);
            Fit();
            Invalidate();
            peek?.Hide();
            FillThumbs();
            // Another row may be under the mouse now (one was removed): show it as such
            if (IsHandleCreated && Visible)
            {
                var p = PointToClient(MousePosition);
                if (ClientRectangle.Contains(p)) OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
            }
        }

        void GoTo(int to)
        {
            to = Math.Clamp(to, 0, Pages - 1);
            if (to == page) return;
            page = to;
            Invalidate();
            // This page's thumbnails come next
            FillThumbs();
            // The row under the mouse is a different project now
            var p = PointToClient(MousePosition);
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
        }

        int RowAt(Point p)
        {
            if (p.Y < Px(Pad) || p.Y >= Px(Pad) + Rows || p.X < Px(Pad) || p.X >= Width - Px(Pad)) return -1;
            var i = page * PerPage + (p.Y - Px(Pad)) / Row;
            return i < Items.Count ? i : -1;
        }

        int ArrowAt(Point p)
        {
            if (!Paged) return 0;
            if (Arrow(-1).Contains(p)) return page > 0 ? -1 : 0;
            if (Arrow(1).Contains(p)) return page < Pages - 1 ? 1 : 0;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var row = RowAt(e.Location);
            var arrow = ArrowAt(e.Location);
            // Left to right across the row runs through the thumbnails
            var inner = Width - 2 * Px(Pad);
            var frame = row >= 0 ? Math.Clamp((e.X - Px(Pad)) * ProjectThumbs.Count / Math.Max(1, inner), 0, ProjectThumbs.Count - 1) : -1;
            var remove = row >= 0 && RemoveBox(RowBox(row)).Contains(e.Location);
            if (row != hover || arrow != overArrow || remove != overRemove)
            {
                (hover, overArrow, overRemove) = (row, arrow, remove);
                Cursor = row >= 0 || arrow != 0 ? Cursors.Hand : Cursors.Default;
                // The preview shows the path; the tooltip is for rows without one
                tips.SetToolTip(this, remove ? "Remove from list" : row >= 0 && Thumbs(Items[row]) == null ? Items[row].Project.File : null);
                Invalidate();
            }
            if (frame != hoverFrame)
            {
                hoverFrame = frame;
                if (row >= 0) Invalidate(ThumbBox(new Rectangle(Px(Pad), Px(Pad) + (row - page * PerPage) * Row, inner, Row)));
            }
            ShowPeek();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ArrowAt(e.Location) is var dir and not 0)
            {
                GoTo(page + dir);
                return;
            }
            var row = RowAt(e.Location);
            if (row < 0) return;
            if (e.Button == MouseButtons.Left && RemoveBox(RowBox(row)).Contains(e.Location))
            {
                // Only an ✕ that's showing, and not the second click of a double-click: the next project has moved
                // up under the mouse by then, and would go too
                if (row != hover || !overRemove || Environment.TickCount64 - lastRemove < SystemInformation.DoubleClickTime) return;
                lastRemove = Environment.TickCount64;
                RowRemoved?.Invoke(row);
            }
            else if (e.Button == MouseButtons.Left) RowClicked?.Invoke(row);
            else if (e.Button == MouseButtons.Right) RowMenu?.Invoke(row, e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            GoTo(page - Math.Sign(e.Delta));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            (hover, overArrow, hoverFrame, overRemove) = (-1, 0, -1, false);
            Invalidate();
            peek?.Hide();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) peek?.Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var box = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Px(8)))
            using (var fill = new SolidBrush(Theme.Bg3)) g.FillPath(fill, box);

            var pad = Px(Pad);
            var right = Width - pad;
            for (var i = page * PerPage; i < Math.Min(Items.Count, (page + 1) * PerPage); i++)
            {
                var item = Items[i];
                var r = new Rectangle(pad, pad + (i - page * PerPage) * Row, right - pad, Row);
                if (i == hover)
                    using (var shape = Theme.Round(new RectangleF(r.X, r.Y + 1, r.Width - 1, r.Height - 2), Px(6)))
                    using (var fill = new SolidBrush(Theme.Bg)) g.FillPath(fill, shape);
                // The thumbnail: the hovered row's follows the mouse, the others show the first
                var tb = ThumbBox(r);
                using (var shape = Theme.Round(new RectangleF(tb.X, tb.Y, tb.Width, tb.Height), Px(4)))
                {
                    if (Thumbs(item) is { } frames)
                    {
                        var state = g.Save();
                        g.SetClip(shape);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        var img = frames[i == hover && hoverFrame >= 0 ? Math.Min(hoverFrame, frames.Length - 1) : 0];
                        // Fill the box, cropping what doesn't fit
                        var scale = Math.Max(tb.Width / (float)img.Width, tb.Height / (float)img.Height);
                        var (dw, dh) = (img.Width * scale, img.Height * scale);
                        g.DrawImage(img, tb.X + (tb.Width - dw) / 2, tb.Y + (tb.Height - dh) / 2, dw, dh);
                        g.Restore(state);
                    }
                    else using (var fill = new SolidBrush(Theme.Bg2)) g.FillPath(fill, shape);
                }
                var text = Px(10);
                // The ✕ that takes this project off the list: on the row under the mouse, in a column every row keeps
                // clear so the dates line up
                var x = RemoveBox(r);
                if (i == hover)
                {
                    if (overRemove)
                        using (var shape = Theme.Round(new RectangleF(x.X, x.Y, x.Width - 1, x.Height - 1), Px(5)))
                        using (var fill = new SolidBrush(Theme.Bg3)) g.FillPath(fill, shape);
                    float mx = x.X + x.Width / 2f, my = x.Y + x.Height / 2f, arm = Px(4);
                    using var cross = new Pen(overRemove ? Theme.Danger : Theme.Muted, Math.Max(1.5f, Px(2))) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                    g.DrawLine(cross, mx - arm, my - arm, mx + arm, my + arm);
                    g.DrawLine(cross, mx - arm, my + arm, mx + arm, my - arm);
                }
                r = Rectangle.FromLTRB(tb.Right, r.Y, x.X + Px(4), r.Bottom);
                var when = item.Missing ? "moved or deleted · click to locate" : Projects.When(item.Project.Saved);
                var whenW = TextRenderer.MeasureText(when, Font).Width;
                TextRenderer.DrawText(g, when, Font, new Rectangle(r.Right - text - whenW, r.Y, whenW, r.Height), item.Missing ? Theme.Danger : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, System.IO.Path.GetFileName(item.Project.File), Font, new Rectangle(r.X + text, r.Y, r.Width - 3 * text - whenW, r.Height), item.Missing ? Theme.Muted : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            // Which ones these are, and the arrows to the others
            if (!Paged) return;
            var f = Foot;
            var inset = Px(10);
            var shown = $"{page * PerPage + 1}–{Math.Min(Items.Count, (page + 1) * PerPage)} of {Items.Count}";
            TextRenderer.DrawText(g, shown, Font, new Rectangle(f.X + inset, f.Y, f.Width / 2, f.Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            foreach (var dir in new[] { -1, 1 })
            {
                var a = Arrow(dir);
                var can = dir < 0 ? page > 0 : page < Pages - 1;
                if (overArrow == dir)
                    using (var shape = Theme.Round(new RectangleF(a.X + 1, a.Y + 2, a.Width - 2, a.Height - 4), Px(6)))
                    using (var fill = new SolidBrush(Theme.Bg)) g.FillPath(fill, shape);
                // A chevron, drawn so it's the same at any font
                float cx = a.X + a.Width / 2f, cy = a.Y + a.Height / 2f, s = Px(4);
                using var pen = new Pen(can ? Theme.Text : Color.FromArgb(70, Theme.Muted), Math.Max(1.5f, Px(2))) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                g.DrawLines(pen, [new PointF(cx + dir * -s / 2, cy - s), new PointF(cx + dir * s / 2, cy), new PointF(cx + dir * -s / 2, cy + s)]);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) recentMenu.Dispose();
        base.Dispose(disposing);
    }
}
