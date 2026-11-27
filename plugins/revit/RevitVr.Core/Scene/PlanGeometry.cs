namespace RevitVr.Core.Scene;

public readonly record struct PlanPoint(double X, double Y);

/// <summary>
/// One plan face reduced to what the walkable world needs: an outer boundary and the holes cut
/// inside it. A floor slab drawn around a stairwell is exactly this — a donut — and treating it
/// as its bounding box fills the stairwell with a phantom floor you cannot descend through.
/// </summary>
public readonly record struct PlanShell(
    IReadOnlyList<PlanPoint> Outer,
    IReadOnlyList<IReadOnlyList<PlanPoint>> Holes)
{
    /// <summary>Walkable area: the outer boundary less whatever is cut out of it, in the caller's units squared.</summary>
    public double Area =>
        Math.Abs(PlanGeometry.SignedArea(Outer)) -
        Holes.Sum(hole => Math.Abs(PlanGeometry.SignedArea(hole)));

    public bool HasHoles => Holes.Count > 0;
}

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

    /// <summary>
    /// Crossing-number test on a flat [x,y,x,y,...] polygon, which is how outlines are carried in
    /// the manifest. Kept separate so the exporter and the spawn sanity check test the exact
    /// numbers the player's collision world will see, not a re-derived copy.
    /// </summary>
    public static bool IsInside(ReadOnlySpan<float> flat, double x, double y)
    {
        var n = flat.Length / 2;
        if (n < 3) return false;

        var inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = flat[i * 2], yi = flat[i * 2 + 1];
            double xj = flat[j * 2], yj = flat[j * 2 + 1];
            if (yi > y != yj > y &&
                x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Signed plan area by the shoelace formula; negative when the winding is clockwise.</summary>
    public static double SignedArea(IReadOnlyList<PlanPoint> polygon)
    {
        var n = polygon.Count;
        if (n < 3) return 0;

        var sum = 0.0;
        for (int i = 0, j = n - 1; i < n; j = i++)
            sum += (polygon[j].X - polygon[i].X) * (polygon[j].Y + polygon[i].Y);
        return sum / 2;
    }

    /// <summary>
    /// The loops of one Revit face split into its outer boundary and its holes.
    ///
    /// A face's loops arrive unordered and with no marker saying which is which, and the winding
    /// is not dependable across element types, so the largest loop by area is taken as the outer
    /// boundary and every other loop that lies inside it is a hole. A loop that is not inside it
    /// is counted as dropped rather than silently treated as a hole: it means the face was not the
    /// simple outline the caller assumed, and the export report should be able to say so.
    /// </summary>
    public static PlanShell? Shell(IReadOnlyList<IReadOnlyList<PlanPoint>> loops, out int dropped)
    {
        dropped = 0;

        var outerIndex = -1;
        var outerArea = 0.0;
        for (var i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];
            if (loop.Count < 3) continue;
            var area = Math.Abs(SignedArea(loop));
            if (area <= outerArea) continue;
            outerArea = area;
            outerIndex = i;
        }

        if (outerIndex < 0 || outerArea < 1e-6) return null;

        IReadOnlyList<PlanPoint> outer = loops[outerIndex];

        var holes = new List<IReadOnlyList<PlanPoint>>();
        for (var i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];
            if (i == outerIndex || loop.Count < 3) continue;
            var minX = double.MaxValue; var maxX = double.MinValue;
            var minY = double.MaxValue; var maxY = double.MinValue;
            foreach (var p in loop)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }

            // a hole is inside the face whichever way its own outline is wound, so test the
            // middle of its extent as well as a vertex: a hole that shares vertices with the
            // outer boundary reports "outside" for those vertices
            var inside = IsInside(outer, loop[0].X, loop[0].Y) ||
                         IsInside(outer, (minX + maxX) / 2, (minY + maxY) / 2);

            if (inside) holes.Add(loop);
            else dropped++;
        }

        return new PlanShell(outer, holes);
    }

    /// <summary>
    /// Douglas–Peucker thinning of a closed ring, in the caller's units.
    ///
    /// A tessellated 50 m circle arrives as thousands of points, which would dwarf every other
    /// collider in the manifest for no gain — the outline only has to say "the floor stops here"
    /// to within a couple of centimetres. The ring is split at the vertex farthest from the first
    /// one so both halves are simplified as open chains; a plain run over a closed ring would
    /// collapse it onto the start-to-end line.
    /// </summary>
    public static List<PlanPoint> Simplify(IReadOnlyList<PlanPoint> ring, double tolerance)
    {
        var pts = new List<PlanPoint>(ring.Count);
        foreach (var p in ring)
            if (pts.Count == 0 || Math.Abs(pts[^1].X - p.X) > 1e-9 || Math.Abs(pts[^1].Y - p.Y) > 1e-9)
                pts.Add(p);
        if (pts.Count > 1 && Math.Abs(pts[0].X - pts[^1].X) <= 1e-9 && Math.Abs(pts[0].Y - pts[^1].Y) <= 1e-9)
            pts.RemoveAt(pts.Count - 1);

        if (pts.Count < 4 || tolerance <= 0) return pts;

        var far = 1;
        var farDist = -1.0;
        for (var i = 1; i < pts.Count; i++)
        {
            var dx = pts[i].X - pts[0].X;
            var dy = pts[i].Y - pts[0].Y;
            var d = dx * dx + dy * dy;
            if (d <= farDist) continue;
            farDist = d;
            far = i;
        }

        var keep = new bool[pts.Count];
        keep[0] = true;
        keep[far] = true;
        keep[pts.Count - 1] = true;

        SimplifySpan(pts, 0, far, tolerance, keep);
        SimplifySpan(pts, far, pts.Count - 1, tolerance, keep);

        var result = new List<PlanPoint>();
        for (var i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result.Count >= 3 ? result : pts;
    }

    private static void SimplifySpan(
        List<PlanPoint> pts, int first, int last, double tolerance, bool[] keep)
    {
        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));

        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b <= a + 1) continue;

            var worst = 0.0;
            var index = -1;
            for (var i = a + 1; i < b; i++)
            {
                var d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d <= worst) continue;
                worst = d;
                index = i;
            }

            if (index < 0 || worst <= tolerance) continue;
            keep[index] = true;
            stack.Push((a, index));
            stack.Push((index, b));
        }
    }

    private static double DistanceToSegment(PlanPoint p, PlanPoint a, PlanPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        var px = a.X + dx * t;
        var py = a.Y + dy * t;
        return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
    }
}
