namespace AmperDock.Core;

public enum DockEdge { Bottom, Top, Left, Right, Float }

/// <summary>
/// Where a dock lives. Alignment is the dock's centre along its edge (0..1). Float docks are horizontal
/// and centred on (FloatX, FloatY) in screen pixels. MonitorKey is the monitor's device name; null = primary.
/// </summary>
public sealed record DockPlacement(DockEdge Edge = DockEdge.Bottom, string? MonitorKey = null, float Alignment = 0.5f,
    int FloatX = 0, int FloatY = 0)
{
    public static string ToDb(DockEdge e) => e.ToString().ToLowerInvariant();

    public static DockEdge FromDb(string? s) => s switch
    {
        "top" => DockEdge.Top, "left" => DockEdge.Left, "right" => DockEdge.Right, "float" => DockEdge.Float, _ => DockEdge.Bottom,
    };

    /// <summary>
    /// Where a dock dropped at <paramref name="x"/>,<paramref name="y"/> (screen px) should go: the nearest
    /// edge of that monitor's work area if within <paramref name="snap"/> px, otherwise floating right there.
    /// </summary>
    public static DockPlacement Snap(int x, int y, RectF work, float snap, string? monitorKey)
    {
        var d = new (DockEdge edge, float dist)[]
        {
            (DockEdge.Bottom, work.Bottom - y), (DockEdge.Top, y - work.Y),
            (DockEdge.Left, x - work.X), (DockEdge.Right, work.Right - x),
        }.MinBy(t => t.dist);

        if (d.dist > snap)
            return new DockPlacement(DockEdge.Float, monitorKey, 0.5f, x, y);
        var along = d.edge is DockEdge.Left or DockEdge.Right ? (y - work.Y) / work.H : (x - work.X) / work.W;
        return new DockPlacement(d.edge, monitorKey, Math.Clamp(along, 0, 1));
    }
}

/// <summary>
/// Maps between "dock space" and window pixels. Layout always happens in dock space: x runs along the
/// dock (0..Length), y runs across it, and the screen edge is at y = Thickness. Each edge is a flip
/// and/or a transpose of that, so one layout serves all four edges.
/// </summary>
public readonly record struct DockFrame(DockEdge Edge, float Length, float Thickness)
{
    public bool Vertical => Edge is DockEdge.Left or DockEdge.Right;

    public (int w, int h) WindowSize => Vertical
        ? ((int)MathF.Ceiling(Thickness), (int)MathF.Ceiling(Length))
        : ((int)MathF.Ceiling(Length), (int)MathF.Ceiling(Thickness));

    public RectF ToWindow(RectF r) => Edge switch
    {
        DockEdge.Top => new RectF(r.X, Thickness - r.Bottom, r.W, r.H),
        DockEdge.Left => new RectF(Thickness - r.Bottom, r.X, r.H, r.W),
        DockEdge.Right => new RectF(r.Y, r.X, r.H, r.W),
        _ => r,
    };

    public (float x, float y) ToWindow(float x, float y)
    {
        var r = ToWindow(new RectF(x, y, 0, 0));
        return (r.X, r.Y);
    }

    public (float x, float y) ToDock(float wx, float wy) => Edge switch
    {
        DockEdge.Top => (wx, Thickness - wy),
        DockEdge.Left => (wy, Thickness - wx),
        DockEdge.Right => (wy, wx),
        _ => (wx, wy),
    };
}
