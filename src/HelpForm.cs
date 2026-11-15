namespace CompressForDiscord;

/// <summary>How to use the app and every hotkey</summary>
sealed class HelpForm : DarkForm
{
    static readonly (string Section, (string Keys, string What)[] Rows)[] Hotkeys =
    [
        ("Playback", [
            ("Space", "Play / pause"),
            ("Shift + Space", "Play from the start"),
            ("Ctrl + Space", "Play on past the end point"),
            ("← / →", "Step a frame (Shift: 5 seconds)"),
            ("Wheel on preview", "Step a frame"),
            ("Home / End", "Jump to start / end"),
        ]),
        ("Timeline", [
            ("Q / E  (or I / O)", "Set start / end"),
            ("Shift + drag a tab or the bar", "Snap it to the playhead"),
            ("Shift + scrub", "Snap to key points, start and end"),
            ("Wheel / Alt + wheel", "Scroll / zoom to playhead"),
            ("Alt + Shift + wheel", "Zoom to the mouse"),
            ("Double-click zoom bar", "Show the whole video"),
            ("Lock button, left", "Stop on whole frames"),
        ]),
        ("Tracks", [
            ("Shift + wheel", "Taller / shorter tracks"),
            ("Ctrl + click", "Add a key point"),
            ("Shift + click", "Select several, on any tracks; drag to move them all"),
            ("A / Shift + A", "Select every point on this track / all tracks"),
            ("Shift + drag a point", "Time only, snapping to the playhead"),
            ("Ctrl + drag", "Volume: level only"),
            ("← / → over a track", "Previous / next key point"),
            ("Ctrl+C / Ctrl+V", "Copy / paste at the playhead"),
            ("Del", "Delete selected key points"),
            ("Right-click a point", "Linear, smooth, ease or hold"),
            ("1 – 5", "Linear / smooth / ease in / ease out / hold"),
        ]),
        ("Picture", [
            ("Ctrl / Shift + drag", "Snap to edges / one direction"),
            ("Ctrl / Shift / Alt + corner", "Snap / from centre / reshape"),
        ]),
        ("App", [
            ("Ctrl+Z / Ctrl+Shift+Z", "Undo / redo (steps: Settings)"),
            ("Enter / Ctrl+S", "Compress & copy / save…"),
            ("Ctrl+O", "Open another video"),
            ("Esc", "Cancel / close"),
            ("F1", "This help"),
        ]),
    ];

    public HelpForm()
    {
        const int W = 320;
        Text = "Help · Compress for Discord";
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(20, 14), Margin = Padding.Empty };
        root.Controls.Add(Heading("Keys"));
        var keys = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg2, Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0, 4, 0, 12), MinimumSize = new Size(W, 0) };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var firstSection = true;
        foreach (var (section, rows) in Hotkeys)
        {
            var title = new Label { Text = section.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, firstSection ? 0 : 10, 0, 2) };
            keys.Controls.Add(title);
            keys.SetColumnSpan(title, 2);
            firstSection = false;
            foreach (var (k, what) in rows)
            {
                keys.Controls.Add(new Label { Text = k, UseMnemonic = false, AutoSize = true, ForeColor = Theme.Header, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 3, 16, 3) });
                keys.Controls.Add(new Label { Text = what, UseMnemonic = false, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 3, 0, 3) });
            }
        }
        root.Controls.Add(keys);

        var footer = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        footer.Controls.Add(new Label { Text = $"Version {Updater.Current}", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 14, 0) });
        footer.Controls.Add(Link("Project page", () => Updater.Open(Updater.RepoUrl)));
        // For bug reports: opens the folder with app.log selected
        footer.Controls.Add(Link("Show log", () =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", File.Exists(Paths.AppLog) ? $"/select,\"{Paths.AppLog}\"" : $"\"{Paths.Data}\""); } catch { }
        }));
        root.Controls.Add(footer);
        Controls.Add(root);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.F1 or Keys.Enter) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
