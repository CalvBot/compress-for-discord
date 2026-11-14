namespace CompressForDiscord;

/// <summary>What a key-point track on screen needs from the values behind it, whatever they are</summary>
interface IKeyTrack
{
    int Count { get; }
    double TimeAt(int i);
    /// <summary>Adds a point at a time, holding whatever the value already is there. False if there's one there already.</summary>
    bool Add(double t);
    void Move(int i, double t, double duration);
    void RemoveAt(int i);
}

/// <summary>
/// A value that changes over time through key points, moving in a straight line from each to the next and
/// holding before the first and after the last. One point means a fixed value.
/// </summary>
/// <param name="mix">the value a fraction of the way from one point's value to the next's</param>
sealed class Keyed<V>(Func<V, V, double, V> mix) : IKeyTrack
{
    /// <summary>The points, in time order; never empty once Reset has been called</summary>
    public readonly List<(double T, V Value)> Keys = [];
    public int Count => Keys.Count;
    public double TimeAt(int i) => Keys[i].T;

    public void Reset(V value)
    {
        Keys.Clear();
        Keys.Add((0, value));
    }

    public V At(double t)
    {
        if (Keys.Count == 1 || t <= Keys[0].T) return Keys[0].Value;
        for (var i = 1; i < Keys.Count; i++)
            if (t < Keys[i].T)
            {
                var (a, b) = (Keys[i - 1], Keys[i]);
                return mix(a.Value, b.Value, (t - a.T) / Math.Max(0.001, b.T - a.T));
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
        if (i >= 0) Keys[i] = (Keys[i].T, value);
        else Insert(t, value);
    }

    public bool Add(double t)
    {
        if (Keys.Any(k => Math.Abs(k.T - t) < 0.05)) return false;
        Insert(t, At(t));
        return true;
    }

    void Insert(double t, V value)
    {
        var after = Keys.FindIndex(k => k.T > t);
        Keys.Insert(after < 0 ? Keys.Count : after, (t, value));
    }

    /// <summary>Moves a point in time; it can't pass its neighbours</summary>
    public void Move(int i, double t, double duration)
    {
        var lo = i > 0 ? Keys[i - 1].T + 0.05 : 0;
        var hi = i < Keys.Count - 1 ? Keys[i + 1].T - 0.05 : duration;
        Keys[i] = (Math.Clamp(t, lo, Math.Max(lo, hi)), Keys[i].Value);
    }

    /// <summary>Removes a point; the last one stays, as the fixed value</summary>
    public void RemoveAt(int i)
    {
        if (Keys.Count > 1) Keys.RemoveAt(i);
    }

    /// <summary>Rebuilds the points at these times with the values the track already has there</summary>
    public void Resample(IReadOnlyList<double> times)
    {
        var fresh = times.Select(t => (t, At(t))).ToList();
        Keys.Clear();
        Keys.AddRange(fresh);
    }
}
