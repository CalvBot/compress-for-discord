namespace CompressForDiscord;

// Test code: only debug builds compile this folder (see the project file). What the tests in MainForm.Debug.cs
// need from the controls: where things are drawn, so they can be clicked and dragged with posted mouse messages.

sealed partial class Timeline
{
    /// <summary>As if the mouse were held down on the strip, so posted moves scrub</summary>
    public void DebugBeginSeekDrag() => drag = DragKind.Seek;
    public void DebugEndDrag() => drag = DragKind.None;
}

abstract partial class KeyTrackLane
{
    /// <summary>Where key point i is drawn</summary>
    public abstract PointF DebugKey(int i);
}

sealed partial class VolumeLane
{
    public override PointF DebugKey(int i) => new(X(Keys[i].T), Y(Keys[i].V));
}

sealed partial class KeyLane
{
    public override PointF DebugKey(int i) => new(X(Keys[i]), Y(Level(i)));
}

sealed partial class CropView
{
    /// <summary>The box in the control's pixels</summary>
    public RectangleF DebugBoxOnScreen => box is { } b ? ToScreen(b) : RectangleF.Empty;
}
