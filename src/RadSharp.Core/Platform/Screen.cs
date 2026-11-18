namespace RadSharp;

/// <summary>
/// The displays (monitors) the platform host reports (Delphi's <c>Screen.Monitors</c>). Coordinates are screen
/// coordinates in the host's window units, the units of <see cref="Form.RestoredBounds"/> (on Windows: pixels).
/// Without a host, or with one that can't tell, there are no displays.
/// </summary>
public static class Screen
{
    /// <summary>The displays attached now, the primary one first.</summary>
    public static IReadOnlyList<Display> Displays => PlatformHost.Current?.GetDisplays() ?? [];

    /// <summary>The primary display, or null if the host reports none.</summary>
    public static Display? Primary => Primary_(Displays);

    /// <summary>The display containing the screen point (<paramref name="x"/>, <paramref name="y"/>), or null if none does.</summary>
    public static Display? FromPoint(int x, int y)
    {
        foreach (var d in Displays)
            if (Contains(d.Bounds, x, y)) return d;
        return null;
    }

    /// <summary>The display with the largest part of <paramref name="rect"/>; the one nearest to it if none overlaps; null if there are none.</summary>
    public static Display? FromRect(Rect rect) => Nearest(Displays, rect);

    /// <summary>Like <see cref="FromRect"/>, among <paramref name="displays"/> (for hosts and tests).</summary>
    public static Display? FromRectIn(IReadOnlyList<Display> displays, Rect rect) => Nearest(displays, rect);

    /// <summary>The display with this <see cref="Display.Id"/> (valid while the application runs), or null.</summary>
    public static Display? FromId(uint id) => Displays.FirstOrDefault(d => d.Id == id);

    internal static Display? Primary_(IReadOnlyList<Display> displays) =>
        displays.FirstOrDefault(d => d.IsPrimary) ?? (displays.Count > 0 ? displays[0] : null);

    internal static Display? Nearest(IReadOnlyList<Display> displays, Rect rect)
    {
        Display? best = null;
        long bestArea = 0;
        foreach (var d in displays)
        {
            long area = OverlapArea(d.Bounds, rect);
            if (area > bestArea) { best = d; bestArea = area; }
        }
        if (best != null) return best;
        // Nothing overlaps: the display whose rect is closest to the rect's centre.
        int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        long bestDist = long.MaxValue;
        foreach (var d in displays)
        {
            long dx = Math.Max(0, Math.Max(d.Bounds.X - cx, cx - (d.Bounds.X + d.Bounds.Width)));
            long dy = Math.Max(0, Math.Max(d.Bounds.Y - cy, cy - (d.Bounds.Y + d.Bounds.Height)));
            long dist = dx * dx + dy * dy;
            if (dist < bestDist) { best = d; bestDist = dist; }
        }
        return best;
    }

    internal static long OverlapArea(Rect a, Rect b)
    {
        long w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        long h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    internal static bool OverlapAreaAny(IReadOnlyList<Display> displays, Rect rect) => displays.Any(d => OverlapArea(d.Bounds, rect) > 0);

    static bool Contains(Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    /// <summary>
    /// A <paramref name="width"/> x <paramref name="height"/> rect in the middle of <paramref name="area"/>, shrunk to fit it.
    /// </summary>
    internal static Rect CenterIn(Rect area, int width, int height)
    {
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }
}
