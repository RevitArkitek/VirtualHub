using Autodesk.Revit.DB;
using RevitVr.Core.Scene;

namespace RevitVr.Addin.Export;

public sealed record ColliderStats(
    int WallRuns,
    int Segments,
    int Openings,
    int DoorsFound,
    int DoorsMatched,
    int DoorsUnmatched);

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

        AddFloorSurfaces(doc, view, manifest);
        var wallSegments = manifest.Segments.Count;
        var treads = AddStairSurfaces(doc, view, manifest);
        var terrainTris = AddTerrainTriangles(doc, view, manifest);
        var obstacles = AddInstanceObstacles(doc, view, manifest);

        manifest.Warnings.Add(
            $"{runs.Count} wall runs → {wallSegments} wall segments, {obstacles} obstacle boxes; doors: " +
            $"{doorStats.Found} found, {doorStats.Matched} cut, {doorStats.Unmatched} unmatched; " +
            $"{manifest.Surfaces.Count - treads} floor/roof surfaces, {treads} stair treads, " +
            $"{terrainTris} terrain triangles");

        if (doorStats.Found > 0 && doorStats.Matched == 0)
            manifest.Warnings.Add("Every door failed to match a wall — doorways will be impassable.");
        if (manifest.Surfaces.Count == 0)
            manifest.Warnings.Add("No floor slabs in view; using level elevations for walking height");

        return new ColliderStats(
            runs.Count, manifest.Segments.Count, openings,
            doorStats.Found, doorStats.Matched, doorStats.Unmatched);
    }

    private readonly record struct DoorStats(int Found, int Matched, int Unmatched);

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

    private static void AddFloorSurfaces(Document doc, View view, SceneManifest manifest)
    {
        // Roofs count as walkable ground: a flat roof should behave exactly like a floor slab.
        var walkable = new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs };

        foreach (var category in walkable)
        {
            foreach (var surface in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                var bbox = surface.get_BoundingBox(view) ?? surface.get_BoundingBox(null);
                if (bbox is null) continue;
                if (bbox.Max.X - bbox.Min.X < 1e-3 || bbox.Max.Y - bbox.Min.Y < 1e-3) continue;

                manifest.Surfaces.Add(new WalkSurface
                {
                    MinX = (float)(bbox.Min.X * Ft),
                    MinZ = (float)(-bbox.Max.Y * Ft),
                    MaxX = (float)(bbox.Max.X * Ft),
                    MaxZ = (float)(-bbox.Min.Y * Ft),
                    Y = (float)(bbox.Max.Z * Ft),
                });
            }
        }

        if (manifest.Surfaces.Count > 0) return;

        // Fallback: treat every level as an infinite walkable plane across the model extents.
        var modelBox = ModelBounds(doc, view);
        if (modelBox is null) return;
        foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
        {
            manifest.Surfaces.Add(new WalkSurface
            {
                MinX = (float)(modelBox.Min.X * Ft) - 5f,
                MinZ = (float)(-modelBox.Max.Y * Ft) - 5f,
                MaxX = (float)(modelBox.Max.X * Ft) + 5f,
                MaxZ = (float)(-modelBox.Min.Y * Ft) + 5f,
                Y = (float)(level.Elevation * Ft),
            });
        }
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
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine,
        };

        var added = 0;

        foreach (var category in ObstacleCategories)
        {
            foreach (var element in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                GeometryElement? geometry;
                try { geometry = element.get_Geometry(options); }
                catch { continue; }
                if (geometry is null) continue;

                var points = new List<PlanPoint>();
                var minY = double.MaxValue;
                var maxY = double.MinValue;

                CollectObstaclePoints(geometry, Transform.Identity, points, ref minY, ref maxY, 0);

                if (points.Count < 3 || maxY <= minY) continue;

                var fit = PlanGeometry.MinAreaRect(points);
                if (fit is null) continue;

                var rect = fit.Value;
                if (rect.Area < 0.01 / (Ft * Ft)) continue;   // smaller than 100 cm² in plan

                // Emitted as an oriented box, which is the same primitive walls use — so the
                // viewer's existing capsule push-out handles furniture with no new code path.
                manifest.Segments.Add(new WallSegment
                {
                    X1 = (float)((rect.Cx - rect.Ux * rect.HalfLength) * Ft),
                    Z1 = (float)(-(rect.Cy - rect.Uy * rect.HalfLength) * Ft),
                    X2 = (float)((rect.Cx + rect.Ux * rect.HalfLength) * Ft),
                    Z2 = (float)(-(rect.Cy + rect.Uy * rect.HalfLength) * Ft),
                    Thickness = (float)Math.Max(2 * rect.HalfThickness * Ft, 0.02),
                    Y0 = (float)(minY * Ft),
                    Y1 = (float)(maxY * Ft),
                });

                added++;
            }
        }

        return added;
    }

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
