namespace CompressForDiscord;

// Test code: only debug builds compile this folder (see the project file).

sealed partial class SettingsForm
{
    /// <summary>
    /// Test screenshots, from CFD_TEST: recentpage=N shows that page (from 0), recenthover=N or N,F shows row N
    /// hovered at thumbnail F (once the thumbnails are made), recentmenu=N opens its right-click menu
    /// </summary>
    partial void TestShown()
    {
        foreach (var part in (Environment.GetEnvironmentVariable("CFD_TEST") ?? "").Split(';'))
        {
            if (part.StartsWith("recentpage=")) recentList.ShowPage(int.Parse(part[11..]));
            if (part.StartsWith("recenthover="))
            {
                var at = part[12..].Split(',').Select(int.Parse).ToArray();
                var wait = new System.Windows.Forms.Timer { Interval = 100 };
                wait.Tick += (_, _) => { if (!recentList.ThumbsDone) return; wait.Dispose(); recentList.Hover(at[0], at.Length > 1 ? at[1] : -1); };
                wait.Start();
            }
            if (part.StartsWith("recentmenu=")) BeginInvoke(() => ShowRecentMenu(int.Parse(part[11..]), new Point(recentList.Width / 2, 20)));
        }
    }

    sealed partial class RecentList
    {
        /// <summary>Shows a row as hovered, at this thumbnail</summary>
        public void Hover(int i, int frame = -1) { (hover, hoverFrame) = (i, frame); Invalidate(); ShowPeek(); }
        /// <summary>Whether every project on the page has its thumbnails (or can't have any)</summary>
        public bool ThumbsDone => !filling && Items.Skip(page * PerPage).Take(PerPage).All(i => i.Missing || thumbs.ContainsKey(Key(i)));
        public void ShowPage(int p) => GoTo(p);
    }
}
