namespace CompressForDiscord;

/// <summary>
/// How the value moves next to a key point. Each stretch between two points takes the way out of the first and
/// the way into the second: Smooth slows down on both sides of its point, Ease in only arriving at it, Ease out
/// only leaving it. Hold keeps the point's value until the next one, then jumps.
/// </summary>
enum Ease { Linear, Smooth, EaseIn, EaseOut, Hold }

static class Interp
{
    public static readonly (Ease Ease, string Name)[] Kinds =
        [(Ease.Linear, "Linear"), (Ease.Smooth, "Smooth"), (Ease.EaseIn, "Ease in"), (Ease.EaseOut, "Ease out"), (Ease.Hold, "Hold")];

    static bool SlowLeaving(Ease e) => e is Ease.Smooth or Ease.EaseOut;
    static bool SlowArriving(Ease e) => e is Ease.Smooth or Ease.EaseIn;

    /// <summary>How far along (0..1) the value is, a fraction f of the way in time from a point to the next</summary>
    public static double Curve(Ease from, Ease to, double f)
    {
        if (from == Ease.Hold) return 0;
        return (SlowLeaving(from), SlowArriving(to)) switch
        {
            (true, true) => f * f * (3 - 2 * f),
            (true, false) => f * f,
            (false, true) => f * (2 - f),
            _ => f,
        };
    }

    /// <summary>The same curve as an ffmpeg expression of u (0..1); null for Hold</summary>
    public static string? CurveExpr(Ease from, Ease to, string u)
    {
        if (from == Ease.Hold) return null;
        return (SlowLeaving(from), SlowArriving(to)) switch
        {
            (true, true) => $"{u}*{u}*(3-2*{u})",
            (true, false) => $"{u}*{u}",
            (false, true) => $"{u}*(2-{u})",
            _ => u,
        };
    }

    /// <summary>The value at a time, from points in time order (holding before the first and after the last)</summary>
    public static double At(IReadOnlyList<(double T, double V, Ease E)> k, double t)
    {
        if (k.Count == 0) return 0;
        if (t <= k[0].T) return k[0].V;
        for (var i = 1; i < k.Count; i++)
            if (t < k[i].T)
            {
                var (a, b) = (k[i - 1], k[i]);
                return a.V + (b.V - a.V) * Curve(a.E, b.E, (t - a.T) / Math.Max(0.001, b.T - a.T));
            }
        return k[^1].V;
    }
}

/// <summary>What a key-point track on screen needs from the values behind it, whatever they are</summary>
interface IKeyTrack
{
    int Count { get; }
    double TimeAt(int i);
    Ease EaseAt(int i);
    void SetEase(int i, Ease ease);
    /// <summary>Adds a point at a time, holding whatever the value already is there. False if there's one there already.</summary>
    bool Add(double t);
    void Move(int i, double t, double duration);
    void RemoveAt(int i);
}

/// <summary>
/// A value that changes over time through key points, moving from each to the next the way their Ease says and
/// holding before the first and after the last. One point means a fixed value.
/// </summary>
/// <param name="mix">the value a fraction of the way from one point's value to the next's</param>
sealed class Keyed<V>(Func<V, V, double, V> mix) : IKeyTrack
{
    /// <summary>The points, in time order; never empty once Reset has been called</summary>
    public readonly List<(double T, V Value, Ease E)> Keys = [];
    public int Count => Keys.Count;
    public double TimeAt(int i) => Keys[i].T;
    public Ease EaseAt(int i) => Keys[i].E;
    public void SetEase(int i, Ease ease) => Keys[i] = (Keys[i].T, Keys[i].Value, ease);

    public void Reset(V value)
    {
        Keys.Clear();
        Keys.Add((0, value, Ease.Linear));
    }

    public V At(double t)
    {
        if (Keys.Count == 1 || t <= Keys[0].T) return Keys[0].Value;
        for (var i = 1; i < Keys.Count; i++)
            if (t < Keys[i].T)
            {
                var (a, b) = (Keys[i - 1], Keys[i]);
                return mix(a.Value, b.Value, Interp.Curve(a.E, b.E, (t - a.T) / Math.Max(0.001, b.T - a.T)));
            }
        return Keys[^1].Value;
    }

    /// <summary>
    /// Changes the value at a time. With one point that's the fixed value, so it just changes. With several, it
    /// changes the point at that time, or adds one there.
    /// </summary>
    public void Set(double t, V value)
    {
        var i = Keys.Count == 1 ? 0 : Keys.FindIndex(k => Math.Abs(k.T - t) < 0.02);
        if (i >= 0) Keys[i] = (Keys[i].T, value, Keys[i].E);
        else Insert(t, value, Ease.Linear);
    }

    public bool Add(double t)
    {
        if (Keys.Any(k => Math.Abs(k.T - t) < 0.05)) return false;
        Insert(t, At(t), Ease.Linear);
        return true;
    }

    void Insert(double t, V value, Ease ease)
    {
        var after = Keys.FindIndex(k => k.T > t);
        Keys.Insert(after < 0 ? Keys.Count : after, (t, value, ease));
    }

    /// <summary>Moves a point in time; it can't pass its neighbours</summary>
    public void Move(int i, double t, double duration)
    {
        var lo = i > 0 ? Keys[i - 1].T + 0.05 : 0;
        var hi = i < Keys.Count - 1 ? Keys[i + 1].T - 0.05 : duration;
        Keys[i] = (Math.Clamp(t, lo, Math.Max(lo, hi)), Keys[i].Value, Keys[i].E);
    }

    /// <summary>Removes a point; the last one stays, as the fixed value</summary>
    public void RemoveAt(int i)
    {
        if (Keys.Count > 1) Keys.RemoveAt(i);
    }

    /// <summary>
    /// Rebuilds the points at these times with the values the track already has there. A point it already had
    /// keeps its ease; the rest are linear.
    /// </summary>
    public void Resample(IReadOnlyList<double> times)
    {
        var fresh = times.Select(t => (t, At(t), Keys.FirstOrDefault(k => Math.Abs(k.T - t) < 1e-9, (0, default!, Ease.Linear)).E)).ToList();
        Keys.Clear();
        Keys.AddRange(fresh);
    }
}
