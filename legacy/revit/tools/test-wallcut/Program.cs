// Tests the door cut-out geometry in RevitVr.Core — the exact code the add-in runs when it
// decides where a doorway goes. No Revit required.
//
// This exists because the first version of this logic multiplied a curve parameter by the wall
// length, assuming the parameter was normalised 0..1. It is not: Curve.Project returns an
// unnormalised parameter. Every door cut landed in the wrong place, so no doorway was ever
// passable, and the summary line reported the wall count as if it were the door count.
using RevitVr.Core.Scene;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  ok   {name}"); }
    else { fail++; Console.WriteLine($"  FAIL {name}  {detail}"); }
}

bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

// ---------------------------------------------------------------- straight wall
// A 30 ft wall running along +X, as Revit would report it in internal units.
var wall = new List<(double X, double Y)> { (0, 0), (30, 0) };
var cum = WallGeometry.Cumulative(wall);

Check("cumulative length", Near(cum[1], 30), $"cum[1]={cum[1]}");

var mid = WallGeometry.ClosestOnPolyline(wall, cum, 12, 0);
Check("point on the wall: distance along", Near(mid.Along, 12), $"along={mid.Along}");
Check("point on the wall: zero offset", Near(mid.Distance, 0), $"dist={mid.Distance}");

var off = WallGeometry.ClosestOnPolyline(wall, cum, 12, 0.5);
Check("point 0.5 ft beside: along unchanged", Near(off.Along, 12), $"along={off.Along}");
Check("point 0.5 ft beside: offset measured", Near(off.Distance, 0.5), $"dist={off.Distance}");

var before = WallGeometry.ClosestOnPolyline(wall, cum, -5, 0);
Check("point past the start clamps", Near(before.Along, 0) && Near(before.Distance, 5),
    $"along={before.Along} dist={before.Distance}");

var after = WallGeometry.ClosestOnPolyline(wall, cum, 35, 0);
Check("point past the end clamps", Near(after.Along, 30) && Near(after.Distance, 5),
    $"along={after.Along} dist={after.Distance}");

// The regression that shipped: a door 40% along a 30 ft wall sits at 12 ft, not 12 * 30.
var fortyPercent = WallGeometry.ClosestOnPolyline(wall, cum, 0.4 * 30, 0);
Check("40% along reads as 12 ft, not 360", Near(fortyPercent.Along, 12), $"along={fortyPercent.Along}");

// ---------------------------------------------------------------- cutting
var single = WallGeometry.Subtract(0, 30, new[] { (10.5, 13.5) });
Check("one door splits a wall in two", single.Count == 2
    && Near(single[0].A, 0) && Near(single[0].B, 10.5)
    && Near(single[1].A, 13.5) && Near(single[1].B, 30),
    string.Join(" ", single.Select(s => $"[{s.A},{s.B}]")));

var leading = WallGeometry.Subtract(0, 30, new[] { (0d, 3d) });
Check("door at the very start leaves one span", leading.Count == 1 && Near(leading[0].A, 3) && Near(leading[0].B, 30),
    string.Join(" ", leading.Select(s => $"[{s.A},{s.B}]")));

var whole = WallGeometry.Subtract(0, 2, new[] { (-1d, 5d) });
Check("door wider than the wall removes it", whole.Count == 0, $"got {whole.Count} spans");

var touching = WallGeometry.Subtract(0, 10, new[] { (10d, 12d) });
Check("door starting at the end is a no-op", touching.Count == 1 && Near(touching[0].B, 10),
    string.Join(" ", touching.Select(s => $"[{s.A},{s.B}]")));

var overlap = WallGeometry.Subtract(0, 15, new[] { (4d, 8d), (7d, 10d) });
Check("overlapping doors merge", overlap.Count == 2
    && Near(overlap[0].B, 4) && Near(overlap[1].A, 10) && Near(overlap[1].B, 15),
    string.Join(" ", overlap.Select(s => $"[{s.A},{s.B}]")));

var none = WallGeometry.Subtract(0, 10, Array.Empty<(double, double)>());
Check("no doors leaves the wall intact", none.Count == 1 && Near(none[0].B, 10));

// the add-in cuts per polyline segment, not per wall, so a cut spanning two segments is normal
var across = WallGeometry.Subtract(4, 8, new[] { (6d, 20d) });
Check("cut spanning a segment end", across.Count == 1 && Near(across[0].A, 4) && Near(across[0].B, 6),
    string.Join(" ", across.Select(s => $"[{s.A},{s.B}]")));

// ---------------------------------------------------------------- curved wall
// An L-shaped run, 10 ft + 10 ft: total length 20.
var ell = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10) };
var ellCum = WallGeometry.Cumulative(ell);
Check("L-shape cumulative length", Near(ellCum[2], 20), $"cum[2]={ellCum[2]}");

var corner = WallGeometry.ClosestOnPolyline(ell, ellCum, 10, 5);
Check("point on the second leg: along 15", Near(corner.Along, 15) && Near(corner.Distance, 0),
    $"along={corner.Along} dist={corner.Distance}");

var outside = WallGeometry.ClosestOnPolyline(ell, ellCum, 10.5, 5);
Check("offset from the second leg", Near(outside.Along, 15) && Near(outside.Distance, 0.5),
    $"along={outside.Along} dist={outside.Distance}");

var ellCut = WallGeometry.Subtract(ellCum[1], ellCum[2], new[] { (14.0, 16.0) });
Check("door in the second leg splits only that leg", ellCut.Count == 2
    && Near(ellCut[0].A, 10) && Near(ellCut[0].B, 14)
    && Near(ellCut[1].A, 16) && Near(ellCut[1].B, 20),
    string.Join(" ", ellCut.Select(s => $"[{s.A},{s.B}]")));

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
