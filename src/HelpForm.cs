namespace CompressForDiscord;

/// <summary>How to use the app and every hotkey</summary>
sealed class HelpForm : DarkForm
{
    static readonly (string Keys, string What)[] Hotkeys =
    [
        ("Space", "Play / pause"),
        ("← / →", "Step a frame (Shift: 1 second)"),
        ("Q  or  I", "Set start"),
        ("E  or  O", "Set end"),
        ("Home / End", "Jump to start / end"),
        ("Enter", "Compress & copy"),
        ("Ctrl+S", "Compress & save…"),
        ("Ctrl+O", "Open another video"),
        ("F1", "This help"),
        ("Esc", "Cancel / close"),
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
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (k, what) in Hotkeys)
        {
            keys.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = Theme.Header, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 3, 0, 3) });
            keys.Controls.Add(new Label { Text = what, UseMnemonic = false, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 3, 0, 3) });
        }
        root.Controls.Add(keys);

        var footer = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        footer.Controls.Add(new Label { Text = $"Version {Updater.Current}", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 14, 0) });
        footer.Controls.Add(Link("Project page", () => Updater.Open(Updater.RepoUrl)));
        root.Controls.Add(footer);
        Controls.Add(root);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.F1 or Keys.Enter) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
