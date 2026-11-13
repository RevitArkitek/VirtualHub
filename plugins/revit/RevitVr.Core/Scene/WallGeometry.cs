namespace RevitVr.Core.Scene;

/// <summary>
/// Plan-view geometry behind door cut-outs.
///
/// This lives here, free of any Revit dependency, because it is the part of the pipeline that
/// decides whether a doorway is passable — and getting it wrong produces a model that looks
/// right but cannot be walked. Keeping it out of the add-in means it can be tested without a
/// Revit session.
///
/// Everything is in Revit's internal units (feet) on the plan plane; the caller converts.
/// </summary>
public static class WallGeometry
{
    /// <summary>Running length along a polyline, one entry per point (first is 0).</summary>
    public static double[] Cumulative(IReadOnlyList<(double X, double Y)> pts)
    {
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++)
        {
            var dx = pts[i].X - pts[i - 1].X;
            var dy = pts[i].Y - pts[i - 1].Y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        return cum;
    }

    /// <summary>
    /// Nearest point on an open polyline. Returns the distance along the polyline to that point
    /// and the perpendicular distance from it — deliberately explicit rather than relying on a
    /// curve's parameter, whose meaning differs between line, arc and spline.
    /// </summary>
    public static (double Along, double Distance) ClosestOnPolyline(
        IReadOnlyList<(double X, double Y)> pts, IReadOnlyList<double> cum, double px, double py)
    {
        var bestAlong = 0.0;
        var bestDist = double.MaxValue;

        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var (ax, ay) = pts[i];
            var (bx, by) = pts[i + 1];
            var abx = bx - ax;
            var aby = by - ay;
            var len2 = abx * abx + aby * aby;
            var t = len2 < 1e-12 ? 0.0 : Math.Clamp(((px - ax) * abx + (py - ay) * aby) / len2, 0, 1);
            var qx = ax + abx * t;
            var qy = ay + aby * t;
            var dx = px - qx;
            var dy = py - qy;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d >= bestDist) continue;
            bestDist = d;
            bestAlong = cum[i] + t * (cum[i + 1] - cum[i]);
        }

        return (bestAlong, bestDist);
    }

    /// <summary>The parts of [a,b] not covered by any cut interval, in order.</summary>
    public static List<(double A, double B)> Subtract(
        double a, double b, IReadOnlyList<(double A, double B)> cuts)
    {
        var result = new List<(double A, double B)>();

        var overlapping = cuts
            .Where(c => c.B > a && c.A < b)
            .OrderBy(c => c.A)
            .ToList();

        if (overlapping.Count == 0)
        {
            result.Add((a, b));
            return result;
        }

        var cursor = a;
        foreach (var (ca, cb) in overlapping)
        {
            if (ca > cursor) result.Add((cursor, Math.Min(ca, b)));
            cursor = Math.Max(cursor, cb);
            if (cursor >= b) break;
        }
        if (cursor < b) result.Add((cursor, b));

        return result;
    }
}
