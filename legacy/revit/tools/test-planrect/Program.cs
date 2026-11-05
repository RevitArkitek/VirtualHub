// Tests the minimum-area rectangle fitting used to turn furniture footprints into colliders.
// A world-axis bounding box is the tempting shortcut and the wrong answer: a desk rotated 45
// degrees blocks roughly twice its own area, which you feel immediately in VR.
using RevitVr.Core.Scene;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  ok   {name}"); }
    else { fail++; Console.WriteLine($"  FAIL {name}  {detail}"); }
}

bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

PlanPoint P(double x, double y) => new(x, y);

// ---------------------------------------------------------------- hull
var square = new[] { P(0, 0), P(1, 0), P(1, 1), P(0, 1) };
var hull = PlanGeometry.ConvexHull(square);
Check("hull of a square has 4 corners", hull.Count == 4, $"got {hull.Count}");

var withInterior = new[] { P(0, 0), P(1, 0), P(1, 1), P(0, 1), P(0.5, 0.5), P(0.25, 0.75) };
Check("interior points are dropped", PlanGeometry.ConvexHull(withInterior).Count == 4);

// ---------------------------------------------------------------- rectangles
var rect = PlanGeometry.MinAreaRect(square);
Check("axis-aligned square", rect is not null
    && Near(rect.Value.Area, 1) && Near(rect.Value.HalfLength, 0.5) && Near(rect.Value.HalfThickness, 0.5),
    rect?.Area.ToString() ?? "null");

// a 4 x 1 rectangle, rotated 30 degrees: the fit must follow the rotation, not the axes
var rotated = new List<PlanPoint>();
var ang = 30 * Math.PI / 180;
foreach (var (u, v) in new[] { (-2.0, -0.5), (2.0, -0.5), (2.0, 0.5), (-2.0, 0.5) })
    rotated.Add(P(u * Math.Cos(ang) - v * Math.Sin(ang), u * Math.Sin(ang) + v * Math.Cos(ang)));

var fit = PlanGeometry.MinAreaRect(rotated);
Check("rotated rectangle keeps its area", fit is not null && Near(fit.Value.Area, 4, 1e-6),
    fit?.Area.ToString() ?? "null");
Check("rotated rectangle keeps its long axis", fit is not null
    && Near(fit.Value.HalfLength, 2) && Near(fit.Value.HalfThickness, 0.5),
    fit is null ? "null" : $"{fit.Value.HalfLength} x {fit.Value.HalfThickness}");
Check("fitted axis follows the 30 degree rotation",
    fit is not null && Near(Math.Abs(fit.Value.AngleDegrees) % 180, 30, 1e-6),
    fit?.AngleDegrees.ToString() ?? "null");

// the whole point: an axis-aligned box would report area 4 * sqrt(...) — much larger
var xs = rotated.Select(p => p.X).ToList();
var ys = rotated.Select(p => p.Y).ToList();
var axisAlignedArea = (xs.Max() - xs.Min()) * (ys.Max() - ys.Min());
Check("minimum-area fit beats a world-axis box", fit is not null && fit.Value.Area < axisAlignedArea,
    $"fit {fit?.Area:0.00} vs axis-aligned {axisAlignedArea:0.00}");

// ---------------------------------------------------------------- other shapes
var triangle = new[] { P(0, 0), P(4, 0), P(0, 3) };
var triFit = PlanGeometry.MinAreaRect(triangle);
Check("triangle fits its bounding rectangle", triFit is not null && Near(triFit.Value.Area, 12, 1e-6),
    triFit?.Area.ToString() ?? "null");

var round = Enumerable.Range(0, 32)
    .Select(i => P(Math.Cos(i * Math.PI / 16), Math.Sin(i * Math.PI / 16)))
    .ToList();
var circleFit = PlanGeometry.MinAreaRect(round);
Check("circle fits a square of its diameter", circleFit is not null
    && Near(circleFit.Value.Area, 4, 0.05)
    && Near(circleFit.Value.HalfLength, 1, 0.02),
    circleFit is null ? "null" : $"area {circleFit.Value.Area:0.000}");

Check("single point does not throw", PlanGeometry.MinAreaRect(new[] { P(3, 4) }) is not null);
Check("collinear points do not throw", PlanGeometry.MinAreaRect(new[] { P(0, 0), P(1, 1), P(2, 2) }) is not null);
Check("empty input returns null", PlanGeometry.MinAreaRect(Array.Empty<PlanPoint>()) is null);

// ---------------------------------------------------------------- railing boxes
// A railing becomes boxes along its path. What matters is that the boxes are continuous (a gap
// means walking through a guard rail), that a sloped railing steps its height range instead of
// spanning the whole flight, and that the plan axes are not silently swapped.
const double FtM = ColliderGeometry.FeetToMetres;
bool NearM(double a, double b, double tol = 1e-4) => Math.Abs(a - b) <= tol;

// 10 ft of horizontal railing, 3 ft tall, 0.1 ft thick — one box per metre of plan length
var straight = ColliderGeometry.AlongPath(
    new (double, double, double)[] { (0, 0, 0), (10, 0, 0) }, heightFt: 3, thicknessFt: 0.1);

Check("straight railing: split into 1 m pieces", straight.Count == Math.Ceiling(10 * FtM / ColliderGeometry.MaxPieceMetres),
    $"{straight.Count} boxes");
Check("straight railing: height range is the railing, not the world",
    straight.Count > 0 && NearM(straight[0].Y0, 0) && NearM(straight[0].Y1, 3 * FtM),
    straight.Count > 0 ? $"y {straight[0].Y0}..{straight[0].Y1}" : "none");
Check("straight railing: pieces are continuous",
    straight.Count > 1 && straight.Zip(straight.Skip(1)).All(p => NearM(p.First.X2, p.Second.X1) && NearM(p.First.Z2, p.Second.Z1)),
    "a gap between pieces is a hole you can walk through");
Check("straight railing: full length covered",
    straight.Count > 0 && NearM(straight[^1].X2 - straight[0].X1, 10 * FtM),
    straight.Count > 0 ? $"{straight[^1].X2 - straight[0].X1:0.000} m" : "none");
Check("straight railing: thickness is carried", straight.Count > 0 && NearM(straight[0].Thickness, 0.1 * FtM));

// Revit's +Y must land on glTF's -Z, or every railing sits mirrored about the origin
var alongY = ColliderGeometry.AlongPath(
    new (double, double, double)[] { (0, 0, 0), (0, 2, 0) }, heightFt: 3, thicknessFt: 0.1);
Check("path along +Y maps to -Z", alongY.Count > 0 && NearM(alongY[0].Z1, 0) && NearM(alongY[0].Z2, -2 * FtM),
    alongY.Count > 0 ? $"z {alongY[0].Z1}..{alongY[0].Z2}" : "none");

// A railing climbing a stair: the box height must follow the slope, or the space under the
// upper half of the flight becomes solid and you cannot walk beneath the stair.
var sloped = ColliderGeometry.AlongPath(
    new (double, double, double)[] { (0, 0, 0), (10, 0, 5) }, heightFt: 3, thicknessFt: 0.1);
Check("sloped railing: subdivided by rise, not just length", sloped.Count >= 5, $"{sloped.Count} boxes");
Check("sloped railing: every box covers the railing and at most one step of slope",
    sloped.All(s => s.Y1 - s.Y0 >= 3 * FtM - 1e-3
                    && s.Y1 - s.Y0 <= (3 + 3.5) * FtM),
    sloped.Count > 0 ? $"{(sloped[0].Y1 - sloped[0].Y0):0.000} m tall" : "none");
Check("sloped railing: base follows the slope",
    sloped.Count > 1 && sloped.Zip(sloped.Skip(1)).All(p => p.Second.Y0 > p.First.Y0 - 1e-6)
        && NearM(sloped[^1].Y1, 8 * FtM, 0.02),
    sloped.Count > 0 ? $"last box tops out at {sloped[^1].Y1:0.000} m" : "none");
Check("sloped railing: the space under the top of the flight stays open",
    sloped.Count > 1 && sloped[^1].Y0 > 1.0,
    sloped.Count > 1 ? $"highest box starts at {sloped[^1].Y0:0.000} m" : "none");

// A curved railing is tessellated into many short chords; the barrier must still be unbroken.
var curve = Enumerable.Range(0, 24)
    .Select(i => (X: 4 * Math.Cos(i * Math.PI / 46), Y: 4 * Math.Sin(i * Math.PI / 46), Z: 0.0))
    .ToList();
var arced = ColliderGeometry.AlongPath(curve, heightFt: 3, thicknessFt: 0.1);
Check("curved railing: no gaps where chords meet",
    arced.Count > 1 && arced.Zip(arced.Skip(1)).All(p => NearM(p.First.X2, p.Second.X1) && NearM(p.First.Z2, p.Second.Z1)),
    $"{arced.Count} boxes");

Check("duplicate points are ignored, not turned into boxes",
    ColliderGeometry.AlongPath(new (double, double, double)[] { (1, 1, 0), (1, 1, 0), (1, 1, 0) }, 3, 0.1).Count == 0);
Check("a single point is not a railing",
    ColliderGeometry.AlongPath(new (double, double, double)[] { (1, 1, 0) }, 3, 0.1).Count == 0);
Check("zero thickness still collides",
    ColliderGeometry.AlongPath(new (double, double, double)[] { (0, 0, 0), (3, 0, 0) }, 3, 0)
        .All(s => s.Thickness >= (float)ColliderGeometry.MinThicknessMetres));

// ---------------------------------------------------------------- footprint boxes
var r30 = PlanGeometry.MinAreaRect(rotated);
var box = r30 is null ? null : ColliderGeometry.FromRect(r30.Value, baseFt: 0, topFt: 3);
var boxLen = box is null ? 0 : Math.Sqrt(Math.Pow(box.X2 - box.X1, 2) + Math.Pow(box.Z2 - box.Z1, 2));
Check("footprint box follows its rotation",
    box is not null && NearM(boxLen, 4 * FtM, 1e-3) && NearM(box.Thickness, FtM, 1e-3),
    box is null ? "null" : $"length {boxLen:0.000}, thickness {box.Thickness:0.000}");
Check("footprint box carries its height range", box is not null && NearM(box.Y0, 0) && NearM(box.Y1, 3 * FtM),
    box is null ? "null" : $"y {box.Y0}..{box.Y1}");
Check("a speck is not a collider",
    ColliderGeometry.FromRect(new PlanRect(0, 0, 1, 0, 0.001, 0.001), 0, 3) is null);
Check("an inverted height range is refused",
    ColliderGeometry.FromRect(r30!.Value, baseFt: 3, topFt: 0) is null);

// ---------------------------------------------------------------- measured railing heights
// The bug this guards: Railing.GetPath() reports the path at the LEVEL's elevation, so a railing
// on an upper floor arrives flat at that floor's Z. The barrier must come from the geometry, not
// from the path, or it sits one storey below the railing.
var flatPath = new List<ColliderGeometry.PathSample>
{
    new(0, 0, 9.0, 10.1),      // path says z=0; geometry says the railing is at 9.0..10.1 m
    new(4, 0, 9.0, 10.1),      // (in feet: this is a two-storey error)
};
var measured = ColliderGeometry.AlongPath(flatPath, thicknessFt: 0.1);
Check("measured heights beat the path elevation",
    measured.Count > 0 && NearM(measured[0].Y0, 9.0 * FtM, 1e-3) && NearM(measured[0].Y1, 10.1 * FtM, 1e-3),
    measured.Count > 0 ? $"y {measured[0].Y0:0.00}..{measured[0].Y1:0.00}" : "none");

// a path piece that runs on past the railing has no geometry, so it must not become a collider
var withGap = new List<ColliderGeometry.PathSample>
{
    new(0, 0, 0, 3), new(4, 0, 0, 3),
    ColliderGeometry.PathSample.Missing(8, 0), ColliderGeometry.PathSample.Missing(12, 0),
};
var gapBoxes = ColliderGeometry.AlongPath(withGap, thicknessFt: 0.1);
Check("pieces past the end of a railing are dropped",
    gapBoxes.Count > 0 && gapBoxes.All(b => Math.Max(b.X1, b.X2) <= 4 * FtM + 1e-3),
    $"{gapBoxes.Count} boxes, furthest x {gapBoxes.Max(b => Math.Max(b.X1, b.X2)):0.00} m");

// a railing climbing a stair: measured base and top both rise, so each box is one railing tall
var climbing = new List<ColliderGeometry.PathSample>
{
    new(0, 0, 0, 3), new(10, 0, 5, 8),
};
var climb = ColliderGeometry.AlongPath(climbing, thicknessFt: 0.1);
Check("measured slope interpolates base and top",
    climb.Count > 1 && climb.All(b => NearM(b.Y1 - b.Y0, 3 * FtM + (b.X2 - b.X1) / (10 * FtM) * 5 * FtM, 0.06)),
    climb.Count > 0 ? $"first {climb[0].Y0:0.00}..{climb[0].Y1:0.00}, last {climb[^1].Y0:0.00}..{climb[^1].Y1:0.00}" : "none");

Check("a point with no barrier at both ends makes no box",
    ColliderGeometry.AlongPath(new List<ColliderGeometry.PathSample>
    {
        ColliderGeometry.PathSample.Missing(0, 0), ColliderGeometry.PathSample.Missing(3, 0),
    }, 0.1).Count == 0);

// ---------------------------------------------------------------- path resampling
// The fix for an over-tall railing collider: measure along the path, not only at its vertices.
var straightResampled = ColliderGeometry.Resample(
    new (double, double, double)[] { (0, 0, 0), (10, 0, 0) }, spacingFt: 2);
Check("resample: a two-point line gains intermediate samples",
    straightResampled.Count == 6, $"{straightResampled.Count} points");
Check("resample: spacing is honoured",
    straightResampled.Zip(straightResampled.Skip(1)).All(p => Near(p.Second.X - p.First.X, 2, 1e-9)),
    string.Join(", ", straightResampled.Select(p => p.X.ToString("0.0"))));
Check("resample: the far end is kept exactly",
    Near(straightResampled[^1].X, 10) && Near(straightResampled[^1].Y, 0));

// an L keeps its corner, and elevations interpolate along each leg
var lResampled = ColliderGeometry.Resample(
    new (double, double, double)[] { (0, 0, 0), (4, 0, 1), (4, 4, 2) }, spacingFt: 2);
Check("resample: the corner survives", lResampled.Any(p => Near(p.X, 4) && Near(p.Y, 0)) && Near(lResampled[^1].Y, 4),
    $"{lResampled.Count} points");
Check("resample: elevation interpolates",
    Near(lResampled[^1].Z, 2) && lResampled.Any(p => Near(p.X, 4) && Near(p.Y, 0) && Near(p.Z, 1))
        && lResampled.Any(p => Near(p.X, 4) && Near(p.Y, 2) && Near(p.Z, 1.5)),
    string.Join(" ", lResampled.Select(p => $"({p.X:0.0},{p.Y:0.0},{p.Z:0.00})")));
Check("resample: a single point is unchanged", ColliderGeometry.Resample(new (double, double, double)[] { (1, 2, 3) }, 2).Count == 1);
Check("resample: zero spacing falls back to a sane default",
    ColliderGeometry.Resample(new (double, double, double)[] { (0, 0, 0), (5, 0, 0) }, 0).Count > 1);
Check("resample: duplicate points do not add samples",
    ColliderGeometry.Resample(new (double, double, double)[] { (2, 2, 0), (2, 2, 0) }, 1).Count == 1);

// ---------------------------------------------------------------- point vs solid footprint
// The bug that left walk-through holes: a railing's height was measured from the corners of its
// solids' bounding boxes, which for a 4 m rail are only at the two ends. Every sample between
// them found nothing, so the piece was dropped and you walked through the gap. A point is inside
// a rail's plan footprint wherever it sits along it — that is what these assert.
var rail = new List<PlanPoint> { new(0, -0.03), new(4, -0.03), new(4, 0.03), new(0, 0.03) };
var alongRail = Enumerable.Range(0, 7).Select(i => i * 0.6).ToList();
Check("a point mid-rail is inside the rail's footprint",
    alongRail.All(x => PlanGeometry.DistanceToPolygon(rail, x, 0) == 0),
    string.Join(" ", alongRail.Select(x => $"{x:0.0}:{PlanGeometry.DistanceToPolygon(rail, x, 0):0.00}")));
Check("a point just outside the rail is not inside it",
    PlanGeometry.DistanceToPolygon(rail, 2, 0.25) > 0.19,
    PlanGeometry.DistanceToPolygon(rail, 2, 0.25).ToString("0.000"));
Check("a point past the end of the rail is not inside it",
    PlanGeometry.DistanceToPolygon(rail, 4.5, 0) > 0.4,
    PlanGeometry.DistanceToPolygon(rail, 4.5, 0).ToString("0.000"));
// the measurement tolerance is what absorbs sketch slop: 0.1 m past the end still counts, 0.3 m
// does not — so a path running on past the railing loses those pieces instead of inventing a box
Check("0.10 m past the rail end is within tolerance",
    PlanGeometry.DistanceToPolygon(rail, 4.10, 0) < 0.15);
Check("0.30 m past the rail end is outside tolerance",
    PlanGeometry.DistanceToPolygon(rail, 4.30, 0) > 0.15);

// a rotated post: containment must follow the rotation, not the world axes
var post = new List<PlanPoint>();
var a45 = 45 * Math.PI / 180;
foreach (var (u, v) in new[] { (-0.05, -0.05), (0.05, -0.05), (0.05, 0.05), (-0.05, 0.05) })
    post.Add(new PlanPoint(2 + u * Math.Cos(a45) - v * Math.Sin(a45), 1 + u * Math.Sin(a45) + v * Math.Cos(a45)));
Check("containment follows a rotated footprint",
    PlanGeometry.DistanceToPolygon(post, 2, 1) == 0 && PlanGeometry.DistanceToPolygon(post, 2.2, 1) > 0.1,
    PlanGeometry.DistanceToPolygon(post, 2.2, 1).ToString("0.000"));

Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
