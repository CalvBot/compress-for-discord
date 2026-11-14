using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>Discord's dark theme colors</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x31, 0x33, 0x38);
    public static readonly Color Bg2 = Color.FromArgb(0x2b, 0x2d, 0x31);
    public static readonly Color Bg3 = Color.FromArgb(0x1e, 0x1f, 0x22);
    public static readonly Color Text = Color.FromArgb(0xdb, 0xde, 0xe1);
    public static readonly Color Header = Color.FromArgb(0xf2, 0xf3, 0xf5);
    public static readonly Color Muted = Color.FromArgb(0x94, 0x9b, 0xa4);
    public static readonly Color Brand = Color.FromArgb(0x58, 0x65, 0xf2);
    public static readonly Color BrandHover = Color.FromArgb(0x47, 0x52, 0xc4);
    public static readonly Color Secondary = Color.FromArgb(0x4e, 0x50, 0x58);
    public static readonly Color SecondaryHover = Color.FromArgb(0x6d, 0x6f, 0x78);
    public static readonly Color Danger = Color.FromArgb(0xf2, 0x3f, 0x43);
    public static readonly Color Warn = Color.FromArgb(0xf0, 0xb2, 0x32);
    public static readonly Color Green = Color.FromArgb(0x23, 0xa5, 0x59);
    public static readonly Color Link = Color.FromArgb(0x00, 0xa8, 0xfc);

    /// <summary>A colour part of the way from one to another</summary>
    public static Color Blend(Color from, Color to, double amount) =>
        Color.FromArgb((int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

enum PillKind { Chip, Primary, Secondary }

/// <summary>Symbols a chip can show instead of text</summary>
enum PillIcon { None, Play, Pause, Eye, Sliders, Gear, Help }

/// <summary>Rounded chip or button, painted like Discord's</summary>
sealed class Pill : Control
{
    readonly PillKind kind;
    bool on, hover;

    public bool On { get => on; set { on = value; Invalidate(); } }
    /// <summary>Minimum width of a button (not a chip), in 96-dpi pixels</summary>
    public int MinWidth { get; init; } = 96;
    /// <summary>Rounds only the left or right corners, for split buttons</summary>
    public bool FlatLeft { get; init; }
    public bool FlatRight { get; init; }
    PillIcon icon;
    /// <summary>Shows a symbol instead of the text; the chip is then just big enough for it</summary>
    public PillIcon Icon { get => icon; set { icon = value; Fit(); Invalidate(); } }
    string? alt;
    /// <summary>Drawn in place of Text without resizing the button, e.g. "Compressing…"</summary>
    public string? AltText { get => alt; set { alt = value; Invalidate(); } }
    /// <summary>Drawn on a red fill while the mouse is over the button, e.g. "✕  Cancel"</summary>
    public string? HoverText { get; set; }

    public Pill(string text, PillKind kind = PillKind.Chip)
    {
        this.kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 6, 6);
        Text = text;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (icon != PillIcon.None) return new Size(Px(40), Px(28));
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return kind == PillKind.Chip ? new Size(t.Width + Px(20), Px(28)) : new Size(Math.Max(t.Width + Px(MinWidth < 96 ? 16 : 32), Px(MinWidth)), Px(38));
    }

    void Fit() => Size = GetPreferredSize(Size.Empty);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    // DeviceDpi is only right once the handle exists (before that it's the primary screen's), so measure again
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    // Size is already in device pixels (Px), so don't let form auto-scaling scale it again
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, text = Color.White;
        Color? border = null;
        switch (kind)
        {
            case PillKind.Chip:
                fill = on ? Theme.Brand : hover && Enabled ? Color.FromArgb(60, Theme.Secondary) : Parent?.BackColor ?? Theme.Bg;
                border = on ? Theme.Brand : Theme.Secondary;
                text = on ? Color.White : Theme.Text;
                break;
            case PillKind.Primary:
                fill = hover && Enabled ? HoverText != null ? Theme.Danger : Theme.BrandHover : Theme.Brand;
                break;
            default:
                fill = hover && Enabled ? Theme.SecondaryHover : Theme.Secondary;
                break;
        }
        if (!Enabled)
        {
            fill = Color.FromArgb(110, fill);
            text = Color.FromArgb(130, text);
        }
        using var path = Shape(r, kind == PillKind.Chip ? Height / 2f : Px(8));
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is { } bc) using (var pen = new Pen(Enabled ? bc : Color.FromArgb(110, bc))) g.DrawPath(pen, path);
        if (icon != PillIcon.None) { DrawIcon(g, text); return; }
        var label = hover && Enabled && HoverText != null ? HoverText : alt ?? Text;
        TextRenderer.DrawText(g, label, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    void DrawIcon(Graphics g, Color ink)
    {
        float cx = Width / 2f, cy = Height / 2f, u = DeviceDpi / 96f;
        using var fill = new SolidBrush(ink);
        using var pen = new Pen(ink, 1.6f * u);
        switch (icon)
        {
            case PillIcon.Play:
                g.FillPolygon(fill, [new PointF(cx - 4 * u, cy - 6 * u), new PointF(cx + 6 * u, cy), new PointF(cx - 4 * u, cy + 6 * u)]);
                break;
            case PillIcon.Pause:
                g.FillRectangle(fill, cx - 5 * u, cy - 6 * u, 3.5f * u, 12 * u);
                g.FillRectangle(fill, cx + 1.5f * u, cy - 6 * u, 3.5f * u, 12 * u);
                break;
            case PillIcon.Eye:
                using (var lids = new GraphicsPath())
                {
                    PointF left = new(cx - 9 * u, cy), right = new(cx + 9 * u, cy);
                    lids.AddBezier(left, new PointF(cx - 4 * u, cy - 7 * u), new PointF(cx + 4 * u, cy - 7 * u), right);
                    lids.AddBezier(right, new PointF(cx + 4 * u, cy + 7 * u), new PointF(cx - 4 * u, cy + 7 * u), left);
                    g.DrawPath(pen, lids);
                }
                g.FillEllipse(fill, cx - 3 * u, cy - 3 * u, 6 * u, 6 * u);
                break;
            case PillIcon.Gear:
                // A ring with eight teeth
                using (var teeth = new Pen(ink, 2.6f * u))
                    for (var i = 0; i < 8; i++)
                    {
                        var a = i * Math.PI / 4;
                        float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                        g.DrawLine(teeth, cx + c * 5.5f * u, cy + sn * 5.5f * u, cx + c * 8.2f * u, cy + sn * 8.2f * u);
                    }
                using (var ring = new Pen(ink, 2.4f * u)) g.DrawEllipse(ring, cx - 4.6f * u, cy - 4.6f * u, 9.2f * u, 9.2f * u);
                break;
            case PillIcon.Help:
                using (var bold = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold))
                    TextRenderer.DrawText(g, "?", bold, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                break;
            case PillIcon.Sliders:
                // Three sliders with their knobs in different places
                float[] knobs = [3, -4, 1];
                for (var i = 0; i < 3; i++)
                {
                    var y = cy + (i - 1) * 5 * u;
                    g.DrawLine(pen, cx - 8 * u, y, cx + 8 * u, y);
                    g.FillEllipse(fill, cx + knobs[i] * u - 2.5f * u, y - 2.5f * u, 5 * u, 5 * u);
                }
                break;
        }
    }

    /// <summary>Rounded rectangle; a split-button half keeps its joined side square, out to the edge</summary>
    GraphicsPath Shape(RectangleF r, float rad)
    {
        if (!FlatLeft && !FlatRight) return Theme.Round(r, rad);
        var p = new GraphicsPath();
        var d = rad * 2;
        if (FlatLeft)
        {
            p.AddLine(0, r.Top, r.Right - rad, r.Top);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddLine(r.Right - rad, r.Bottom, 0, r.Bottom);
        }
        else
        {
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddLine(r.Left + rad, r.Top, Width, r.Top);
            p.AddLine(Width, r.Bottom, r.Left + rad, r.Bottom);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        }
        p.CloseFigure();
        return p;
    }
}

/// <summary>Popup menu in Discord's colors</summary>
sealed class DarkMenu : ContextMenuStrip
{
    public DarkMenu()
    {
        Renderer = new Painter();
        BackColor = Theme.Bg3;
        ForeColor = Theme.Text;
        ShowImageMargin = false;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(4);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2; // DWMWCP_ROUND: Windows 11 rounds the popup's corners (ignored on Windows 10)
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
    }

    /// <summary>Opens just below a control, at least `width` wide, with its left edge (or right edge) lined up with it</summary>
    public void ShowUnder(Control anchor, int width, bool alignRight = false)
    {
        MinimumSize = new Size(width, 0);
        var x = alignRight ? width - Math.Max(width, GetPreferredSize(Size.Empty).Width) : 0;
        Show(anchor, new Point(x, anchor.Height + 4));
    }

    public ToolStripMenuItem Add(string text, string keys, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys == "" ? null : keys, ForeColor = Theme.Text, Padding = new Padding(6, 7, 6, 7) };
        item.Click += (_, _) => onClick();
        Items.Add(item);
        return item;
    }

    sealed class Painter : ToolStripProfessionalRenderer
    {
        public Painter() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Theme.Round(new RectangleF(0.5f, 0.5f, e.Item.Width - 1.5f, e.Item.Height - 1.5f), 5 * e.Item.Owner!.DeviceDpi / 96f);
            using var fill = new SolidBrush(Theme.Brand);
            e.Graphics.FillPath(fill, shape);
        }

        // The stock renderer sits the text low; centre it in the full height of the item
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var shortcut = e.Text != e.Item.Text;
            var r = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, e.Item.Selected ? Color.White : shortcut ? Theme.Muted : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | (shortcut ? TextFormatFlags.Right : TextFormatFlags.Left));
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Bg3;
        public override Color MenuBorder => Theme.Bg3;
        public override Color MenuItemBorder => Theme.Brand;
        public override Color MenuItemSelected => Theme.Brand;
        public override Color MenuItemSelectedGradientBegin => Theme.Brand;
        public override Color MenuItemSelectedGradientEnd => Theme.Brand;
        public override Color SeparatorDark => Theme.Secondary;
        public override Color SeparatorLight => Theme.Bg3;
    }
}

/// <summary>Thin progress bar</summary>
sealed class Bar : Control
{
    double value;
    public double Value { get => value; set { this.value = Math.Clamp(value, 0, 1); Invalidate(); } }

    public Bar() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, Height / 2f)) using (var b = new SolidBrush(Theme.Secondary)) g.FillPath(b, path);
        if (value > 0)
        {
            using var path = Theme.Round(new RectangleF(0, 0, Math.Max(Height, (float)((Width - 1) * value)), Height - 1), Height / 2f);
            using var b = new SolidBrush(Theme.Brand);
            g.FillPath(b, path);
        }
    }
}

/// <summary>
/// Thumbnail strip with a playhead. Dragging on the strip scrubs. The start and end are moved with the tabs
/// above it, and the bar between the tabs moves the whole selection. Keys live in MainForm.
/// </summary>
sealed class Timeline : Control
{
    const double MinClip = 0.1;
    enum DragKind { None, Start, End, Range, Seek }

    public double Duration = 1, Fps = 30;
    public double Start, End, Current;
    public Image?[] Thumbs = new Image?[16];
    /// <summary>Start or End changed</summary>
    public event Action? RangeChanged;
    /// <summary>The playhead moved; the form shows that frame</summary>
    public event Action<double>? Seeked;
    /// <summary>The strip was double-clicked at this time; the form plays from there, trimmed or not</summary>
    public event Action<double>? PlayRequested;

    DragKind drag;
    double grabOffset, length;

    public Timeline()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    /// <summary>Height of the row of tabs above the thumbnails</summary>
    int TabH => Px(14);
    int TabW => Px(16);
    /// <summary>The part of the video on screen (the timeline's zoom). End at or before start = all of it.</summary>
    public double ViewStart, ViewEnd;
    double From => ViewEnd > ViewStart ? ViewStart : 0;
    double Span => ViewEnd > ViewStart ? ViewEnd - ViewStart : Duration;
    /// <summary>Length of one frame at the chosen frame rate, in seconds. Frames are marked once zoomed in far enough.</summary>
    public double Grid;
    /// <summary>The stretch of video the thumbnails were made for (they're remade after zooming)</summary>
    public double ThumbStart, ThumbSpan = 1;
    /// <summary>Alt + mouse wheel: zoom by this factor around this time</summary>
    public event Action<double, double>? ZoomRequested;
    /// <summary>Mouse wheel: scroll by this fraction of what's on screen</summary>
    public event Action<double>? PanRequested;

    /// <summary>Empty strip at the left, so the timeline lines up with the tracks below and their buttons</summary>
    int G => Px(Tracks.Gutter);
    float X(double t) => G + (float)((t - From) / Span * (Width - G));
    double T(int x) => Math.Clamp(From + (x - G) / (double)Math.Max(1, Width - G) * Span, 0, Duration);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled) return;
        if (ModifierKeys.HasFlag(Keys.Alt)) ZoomRequested?.Invoke(e.Delta > 0 ? 0.75 : 1 / 0.75, T(e.X));
        else PanRequested?.Invoke(e.Delta > 0 ? -0.15 : 0.15);
    }

    public void Reset(double duration)
    {
        Duration = Math.Max(0.01, duration);
        Start = 0;
        End = Duration;
        Current = 0;
        (ViewStart, ViewEnd, ThumbStart, ThumbSpan) = (0, Duration, 0, Duration);
        Invalidate();
    }

    /// <summary>Moves the playhead without raising Seeked (used during playback)</summary>
    public void ShowTime(double t)
    {
        Current = Math.Clamp(t, 0, Duration);
        Invalidate();
    }

    void SetRange(double s, double e)
    {
        Start = s;
        End = e;
        Invalidate();
        RangeChanged?.Invoke();
    }

    public void Seek(double t)
    {
        Current = Math.Clamp(t, 0, Duration);
        Invalidate();
        Seeked?.Invoke(Current);
    }

    public void SetStart(double t) => SetRange(Math.Min(Math.Clamp(t, 0, Duration), End - MinClip), End);
    public void SetEnd(double t) => SetRange(Start, Math.Max(Math.Clamp(t, 0, Duration), Start + MinClip));

    DragKind HitTest(Point p)
    {
        if (p.Y >= TabH + 2) return DragKind.Seek;
        float s = X(Start), e = X(End);
        bool onStart = p.X >= s - 4 && p.X <= s + TabW + 4, onEnd = p.X >= e - TabW - 4 && p.X <= e + 4;
        // A very short selection puts the tabs on top of each other: take the nearer edge
        if (onStart && onEnd) return Math.Abs(p.X - s) <= Math.Abs(p.X - e) ? DragKind.Start : DragKind.End;
        if (onStart) return DragKind.Start;
        if (onEnd) return DragKind.End;
        return p.X > s && p.X < e ? DragKind.Range : DragKind.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        var t = T(e.X);
        drag = HitTest(e.Location);
        if (drag == DragKind.Range) { grabOffset = t - Start; length = End - Start; }
        if (drag == DragKind.Seek) Seek(t);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag == DragKind.None)
        {
            Cursor = HitTest(e.Location) switch { DragKind.Start or DragKind.End => Cursors.SizeWE, DragKind.Range => Cursors.SizeAll, DragKind.Seek => Cursors.Hand, _ => Cursors.Default };
            return;
        }
        var t = T(e.X);
        switch (drag)
        {
            // Normally the playhead follows the edge so you see the frame you're cutting on. With Shift it stays
            // where it is, and the edge snaps to it when close.
            case DragKind.Start when ModifierKeys.HasFlag(Keys.Shift): SetStart(SnapToPlayhead(t, e.X)); break;
            case DragKind.End when ModifierKeys.HasFlag(Keys.Shift): SetEnd(SnapToPlayhead(t, e.X)); break;
            case DragKind.Start: SetStart(t); Seek(Start); break;
            case DragKind.End: SetEnd(t); Seek(End); break;
            case DragKind.Range:
                var s = Math.Clamp(t - grabOffset, 0, Duration - length);
                SetRange(s, s + length);
                Seek(s);
                break;
            default: Seek(t); break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Enabled && e.Button == MouseButtons.Left && HitTest(e.Location) == DragKind.Seek) PlayRequested?.Invoke(T(e.X));
    }

    double SnapToPlayhead(double t, int x) => Math.Abs(x - X(Current)) <= Px(7) ? Current : t;

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = DragKind.None; }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    /// <summary>A tab: rounded on top, square where it joins the frame</summary>
    static GraphicsPath TabShape(int x, int w, int h, int r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, 0, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, 0, r * 2, r * 2, 270, 90);
        p.AddLine(x + w, h, x, h);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        int top = TabH, bodyH = Height - top;
        int left = G, bodyW = Width - left;
        using (var black = new SolidBrush(Color.Black)) g.FillRectangle(black, left, top, bodyW, bodyH);

        // Zoomed in, the start, end and playhead can be off either side: keep everything out of the button strip
        g.SetClip(new Rectangle(left, 0, bodyW, Height));

        // Thumbnails, cropped to fill each cell
        var n = Thumbs.Length;
        for (var i = 0; i < n; i++)
        {
            // Each thumbnail sits where its stretch of video is, so the strip stays right while zoomed thumbnails load
            float x0 = X(ThumbStart + i * ThumbSpan / n), x1 = X(ThumbStart + (i + 1) * ThumbSpan / n);
            if (x1 < left || x0 > Width) continue;
            var cell = new RectangleF(x0, top, x1 - x0 + 1, bodyH);
            if (Thumbs[i] is not { } img)
            {
                using var b = new SolidBrush(Color.FromArgb(i % 2 == 0 ? 18 : 26, Color.White));
                g.FillRectangle(b, cell);
                continue;
            }
            var scale = Math.Max(cell.Width / img.Width, cell.Height / img.Height);
            float sw = cell.Width / scale, sh = cell.Height / scale;
            g.DrawImage(img, cell, new RectangleF((img.Width - sw) / 2, (img.Height - sh) / 2, sw, sh), GraphicsUnit.Pixel);
        }

        Tracks.DrawFrames(g, Grid, From, Span, left, bodyW, top, Height, DeviceDpi, 70);

        // Whole pixels throughout, so the tabs and the frame meet without soft or missing pixels
        int s = (int)Math.Round(X(Start)), en = (int)Math.Round(X(End)), edge = Math.Max(2, Px(3)), tw = TabW;
        using (var dim = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        {
            g.FillRectangle(dim, left, top, Math.Max(0, s - left), bodyH);
            g.FillRectangle(dim, en, top, Math.Max(0, Width - en), bodyH);
        }
        using (var brand = new SolidBrush(Enabled ? Theme.Brand : Theme.Secondary))
        {
            // Frame around the selection
            g.FillRectangle(brand, s, top, en - s, edge);
            g.FillRectangle(brand, s, Height - edge, en - s, edge);
            g.FillRectangle(brand, s, top, edge, bodyH);
            g.FillRectangle(brand, en - edge, top, edge, bodyH);
            // Bar between the tabs (drag it to move the selection) and the two tabs
            g.FillRectangle(brand, s, top - edge, en - s, edge);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var x in new[] { s, en - tw })
                using (var tab = TabShape(x, tw, top, Px(4)))
                    g.FillPath(brand, tab);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }
        // Grip: a 3x3 grid of dots on each tab
        using (var dots = new SolidBrush(Color.FromArgb(235, Color.White)))
        {
            int dot = Px(2), gap = Px(2), grid = dot * 3 + gap * 2;
            foreach (var x in new[] { s, en - tw })
                for (var i = 0; i < 9; i++)
                    g.FillRectangle(dots, x + (tw - grid) / 2 + i % 3 * (dot + gap), (top - grid) / 2 + i / 3 * (dot + gap), dot, dot);
        }
        using (var head = new Pen(Color.White, 2))
            g.DrawLine(head, X(Current), top, X(Current), Height);
        if (Focused)
            using (var focus = new Pen(Color.FromArgb(120, Theme.Brand), 2))
                g.DrawRectangle(focus, left + 1, top + 1, bodyW - 2, bodyH - 2);
    }
}

/// <summary>
/// Chip-shaped frame for a text box, so it sits in a row of chips without looking out of place.
/// Sizes itself from DeviceDpi like Pill does, so it's always exactly as tall as the chips next to it.
/// </summary>
sealed class RoundBox : Panel
{
    public RoundBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg2;
        Fit();
    }

    // Sizes in 96-dpi pixels. Width 0 = the layout decides the width (docked or anchored).
    readonly int logicalWidth = 96, logicalHeight = 28;
    public int LogicalWidth { get => logicalWidth; init { logicalWidth = value; Fit(); } }
    public int LogicalHeight { get => logicalHeight; init { logicalHeight = value; Fit(); } }
    /// <summary>Corner radius; 0 = fully rounded ends, like a chip</summary>
    public int Radius { get; init; }
    /// <summary>Filled with this colour (a text field); otherwise just an outline</summary>
    public Color? Fill { get; init; }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Size = new Size(logicalWidth > 0 ? Px(logicalWidth) : Width, Px(logicalHeight));
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

    // Text box centred vertically, clear of the rounded ends
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Controls.Count == 0) return;
        var c = Controls[0];
        c.Bounds = new Rectangle(Px(12), (Height - c.Height) / 2, Width - Px(24), c.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius > 0 ? Px(Radius) : Height / 2f);
        if (Fill is { } fill) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        var focused = Controls.Count > 0 && Controls[0].Focused;
        using var pen = new Pen(focused ? Theme.Brand : Theme.Secondary);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// One audio track drawn as a waveform with its volume line on top. Dragging up or down changes the volume.
/// To its left are two buttons: mute, and add a point at the playhead so the volume can change over time
/// (Ctrl+click adds one under the mouse). Right-clicking a point removes it. Holding Shift while dragging a
/// point keeps its value, and a level stretch between two points at the same value drags as one.
/// </summary>
sealed class VolumeLane : Control
{
    static readonly Font Small = new("Segoe UI", 8f);

    /// <summary>Loudest sample in each slice of the track, 0..1; null until measured</summary>
    public float[]? Peaks;
    public double Duration = 1, Start, End = 1, Current;
    /// <summary>False for a muted track (drawn grey, left out of the mix)</summary>
    public bool Active = true;
    /// <summary>Background of the lane. The result (master) lane has its own, so it stands apart from the tracks.</summary>
    public Color Back = Theme.Bg3;
    /// <summary>The track's name, shown at the top left of the lane</summary>
    public string Label = "";
    /// <summary>Nothing audible in this track; said on the badge</summary>
    public bool Silent;
    /// <summary>The mute button was clicked: Active has flipped</summary>
    public event Action? MuteToggled;
    /// <summary>Volume while there are no points: 1 = unchanged, 0 = silent, 2 = double</summary>
    public double Volume = 1;
    /// <summary>Volume points in time order; empty = the same volume throughout</summary>
    public readonly List<(double T, double V)> Keys = [];
    /// <summary>The volume changed and the mouse was let go</summary>
    public event Action? Committed;
    /// <summary>Right-clicked somewhere that isn't a point</summary>
    public event Action<Point>? MenuRequested;
    /// <summary>Shift + wheel: make the tracks taller (+1) or shorter (-1)</summary>
    public event Action<int>? HeightRequested;
    /// <summary>Alt + wheel: zoom the timeline by this factor around this time</summary>
    public event Action<double, double>? ZoomRequested;
    public IEnumerable<double> KeyTimes => Keys.Select(k => k.T);

    /// <summary>Length of one frame at the chosen frame rate, in seconds. Frames are marked once zoomed in far enough.</summary>
    public double Grid;
    /// <summary>Where a point goes when it's moved or Ctrl+clicked in: onto a frame of the chosen frame rate</summary>
    double OnFrame(double t) => Grid > 0 ? Math.Clamp(Math.Round(t / Grid) * Grid, 0, Duration) : t;
    int grabX;
    double grabT;
    int rowHeight = 56;
    /// <summary>Height in 96-dpi pixels; every keyframable track shares it</summary>
    public int RowHeight { get => rowHeight; set { rowHeight = value; Fit(); } }
    /// <summary>First of two neighbouring points being dragged together (a level stretch); -1 = none</summary>
    int dragStretch = -1;

    int dragKey = -1;
    bool dragFlat;
    int grabY;
    double grabVolume;
    int pixelWidth = 300;

    public VolumeLane()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.SizeNS;
    }

    /// <summary>Width in real pixels (the form decides it from the room it has); the height follows the display scaling like Pill</summary>
    public int PixelWidth { get => pixelWidth; set { pixelWidth = value; Fit(); } }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Size = new Size(pixelWidth, Px(rowHeight));
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }
    int Pad => Px(4);
    /// <summary>Room at the left for the buttons; the lane itself starts after it, in line with the timeline</summary>
    int G => Px(Tracks.Gutter);
    int BodyW => Math.Max(1, Width - G);
    /// <summary>The part of the video on screen (the timeline's zoom). End at or before start = all of it.</summary>
    public double ViewStart, ViewEnd;
    double From => ViewEnd > ViewStart ? ViewStart : 0;
    double Span => ViewEnd > ViewStart ? ViewEnd - ViewStart : Duration;
    float X(double t) => G + (float)((t - From) / Span * BodyW);
    double T(int x) => Math.Clamp(From + (x - G) / (double)BodyW * Span, 0, Duration);
    float Y(double v) => Pad + (float)((2 - v) / 2 * (Height - 2 * Pad));
    // Sticks to 100% when close, so "unchanged" is easy to hit
    static double Snap(double v) { v = Math.Clamp(v, 0, 2); return Math.Abs(v - 1) < 0.06 ? 1 : Math.Round(v, 2); }
    double V(int y) => Snap(2 - (y - Pad) / (double)Math.Max(1, Height - 2 * Pad) * 2);

    public double GainAt(double t)
    {
        if (Keys.Count == 0) return Volume;
        if (t <= Keys[0].T) return Keys[0].V;
        for (var i = 1; i < Keys.Count; i++)
            if (t < Keys[i].T)
            {
                var (a, b) = (Keys[i - 1], Keys[i]);
                return a.V + (b.V - a.V) * (t - a.T) / Math.Max(0.001, b.T - a.T);
            }
        return Keys[^1].V;
    }

    int HitKey(Point p)
    {
        for (var i = 0; i < Keys.Count; i++)
            if (Math.Abs(p.X - X(Keys[i].T)) <= Px(7) && Math.Abs(p.Y - Y(Keys[i].V)) <= Px(9)) return i;
        return -1;
    }

    string BadgeText => Label + (Silent ? " · silent" : "");
    Rectangle MuteButton => Tracks.Button(this, 0, 2);
    Rectangle AddButton => Tracks.Button(this, 1, 2);
    Rectangle Badge => new(G + Px(5), Px(4), Px(10) + TextRenderer.MeasureText(BadgeText, Small, Size.Empty, TextFormatFlags.NoPrefix).Width, Px(18));

    /// <summary>The level stretch under the mouse: two neighbouring points at the same value, with the line between them</summary>
    int HitStretch(Point p)
    {
        for (var i = 0; i + 1 < Keys.Count; i++)
            if (Math.Abs(Keys[i].V - Keys[i + 1].V) < 0.005 && p.X > X(Keys[i].T) + Px(7) && p.X < X(Keys[i + 1].T) - Px(7) && Math.Abs(p.Y - Y(Keys[i].V)) <= Px(6)) return i;
        return -1;
    }

    public void ClearKeys()
    {
        Keys.Clear();
        Invalidate();
        Committed?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift)) HeightRequested?.Invoke(Math.Sign(e.Delta));
        else if (ModifierKeys.HasFlag(System.Windows.Forms.Keys.Alt)) ZoomRequested?.Invoke(e.Delta > 0 ? 0.75 : 1 / 0.75, T(e.X));
        else { base.OnMouseWheel(e); return; } // on to the box, which scrolls
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    /// <summary>Adds a point on the line at a time (or finds the one already there); returns its index</summary>
    int AddKey(double t)
    {
        var near = Keys.FindIndex(k => Math.Abs(k.T - t) < 0.05);
        if (near >= 0) return near;
        var after = Keys.FindIndex(k => k.T > t);
        var at = after < 0 ? Keys.Count : after;
        Keys.Insert(at, (t, GainAt(t)));
        Invalidate();
        return at;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled) return;
        if (e.Button == MouseButtons.Left && AddButton.Contains(e.Location))
        {
            AddKey(Current);
            Committed?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Left && MuteButton.Contains(e.Location))
        {
            Active = !Active;
            Invalidate();
            MuteToggled?.Invoke();
            return;
        }
        if (e.X < G) return;
        var k = HitKey(e.Location);
        if (e.Button == MouseButtons.Right && k >= 0)
        {
            Keys.RemoveAt(k);
            Invalidate();
            Committed?.Invoke();
        }
        else if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(e.Location);
        else if (e.Button == MouseButtons.Left)
        {
            // Ctrl+click puts a new point under the mouse, already held so it can be dragged into place
            if (k < 0 && ModifierKeys.HasFlag(System.Windows.Forms.Keys.Control)) k = AddKey(OnFrame(T(e.X)));
            dragKey = k;
            grabX = e.X;
            grabT = k >= 0 ? Keys[k].T : 0;
            dragStretch = k < 0 ? HitStretch(e.Location) : -1;
            dragFlat = k < 0 && Keys.Count == 0;
            grabY = e.Y;
            grabVolume = dragStretch >= 0 ? Keys[dragStretch].V : Volume;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragKey >= 0)
        {
            // A point can't pass its neighbours
            var lo = dragKey > 0 ? Keys[dragKey - 1].T + 0.05 : 0;
            var hi = dragKey < Keys.Count - 1 ? Keys[dragKey + 1].T - 0.05 : Duration;
            // Shift: slide it in time only
            // A point can sit between frames (after the frame rate was lowered, say) and stays there while only its
            // value is dragged. Once it's moved sideways it lands on a frame.
            var t = Math.Abs(e.X - grabX) <= Px(3) ? grabT : OnFrame(T(e.X));
            Keys[dragKey] = (Math.Clamp(t, lo, Math.Max(lo, hi)), ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift) ? Keys[dragKey].V : V(e.Y));
            Invalidate();
        }
        else if (dragStretch >= 0)
        {
            var v = Snap(grabVolume + (grabY - e.Y) / (double)Math.Max(1, Height - 2 * Pad) * 2);
            Keys[dragStretch] = (Keys[dragStretch].T, v);
            Keys[dragStretch + 1] = (Keys[dragStretch + 1].T, v);
            Invalidate();
        }
        else if (dragFlat)
        {
            // Relative to where the drag began, so a plain click never jumps the volume
            Volume = Snap(grabVolume + (grabY - e.Y) / (double)Math.Max(1, Height - 2 * Pad) * 2);
            Invalidate();
        }
        else Cursor = e.X < G ? MuteButton.Contains(e.Location) || AddButton.Contains(e.Location) ? Cursors.Hand : Cursors.Default : HitKey(e.Location) >= 0 ? Cursors.SizeAll : Keys.Count == 0 || HitStretch(e.Location) >= 0 ? Cursors.SizeNS : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var changed = dragKey >= 0 || dragStretch >= 0 || (dragFlat && Volume != grabVolume);
        dragKey = dragStretch = -1;
        dragFlat = false;
        if (changed) Committed?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.Round(new RectangleF(G, 0, BodyW - 1, Height - 1), Px(6));
        using (var bg = new SolidBrush(Back)) g.FillPath(bg, shape);
        g.SetClip(shape);

        float mid = Height / 2f, half = Height / 2f - 2;
        if (Peaks is { Length: > 0 } peaks)
        {
            var loudest = Math.Max(peaks.Max(), 0.02f);
            using var inside = new Pen(Active ? Theme.Brand : Theme.Secondary);
            using var outside = new Pen(Color.FromArgb(90, Active ? Theme.Brand : Theme.Secondary));
            for (var x = 0; x < BodyW; x++)
            {
                var t = From + x / (double)BodyW * Span;
                // Half height at 100% so there's room to show a boost
                var h = (float)Math.Min(1, peaks[Math.Clamp((int)(t / Duration * peaks.Length), 0, peaks.Length - 1)] / loudest * 0.5 * GainAt(t)) * half;
                if (h >= 0.5f) g.DrawLine(t >= Start && t <= End ? inside : outside, G + x + 0.5f, mid - h, G + x + 0.5f, mid + h);
            }
        }

        Tracks.DrawFrames(g, Grid, From, Span, G, BodyW, 0, Height, DeviceDpi, 28);

        // Volume line and its points
        var line = Color.FromArgb(Active ? 235 : 110, Color.White);
        var pts = new List<PointF> { new(X(0), Y(GainAt(0))) };
        pts.AddRange(Keys.Select(k => new PointF(X(k.T), Y(k.V))));
        pts.Add(new PointF(X(Duration), Y(GainAt(Duration))));
        using (var pen = new Pen(line, 1.5f)) g.DrawLines(pen, pts.ToArray());
        using (var dot = new SolidBrush(line))
            foreach (var k in Keys) g.FillEllipse(dot, X(k.T) - Px(4), Y(k.V) - Px(4), Px(8), Px(8));

        using (var head = new Pen(Color.FromArgb(160, Color.White))) g.DrawLine(head, X(Current), 0, X(Current), Height);
        g.ResetClip();

        // Buttons in the gutter: mute (speaker, struck through when muted) and add a point
        var mute = MuteButton;
        Tracks.DrawButton(g, mute);
        var ink = Active ? Theme.Text : Theme.Muted;
        float s = Px(11), ix = mute.X + (mute.Width - s * 0.8f) / 2f, iy = mute.Y + (mute.Height - s) / 2f;
        using (var speaker = new SolidBrush(ink))
            g.FillPolygon(speaker, [new PointF(ix, iy + s * 0.3f), new PointF(ix + s * 0.35f, iy + s * 0.3f), new PointF(ix + s * 0.8f, iy), new PointF(ix + s * 0.8f, iy + s), new PointF(ix + s * 0.35f, iy + s * 0.7f), new PointF(ix, iy + s * 0.7f)]);
        if (!Active)
            using (var strike = new Pen(Theme.Danger, Math.Max(1.5f, Px(2)))) g.DrawLine(strike, ix - 2, iy + s, ix + s, iy);
        KeyButton.Draw(g, AddButton, Enabled);

        // The track's name on the lane
        var badge = Badge;
        using (var back = Theme.Round(badge, Px(4)))
        using (var fill = new SolidBrush(Color.FromArgb(215, Theme.Bg2))) g.FillPath(fill, back);
        TextRenderer.DrawText(g, BadgeText, Small, new Rectangle(badge.X + Px(5), badge.Y, badge.Width, badge.Height), ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var label = dragKey >= 0 ? $"{Keys[dragKey].V * 100:0}%" : Keys.Count == 0 ? $"{Volume * 100:0}%" : "";
        TextRenderer.DrawText(g, label, Small, new Rectangle(0, Px(2), Width - Px(6), Height), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// The video preview. With a box set, it's drawn over the frame (everything outside dimmed) and can be moved by
/// dragging inside it and resized by its corners. The box may hang off the picture: that part comes out black.
/// Moving: Ctrl snaps to the picture's edges, Shift keeps to one direction. Resizing: Shift works from the
/// centre instead of the opposite corner, Alt lets the shape change.
/// </summary>
sealed class CropView : Control
{
    enum Grab { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }

    Image? image;
    RectangleF? box;
    bool showResult;
    public Image? Image { get => image; set { image = value; Invalidate(); } }
    /// <summary>Width / height of the video, so the box maps to the picture and not the black bars</summary>
    public double FrameAspect = 16 / 9.0;
    /// <summary>The box as fractions of the frame (0..1 is on the picture); null = none</summary>
    public RectangleF? Box { get => box; set { box = value; Invalidate(); } }
    /// <summary>Width / height the box must keep, in those same fractions; 0 = any shape</summary>
    public double BoxAspect;
    /// <summary>Widest the box may be, as a fraction of the frame</summary>
    public float MaxBoxWidth = 1;
    /// <summary>Show what the video will look like (just the inside of the box, filling the view) instead of the box over the whole frame</summary>
    public bool ShowResult { get => showResult; set { showResult = value; Invalidate(); } }
    /// <summary>The box is being dragged to this</summary>
    public event Action<RectangleF>? BoxChanged;
    /// <summary>The drag ended</summary>
    public event Action? BoxCommitted;
    /// <summary>Mouse wheel: one frame forward (+1) or back (-1)</summary>
    public event Action<int>? Stepped;
    public event Action<Point>? MenuRequested;

    Grab grab;
    PointF grabAt;
    RectangleF grabBox;

    public CropView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Black;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    /// <summary>Where the picture sits inside the control (fitted, centred)</summary>
    RectangleF Picture
    {
        get
        {
            var scale = Math.Min(Width / FrameAspect, Height);
            float w = (float)(scale * FrameAspect), h = (float)scale;
            return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
        }
    }

    RectangleF ToScreen(RectangleF b) { var p = Picture; return new RectangleF(p.X + b.X * p.Width, p.Y + b.Y * p.Height, b.Width * p.Width, b.Height * p.Height); }
    PointF ToFrame(Point at) { var p = Picture; return new PointF((at.X - p.X) / p.Width, (at.Y - p.Y) / p.Height); }

    Grab HitTest(Point at)
    {
        if (box is not { } b || !Enabled || showResult) return Grab.None;
        var r = ToScreen(b);
        var reach = Px(10);
        bool left = Math.Abs(at.X - r.Left) <= reach, right = Math.Abs(at.X - r.Right) <= reach, top = Math.Abs(at.Y - r.Top) <= reach, bottom = Math.Abs(at.Y - r.Bottom) <= reach;
        if (top && left) return Grab.TopLeft;
        if (top && right) return Grab.TopRight;
        if (bottom && left) return Grab.BottomLeft;
        if (bottom && right) return Grab.BottomRight;
        return r.Contains(at) ? Grab.Move : Grab.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { MenuRequested?.Invoke(e.Location); return; }
        if (e.Button != MouseButtons.Left || box is not { } b) return;
        grab = HitTest(e.Location);
        grabAt = ToFrame(e.Location);
        grabBox = b;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (grab == Grab.None)
        {
            Cursor = HitTest(e.Location) switch { Grab.TopLeft or Grab.BottomRight => Cursors.SizeNWSE, Grab.TopRight or Grab.BottomLeft => Cursors.SizeNESW, Grab.Move => Cursors.SizeAll, _ => Cursors.Default };
            return;
        }
        var at = ToFrame(e.Location);
        var pic = Picture;
        bool shift = ModifierKeys.HasFlag(Keys.Shift), ctrl = ModifierKeys.HasFlag(Keys.Control), alt = ModifierKeys.HasFlag(Keys.Alt);
        if (grab == Grab.Move)
        {
            float dx = at.X - grabAt.X, dy = at.Y - grabAt.Y;
            // Shift: only along whichever way it's been dragged further, so it stays lined up with where it was
            if (shift) { if (Math.Abs(dx * pic.Width) >= Math.Abs(dy * pic.Height)) dy = 0; else dx = 0; }
            // It can leave the picture, as far as its centre reaching the edge
            var x = Math.Clamp(grabBox.X + dx, -grabBox.Width / 2, 1 - grabBox.Width / 2);
            var y = Math.Clamp(grabBox.Y + dy, -grabBox.Height / 2, 1 - grabBox.Height / 2);
            if (ctrl)
            {
                // Ctrl: stick to the picture's edges when close
                float nearX = Px(12) / pic.Width, nearY = Px(12) / pic.Height;
                if (Math.Abs(x) < nearX) x = 0; else if (Math.Abs(x + grabBox.Width - 1) < nearX) x = 1 - grabBox.Width;
                if (Math.Abs(y) < nearY) y = 0; else if (Math.Abs(y + grabBox.Height - 1) < nearY) y = 1 - grabBox.Height;
            }
            BoxChanged?.Invoke(new RectangleF(x, y, grabBox.Width, grabBox.Height));
            return;
        }
        // Resizing. Alt lets the shape change; otherwise it keeps its shape.
        var locked = BoxAspect > 0 && !alt;
        float maxW = locked ? MaxBoxWidth : 1, maxH = locked ? (float)(MaxBoxWidth / BoxAspect) : 1;
        bool fromLeft = grab is Grab.TopLeft or Grab.BottomLeft, fromTop = grab is Grab.TopLeft or Grab.TopRight;
        float w, h;
        if (shift)
        {
            // Shift: grow and shrink around the centre
            float cx = grabBox.X + grabBox.Width / 2, cy = grabBox.Y + grabBox.Height / 2;
            w = Math.Clamp(2 * Math.Abs(at.X - cx), 0.05f, maxW);
            h = Math.Clamp(2 * Math.Abs(at.Y - cy), 0.05f, maxH);
            if (locked) { w = (float)Math.Min(Math.Max(w, h * BoxAspect), maxW); h = (float)(w / BoxAspect); }
            BoxChanged?.Invoke(new RectangleF(cx - w / 2, cy - h / 2, w, h));
            return;
        }
        // Otherwise the opposite corner stays put
        float ax = fromLeft ? grabBox.Right : grabBox.Left, ay = fromTop ? grabBox.Bottom : grabBox.Top;
        w = Math.Clamp(fromLeft ? ax - at.X : at.X - ax, 0.05f, maxW);
        h = Math.Clamp(fromTop ? ay - at.Y : at.Y - ay, 0.05f, maxH);
        // Keeping the shape: follow whichever direction was dragged further
        if (locked) { w = (float)Math.Min(Math.Max(w, h * BoxAspect), maxW); h = (float)(w / BoxAspect); }
        BoxChanged?.Invoke(new RectangleF(fromLeft ? ax - w : ax, fromTop ? ay - h : ay, w, h));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (grab == Grab.None) return;
        grab = Grab.None;
        BoxCommitted?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Stepped?.Invoke(e.Delta > 0 ? -1 : 1);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.Bilinear;
        if (showResult && image != null && box is { } shown)
        {
            // The inside of the box, as large as fits. Whatever part of it is off the picture stays black.
            var shape = shown.Width / shown.Height * FrameAspect;
            var fit = Math.Min(Width / shape, Height);
            float w = (float)(fit * shape), h = (float)fit, left = (Width - w) / 2, top = (Height - h) / 2;
            var on = RectangleF.Intersect(shown, new RectangleF(0, 0, 1, 1));
            if (on.Width > 0 && on.Height > 0)
                g.DrawImage(image,
                    new RectangleF(left + (on.X - shown.X) / shown.Width * w, top + (on.Y - shown.Y) / shown.Height * h, on.Width / shown.Width * w, on.Height / shown.Height * h),
                    new RectangleF(on.X * image.Width, on.Y * image.Height, on.Width * image.Width, on.Height * image.Height), GraphicsUnit.Pixel);
            return;
        }
        var pic = Picture;
        if (image != null) g.DrawImage(image, pic);
        if (box is not { } b) return;
        var r = Rectangle.Round(ToScreen(b));
        // Dim everything outside the box
        g.ExcludeClip(r);
        using (var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(dim, ClientRectangle);
        g.ResetClip();
        using (var frame = new Pen(Color.White, Math.Max(1, Px(2)))) g.DrawRectangle(frame, r);
        var hs = Px(9);
        foreach (var c in new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom) })
            g.FillRectangle(Brushes.White, c.X - hs / 2, c.Y - hs / 2, hs, hs);
    }
}

/// <summary>The strip at the left of the timeline and every track, holding each track's buttons</summary>
static class Tracks
{
    /// <summary>Width of the strip, in 96-dpi pixels: one button and a gap before the track</summary>
    public const int Gutter = 30;

    /// <summary>Where button number `slot` of a track's `count` goes: stacked one above the other, centred on the track's height</summary>
    public static Rectangle Button(Control track, int slot, int count)
    {
        int Px(int v) => (int)Math.Round(v * track.DeviceDpi / 96f);
        int size = Px(24), gap = Px(4), top = (track.Height - (count * size + (count - 1) * gap)) / 2;
        return new Rectangle(0, top + slot * (size + gap), size, size);
    }

    /// <summary>A faint line at the start of every frame, once they're far enough apart to tell from one another</summary>
    public static void DrawFrames(Graphics g, double frame, double from, double span, int left, int width, int top, int bottom, int dpi, int strength)
    {
        if (frame <= 0 || span <= 0 || frame / span * width < 6 * dpi / 96f) return;
        using var pen = new Pen(Color.FromArgb(strength, Color.White));
        for (var k = Math.Ceiling(from / frame); k * frame <= from + span; k++)
        {
            var x = left + (float)((k * frame - from) / span * width);
            g.DrawLine(pen, x, top, x, bottom);
        }
    }

    public static void DrawButton(Graphics g, Rectangle r)
    {
        using var back = Theme.Round(r, r.Height / 4f);
        using var fill = new SolidBrush(Theme.Bg3);
        g.FillPath(fill, back);
    }
}

/// <summary>The "add a key point at the playhead" button each track has</summary>
static class KeyButton
{
    public static void Draw(Graphics g, Rectangle r, bool enabled)
    {
        Tracks.DrawButton(g, r);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = r.Height * 0.25f;
        using var ink = new SolidBrush(enabled ? Theme.Text : Theme.Muted);
        g.FillPolygon(ink, [new PointF(cx, cy - d), new PointF(cx + d, cy), new PointF(cx, cy + d), new PointF(cx - d, cy)]);
    }
}

/// <summary>
/// Track for the picture's position and scale. Each key point is a diamond, and the line through them shows the
/// value (for scale, higher = closer in). Ctrl+click, or the button at the start, adds a point; dragging moves it in time,
/// right-click removes it, clicking jumps to it. What a point holds is set by the box on the preview.
/// </summary>
sealed class KeyLane : Control
{
    static readonly Font SmallFont = new("Segoe UI", 8f);

    public double Duration = 1, Current;
    /// <summary>Times of the key points, in order</summary>
    public IReadOnlyList<double> Keys = [];
    /// <summary>Height of the line at each key point, 0 (bottom) to 1</summary>
    public IReadOnlyList<double> Levels = [];
    /// <summary>A second, fainter line (position has two values: across and down); empty = none</summary>
    public IReadOnlyList<double> Levels2 = [];
    public string Label = "Position & scale";
    public Color Back = Theme.Bg3;
    /// <summary>Right-clicked somewhere that isn't a key point</summary>
    public event Action<Point>? MenuRequested;
    /// <summary>Shift + wheel: make the tracks taller (+1) or shorter (-1)</summary>
    public event Action<int>? HeightRequested;
    /// <summary>Alt + wheel: zoom the timeline by this factor around this time</summary>
    public event Action<double, double>? ZoomRequested;
    public IEnumerable<double> KeyTimes => Keys;

    /// <summary>Length of one frame at the chosen frame rate, in seconds. Frames are marked once zoomed in far enough.</summary>
    public double Grid;
    double OnFrame(double t) => Grid > 0 ? Math.Clamp(Math.Round(t / Grid) * Grid, 0, Duration) : t;
    int rowHeight = 56;
    /// <summary>Height in 96-dpi pixels; every keyframable track shares it</summary>
    public int RowHeight { get => rowHeight; set { rowHeight = value; Fit(); } }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift)) HeightRequested?.Invoke(Math.Sign(e.Delta));
        else if (ModifierKeys.HasFlag(System.Windows.Forms.Keys.Alt)) ZoomRequested?.Invoke(e.Delta > 0 ? 0.75 : 1 / 0.75, T(e.X));
        else { base.OnMouseWheel(e); return; } // on to the box, which scrolls
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }
    /// <summary>A point wanted at this time</summary>
    public event Action<double>? AddRequested;
    public event Action<int>? RemoveRequested, Picked;
    public event Action<int, double>? MoveRequested;

    int dragKey = -1;
    bool moved;

    public KeyLane()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Height = Px(rowHeight);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

    Rectangle AddButton => Tracks.Button(this, 0, 1);
    int G => Px(Tracks.Gutter);
    int BodyW => Math.Max(1, Width - G);
    /// <summary>The part of the video on screen (the timeline's zoom). End at or before start = all of it.</summary>
    public double ViewStart, ViewEnd;
    double From => ViewEnd > ViewStart ? ViewStart : 0;
    double Span => ViewEnd > ViewStart ? ViewEnd - ViewStart : Duration;
    float X(double t) => G + (float)((t - From) / Span * BodyW);
    double T(int x) => Math.Clamp(From + (x - G) / (double)BodyW * Span, 0, Duration);
    float Y(double level) => Height - Px(9) - (float)(Math.Clamp(level, 0, 1) * (Height - Px(18)));
    double Level(int i) => i < Levels.Count ? Levels[i] : 0;

    int HitKey(int x)
    {
        for (var i = 0; i < Keys.Count; i++) if (Math.Abs(x - X(Keys[i])) <= Px(8)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled) return;
        if (e.Button == MouseButtons.Left && AddButton.Contains(e.Location)) { AddRequested?.Invoke(Current); return; }
        if (e.X < G) return;
        var k = HitKey(e.X);
        if (e.Button == MouseButtons.Right && k >= 0) RemoveRequested?.Invoke(k);
        else if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(e.Location);
        else if (e.Button == MouseButtons.Left && k < 0 && ModifierKeys.HasFlag(System.Windows.Forms.Keys.Control)) AddRequested?.Invoke(OnFrame(T(e.X)));
        else if (e.Button == MouseButtons.Left) { dragKey = k; moved = false; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragKey < 0) { Cursor = AddButton.Contains(e.Location) ? Cursors.Hand : HitKey(e.X) >= 0 ? Cursors.SizeWE : Cursors.Default; return; }
        moved = true;
        // A moved point lands on a frame of the chosen frame rate
        MoveRequested?.Invoke(dragKey, OnFrame(T(e.X)));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragKey >= 0 && !moved) Picked?.Invoke(dragKey);
        dragKey = -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = Theme.Round(new RectangleF(G, 0, BodyW - 1, Height - 1), Px(6));
        using (var bg = new SolidBrush(Back)) g.FillPath(bg, track);
        g.SetClip(track);

        Tracks.DrawFrames(g, Grid, From, Span, G, BodyW, 0, Height, DeviceDpi, 28);
        if (Keys.Count > 0 && Levels2.Count == Keys.Count)
        {
            var second = new List<PointF> { new(X(0), Y(Levels2[0])) };
            for (var i = 0; i < Keys.Count; i++) second.Add(new PointF(X(Keys[i]), Y(Levels2[i])));
            second.Add(new PointF(X(Duration), Y(Levels2[^1])));
            using var faint = new Pen(Color.FromArgb(110, Theme.Brand), Math.Max(1f, Px(1)));
            g.DrawLines(faint, second.ToArray());
        }
        // The value over time: flat before the first point and after the last
        if (Keys.Count > 0)
        {
            var pts = new List<PointF> { new(X(0), Y(Level(0))) };
            for (var i = 0; i < Keys.Count; i++) pts.Add(new PointF(X(Keys[i]), Y(Level(i))));
            pts.Add(new PointF(X(Duration), Y(Level(Keys.Count - 1))));
            using var line = new Pen(Color.FromArgb(200, Theme.Brand), Math.Max(1.5f, Px(2)));
            g.DrawLines(line, pts.ToArray());
        }
        using (var head = new Pen(Color.FromArgb(160, Color.White))) g.DrawLine(head, X(Current), 0, X(Current), Height);
        float d = Px(6);
        for (var i = 0; i < Keys.Count; i++)
        {
            float x = X(Keys[i]), y = Y(Level(i));
            // White when the playhead is on it: that's the point the box on the preview edits
            using var fill = new SolidBrush(Math.Abs(Keys[i] - Current) < 0.02 ? Color.White : Theme.Brand);
            g.FillPolygon(fill, [new PointF(x, y - d), new PointF(x + d, y), new PointF(x, y + d), new PointF(x - d, y)]);
        }
        g.ResetClip();

        KeyButton.Draw(g, AddButton, Enabled);
        var name = Label;
        var badge = new Rectangle(G + Px(5), Px(4), Px(10) + TextRenderer.MeasureText(name, SmallFont, Size.Empty, TextFormatFlags.NoPrefix).Width, Px(18));
        using (var back = Theme.Round(badge, Px(4)))
        using (var fill = new SolidBrush(Color.FromArgb(215, Theme.Bg2))) g.FillPath(fill, back);
        TextRenderer.DrawText(g, name, SmallFont, new Rectangle(badge.X + Px(5), badge.Y, badge.Width, badge.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// The whole video as a thin bar, with a window on it marking the part the timeline and tracks are showing.
/// Drag the window to scroll, drag either end to zoom, double-click to see everything again.
/// </summary>
sealed class ZoomBar : Control
{
    enum Grab { None, Left, Right, Move }

    public double Duration = 1, ViewStart, ViewEnd = 1, Current, TrimStart, TrimEnd = 1;
    /// <summary>The window was dragged to this start and end</summary>
    public event Action<double, double>? ViewChanged;

    Grab grab;
    double grabOffset, grabSpan;

    public ZoomBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Height = Px(16);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

    int G => Px(Tracks.Gutter);
    int BodyW => Math.Max(1, Width - G);
    float X(double t) => G + (float)(t / Duration * BodyW);
    double T(int x) => Math.Clamp((x - G) / (double)BodyW * Duration, 0, Duration);
    /// <summary>The closest it zooms: the shortest stretch of video that can fill the timeline</summary>
    public double MinSpan = 1;

    Grab HitTest(int x)
    {
        float a = X(ViewStart), b = X(ViewEnd);
        var reach = Px(7);
        if (Math.Abs(x - a) <= reach && x < (a + b) / 2) return Grab.Left;
        if (Math.Abs(x - b) <= reach) return Grab.Right;
        return x > a && x < b ? Grab.Move : Grab.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        grab = HitTest(e.X);
        grabSpan = ViewEnd - ViewStart;
        if (grab == Grab.None)
        {
            // Click outside the window: bring it there and keep hold of it
            var centre = T(e.X);
            ViewChanged?.Invoke(centre - grabSpan / 2, centre + grabSpan / 2);
            grab = Grab.Move;
        }
        grabOffset = T(e.X) - ViewStart;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (grab == Grab.None)
        {
            Cursor = HitTest(e.X) switch { Grab.Left or Grab.Right => Cursors.SizeWE, Grab.Move => Cursors.Hand, _ => Cursors.Default };
            return;
        }
        var t = T(e.X);
        switch (grab)
        {
            case Grab.Left: ViewChanged?.Invoke(Math.Min(t, ViewEnd - MinSpan), ViewEnd); break;
            case Grab.Right: ViewChanged?.Invoke(ViewStart, Math.Max(t, ViewStart + MinSpan)); break;
            default: ViewChanged?.Invoke(t - grabOffset, t - grabOffset + grabSpan); break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); grab = Grab.None; }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Enabled && e.Button == MouseButtons.Left) ViewChanged?.Invoke(0, Duration);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = Height - 1;
        using var track = Theme.Round(new RectangleF(G, 0, BodyW - 1, h), h / 2);
        using (var bg = new SolidBrush(Theme.Bg3)) g.FillPath(bg, track);
        g.SetClip(track);
        // The trimmed part, faintly, so you can see where the clip sits in the whole video
        using (var trim = new SolidBrush(Color.FromArgb(70, Theme.Brand))) g.FillRectangle(trim, X(TrimStart), 0, Math.Max(1, X(TrimEnd) - X(TrimStart)), Height);
        g.ResetClip();

        // The window: what the timeline is showing
        float a = X(ViewStart), b = Math.Max(X(ViewEnd), a + Px(12));
        using (var window = Theme.Round(new RectangleF(a, 0, Math.Min(b, Width - 1) - a, h), h / 2))
        {
            using var fill = new SolidBrush(Color.FromArgb(Enabled ? 150 : 80, Theme.Secondary));
            using var edge = new Pen(Enabled ? Theme.SecondaryHover : Theme.Secondary);
            g.FillPath(fill, window);
            g.DrawPath(edge, window);
        }
        using (var grip = new Pen(Color.FromArgb(200, Color.White), Math.Max(1, Px(2))))
            foreach (var x in new[] { a + Px(5), Math.Min(b, Width - 1) - Px(5) })
                g.DrawLine(grip, x, Px(4), x, Height - Px(5));
        using (var head = new Pen(Color.White, Math.Max(1, Px(2)))) g.DrawLine(head, X(Current), 0, X(Current), Height);
    }
}

/// <summary>
/// A box of tracks that scrolls when there are more than fit. It scrolls by moving its content, with a slim bar
/// drawn over the right edge, because a standard scroll bar would take width and the tracks would stop lining up
/// with the timeline.
/// </summary>
sealed class TrackScroller : Panel
{
    public readonly FlowLayoutPanel Content = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Location = Point.Empty };
    readonly Control thumb = new Thumb();
    int offset, grabY, grabOffset;
    bool dragging;

    public TrackScroller()
    {
        Controls.Add(thumb);
        Controls.Add(Content);
        Content.SizeChanged += (_, _) => ScrollTo(offset);
        thumb.MouseDown += (_, e) => { dragging = e.Button == MouseButtons.Left; grabY = Cursor.Position.Y; grabOffset = offset; };
        thumb.MouseMove += (_, _) => { if (dragging) ScrollTo(grabOffset + (int)((Cursor.Position.Y - grabY) * (Content.Height / (double)Math.Max(1, Height)))); };
        thumb.MouseUp += (_, _) => dragging = false;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    bool scrolls = true;
    /// <summary>Off when the box is sized to hold everything in it, so a pixel of rounding never brings up the bar</summary>
    public bool Scrolls { get => scrolls; set { scrolls = value; ScrollTo(offset); } }
    int Hidden => scrolls && Content.Height - Height > 2 ? Content.Height - Height : 0;

    public void ScrollTo(int to)
    {
        offset = Math.Clamp(to, 0, Hidden);
        Content.Top = -offset;
        // The bar: as long as the share of the content in view, placed by how far down it's scrolled
        thumb.Visible = Hidden > 0;
        if (Hidden > 0)
        {
            var length = Math.Max(Px(20), (int)((long)Height * Height / Content.Height));
            thumb.Bounds = new Rectangle(Width - Px(6), (int)((long)(Height - length) * offset / Hidden), Px(5), length);
            thumb.BringToFront();
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(offset); }

    // The wheel reaches here from whichever track the mouse is over
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Hidden == 0) { base.OnMouseWheel(e); return; }
        ScrollTo(offset - Math.Sign(e.Delta) * Px(48));
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    sealed class Thumb : Control
    {
        public Thumb()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg3);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Width / 2f);
            using var fill = new SolidBrush(Theme.SecondaryHover);
            e.Graphics.FillPath(fill, shape);
        }
    }
}

/// <summary>A thin bar to drag up or down, for making the box above it taller or shorter</summary>
sealed class SizeGrip : Control
{
    public event Action? DragStarted;
    /// <summary>Dragged this many pixels down from where it started</summary>
    public event Action<int>? Dragged;
    bool dragging;
    int grabY;

    public SizeGrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.SizeNS;
    }

    int Px(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    void Fit() => Height = Px(10);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Fit(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        grabY = Cursor.Position.Y;
        DragStarted?.Invoke();
    }

    // Screen position, not the mouse event's: the grip itself moves as the box changes height
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Dragged?.Invoke(Cursor.Position.Y - grabY); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Px(36), h = Math.Max(2, Px(3));
        using var shape = Theme.Round(new RectangleF((Width - w) / 2, (Height - h) / 2, w, h), h / 2);
        using var fill = new SolidBrush(Theme.Secondary);
        g.FillPath(fill, shape);
    }
}
