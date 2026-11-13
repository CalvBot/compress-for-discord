namespace CompressForDiscord;

/// <summary>ffmpeg location, desktop shortcut and updates</summary>
sealed class SettingsForm : DarkForm
{
    const int W = 480;
    readonly AppConfig cfg = AppConfig.Current;
    readonly Label ffmpegStatus, updateStatus;
    readonly Pill chooseBtn, autoBtn, downloadBtn, checkBtn, installBtn;
    readonly Bar bar;
    CancellationTokenSource? download;
    Release? release;

    /// <summary>ffmpeg was found, changed or downloaded</summary>
    public event Action? FfmpegChanged;

    public SettingsForm()
    {
        Text = "Settings · Compress for Discord";
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(20, 14), Margin = Padding.Empty };
        root.Controls.Add(Heading("Settings"));

        // ffmpeg
        root.Controls.Add(Section("ffmpeg"));
        ffmpegStatus = Muted("", W);
        ffmpegStatus.AutoEllipsis = true;
        bar = new Bar { Size = new Size(W, 8), Visible = false, Margin = new Padding(0, 0, 0, 8) };
        chooseBtn = new Pill("Choose folder…", PillKind.Secondary) { Margin = new Padding(0, 0, 8, 0) };
        autoBtn = new Pill("Find automatically", PillKind.Secondary) { Margin = new Padding(0, 0, 8, 0) };
        downloadBtn = new Pill("Download", PillKind.Secondary) { Margin = Padding.Empty };
        chooseBtn.Click += (_, _) => ChooseFfmpeg();
        autoBtn.Click += (_, _) => { cfg.FfmpegPath = ""; cfg.Save(); RefreshFfmpeg(); };
        downloadBtn.Click += (_, _) => DownloadFfmpeg();
        root.Controls.Add(ffmpegStatus);
        root.Controls.Add(bar);
        root.Controls.Add(Row(chooseBtn, autoBtn, downloadBtn));

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
        updateStatus = Muted($"You have version {Updater.Current}.", W);
        root.Controls.Add(updateStatus);
        checkBtn = new Pill("Check now", PillKind.Secondary) { Margin = new Padding(0, 0, 8, 0) };
        installBtn = new Pill("Install update", PillKind.Primary) { Visible = false, Margin = Padding.Empty };
        checkBtn.Click += (_, _) => CheckNow();
        installBtn.Click += (_, _) => InstallUpdate();
        root.Controls.Add(Row(checkBtn, installBtn));

        var close = new Pill("Done", PillKind.Primary) { Dock = DockStyle.Right };
        close.Click += (_, _) => Close();
        var bottom = new Panel { Size = new Size(W, 38), Margin = new Padding(0, 18, 0, 0) };
        bottom.Controls.Add(close);
        root.Controls.Add(bottom);
        Controls.Add(root);
        RefreshFfmpeg();
    }

    static Label Section(string text) =>
        new() { Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, 14, 0, 6) };

    static FlowLayoutPanel Row(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(items);
        return row;
    }

    static CheckBox Check(string text, bool on) => new() { Text = text, Checked = on, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 4) };

    void RefreshFfmpeg()
    {
        if (Ff.Locate(cfg.FfmpegPath) == null)
        {
            ffmpegStatus.Text = "Using " + Ff.Ffmpeg + (cfg.FfmpegPath != "" ? "  (chosen folder)" : "");
            ffmpegStatus.ForeColor = Theme.Muted;
            FfmpegChanged?.Invoke();
        }
        else
        {
            ffmpegStatus.Text = cfg.FfmpegPath != "" ? $"ffmpeg isn't in {cfg.FfmpegPath}." : "ffmpeg wasn't found. Download it or choose its folder.";
            ffmpegStatus.ForeColor = Theme.Danger;
        }
    }

    void ChooseFfmpeg()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder containing ffmpeg.exe and ffprobe.exe", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        cfg.FfmpegPath = dlg.SelectedPath;
        cfg.Save();
        RefreshFfmpeg();
    }

    async void DownloadFfmpeg()
    {
        if (download != null) return;
        download = new CancellationTokenSource();
        chooseBtn.Enabled = autoBtn.Enabled = downloadBtn.Enabled = false;
        bar.Value = 0;
        bar.Visible = true;
        var progress = new Progress<double>(p => { ffmpegStatus.Text = $"Downloading ffmpeg · {p * 100:0}%"; ffmpegStatus.ForeColor = Theme.Text; bar.Value = p; });
        try
        {
            await Task.Run(() => Ff.DownloadFfmpeg(progress, download.Token));
            cfg.FfmpegPath = ""; // the download is found automatically
            cfg.Save();
            RefreshFfmpeg();
        }
        catch (OperationCanceledException) { RefreshFfmpeg(); }
        catch (Exception ex) { ffmpegStatus.Text = "Download failed: " + ex.Message; ffmpegStatus.ForeColor = Theme.Danger; }
        finally
        {
            download = null;
            bar.Visible = false;
            chooseBtn.Enabled = autoBtn.Enabled = downloadBtn.Enabled = true;
        }
    }

    async void CheckNow()
    {
        checkBtn.Enabled = false;
        updateStatus.Text = "Checking…";
        updateStatus.ForeColor = Theme.Muted;
        try
        {
            release = await Updater.Check();
            cfg.LastUpdateCheck = DateTime.UtcNow;
            cfg.Save();
            updateStatus.Text = release == null ? $"You're up to date (version {Updater.Current})." : $"Version {release.Version} is available (you have {Updater.Current}).";
            updateStatus.ForeColor = release == null ? Theme.Muted : Theme.Green;
            installBtn.Visible = release != null;
        }
        catch
        {
            updateStatus.Text = "Couldn't check for updates. Are you online?";
            updateStatus.ForeColor = Theme.Danger;
        }
        checkBtn.Enabled = true;
    }

    async void InstallUpdate()
    {
        if (release == null) return;
        installBtn.Enabled = checkBtn.Enabled = false;
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
            installBtn.Enabled = checkBtn.Enabled = true;
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
