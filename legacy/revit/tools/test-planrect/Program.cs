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

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
