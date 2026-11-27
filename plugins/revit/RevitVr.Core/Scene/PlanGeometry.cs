namespace RevitVr.Core.Scene;

public readonly record struct PlanPoint(double X, double Y);

/// <summary>An oriented rectangle in plan: centre, unit direction, and half extents.</summary>
public readonly record struct PlanRect(
    double Cx, double Cy,
    double Ux, double Uy,
    double HalfLength, double HalfThickness)
{
    public double Area => 4 * HalfLength * HalfThickness;
    public double AngleDegrees => Math.Atan2(Uy, Ux) * 180.0 / Math.PI;
}

/// <summary>
/// Plan-view fitting for obstacle colliders.
///
/// Furniture, casework and columns need to stop you walking through them, and a world-axis
/// bounding box is a poor fit: a desk rotated 45 degrees would block a square roughly twice its
/// own area. The minimum-area rectangle around the footprint tracks the object instead, at the
/// cost of a convex hull — cheap for the few dozen vertices that matter here.
///
/// Lives in Core, away from Revit, so the fitting maths can be tested directly.
/// </summary>
public static class PlanGeometry
{
    public static List<PlanPoint> ConvexHull(IReadOnlyList<PlanPoint> points)
    {
        if (points.Count == 0) return new List<PlanPoint>();

        var pts = points
            .GroupBy(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6)))
            .Select(g => g.First())
            .OrderBy(p => p.X).ThenBy(p => p.Y)
            .ToList();

        if (pts.Count <= 2) return pts;

        var hull = new List<PlanPoint>();

        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        var lowerCount = hull.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        if (hull.Count > 0) hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>Smallest-area rectangle enclosing the points, or null when there is nothing to fit.</summary>
    public static PlanRect? MinAreaRect(IReadOnlyList<PlanPoint> points)
    {
        if (points.Count == 0) return null;

        var hull = ConvexHull(points);
        if (hull.Count == 0) return null;

        if (hull.Count == 1)
            return new PlanRect(hull[0].X, hull[0].Y, 1, 0, 0, 0);

        if (hull.Count == 2)
        {
            var a = hull[0];
            var b = hull[1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) return new PlanRect(a.X, a.Y, 1, 0, 0, 0);
            return new PlanRect((a.X + b.X) / 2, (a.Y + b.Y) / 2, dx / len, dy / len, len / 2, 0);
        }

        PlanRect? best = null;
        var bestArea = double.MaxValue;

        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) continue;

            var ux = dx / len;
            var uy = dy / len;
            var vx = -uy;
            var vy = ux;

            var minU = double.MaxValue; var maxU = double.MinValue;
            var minV = double.MaxValue; var maxV = double.MinValue;

            foreach (var p in hull)
            {
                var pu = (p.X - a.X) * ux + (p.Y - a.Y) * uy;
                var pv = (p.X - a.X) * vx + (p.Y - a.Y) * vy;
                if (pu < minU) minU = pu;
                if (pu > maxU) maxU = pu;
                if (pv < minV) minV = pv;
                if (pv > maxV) maxV = pv;
            }

            var width = maxU - minU;
            var height = maxV - minV;
            var area = width * height;
            if (area >= bestArea) continue;

            var cu = (minU + maxU) / 2;
            var cv = (minV + maxV) / 2;
            var cx = a.X + ux * cu + vx * cv;
            var cy = a.Y + uy * cu + vy * cv;

            bestArea = area;
            best = width >= height
                ? new PlanRect(cx, cy, ux, uy, width / 2, height / 2)
                : new PlanRect(cx, cy, vx, vy, height / 2, width / 2);
        }

        return best;
    }

    private static double Cross(PlanPoint o, PlanPoint a, PlanPoint b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// Distance from a point to a plan polygon, zero inside it.
    ///
    /// This is how a railing's height gets measured at a point along its path: a long rail's
    /// bounding box has corners only at its ends, so a sample in the middle of the rail is inside
    /// the solid but far from every corner. Asking "which solids span this point" is the question
    /// that has an answer everywhere along the rail.
    /// </summary>
    public static double DistanceToPolygon(IReadOnlyList<PlanPoint> polygon, double x, double y)
    {
        if (polygon.Count < 3) return double.MaxValue;
        var distance = DistanceToPolygonEdge(polygon, x, y, out var inside);
        return inside ? 0 : distance;
    }

    private static double DistanceToPolygonEdge(IReadOnlyList<PlanPoint> polygon, double x, double y, out bool inside)
    {
        var best = double.MaxValue;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len2 = dx * dx + dy * dy;

            double t = len2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
            var px = a.X + dx * t;
            var py = a.Y + dy * t;
            var dist = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (dist < best) best = dist;
        }

        inside = IsInside(polygon, x, y);
        return best;
    }

    /// <summary>
    /// The nearest point on a polygon's boundary, or the point itself when it lies inside.
    ///
    /// Distance alone is not enough to place something on a railing: a rail's path is its sketch
    /// line, and the rail itself can run beside it. This says where the geometry is, so a barrier
    /// can be moved onto it instead of left behind on the path.
    /// </summary>
    public static (double X, double Y, double Distance) NearestPointOnPolygon(
        IReadOnlyList<PlanPoint> polygon, double x, double y)
    {
        if (polygon.Count == 0) return (x, y, double.MaxValue);
        if (polygon.Count == 1)
        {
            var only = polygon[0];
            var d = Math.Sqrt((x - only.X) * (x - only.X) + (y - only.Y) * (y - only.Y));
            return (only.X, only.Y, d);
        }

        if (polygon.Count == 2) return NearestOnSegment(polygon[0], polygon[1], x, y);
        if (IsInside(polygon, x, y)) return (x, y, 0);

        var best = (X: x, Y: y, Distance: double.MaxValue);
        for (var i = 0; i < polygon.Count; i++)
        {
            var candidate = NearestOnSegment(polygon[i], polygon[(i + 1) % polygon.Count], x, y);
            if (candidate.Distance < best.Distance) best = candidate;
        }

        return best;
    }

    private static (double X, double Y, double Distance) NearestOnSegment(
        PlanPoint a, PlanPoint b, double x, double y)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
        var px = a.X + dx * t;
        var py = a.Y + dy * t;
        return (px, py, Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)));
    }

    /// <summary>Crossing-number test. Polygons here are convex quads or hulls.</summary>
    public static bool IsInside(IReadOnlyList<PlanPoint> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (pi.Y > y != pj.Y > y &&
                x < (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }
}
