namespace CompressForDiscord;

/// <summary>ffmpeg location, desktop shortcut, done sound and updates. Everything applies as soon as it's changed.</summary>
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

    public SettingsForm()
    {
        Text = "Settings · Compress for Discord";
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(20, 6), Margin = Padding.Empty };

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
        var cpu = new Pill("CPU") { Margin = new Padding(0, 0, 12, 0) };
        var encoderNote = Muted("GPU is much faster. CPU looks slightly better at the same size.");
        encoderNote.Margin = new Padding(0, 5, 0, 0);
        void PickEncoder(string value)
        {
            cfg.Encoder = value;
            cfg.Save();
            (gpu.On, cpu.On) = (value != "x264", value == "x264");
        }
        gpu.Click += (_, _) => PickEncoder("auto");
        cpu.Click += (_, _) => PickEncoder("x264");
        (gpu.On, cpu.On) = (cfg.Encoder != "x264", cfg.Encoder == "x264");
        root.Controls.Add(Row(gpu, cpu, encoderNote));
        // "GPU" means NVIDIA or AMD hardware encoding; say so when this PC has neither
        Shown += async (_, _) =>
        {
            if (Ff.Ffmpeg == "") return;
            string found;
            try { found = await Ff.PickEncoder("auto"); } catch { return; }
            if (IsDisposed || found != "x264") return;
            gpu.Enabled = false;
            (gpu.On, cpu.On) = (false, true);
            encoderNote.Text = "No supported graphics card found, so the CPU is used.";
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
    }

    static Label Section(string text) =>
        new() { Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, 14, 0, 8) };

    static FlowLayoutPanel Row(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(items);
        return row;
    }

    static CheckBox Check(string text, bool on) => new() { Text = text, Checked = on, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 4) };

    void RefreshFfmpeg()
    {
        var custom = cfg.FfmpegPath != "";
        var found = Ff.Locate(cfg.FfmpegPath) == null;
        // With nothing typed, the box shows where it was found
        pathBox.PlaceholderText = found && !custom ? Path.GetDirectoryName(Ff.Ffmpeg) ?? "" : "Folder with ffmpeg.exe";
        // A folder that doesn't have it still falls back to an automatic find, so check it's really the typed one
        var usingTyped = found && custom && Ff.Ffmpeg.StartsWith(cfg.FfmpegPath.TrimEnd('\\', '/').Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        (ffmpegStatus.Text, ffmpegStatus.ForeColor) =
            custom && !usingTyped ? ("ffmpeg.exe isn't in that folder.", Theme.Danger)
            : found ? (custom ? "Using this folder." : "Found automatically.", Theme.Muted)
            : ("Not found. Download it, or paste its folder above.", Theme.Danger);
        openBtn.Enabled = found;
        // Offer the download only when it's wanted: nothing found yet, or to refresh the copy this app downloaded.
        // An ffmpeg from somewhere else is the user's own to keep up to date.
        var ours = found && Ff.Ffmpeg.StartsWith(Paths.Ffmpeg, StringComparison.OrdinalIgnoreCase);
        downloadLink.Visible = !found || ours;
        downloadLink.Text = ours ? "Update" : "Download ffmpeg";
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
