namespace RevitVr.Core.Scene;

/// <summary>
/// Turns model data into the oriented plan boxes the viewer collides against.
///
/// Walls, furniture and railings all reduce to the same primitive — a line with a thickness and a
/// height range — so the viewer needs one collision path rather than three. That also means a
/// railing costs the same to test as a wall: it is not mesh geometry.
///
/// Everything here takes Revit internal units (feet, Revit plan XY) and emits metres in glTF
/// space (X right, Y up, -Z forward). Kept out of the add-in so the arithmetic can be tested
/// without a Revit session.
/// </summary>
public static class ColliderGeometry
{
    public const double FeetToMetres = 0.3048;

    /// <summary>
    /// Longest plan length of a single box along a path. Bounds how many boxes a curved or
    /// hand-drawn railing produces.
    /// </summary>
    public const double MaxPieceMetres = 1.0;

    /// <summary>
    /// Height change above which a piece is subdivided as well. This is what keeps a railing on a
    /// stair honest: one box for the whole flight would carry the height range from the bottom
    /// riser to the top handrail, and would therefore block the space underneath the upper half of
    /// the stair. Stepping the height range along the slope leaves that space passable.
    /// </summary>
    public const double MaxRisePerPieceMetres = 0.35;

    /// <summary>Floor for collider thickness — a zero-width box is not a barrier at all.</summary>
    public const double MinThicknessMetres = 0.02;

    /// <summary>
    /// A point on a railing path, with the barrier's measured bottom and top there (feet).
    ///
    /// The measured heights matter because <c>Railing.GetPath()</c> does not report the railing's
    /// elevation: it reports the path at the level's elevation. A railing on an upper floor comes
    /// back flat at that floor's Z, so trusting it puts the barrier one storey below the railing —
    /// you walk under the railing upstairs and are stopped by nothing downstairs.
    /// </summary>
    public readonly record struct PathSample(double X, double Y, double Base, double Top)
    {
        public bool HasBarrier => Top > Base;

        /// <summary>A path point with no railing geometry near it; pieces touching it are dropped.</summary>
        public static PathSample Missing(double x, double y) => new(x, y, 0, 0);
    }

    /// <summary>
    /// One box per straight, roughly level piece of a path, with the height range interpolated
    /// between samples. Pieces with no barrier at either end are dropped, so a path that runs on
    /// past the railing does not leave a collider in open space.
    /// </summary>
    public static List<WallSegment> AlongPath(IReadOnlyList<PathSample> samples, double thicknessFt)
    {
        var result = new List<WallSegment>();
        if (samples.Count < 2) return result;

        const double ft = FeetToMetres;
        var thickness = (float)Math.Max(thicknessFt * ft, MinThicknessMetres);

        for (var i = 0; i + 1 < samples.Count; i++)
        {
            var a = samples[i];
            var b = samples[i + 1];
            if (!a.HasBarrier || !b.HasBarrier) continue;

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy) * ft;
            if (length < 1e-4) continue;

            var rise = Math.Max(Math.Abs(b.Top - a.Top), Math.Abs(b.Base - a.Base)) * ft;
            var pieces = Math.Max(1, Math.Max(
                (int)Math.Ceiling(length / MaxPieceMetres),
                (int)Math.Ceiling(rise / MaxRisePerPieceMetres)));

            for (var k = 0; k < pieces; k++)
            {
                var t0 = (double)k / pieces;
                var t1 = (double)(k + 1) / pieces;

                var baseA = a.Base + (b.Base - a.Base) * t0;
                var baseB = a.Base + (b.Base - a.Base) * t1;
                var topA = a.Top + (b.Top - a.Top) * t0;
                var topB = a.Top + (b.Top - a.Top) * t1;

                result.Add(new WallSegment
                {
                    X1 = (float)((a.X + dx * t0) * ft),
                    Z1 = (float)(-(a.Y + dy * t0) * ft),
                    X2 = (float)((a.X + dx * t1) * ft),
                    Z2 = (float)(-(a.Y + dy * t1) * ft),
                    Thickness = thickness,
                    Y0 = (float)(Math.Min(baseA, baseB) * ft),
                    Y1 = (float)(Math.Max(topA, topB) * ft),
                });
            }
        }

        return result;
    }

    /// <summary>
    /// A path resampled at a fixed spacing, keeping every original vertex.
    ///
    /// A straight path arrives as two points, so measuring a railing only at its vertices measures
    /// a whole flight at once: the bottom of a stair and its top land in one sample, and the
    /// barrier ends up four metres tall. Sampling along the path instead makes the barrier follow
    /// the railing, which is what keeps it minimal.
    /// </summary>
    public static List<(double X, double Y, double Z)> Resample(
        IReadOnlyList<(double X, double Y, double Z)> pathFt,
        double spacingFt)
    {
        var result = new List<(double X, double Y, double Z)>();
        if (pathFt.Count == 0) return result;

        var spacing = spacingFt <= 1e-6 ? 1.0 : spacingFt;
        result.Add(pathFt[0]);

        for (var i = 0; i + 1 < pathFt.Count; i++)
        {
            var a = pathFt[i];
            var b = pathFt[i + 1];

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var dz = b.Z - a.Z;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) continue;

            var steps = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (var k = 1; k <= steps; k++)
            {
                var t = (double)k / steps;
                result.Add((a.X + dx * t, a.Y + dy * t, a.Z + dz * t));
            }
        }

        return result;
    }

    /// <summary>
    /// The same path without measured heights: the barrier is raised from the path's own Z.
    ///
    /// Only correct when the path really is at the railing's elevation, which is why the add-in
    /// uses this as a fallback and says so in the export report.
    /// </summary>
    public static List<WallSegment> AlongPath(
        IReadOnlyList<(double X, double Y, double Z)> pathFt,
        double heightFt,
        double thicknessFt)
    {
        var samples = new List<PathSample>(pathFt.Count);
        var height = Math.Max(heightFt, 0.1);
        foreach (var (x, y, z) in pathFt)
            samples.Add(new PathSample(x, y, z, z + height));
        return AlongPath(samples, thicknessFt);
    }

    /// <summary>
    /// Height of the walkable surface a railing stands on, at a plan point (metres, glTF space).
    ///
    /// A railing belongs to the floor or tread it stands beside, not to the lowest point of its own
    /// geometry. That distinction matters because the only geometry available for a railing is
    /// often coarse: one simplified solid spanning the whole railing, whose minimum is the bottom
    /// of the stair the railing climbs. Placing the barrier from that minimum put it a storey below
    /// the railing — a barrier that dives away from the floor it belongs to.
    ///
    /// Surfaces above <paramref name="top"/> minus <paramref name="minDrop"/> are ignored, so an
    /// overhanging slab is never mistaken for the floor a railing stands on.
    /// </summary>
    public static double? SurfaceBaseAt(
        SceneManifest manifest, double x, double z, double top, double radius, double minDrop)
        => HighestSurfaceBelow(manifest, x, z, top, radius, minDrop);

    /// <summary>
    /// Height of the walking surface a railing's path point stands on, when the railing's own
    /// geometry cannot say (metres).
    ///
    /// Only surfaces with a real outline count. A floor slab or roof is recorded as a rectangle —
    /// the whole element's box — so one can cover a stair it does not stand over, and taking the
    /// highest such box under a climbing railing picks the floor of the level above. Treads carry
    /// their own outline, which is why they are the surfaces this can trust.
    ///
    /// The search starts within a hand's width and only widens to a metre if that finds nothing.
    /// The difference matters on a stair: a metre's radius reaches the treads half a turn above,
    /// and taking the highest of those lifts the barrier clear of the tread the railing actually
    /// guards — a barrier you can walk under is worse than none, because nothing looks wrong.
    ///
    /// This is the fallback for coarse railing geometry: a curved or climbing railing arrives as
    /// one aggregate solid over the whole element, whose minimum is the bottom of the flight and
    /// whose maximum is its top, so no single point along it can be measured from it.
    /// </summary>
    public static double? SurfaceHeightUnder(
        SceneManifest manifest, double x, double z, double ceiling, double minDrop,
        double nearRadius = 0.15, double wideRadius = 1.0)
        => HighestSurfaceBelow(manifest, x, z, ceiling, nearRadius, minDrop, outlinedOnly: true)
           ?? HighestSurfaceBelow(manifest, x, z, ceiling, wideRadius, minDrop, outlinedOnly: true);

    private static double? HighestSurfaceBelow(
        SceneManifest manifest, double x, double z, double top, double radius, double minDrop,
        bool outlinedOnly = false)
    {
        double? best = null;

        foreach (var surface in manifest.Surfaces)
        {
            if (x < surface.MinX - radius || x > surface.MaxX + radius) continue;
            if (z < surface.MinZ - radius || z > surface.MaxZ + radius) continue;
            if (surface.Y > top - minDrop) continue;

            if (surface.P is { Length: >= 6 })
            {
                var polygon = new List<PlanPoint>(surface.P.Length / 2);
                for (var i = 0; i + 1 < surface.P.Length; i += 2)
                    polygon.Add(new PlanPoint(surface.P[i], surface.P[i + 1]));

                if (PlanGeometry.DistanceToPolygon(polygon, x, z) > radius) continue;
            }
            else if (outlinedOnly) continue;

            if (best is null || surface.Y > best) best = surface.Y;
        }

        return best;
    }

    /// <summary>
    /// A fitted footprint rectangle as one oriented box — the shape furniture, casework and
    /// columns are emitted as, and the fallback for a railing whose path cannot be read.
    /// </summary>
    public static WallSegment? FromRect(
        PlanRect rect, double baseFt, double topFt, double minAreaSqMetres = 0.01)
    {
        const double ft = FeetToMetres;

        if (rect.Area * ft * ft < minAreaSqMetres) return null;
        if (topFt <= baseFt) return null;

        return new WallSegment
        {
            X1 = (float)((rect.Cx - rect.Ux * rect.HalfLength) * ft),
            Z1 = (float)(-(rect.Cy - rect.Uy * rect.HalfLength) * ft),
            X2 = (float)((rect.Cx + rect.Ux * rect.HalfLength) * ft),
            Z2 = (float)(-(rect.Cy + rect.Uy * rect.HalfLength) * ft),
            Thickness = (float)Math.Max(2 * rect.HalfThickness * ft, MinThicknessMetres),
            Y0 = (float)(baseFt * ft),
            Y1 = (float)(topFt * ft),
        };
    }
}
