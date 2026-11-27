using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitVr.Core.Scene;

namespace RevitVr.Addin.Export;

public sealed record ColliderStats(
    int WallRuns,
    int Segments,
    int Openings,
    int DoorsFound,
    int DoorsMatched,
    int DoorsUnmatched,
    int RailingBoxes,
    int RailingsUnplaced,
    IReadOnlyList<RailingReport> Railings);

/// <summary>
/// What one railing produced, written to railings.json beside the scene.
///
/// Without this a collider that misbehaves in the headset can only be described by its
/// coordinates; with it, an ElementId the user names in Revit leads straight to what the
/// exporter did with that element and why.
/// </summary>
public sealed record RailingReport(
    long Id,
    string? TypeName,
    int PathCurves,
    double PathLengthFt,
    int Samples,
    int SamplesWithoutGeometry,
    int Boxes,
    string Outcome,
    double HeightFt,
    float MinY,
    float MaxY,
    string Geometry,
    int Solids,
    int RayHits = 0,
    int RayAggregates = 0,
    string? PathDigest = null,
    string? SpanDigest = null,
    string? MissDigest = null);

/// <summary>
/// Derives the walkable world from Revit elements analytically instead of from the render mesh.
///
/// Every wall becomes one or more 2D boxes running along its location curve. Each door cuts a
/// gap out of the wall run it sits in, which is what makes doorways passable while the wall
/// either side of them stays solid. Because the result is a few hundred 2D boxes rather than
/// hundreds of thousands of triangles, collision stays cheap enough for a standalone headset —
/// and it stays exact, because it comes from the same model data the walls are built from.
/// </summary>
public static class ColliderExtractor
{
    private const double Ft = GeometryExporter.FeetToMetres;
    /// <summary>Tessellation density for curved faces, 0..1.</summary>
    private const double TessellationLod = 0.5;

    // A path point that only finds a bracket or the edge of one rail is not a railing (feet).
    private const double MinBarrierFt = 0.3 / Ft;

    // How far below a railing's own top a walkable surface may be and still count as the thing it
    // stands on — anything nearer is the slab above, not the floor below.
    private const double SurfaceMinDropM = 0.25;

    // How far from a railing's plan rectangle a path point may be and still be worth casting a
    // ray at it (feet). Slack, not tolerance: the ray decides, this only skips distant solids.
    private const double RayReachFt = 0.5;

    // How far a slab outline may deviate from the real edge once its loops are thinned (metres).
    // Big enough to turn a tessellated 50 m circle into ~50 points, small enough that "the floor
    // stops here" is still true to a few centimetres.
    private const double OutlineToleranceM = 0.05;

    // Most upward faces one slab may contribute as separate walkable surfaces.
    private const int MaxFacesPerSlab = 64;

    /// <summary>
    /// Categories that stop you walking through them. Doors are deliberately absent: a door gets
    /// a gap cut in its host wall instead, and a leaf you cannot pass would defeat the point.
    /// </summary>
    private static readonly BuiltInCategory[] ObstacleCategories =
    {
        BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_FurnitureSystems,
        BuiltInCategory.OST_Entourage,
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_PlumbingFixtures,
    };
    /// <summary>Doors are cut 5 cm wider than their nominal opening, to clear the frame.</summary>
    private const double Clearance = 0.05 / Ft;

    private sealed class WallRun
    {
        public required ElementId Id;
        public required List<XYZ> Pts;
        public required List<(double X, double Y)> Plan;
        public required double[] Cum;
        public double Length;
        public double Thickness;   // feet
        public double Y0, Y1;      // feet
        public readonly List<(double A, double B)> Cuts = new();
    }

    public static ColliderStats Build(Document doc, View view, SceneManifest manifest)
    {
        var runs = new List<WallRun>();

        foreach (var wall in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>())
        {
            if (wall.Location is not LocationCurve lc) continue;
            var curve = lc.Curve;
            if (curve is null) continue;

            var pts = Tessellate(curve);
            if (pts.Count < 2) continue;

            var bbox = wall.get_BoundingBox(view) ?? wall.get_BoundingBox(null);
            var thickness = Math.Max(wall.Width, 0.1);

            var plan = pts.Select(p => (p.X, p.Y)).ToList();
            var cum = WallGeometry.Cumulative(plan);
            var length = cum[^1];
            if (length < 1e-3) continue;

            runs.Add(new WallRun
            {
                Id = wall.Id,
                Pts = pts,
                Plan = plan,
                Cum = cum,
                Length = length,
                Thickness = thickness,
                Y0 = bbox?.Min.Z ?? 0,
                Y1 = bbox?.Max.Z ?? 0,
            });
        }

        var (doorStats, openings) = CutDoorOpenings(doc, view, runs);

        foreach (var run in runs)
        {
            for (var i = 0; i + 1 < run.Pts.Count; i++)
            {
                var a = run.Cum[i];
                var b = run.Cum[i + 1];
                foreach (var (sa, sb) in WallGeometry.Subtract(a, b, run.Cuts))
                {
                    if (sb - sa < 0.05) continue;               // 15 mm sliver, ignore
                    var p = PointAt(run, sa);
                    var q = PointAt(run, sb);
                    manifest.Segments.Add(new WallSegment
                    {
                        X1 = (float)(p.X * Ft),
                        Z1 = (float)(-p.Y * Ft),
                        X2 = (float)(q.X * Ft),
                        Z2 = (float)(-q.Y * Ft),
                        Thickness = (float)(run.Thickness * Ft),
                        Y0 = (float)(run.Y0 * Ft),
                        Y1 = (float)(run.Y1 * Ft),
                    });
                }
            }
        }

        var floors = AddFloorSurfaces(doc, view, manifest);
        var wallSegments = manifest.Segments.Count;
        var treads = AddStairSurfaces(doc, view, manifest);
        var terrainTris = AddTerrainTriangles(doc, view, manifest);
        var obstacles = AddInstanceObstacles(doc, view, manifest);
        var railings = AddRailingColliders(doc, view, manifest);

        manifest.Warnings.Add(
            $"{runs.Count} wall runs → {wallSegments} wall segments, {obstacles} obstacle boxes; doors: " +
            $"{doorStats.Found} found, {doorStats.Matched} cut, {doorStats.Unmatched} unmatched; " +
            $"railings: {railings.Found} found → {railings.Boxes} colliders " +
            $"({railings.Measured} measured, {railings.FromPath3D} from a climbing path, " +
            $"{railings.FromSurface} on the surface under the path, " +
            $"{railings.FromBox} bbox-placed, {railings.Unplaced} unplaced, " +
            $"{railings.FromFootprint} footprint, {railings.NoCollider} No Collider); " +
            $"{floors.Outlined + floors.FromBox} floor/roof surfaces " +
            $"({floors.WithHoles} with a stairwell or shaft hole cut out, " +
            $"{floors.FromBox} from a bounding box), {treads} stair treads, " +
            $"{terrainTris} terrain triangles");

        if (floors.Sloped > 0)
            manifest.Warnings.Add(
                $"{floors.Sloped} floor/roof surfaces are sloped and were flattened to a walk plane at " +
                $"their highest edge (drop up to {floors.MaxDropM:0.00} m). A ramp is walked as a " +
                "level surface at the top of its slope.");

        if (floors.FromBox > 0)
            manifest.Warnings.Add(
                $"{floors.FromBox} floor/roof slabs could not be read as their own outline and fell " +
                $"back to their bounding box, so any stairwell or shaft in them is filled with a " +
                $"phantom floor (ids {string.Join(", ", floors.BoxIds)}). Walking down such an " +
                "opening will stop at storey level.");

        if (doorStats.Found > 0 && doorStats.Matched == 0)
            manifest.Warnings.Add("Every door failed to match a wall — doorways will be impassable.");
        if (railings.Found > 0 && railings.Boxes == 0)
            manifest.Warnings.Add(
                "Railings were found but produced no collider — you can walk through them.");
        var dropped = railings.Reports.Count(r => r.Outcome == "measured" && r.SamplesWithoutGeometry > 0);
        if (dropped > 0)
            manifest.Warnings.Add(
                $"{dropped} railings lost path pieces where no railing geometry spanned the path; " +
                "see railings.json for the element ids, and check those railings in the headset.");

        if (railings.RayAggregateSamples > 0)
            manifest.Warnings.Add(
                $"{railings.RayAggregateSamples} railing samples found only aggregate geometry under the " +
                "path and were stood on the floor beneath instead, which is right for a flat railing " +
                "and wrong on a stair. Compare the railings' 'rayAggregates' in railings.json against " +
                "their 'samples' to see how much of each barrier came that way.");

        if (railings.FromBox > 0)
            manifest.Warnings.Add(
                $"{railings.FromBox} railings could not be measured from their geometry and were " +
                "placed from their bounding box. Fine for a flat railing; check them in the headset.");

        // A barrier whose height jumps between surfaces along the path becomes a run of thin slats:
        // one piece per 0.35 m of jump, hundreds of them, and the railing no longer reads as a
        // railing. Sampling is every 0.6 m, so more than three boxes per sample cannot be a legit
        // stair pitch — it is a barrier standing on two different things.
        var slatted = railings.Reports
            .Where(r => r.Samples > 0 && r.Boxes > 3 * r.Samples)
            .ToList();

        if (slatted.Count > 0)
            manifest.Warnings.Add(
                $"{slatted.Count} railings produced more than three boxes per path sample (ids " +
                $"{string.Join(", ", slatted.Take(8).Select(r => r.Id))}) — their barrier height is " +
                "jumping between surfaces along the path, which reads as a wall of thin slats in the " +
                "headset. Check what those samples are standing on before trusting the barriers.");

        if (railings.FootprintRejected > 0)
            manifest.Warnings.Add(
                $"{railings.FootprintRejected} railings had no measurable geometry and a footprint too " +
                "wide to be one railing — the fit was the sweep of a curved railing, which as a box " +
                "would block the space it runs through. They have no collider; railings.json names " +
                "them by ElementId.");

        if (railings.FromSurface > 0)
            manifest.Warnings.Add(
                $"{railings.FromSurface} climbing railings could not be measured from their own " +
                "geometry (coarse geometry returns one solid for the whole element) and their " +
                "barriers were stood on the treads or floors under the path instead. They climb the " +
                "stair; check where the barrier stops at the top and bottom of a flight.");

        if (railings.Unplaced > 0)
            manifest.Warnings.Add(
                $"{railings.Unplaced} railings could not be placed at all (no readable geometry, and " +
                "their extent spans more than one storey, so a flat barrier would wall off a stair). " +
                "They have no collider — railings.json names them by ElementId, with the geometry " +
                "strategy that failed.");
        if (manifest.Surfaces.Count == 0)
            manifest.Warnings.Add("No floor slabs in view; using level elevations for walking height");

        return new ColliderStats(
            runs.Count, manifest.Segments.Count, openings,
            doorStats.Found, doorStats.Matched, doorStats.Unmatched,
            railings.Boxes, railings.Unplaced, railings.Reports);
    }

    private readonly record struct DoorStats(int Found, int Matched, int Unmatched);

    /// <summary>
    /// What the floor harvest managed, per element: read as its own outline, read with a hole cut
    /// out of it, or fallen back to a bounding box because its geometry could not be read.
    /// </summary>
    private readonly record struct FloorStats(
        int Outlined, int WithHoles, int FromBox, IReadOnlyList<long> BoxIds,
        int Sloped, double MaxDropM);

    /// <summary>One walkable face: its plan outline, and the height its barrier stands at (metres).</summary>
    private readonly record struct TopShell(PlanShell Shell, double TopM, double DropM);

    private readonly record struct RailingStats(
        int Found, int NoCollider, int Measured, int FromPath3D, int FromSurface, int FromBox,
        int Unplaced, int FromFootprint, int FootprintRejected, int Boxes, int RayAggregateSamples,
        IReadOnlyList<RailingReport> Reports);


    /// <summary>
    /// Railings become thin boxes along their own path, at the height their geometry actually
    /// occupies.
    ///
    /// Both halves of that sentence were learned the hard way. <c>Railing.GetPath()</c> reports
    /// the path at the *level's* elevation, not the railing's: a railing on an upper floor comes
    /// back flat at that floor's Z. Trusting it puts the barrier one storey below the railing, so
    /// you walk through it upstairs and are stopped by nothing downstairs. The height range is
    /// therefore measured from the railing's own geometry at each path point, which also drops
    /// path pieces that run on past the visible railing instead of leaving a collider in open
    /// space.
    /// </summary>
    private static RailingStats AddRailingColliders(Document doc, View view, SceneManifest manifest)
    {
        // A collider is a barrier, not a drawing: thick enough to be a barrier and no thicker,
        // since the player's own radius already keeps them off it.
        const double ThicknessFt = 0.1 / Ft;

        // How far outside a solid a path point may sit and still count as inside it. The path
        // should run through its own railing; this only absorbs sketch slop.
        const double MeasureToleranceFt = 0.15 / Ft;

        // Distance between measurements along a path. A straight railing arrives as two points,
        // so this is what makes the barrier follow a stair instead of spanning the whole flight.
        const double SampleSpacingFt = 0.6 / Ft;

        // How far from a railing to look for the floor or tread it stands on. Coarse geometry
        // spans whole railings, so its lowest point is the bottom of the stair — using it put
        // barriers a storey below the railing.
        const double SurfaceRadiusM = 1.0;

        // A solid taller than this is an aggregate standing in for whole sub-elements, so its
        // height says nothing about the railing at one point along it.
        const double AggregateSpanFt = 2.0 / Ft;

        // A measured span outside this band around the railing type's own height means the
        // measurement caught something else — the flight above, or a single rail — so the type
        // height wins.
        const double MaxSpanRatio = 1.35;
        const double MinSpanRatio = 0.4;

        // Above this, a railing's bounding box spans more than one flight or storey, so it cannot
        // be turned into a single flat barrier.
        const double FlatRailingFt = 1.6 / Ft;

        // A footprint rectangle is only a fair stand-in for a railing that is a couple of metres
        // wide at most. Much wider and the fitted rectangle is the *sweep* of a curved railing:
        // three of those in the Silo covered 99 m² each and stood 4.7 m tall, and every tread of
        // its spiral stair sat inside one of them.
        const double MaxFootprintWidthM = 2.5;

        // A path whose own Z varies is a real 3-D path — the railing climbs — and then its Z is
        // better evidence than the geometry. A spiral passes over the same plan position twice at
        // different heights, so a plan-only measurement cannot tell the turns apart and puts the
        // upper turn's barrier on the lower one.
        const double PathIs3DFt = 0.25 / Ft;

        var found = 0;
        var noCollider = 0;
        var measured = 0;
        var fromPath3D = 0;
        var fromBox = 0;
        var fromSurface = 0;
        var unplaced = 0;
        var fromFootprint = 0;
        var footprintRejected = 0;
        var rayAggregateTotal = 0;
        var boxes = 0;
        var emitted = new HashSet<(int, int, int, int, int, int)>();
        var reports = new List<RailingReport>();

        var railings = new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_StairsRailing)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var element in railings)
        {
            found++;

            // Railings get used for things that are not railings — a parking bay edge, a planting
            // border, a line on a site plan. A type marked 'No Collider' stays visual only.
            if (HasNoColliderFlag(doc, element))
            {
                noCollider++;
                reports.Add(new RailingReport(
                    element.Id.Value,
                    doc.GetElement(element.GetTypeId())?.Name,
                    0, 0, 0, 0, 0, "no-collider",
                    Math.Round(RailingHeight(doc, element) * Ft, 3), 0f, 0f, "skipped", 0));
                continue;
            }

            var height = RailingHeight(doc, element);
            var candidates = new List<WallSegment>();
            var outcome = "none";
            var samples = 0;
            var samplesMissing = 0;
            var pathLength = 0.0;
            var pathCurves = 0;

            // The element's own extent, read once: it decides whether this railing climbs
            // something, and a climbing railing cannot be described by any single flat box.
            var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            var extentFt = bbox is null ? 0.0 : bbox.Max.Z - bbox.Min.Z;
            var climbs = extentFt > FlatRailingFt;

            // Which way each sample got its height, so the report can say what a barrier of this
            // railing's actually rests on.
            var usedSurface = false;
            var usedMeasured = false;
            var rayHits = 0;
            var rayAggregates = 0;
            var misses = new List<string>();

            var spans = new List<SolidSpan>();
            var haveGeometry = TryCollectSpans(element, view, spans, out var strategy);

            // Coarse detail is a stand-in for the whole element — one blocky solid where the
            // railing is really a rail plus panels — and on this model that stand-in leaves a 2 m
            // stretch of railing with no geometry at all, so the barrier drops there. The
            // as-modelled geometry is what the headset renders, so a flat railing that only has a
            // coarse stand-in is re-read from it. Climbing railings keep their tier untouched.
            if (!climbs && strategy is "coarse-visible" or "view-medium-visible")
            {
                var refined = new List<SolidSpan>();
                if (TryCollectFineNonVisible(element, view, refined))
                {
                    spans = refined;
                    strategy += "+fine";
                }
            }

            IList<Curve>? path = null;
            if (element is Railing railing)
            {
                // a railing whose sketch is broken can throw rather than return an empty list
                try { path = railing.GetPath(); }
                catch { path = null; }
            }

            if (path is { Count: > 0 })
            {
                pathCurves = path.Count;

                // ---- 1. the railing's own path, at measured heights ---------------
                foreach (var curve in path)
                {
                    if (curve is null) continue;
                    pathLength += curve.Length;

                    var verts = Tessellate(curve);
                    if (verts.Count < 2) continue;

                    // Measure along the path, not only at its vertices: a straight railing comes
                    // back as two points, and sampling just those makes one 4 m box out of a
                    // whole stair flight.
                    var zSpread = verts.Max(pt => pt.Z) - verts.Min(pt => pt.Z);
                    if (zSpread > PathIs3DFt)
                    {
                        // the path climbs: follow it, one railing tall above each point
                        candidates.AddRange(ColliderGeometry.AlongPath(
                            verts.Select(pt => (pt.X, pt.Y, pt.Z)).ToList(), height, ThicknessFt));
                        if (outcome == "none") outcome = "path-3d";
                        continue;
                    }

                    var along = ColliderGeometry.Resample(
                        verts.Select(p => (p.X, p.Y, p.Z)).ToList(), SampleSpacingFt);

                    var samplesHere = new List<ColliderGeometry.PathSample>(along.Count);
                    foreach (var p in along)
                    {
                        samples++;

                        // Two witnesses, and the exact one wins where it hits. The ray goes through
                        // the railing's own solid, so it finds the rail where the plan rectangle
                        // has already turned away; where the ray misses — a slender rail, a path a
                        // few centimetres off the geometry — the rectangle test still answers, so
                        // this only ever adds barriers, never takes one away.
                        //
                        // Flat railings only. On a climbing railing the ray finds the odd baluster
                        // or bracket rather than a band of railing, and the floor it then stands the
                        // barrier on is found by a rectangle-tolerant lookup that can pick the slab
                        // of the level above — which slants the barrier across the stair. Stairs
                        // keep the tread-based tier, which is measured at every sample.
                        var ray = haveGeometry && !climbs
                            ? MeasureBarrierByRay(spans, p.X, p.Y, AggregateSpanFt, RayReachFt)
                            : default;

                        if (ray.Hit) rayHits++;
                        if (ray.AggregateOnly) rayAggregates++;

                        // Follow the path. A railing's path is where the railing is: a plane cut
                        // through the render mesh of a real model found the pipe 0.1–0.2 m from its
                        // path, i.e. on it — while the element's *reported geometry* is a slanted
                        // stand-in box whose edge wanders up to 0.7 m away. Moving the barrier onto
                        // that box (an earlier attempt) dragged it off the railing along the run and
                        // put a jog in at the end; the geometry is only good for heights.
                        var barrier = ray.Hit && !ray.AggregateOnly
                            ? (ray.Bottom, ray.Top)
                            : haveGeometry
                                ? MeasureBarrier(
                                    spans, p.X, p.Y, MeasureToleranceFt, AggregateSpanFt)
                                : null;

                        if (barrier is null)
                        {
                            // No part of the railing spans this point. Three reasons, and only two
                            // of them are holes:
                            //  - a climbing railing's coarse geometry is one aggregate solid, so
                            //    the surface it stands on is the only local height there is;
                            //  - a flat railing whose solids are aggregates where the path crosses
                            //    them is still railing, so a barrier at floor height belongs there;
                            //  - the point is inside the railing's own footprint but its stand-in
                            //    geometry does not reach it, which is a gap in the *representation*,
                            //    not in the railing: the pipe is drawn there and unmeasured.
                            ColliderGeometry.PathSample? onSurface = null;

                            if (climbs)
                                onSurface = FromSurface(
                                    manifest, p.X, p.Y, height, bbox?.Max.Z ?? p.Z, SurfaceMinDropM);
                            else if ((ray.Hit && ray.AggregateOnly) ||
                                     InsidePlanExtent(spans, p.X, p.Y, MeasureToleranceFt))
                                onSurface = FromSurfaceOnFloor(
                                    manifest, p.X, p.Y, height, bbox?.Max.Z ?? p.Z, SurfaceRadiusM);

                            if (onSurface is { } surfaceSample)
                            {
                                samplesHere.Add(surfaceSample);
                                usedSurface = true;
                                continue;
                            }

                            // either the path runs past the railing, or a baluster gap leaves
                            // nothing to measure
                            if (misses.Count < 4)
                            {
                                var nearest = spans.Count == 0
                                    ? -1.0
                                    : spans.Min(s => PlanGeometry.DistanceToPolygon(s.Plan, p.X, p.Y));
                                misses.Add($"({p.X:0.0},{p.Y:0.0}) d{nearest:0.00}");
                            }

                            samplesMissing++;
                            samplesHere.Add(ColliderGeometry.PathSample.Missing(p.X, p.Y));
                            continue;
                        }

                        usedMeasured = true;
                        var (bottom, top) = barrier.Value;

                        // The railing stands on the floor or tread beside it. That is a better
                        // answer than the minimum of its own geometry, which for coarse geometry is
                        // the bottom of the whole railing — the floor a storey down. A climbing
                        // railing only trusts surfaces with an outline: a level's slab is stored as
                        // one rectangle for the whole element, and the highest such rectangle under
                        // a stair railing is the floor of the level above.
                        var surface = climbs
                            ? ColliderGeometry.SurfaceHeightUnder(
                                manifest, p.X * Ft, -p.Y * Ft, top * Ft, SurfaceMinDropM)
                            : ColliderGeometry.SurfaceBaseAt(
                                manifest, p.X * Ft, -p.Y * Ft, top * Ft, SurfaceRadiusM,
                                SurfaceMinDropM);

                        if (surface is not null)
                        {
                            bottom = surface.Value / Ft;
                            top = bottom + Math.Max(height, MinBarrierFt);
                        }
                        else
                        {
                            var span = top - bottom;
                            if (span > height * MaxSpanRatio || span < height * MinSpanRatio)
                                top = bottom + Math.Max(height, MinBarrierFt);
                        }

                        samplesHere.Add(new ColliderGeometry.PathSample(p.X, p.Y, bottom, top));
                    }

                    var pieces = ColliderGeometry.AlongPath(samplesHere, ThicknessFt);
                    if (pieces.Count > 0)
                    {
                        candidates.AddRange(pieces);
                        if (outcome == "none")
                            outcome = usedSurface && !usedMeasured ? "surface" : "measured";
                    }
                }

                // ---- 2. no measurable geometry: place the barrier from the railing's extent
                if (candidates.Count == 0)
                {
                    // The element's bounding box still knows where the railing is even when its
                    // geometry cannot be read — unlike the path, whose Z is the level's elevation.
                    // Only trustworthy for a flat railing: a box spanning a whole stair flight
                    // would flatten a barrier into the bottom of the flight, so those are left
                    // unplaced and named in the report instead.
                    var span = extentFt;

                    if (bbox is not null && span > 0.15 / Ft && span <= FlatRailingFt)
                    {
                        var baseZ = bbox.Min.Z;
                        var topZ = Math.Min(bbox.Max.Z, baseZ + Math.Max(height, MinBarrierFt));

                        foreach (var curve in path)
                        {
                            if (curve is null) continue;
                            var verts = Tessellate(curve)
                                .Select(pt => new ColliderGeometry.PathSample(pt.X, pt.Y, baseZ, topZ))
                                .ToList();
                            candidates.AddRange(ColliderGeometry.AlongPath(verts, ThicknessFt));
                        }

                        if (candidates.Count > 0) outcome = "bbox";
                    }
                    else if (bbox is not null)
                    {
                        // a railing that climbs: its barrier has to follow the geometry, and there
                        // is none. Better to leave it out and say so than to wall off a stair.
                        outcome = "unplaced";
                    }
                }
            }

            // ---- 3. no path at all: fit the footprint -----------------------------
            if (candidates.Count == 0 && !climbs)
            {
                // Conservative on purpose, but bounded. One box over a flat railing cannot leak,
                // and a leakable railing means walking off a stair — a corner cut off a landing is
                // the better failure. It stops being conservative once the railing climbs or
                // sweeps: then the fitted rectangle is not the railing, it is a box across the
                // space the railing runs through, and no collider is the safer answer.
                GeometryElement? geometry;
                try { geometry = element.get_Geometry(FineOptions()); }
                catch { geometry = null; }

                var plan = new List<PlanPoint>();
                var minY = double.MaxValue;
                var maxY = double.MinValue;

                if (geometry is not null)
                {
                    CollectObstaclePoints(geometry, Transform.Identity, plan, ref minY, ref maxY, 0);
                }
                else
                {
                    if (bbox is not null)
                    {
                        plan.Add(new PlanPoint(bbox.Min.X, bbox.Min.Y));
                        plan.Add(new PlanPoint(bbox.Max.X, bbox.Min.Y));
                        plan.Add(new PlanPoint(bbox.Max.X, bbox.Max.Y));
                        plan.Add(new PlanPoint(bbox.Min.X, bbox.Max.Y));
                        minY = bbox.Min.Z;
                        maxY = bbox.Max.Z;
                    }
                }

                if (plan.Count >= 3 && maxY > minY && maxY - minY <= FlatRailingFt)
                {
                    var fit = PlanGeometry.MinAreaRect(plan);
                    if (fit is not null && 2 * fit.Value.HalfThickness * Ft <= MaxFootprintWidthM)
                    {
                        var top = Math.Max(maxY, minY + height);
                        var segment = ColliderGeometry.FromRect(fit.Value, minY, top);
                        if (segment is not null)
                        {
                            candidates.Add(segment);
                            outcome = "footprint";
                        }
                    }
                    else if (fit is not null)
                    {
                        footprintRejected++;
                    }
                }
            }

            if (candidates.Count == 0 && outcome == "none") outcome = "unplaced";

            var added = Emit(manifest, candidates, emitted, element.Id.Value);

            switch (outcome)
            {
                case "measured": measured++; break;
                case "path-3d": fromPath3D++; break;
                case "surface": fromSurface++; break;
                case "bbox": fromBox++; break;
                case "unplaced": unplaced++; break;
                case "footprint": fromFootprint++; break;
            }

            boxes += added;
            rayAggregateTotal += rayAggregates;

            // The path and the geometry are the two things a railing's collider is built from, and
            // when a barrier goes missing it is because they disagree. Neither was recorded, so
            // every diagnosis was an inference from the render mesh; these two strings end that.
            var pathDigest = path is null
                ? null
                : string.Join("; ", path.Take(4).Select((c, i) => c is null
                    ? $"c{i} null"
                    : $"c{i} ({c.GetEndPoint(0).X:0.00},{c.GetEndPoint(0).Y:0.00})->" +
                      $"({c.GetEndPoint(1).X:0.00},{c.GetEndPoint(1).Y:0.00})"));

            var spanDigest = spans.Count == 0
                ? null
                : string.Join(" | ", spans.Take(6).Select(s =>
                    $"z {s.MinZ:0.00}..{s.MaxZ:0.00} x {s.Plan.Min(p => p.X):0.00}..{s.Plan.Max(p => p.X):0.00} " +
                    $"y {s.Plan.Min(p => p.Y):0.00}..{s.Plan.Max(p => p.Y):0.00}"));

            reports.Add(new RailingReport(
                element.Id.Value,
                doc.GetElement(element.GetTypeId())?.Name,
                pathCurves,
                Math.Round(pathLength * Ft, 3),
                samples,
                samplesMissing,
                added,
                outcome,
                Math.Round(height * Ft, 3),
                candidates.Count > 0 ? candidates.Min(x => x.Y0) : 0f,
                candidates.Count > 0 ? candidates.Max(x => x.Y1) : 0f,
                strategy,
                spans.Count,
                rayHits,
                rayAggregates,
                pathDigest,
                spanDigest,
                misses.Count == 0 ? null : string.Join(" ", misses)));
        }

        return new RailingStats(
            found, noCollider, measured, fromPath3D, fromSurface, fromBox, unplaced, fromFootprint,
            footprintRejected, boxes, rayAggregateTotal, reports);
    }

    /// <summary>
    /// A barrier sample whose base is the surface under the path point, for a railing that climbs
    /// and whose own geometry cannot be measured at a point.
    ///
    /// The height above that surface is the railing type's own top-rail height, so the barrier
    /// stops where the railing does. Returns null when nothing walkable is under the point — a
    /// railing over a void has nowhere to stand, and guessing would put a barrier in mid air.
    /// </summary>
    private static ColliderGeometry.PathSample? FromSurface(
        SceneManifest manifest, double xFt, double yFt, double heightFt, double ceilingFt,
        double minDropM)
    {
        var baseM = ColliderGeometry.SurfaceHeightUnder(
            manifest, xFt * Ft, -yFt * Ft, ceilingFt * Ft, minDropM);

        if (baseM is null) return null;

        var bottom = baseM.Value / Ft;
        return new ColliderGeometry.PathSample(
            xFt, yFt, bottom, bottom + Math.Max(heightFt, MinBarrierFt));
    }

    /// <summary>
    /// A barrier sample for a flat railing that is really there but whose own geometry is an
    /// aggregate at this point: stood on the floor under the path, one railing type-height tall.
    ///
    /// Unlike the climbing case this may use a slab recorded as a rectangle, because that is what
    /// a level's floor is in the manifest, and a flat railing cannot be led astray by the floor of
    /// the level above the way a climbing one can.
    /// </summary>
    private static ColliderGeometry.PathSample? FromSurfaceOnFloor(
        SceneManifest manifest, double xFt, double yFt, double heightFt, double ceilingFt,
        double radiusM)
    {
        var baseM = ColliderGeometry.SurfaceBaseAt(
            manifest, xFt * Ft, -yFt * Ft, ceilingFt * Ft, radiusM, SurfaceMinDropM);

        if (baseM is null) return null;

        var bottom = baseM.Value / Ft;
        return new ColliderGeometry.PathSample(
            xFt, yFt, bottom, bottom + Math.Max(heightFt, MinBarrierFt));
    }

    /// <summary>
    /// Emits a railing's boxes, skipping any the model already contributed at the same place —
    /// a railing often arrives with its handrail as a second element on the same line — and
    /// stamping each with the element it came from.
    /// </summary>
    private static int Emit(
        SceneManifest manifest, List<WallSegment> candidates,
        HashSet<(int, int, int, int, int, int)> seen, long sourceId)
    {
        var added = 0;
        foreach (var s in candidates)
        {
            var key = (
                (int)Math.Round(s.X1 * 100), (int)Math.Round(s.Z1 * 100),
                (int)Math.Round(s.X2 * 100), (int)Math.Round(s.Z2 * 100),
                (int)Math.Round(s.Y0 * 100), (int)Math.Round(s.Y1 * 100));

            if (!seen.Add(key)) continue;

            s.SourceId = sourceId;
            manifest.Segments.Add(s);
            added++;
        }
        return added;
    }

    /// <summary>One solid of a railing, reduced to its plan shape and vertical extent (feet).</summary>
    private sealed class SolidSpan
    {
        public required List<PlanPoint> Plan;
        public double MinZ, MaxZ;

        /// <summary>
        /// The solid itself, already in project coordinates, when the geometry came from a solid
        /// rather than a mesh. Kept so a path point can be tested with a ray: the plan polygon
        /// above is the solid's bounding rectangle, which for an L-shaped or twin-curve railing
        /// covers ground the rail never crosses.
        /// </summary>
        public Solid? Source;
    }

    /// <summary>What a vertical ray through a railing found at one plan point (feet).</summary>
    private readonly record struct RayHit(bool Hit, bool AggregateOnly, double Bottom, double Top);

    /// <summary>
    /// Bottom and top of the railing at a plan point, from a vertical ray through its own solids.
    ///
    /// The plan-rectangle test above answers "is this point inside the box around a solid", which
    /// is not the same question as "is there railing here". On the Silo the two answers differed
    /// both ways: 2.4 m of visible level railing had no barrier because the only solid covering it
    /// was an aggregate and got skipped, and 230 pieces of barrier ran on through empty space
    /// because the path wandered inside a solid's rectangle after the railing had turned away.
    /// </summary>
    private static RayHit MeasureBarrierByRay(
        List<SolidSpan> spans, double xFt, double yFt, double aggregateSpanFt, double reachFt)
    {
        var minZ = double.MaxValue;
        var maxZ = double.MinValue;
        foreach (var span in spans)
        {
            if (span.MinZ < minZ) minZ = span.MinZ;
            if (span.MaxZ > maxZ) maxZ = span.MaxZ;
        }
        if (minZ > maxZ) return default;

        Line ray;
        try
        {
            // Path samples arrive in Revit coordinates, and so do the spans, so the ray takes them
            // as they are. Flipping Y here (as the manifest lookups must, because the scene is in
            // glTF space) put every ray on the mirrored side of the model.
            ray = Line.CreateBound(
                new XYZ(xFt, yFt, maxZ + 1.0),
                new XYZ(xFt, yFt, minZ - 1.0));
        }
        catch { return default; }

        var options = new SolidCurveIntersectionOptions
        {
            ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
        };

        var hit = false;
        var bottom = double.MaxValue;
        var top = double.MinValue;

        foreach (var span in spans)
        {
            if (span.Source is null) continue;
            if (PlanGeometry.DistanceToPolygon(span.Plan, xFt, yFt) > reachFt) continue;

            SolidCurveIntersection? result;
            try { result = span.Source.IntersectWithCurve(ray, options); }
            catch { continue; }

            if (result is null || result.SegmentCount == 0) continue;

            var lo = double.MaxValue;
            var hi = double.MinValue;
            for (var i = 0; i < result.SegmentCount; i++)
            {
                Curve? segment;
                try { segment = result.GetCurveSegment(i); }
                catch { continue; }
                if (segment is null) continue;

                foreach (var end in new[] { segment.GetEndPoint(0), segment.GetEndPoint(1) })
                {
                    if (end.Z < lo) lo = end.Z;
                    if (end.Z > hi) hi = end.Z;
                }
            }
            if (lo > hi) continue;

            hit = true;

            // the same aggregate case as the rectangle test: one solid standing in for the whole
            // railing says nothing about the height at one point along it
            if (hi - lo > aggregateSpanFt) continue;

            if (lo < bottom) bottom = lo;
            if (hi > top) top = hi;
        }

        if (!hit) return default;
        return bottom > top ? new RayHit(true, true, 0, 0) : new RayHit(true, false, bottom, top);
    }

    /// <summary>
    /// Bottom and top of the railing at a plan point, in feet, or null when no part of the railing
    /// spans that point — the signal that the path has run past the railing.
    ///
    /// Asking which solids span the point, rather than which corners are nearby, is what makes
    /// this work along a rail. A 4 m rail's bounding box has corners only at its ends, so a point
    /// in the middle of the rail is inside the solid and nowhere near a corner; measuring corners
    /// therefore leaves gaps between posts, which is a hole you can walk through.
    /// </summary>
    private static (double Bottom, double Top)? MeasureBarrier(
        List<SolidSpan> solids, double x, double y, double tolerance, double maxSolidSpanFt)
    {
        var found = false;
        var bottom = double.MaxValue;
        var top = double.MinValue;

        foreach (var solid in solids)
        {
            if (PlanGeometry.DistanceToPolygon(solid.Plan, x, y) > tolerance) continue;

            // a solid spanning the whole railing cannot place a barrier at one point along it
            if (solid.MaxZ - solid.MinZ > maxSolidSpanFt) continue;

            found = true;
            if (solid.MinZ < bottom) bottom = solid.MinZ;
            if (solid.MaxZ > top) top = solid.MaxZ;
        }

        return found ? (bottom, top) : null;
    }

    /// <summary>
    /// Whether a point lies inside the railing's own plan footprint (the union of its solids'
    /// plan bounds), within a slack.
    ///
    /// A railing's *reported* geometry is only a stand-in for it, and at coarse detail that stand-in
    /// is a slanted box whose edge can miss the railing by most of a metre. The path is the better
    /// guide to where the railing is, so where the stand-in fails to reach the path the barrier is
    /// still placed — but only inside the element's own extent, so a path that runs away from the
    /// railing cannot conjure barriers in open floor.
    /// </summary>
    private static bool InsidePlanExtent(List<SolidSpan> spans, double x, double y, double slackFt)
    {
        if (spans.Count == 0) return false;

        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;

        foreach (var span in spans)
        foreach (var point in span.Plan)
        {
            if (point.X < minX) minX = point.X;
            if (point.X > maxX) maxX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.Y > maxY) maxY = point.Y;
        }

        return x >= minX - slackFt && x <= maxX + slackFt &&
               y >= minY - slackFt && y <= maxY + slackFt;
    }

    /// <summary>
    /// Reads a railing's geometry, trying the option sets that actually return it.
    ///
    /// A railing is a view-dependent element built from sub-elements, and an Options without a
    /// view can come back empty for one — which is how 98 railings in a real model ended up with
    /// no barrier at all while still rendering. The strategy that worked is reported per railing,
    /// so a model where none of them work says so instead of failing silently.
    /// </summary>
    private static bool TryCollectSpans(Element element, View view, List<SolidSpan> spans, out string strategy)
    {
        var attempts = new (string Name, Func<Options> Make)[]
        {
            ("view-fine", () => FineOptions(view)),
            ("fine", () => FineOptions()),
            ("view-medium-visible", () => new Options
            {
                ComputeReferences = false, IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Medium, View = view,
            }),
            ("coarse-visible", () => new Options
            {
                ComputeReferences = false, IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Coarse,
            }),
        };

        foreach (var (name, make) in attempts)
        {
            GeometryElement? geometry;
            try { geometry = element.get_Geometry(make()); }
            catch { continue; }
            if (geometry is null) continue;

            CollectSpans(geometry, Transform.Identity, spans, 0);
            if (spans.Count > 0) { strategy = name; return true; }
        }

        strategy = "none";
        return false;
    }

    /// <summary>
    /// Reads the railing again at a detail level the first ladder never tries: fine geometry that
    /// includes non-visible objects, with and without the view.
    ///
    /// <c>get_Geometry</c> answers with the view-dependent representation, and for a railing the
    /// fine pass can come back empty while its elements still render — which is the case that
    /// leaves a gap in the barrier with nothing in the report to explain it. This is the cheap
    /// retry; <c>spanDigest</c> in the report says what it found.
    /// </summary>
    private static bool TryCollectFineNonVisible(Element element, View view, List<SolidSpan> spans)
    {
        foreach (var make in new Func<Options>[]
                 {
                     () => new Options
                     {
                         ComputeReferences = false, IncludeNonVisibleObjects = true,
                         DetailLevel = ViewDetailLevel.Fine, View = view,
                     },
                     () => new Options
                     {
                         ComputeReferences = false, IncludeNonVisibleObjects = true,
                         DetailLevel = ViewDetailLevel.Fine,
                     },
                 })
        {
            GeometryElement? geometry;
            try { geometry = element.get_Geometry(make()); }
            catch { continue; }
            if (geometry is null) continue;

            CollectSpans(geometry, Transform.Identity, spans, 0);
            if (spans.Count > 0) return true;
        }

        return false;
    }

    private static void CollectSpans(
        GeometryElement geometry, Transform transform, List<SolidSpan> spans, int depth)
    {
        if (depth > 10) return;

        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case GeometryInstance instance:
                {
                    GeometryElement? symbol = null;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch { symbol = null; }

                    if (symbol is not null)
                    {
                        CollectSpans(symbol, transform.Multiply(instance.Transform), spans, depth + 1);
                        break;
                    }

                    // some nested instances only expose project-space geometry
                    GeometryElement? placed = null;
                    try { placed = instance.GetInstanceGeometry(); }
                    catch { placed = null; }
                    if (placed is not null)
                        CollectSpans(placed, transform, spans, depth + 1);

                    break;
                }

                case Solid solid when solid.Faces.Size > 0:
                {
                    var box = solid.GetBoundingBox();
                    if (box is null) break;

                    var corners = new List<PlanPoint>(4);
                    var minZ = double.MaxValue;
                    var maxZ = double.MinValue;

                    // the four plan corners, taken at both heights so a tilted solid still reports
                    // its true vertical extent
                    for (var i = 0; i < 4; i++)
                    {
                        var x = (i & 1) == 0 ? box.Min.X : box.Max.X;
                        var y = (i & 2) == 0 ? box.Min.Y : box.Max.Y;

                        var low = transform.OfPoint(box.Transform.OfPoint(new XYZ(x, y, box.Min.Z)));
                        var high = transform.OfPoint(box.Transform.OfPoint(new XYZ(x, y, box.Max.Z)));

                        corners.Add(new PlanPoint(low.X, low.Y));
                        minZ = Math.Min(minZ, Math.Min(low.Z, high.Z));
                        maxZ = Math.Max(maxZ, Math.Max(low.Z, high.Z));
                    }

                    Solid? source = null;
                    try
                    {
                        source = transform.IsIdentity ? solid : SolidUtils.CreateTransformed(solid, transform);
                    }
                    catch { source = null; }

                    spans.Add(new SolidSpan { Plan = corners, MinZ = minZ, MaxZ = maxZ, Source = source });
                    break;
                }

                case Mesh mesh when mesh.NumTriangles > 0:
                {
                    var corners = new List<PlanPoint>();
                    var minZ = double.MaxValue;
                    var maxZ = double.MinValue;

                    foreach (var v in mesh.Vertices)
                    {
                        var world = transform.OfPoint(v);
                        corners.Add(new PlanPoint(world.X, world.Y));
                        minZ = Math.Min(minZ, world.Z);
                        maxZ = Math.Max(maxZ, world.Z);
                    }

                    var hull = PlanGeometry.ConvexHull(corners);
                    if (hull.Count >= 3)
                        spans.Add(new SolidSpan { Plan = hull, MinZ = minZ, MaxZ = maxZ });
                    break;
                }
            }
        }
    }

    /// <summary>
    /// True when the railing's type carries the 'No Collider' yes/no parameter set.
    ///
    /// A railing is a convenient line-work element, so it is used for parking bay edges, planting
    /// borders and other drawing marks. Those are part of the model but not part of the building
    /// you walk through, and a barrier where a parking line is would be baffling in the headset.
    /// The parameter is read from the type first, then the instance, so either placement works.
    /// </summary>
    private static bool HasNoColliderFlag(Document doc, Element element)
    {
        const string Name = "No Collider";

        var type = doc.GetElement(element.GetTypeId());
        var parameter = FindParameter(type, Name) ?? FindParameter(element, Name);
        if (parameter is null || !parameter.HasValue) return false;

        return parameter.StorageType switch
        {
            StorageType.Integer => parameter.AsInteger() != 0,
            StorageType.String => IsTruthy(parameter.AsString()),
            _ => false,
        };
    }

    /// <summary>
    /// Looks a parameter up by name, ignoring case and surrounding space — a parameter the user
    /// added by hand can arrive as 'No collider' or ' No Collider'.
    /// </summary>
    private static Parameter? FindParameter(Element? element, string name)
    {
        if (element is null) return null;

        var exact = element.LookupParameter(name);
        if (exact is not null) return exact;

        foreach (Parameter parameter in element.Parameters)
        {
            var candidate = parameter.Definition?.Name;
            if (candidate is null) continue;
            if (string.Equals(candidate.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return parameter;
        }

        return null;
    }

    private static bool IsTruthy(string? text)
    {
        var value = text?.Trim();
        return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || value == "1";
    }

    /// <summary>
    /// Height of the barrier above the railing's path, in feet.
    ///
    /// A railing type without a top rail reports zero, and a nonsense number here decides whether
    /// the railing stops you at all, so anything outside a plausible railing height falls back to
    /// the 914 mm code default.
    /// </summary>
    private static double RailingHeight(Document doc, Element element)
    {
        const double Default = 3.0;                 // 914 mm
        var type = doc.GetElement(element.GetTypeId()) as RailingType;
        var height = type?.TopRailHeight ?? 0;
        return height > 0.5 && height < 12 ? height : Default;
    }

    /// <summary>
    /// Cuts a gap in whichever wall each door belongs to.
    ///
    /// The wall is taken from the door's Host when there is one — that is exact, and immune to
    /// the geometry lying. Otherwise the nearest wall run by distance to its tessellated
    /// centreline is used. Note this deliberately does not use Curve.Project's Parameter, which
    /// is unnormalised and whose meaning depends on the curve type; distances are measured
    /// against the polyline instead so the arithmetic is explicit.
    /// </summary>
    private static ((int Found, int Matched, int Unmatched) Stats, int Openings) CutDoorOpenings(
        Document doc, View view, List<WallRun> runs)
    {
        var found = 0;
        var matched = 0;
        var openings = 0;

        var doors = new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var door in doors)
        {
            found++;

            var bbox = door.get_BoundingBox(view) ?? door.get_BoundingBox(null);
            if (bbox is null) continue;
            var center = (bbox.Min + bbox.Max) * 0.5;

            var targets = new List<WallRun>();

            // 1. exact: the wall Revit says this door is hosted by. For a normal door that is the
            //    wall; for one set into a curtain wall it is the curtain wall, which is also a Wall.
            if (door is FamilyInstance fi && fi.Host is Wall hostWall)
            {
                var hosted = runs.FirstOrDefault(r => r.Id == hostWall.Id);
                if (hosted is not null) targets.Add(hosted);
            }

            // 2. geometry: every run whose centreline the door actually sits on. Curtain wall
            //    doors are often hosted to a panel rather than the wall, and a curtain wall can
            //    arrive as more than one run, so all matches get cut rather than just the nearest.
            if (targets.Count == 0)
            {
                foreach (var run in runs)
                {
                    var (_, distance) = ClosestOnRun(run, center);
                    // half the wall thickness plus a little, or 35 cm — whichever is larger
                    var tolerance = Math.Max(run.Thickness / 2 + 0.15 / Ft, 0.35 / Ft);
                    if (distance <= tolerance) targets.Add(run);
                }
            }

            if (targets.Count == 0) continue;

            var width = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0;
            if (width <= 0)
            {
                // measure the door's own footprint along the wall direction
                var (a, b) = ExtentAlongRun(targets[0], bbox);
                width = b - a;
            }
            if (width <= 0) continue;

            var half = width / 2 + Clearance;
            var cutAny = false;

            foreach (var run in targets)
            {
                var (along, _) = ClosestOnRun(run, center);
                var start = Math.Max(0, along - half);
                var end = Math.Min(run.Length, along + half);
                if (end - start < 0.1) continue;

                run.Cuts.Add((start, end));
                cutAny = true;
            }

            if (!cutAny) continue;
            matched++;
            openings++;
        }

        return ((found, matched, found - matched), openings);
    }

    private static FloorStats AddFloorSurfaces(Document doc, View view, SceneManifest manifest)
    {
        // Roofs count as walkable ground: a flat roof should behave exactly like a floor slab.
        var walkable = new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs };

        var outlined = 0;
        var withHoles = 0;
        var fromBox = 0;
        var boxIds = new List<long>();
        var sloped = 0;
        var maxDrop = 0.0;

        foreach (var category in walkable)
        {
            foreach (var element in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                var shells = TopShells(element, view);
                if (shells.Count > 0)
                {
                    foreach (var shell in shells)
                    {
                        var surface = SurfaceFromShell(shell);
                        if (surface is null) continue;
                        manifest.Surfaces.Add(surface);
                        outlined++;
                        if (surface.H is { Length: > 0 }) withHoles++;
                        if (shell.DropM > OutlineToleranceM)
                        {
                            sloped++;
                            maxDrop = Math.Max(maxDrop, shell.DropM);
                        }
                    }
                    continue;
                }

                var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
                if (bbox is null) continue;
                if (bbox.Max.X - bbox.Min.X < 1e-3 || bbox.Max.Y - bbox.Min.Y < 1e-3) continue;

                manifest.Surfaces.Add(new WalkSurface
                {
                    MinX = (float)(bbox.Min.X * Ft),
                    MinZ = (float)(-bbox.Max.Y * Ft),
                    MaxX = (float)(bbox.Max.X * Ft),
                    MaxZ = (float)(-bbox.Min.Y * Ft),
                    Y = (float)(bbox.Max.Z * Ft),
                    Slab = true,
                });
                fromBox++;
                if (boxIds.Count < 8) boxIds.Add(element.Id.Value);
            }
        }

        if (manifest.Surfaces.Count > 0)
            return new FloorStats(outlined, withHoles, fromBox, boxIds, sloped, maxDrop);

        // Fallback: treat every level as an infinite walkable plane across the model extents.
        var modelBox = ModelBounds(doc, view);
        if (modelBox is null)
            return new FloorStats(outlined, withHoles, fromBox, boxIds, sloped, maxDrop);
        foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
        {
            manifest.Surfaces.Add(new WalkSurface
            {
                MinX = (float)(modelBox.Min.X * Ft) - 5f,
                MinZ = (float)(-modelBox.Max.Y * Ft) - 5f,
                MaxX = (float)(modelBox.Max.X * Ft) + 5f,
                MaxZ = (float)(-modelBox.Min.Y * Ft) + 5f,
                Y = (float)(level.Elevation * Ft),
                Slab = true,
            });
        }

        return new FloorStats(outlined, withHoles, fromBox, boxIds, sloped, maxDrop);
    }

    /// <summary>
    /// The upward-facing faces of a slab, as outlines with the holes cut out of them.
    ///
    /// A slab's bounding box is not the slab. A floor drawn around a stairwell is a ring, and its
    /// box spans the opening — so the walkable world gains a phantom floor across the top of the
    /// stair, the ground snap holds the player at storey level for as long as the head is over the
    /// stair, and the flight cannot be descended. The debug overlay shows it plainly as one green
    /// square across the whole storey. The same is true of a lift shaft, a duct penetration, an
    /// atrium edge, or any other inner loop in the sketch.
    ///
    /// So read the face itself: the outer loop is the outline, every loop inside it is a hole. The
    /// ladder of geometry requests mirrors the railing one, because <c>get_Geometry</c> answers with
    /// the view-dependent representation and a floor can come back empty from a request that works
    /// for the wall beside it.
    /// </summary>
    private static List<TopShell> TopShells(Element element, View view)
    {
        var attempts = new Func<Options>[]
        {
            () => FineOptions(view),
            () => FineOptions(),
            () => new Options
            {
                ComputeReferences = false, IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine, View = view,
            },
            () => new Options
            {
                ComputeReferences = false, IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Coarse,
            },
        };

        foreach (var make in attempts)
        {
            GeometryElement? geometry;
            try { geometry = element.get_Geometry(make()); }
            catch { continue; }
            if (geometry is null) continue;

            var shells = new List<TopShell>();
            CollectTopShells(geometry, Transform.Identity, shells, 0);
            if (shells.Count == 0) continue;

            if (shells.Count > MaxFacesPerSlab)
            {
                // a slab split into hundreds of faces is a pathological sketch or a site pad, and
                // 500 surfaces for one element would cost the viewer more than it is worth
                shells.Sort((a, b) => b.Shell.Area.CompareTo(a.Shell.Area));
                shells.RemoveRange(MaxFacesPerSlab, shells.Count - MaxFacesPerSlab);
            }

            return shells;
        }

        return new List<TopShell>();
    }

    private static void CollectTopShells(
        GeometryElement geometry, Transform transform, List<TopShell> shells, int depth)
    {
        if (depth > 10) return;

        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case GeometryInstance instance:
                {
                    GeometryElement? symbol = null;
                    try { symbol = instance.GetSymbolGeometry(); } catch { symbol = null; }
                    if (symbol is not null)
                    {
                        CollectTopShells(symbol, transform.Multiply(instance.Transform), shells, depth + 1);
                        break;
                    }

                    GeometryElement? placed = null;
                    try { placed = instance.GetInstanceGeometry(); } catch { placed = null; }
                    if (placed is not null) CollectTopShells(placed, transform, shells, depth + 1);
                    break;
                }

                case Solid solid when solid.Faces.Size > 0:
                    foreach (Face face in solid.Faces)
                    {
                        var shell = TopShellOf(face, transform);
                        if (shell is not null) shells.Add(shell.Value);
                    }
                    break;
            }
        }
    }

    /// <summary>One upward-facing planar face as an outline plus holes, or null when it is not one.</summary>
    private static TopShell? TopShellOf(Face face, Transform transform)
    {
        if (face is not PlanarFace planar) return null;

        // Only the surface you can stand on. Everything steeper than about 25 degrees is a side
        // face or a stair soffit, and a steeply sloped slab is left to the level fallback rather
        // than claimed as floor you can walk up.
        var normal = transform.OfVector(planar.FaceNormal);
        if (normal.Z < 0.9) return null;

        IList<CurveLoop>? loops;
        try { loops = planar.GetEdgesAsCurveLoops(); }
        catch { return null; }
        if (loops is null || loops.Count == 0) return null;

        var planLoops = new List<IReadOnlyList<PlanPoint>>(loops.Count);
        var minY = double.MaxValue;
        var maxY = double.MinValue;

        foreach (CurveLoop loop in loops)
        {
            var points = new List<PlanPoint>();

            foreach (Curve curve in loop)
            {
                List<XYZ> curvePoints;
                try { curvePoints = Tessellate(curve); }
                catch { continue; }

                foreach (var p in curvePoints)
                {
                    var world = transform.OfPoint(p);
                    points.Add(new PlanPoint(world.X * Ft, -world.Y * Ft));
                    minY = Math.Min(minY, world.Z * Ft);
                    maxY = Math.Max(maxY, world.Z * Ft);
                }
            }

            var thinned = PlanGeometry.Simplify(points, OutlineToleranceM);
            if (thinned.Count >= 3) planLoops.Add(thinned);
        }

        if (planLoops.Count == 0) return null;

        var shell = PlanGeometry.Shell(planLoops, out _);
        if (shell is null) return null;

        return new TopShell(shell.Value, maxY, maxY - minY);
    }

    private static WalkSurface? SurfaceFromShell(TopShell top)
    {
        var outer = top.Shell.Outer;
        if (outer.Count < 3) return null;

        var flat = FlatOutline(outer);
        if (flat.Length < 6) return null;

        var surface = new WalkSurface
        {
            MinX = (float)outer.Min(p => p.X),
            MaxX = (float)outer.Max(p => p.X),
            MinZ = (float)outer.Min(p => p.Y),
            MaxZ = (float)outer.Max(p => p.Y),
            Y = (float)top.TopM,
            P = flat,
            Slab = true,          // railing bases keep treating this as a rectangle; see WalkSurface.Slab
        };

        if (top.Shell.Holes.Count > 0)
        {
            var holes = new List<float[]>(top.Shell.Holes.Count);
            foreach (var hole in top.Shell.Holes)
            {
                var flatHole = FlatOutline(hole);
                if (flatHole.Length >= 6) holes.Add(flatHole);
            }
            if (holes.Count > 0) surface.H = holes.ToArray();
        }

        return surface;
    }

    private static float[] FlatOutline(IReadOnlyList<PlanPoint> points)
    {
        var flat = new float[points.Count * 2];
        for (var i = 0; i < points.Count; i++)
        {
            flat[i * 2] = (float)points[i].X;
            flat[i * 2 + 1] = (float)points[i].Y;
        }
        return flat;
    }

    /// <summary>
    /// Stair treads become walkable surfaces so you can climb without switching to fly mode.
    ///
    /// Each tread is taken from the upward-facing planar faces of the stair geometry, at that
    /// face's own height, and emitted as a polygon rather than a bounding box — a flight on the
    /// diagonal would otherwise become a walkable slab spanning far wider than the stair.
    /// </summary>
    private static int AddStairSurfaces(Document doc, View view, SceneManifest manifest)
    {
        var categories = new[]
        {
            BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_StairsRuns,
            BuiltInCategory.OST_StairsLandings,
        };

        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine,
        };

        // a Stairs element usually contains its runs and landings, so the same tread often
        // arrives twice; key on position and height to keep the list honest
        var seen = new HashSet<(int, int, int)>();
        var added = 0;

        foreach (var category in categories)
        {
            foreach (var element in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                GeometryElement? geometry;
                try { geometry = element.get_Geometry(options); }
                catch { continue; }
                if (geometry is null) continue;

                WalkStairs(geometry, Transform.Identity, manifest, seen, ref added, 0);
            }
        }

        return added;
    }

    private static void WalkStairs(GeometryElement geometry, Transform transform,
        SceneManifest manifest, HashSet<(int, int, int)> seen, ref int added, int depth)
    {
        if (depth > 10) return;

        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case GeometryInstance instance:
                {
                    GeometryElement? symbol;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch { continue; }
                    if (symbol is null) continue;
                    WalkStairs(symbol, transform.Multiply(instance.Transform), manifest, seen, ref added, depth + 1);
                    break;
                }

                case Solid solid when solid.Faces.Size > 0:
                    foreach (Face face in solid.Faces)
                        TryAddTread(face, transform, manifest, seen, ref added);
                    break;
            }
        }
    }

    private static void TryAddTread(Face face, Transform transform, SceneManifest manifest,
        HashSet<(int, int, int)> seen, ref int added)
    {
        if (face is not PlanarFace planar) return;

        var normal = transform.OfVector(planar.FaceNormal);
        if (normal.Z < 0.9) return;                 // only upward-facing surfaces

        List<(double X, double Z)>? best = null;
        var bestArea = 0.0;
        var bestY = 0.0;

        foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
        {
            var points = new List<(double X, double Z)>();
            var minY = double.MaxValue;
            var maxY = double.MinValue;

            foreach (Curve curve in loop)
            {
                foreach (var p in Tessellate(curve))
                {
                    var world = transform.OfPoint(p);
                    points.Add((world.X * Ft, -world.Y * Ft));
                    var y = world.Z * Ft;
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (points.Count < 3) continue;
            if (maxY - minY > 0.05) continue;       // a ramp, not a tread — leave it flat-approximated

            var area = Math.Abs(Shoelace(points));
            if (area <= bestArea) continue;

            bestArea = area;
            best = points;
            bestY = maxY;
        }

        if (best is null || bestArea < 0.01) return;   // ignore slivers

        var cx = best.Average(p => p.X);
        var cz = best.Average(p => p.Z);
        var key = ((int)Math.Round(cx * 10), (int)Math.Round(cz * 10), (int)Math.Round(bestY * 100));
        if (!seen.Add(key)) return;

        var surface = new WalkSurface
        {
            MinX = (float)best.Min(p => p.X),
            MaxX = (float)best.Max(p => p.X),
            MinZ = (float)best.Min(p => p.Z),
            MaxZ = (float)best.Max(p => p.Z),
            Y = (float)bestY,
        };

        // keep the polygon exact unless it is too complex to be worth carrying
        if (best.Count >= 3 && best.Count <= 24)
        {
            var flat = new float[best.Count * 2];
            for (var i = 0; i < best.Count; i++)
            {
                flat[i * 2] = (float)best[i].X;
                flat[i * 2 + 1] = (float)best[i].Z;
            }
            surface.P = flat;
        }

        manifest.Surfaces.Add(surface);
        added++;
    }

    /// <summary>
    /// Topography as walkable ground.
    ///
    /// A hillside cannot be described by the one-height-per-surface model used for slabs and
    /// treads, so terrain is emitted as triangles and the viewer interpolates height across each
    /// one. Every triangle is tested for itself rather than per face, because a toposolid's top
    /// is usually a single non-planar face — there is no one normal to trust.
    /// </summary>
    private static int AddTerrainTriangles(Document doc, View view, SceneManifest manifest)
    {
        var categories = new[]
        {
            BuiltInCategory.OST_Topography,
            BuiltInCategory.OST_TopographySurface,
            BuiltInCategory.OST_BuildingPad,
        };

        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine,
        };

        var added = 0;
        var elements = 0;

        foreach (var category in categories)
        {
            foreach (var element in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                GeometryElement? geometry;
                try { geometry = element.get_Geometry(options); }
                catch { continue; }
                if (geometry is null) continue;

                elements++;
                WalkTerrain(geometry, Transform.Identity, manifest, ref added, 0);
            }
        }

        if (elements > 0 && added == 0)
            manifest.Warnings.Add(
                "Topography was found but produced no walkable triangles — its faces may be wound the other way.");

        return added;
    }

    private static void WalkTerrain(GeometryElement geometry, Transform transform,
        SceneManifest manifest, ref int added, int depth)
    {
        if (depth > 10) return;

        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case GeometryInstance instance:
                {
                    GeometryElement? symbol;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch { continue; }
                    if (symbol is null) continue;
                    WalkTerrain(symbol, transform.Multiply(instance.Transform), manifest, ref added, depth + 1);
                    break;
                }

                case Solid solid when solid.Faces.Size > 0:
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh;
                        try { mesh = face.Triangulate(TessellationLod); }
                        catch { continue; }
                        if (mesh is not null) EmitTerrainMesh(mesh, transform, manifest, ref added);
                    }
                    break;

                case Mesh direct when direct.NumTriangles > 0:
                    EmitTerrainMesh(direct, transform, manifest, ref added);
                    break;
            }
        }
    }

    private static void EmitTerrainMesh(Mesh mesh, Transform transform, SceneManifest manifest, ref int added)
    {
        for (var i = 0; i < mesh.NumTriangles; i++)
        {
            var tri = mesh.get_Triangle(i);
            var a = transform.OfPoint(tri.get_Vertex(0));
            var b = transform.OfPoint(tri.get_Vertex(1));
            var c = transform.OfPoint(tri.get_Vertex(2));

            var normal = (b - a).CrossProduct(c - a);
            if (normal.GetLength() < 1e-9) continue;
            normal = normal.Normalize();
            if (normal.Z < 0.35) continue;      // steeper than ~70 degrees is not walkable ground

            manifest.Terrain ??= new List<float[]>();
            manifest.Terrain.Add(new[]
            {
                (float)(a.X * Ft), (float)(a.Z * Ft), (float)(-a.Y * Ft),
                (float)(b.X * Ft), (float)(b.Z * Ft), (float)(-b.Y * Ft),
                (float)(c.X * Ft), (float)(c.Z * Ft), (float)(-c.Y * Ft),
            });
            added++;

            if (added > 120_000)
            {
                manifest.Warnings.Add("Terrain truncated at 120,000 triangles.");
                return;
            }
        }
    }

    private static int AddInstanceObstacles(Document doc, View view, SceneManifest manifest)
    {
        var added = 0;

        foreach (var category in ObstacleCategories)
        {
            foreach (var element in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                GeometryElement? geometry;
                try { geometry = element.get_Geometry(FineOptions()); }
                catch { continue; }
                if (geometry is null) continue;

                var points = new List<PlanPoint>();
                var minY = double.MaxValue;
                var maxY = double.MinValue;

                CollectObstaclePoints(geometry, Transform.Identity, points, ref minY, ref maxY, 0);

                if (points.Count < 3 || maxY <= minY) continue;

                var fit = PlanGeometry.MinAreaRect(points);
                if (fit is null) continue;

                // Emitted as an oriented box, which is the same primitive walls use — so the
                // viewer's existing capsule push-out handles furniture with no new code path.
                var segment = ColliderGeometry.FromRect(fit.Value, minY, maxY);
                if (segment is null) continue;

                manifest.Segments.Add(segment);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Shared geometry request for collision harvests. Fine detail is forced regardless of the
    /// source view's settings, for the same reason the render export does: a coarse view would
    /// otherwise hide the geometry the collider is fitted to.
    /// </summary>
    private static Options FineOptions(View? view = null) => new()
    {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Fine,
        View = view,
    };

    private static void CollectObstaclePoints(GeometryElement geometry, Transform transform,
        List<PlanPoint> points, ref double minY, ref double maxY, int depth)
    {
        if (depth > 10) return;

        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case GeometryInstance instance:
                {
                    GeometryElement? symbol;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch { continue; }
                    if (symbol is null) continue;
                    CollectObstaclePoints(symbol, transform.Multiply(instance.Transform), points, ref minY, ref maxY, depth + 1);
                    break;
                }

                case Solid solid when solid.Faces.Size > 0:
                {
                    // The solid's own bounding box carries the instance rotation once transformed,
                    // which is all the minimum-area fit needs.
                    var box = solid.GetBoundingBox();
                    if (box is null) break;

                    for (var i = 0; i < 8; i++)
                    {
                        var x = (i & 1) == 0 ? box.Min.X : box.Max.X;
                        var y = (i & 2) == 0 ? box.Min.Y : box.Max.Y;
                        var z = (i & 4) == 0 ? box.Min.Z : box.Max.Z;
                        var world = transform.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)));
                        points.Add(new PlanPoint(world.X, world.Y));
                        minY = Math.Min(minY, world.Z);
                        maxY = Math.Max(maxY, world.Z);
                    }
                    break;
                }

                case Mesh mesh when mesh.NumTriangles > 0:
                {
                    foreach (var v in mesh.Vertices)
                    {
                        var world = transform.OfPoint(v);
                        points.Add(new PlanPoint(world.X, world.Y));
                        minY = Math.Min(minY, world.Z);
                        maxY = Math.Max(maxY, world.Z);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Signed area of a flat XY polygon.</summary>
    private static double Shoelace(List<(double X, double Z)> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var j = (i + 1) % points.Count;
            sum += points[i].X * points[j].Z - points[j].X * points[i].Z;
        }
        return sum / 2.0;
    }

    private static BoundingBoxXYZ? ModelBounds(Document doc, View view)
    {
        var box = new BoundingBoxXYZ();
        var any = false;
        foreach (var el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
        {
            var b = el.get_BoundingBox(view);
            if (b is null) continue;
            if (!any) { box.Min = b.Min; box.Max = b.Max; any = true; }
            else
            {
                box.Min = new XYZ(Math.Min(box.Min.X, b.Min.X), Math.Min(box.Min.Y, b.Min.Y), Math.Min(box.Min.Z, b.Min.Z));
                box.Max = new XYZ(Math.Max(box.Max.X, b.Max.X), Math.Max(box.Max.Y, b.Max.Y), Math.Max(box.Max.Z, b.Max.Z));
            }
        }
        return any ? box : null;
    }

    // ---------------------------------------------------------------- geometry helpers

    private static List<XYZ> Tessellate(Curve curve)
    {
        if (curve is Line line) return new List<XYZ> { line.GetEndPoint(0), line.GetEndPoint(1) };
        var pts = curve.Tessellate().ToList();
        return pts.Count >= 2 ? pts : new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };
    }

    /// <summary>
    /// Nearest point on the run's polyline: returns distance along the run and distance from it.
    /// All in feet.
    /// </summary>
    private static (double Along, double Distance) ClosestOnRun(WallRun run, XYZ p)
        => WallGeometry.ClosestOnPolyline(run.Plan, run.Cum, p.X, p.Y);

    /// <summary>Span of a bounding box projected onto the run's direction, in feet along it.</summary>
    private static (double A, double B) ExtentAlongRun(WallRun run, BoundingBoxXYZ bbox)
    {
        var lo = double.MaxValue;
        var hi = double.MinValue;
        var corners = new[]
        {
            new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z), new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
            new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z), new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
            new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z), new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
            new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z), new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z),
        };
        foreach (var c in corners)
        {
            var (along, _) = ClosestOnRun(run, c);
            lo = Math.Min(lo, along);
            hi = Math.Max(hi, along);
        }
        return (lo, hi);
    }

    private static XYZ PointAt(WallRun run, double d)
    {
        if (d <= 0) return run.Pts[0];
        if (d >= run.Length) return run.Pts[^1];
        for (var i = 0; i + 1 < run.Cum.Length; i++)
        {
            if (d > run.Cum[i + 1]) continue;
            var span = run.Cum[i + 1] - run.Cum[i];
            var t = span < 1e-9 ? 0 : (d - run.Cum[i]) / span;
            var p = run.Pts[i];
            var q = run.Pts[i + 1];
            return new XYZ(
                p.X + (q.X - p.X) * t,
                p.Y + (q.Y - p.Y) * t,
                p.Z + (q.Z - p.Z) * t);
        }
        return run.Pts[^1];
    }
}
