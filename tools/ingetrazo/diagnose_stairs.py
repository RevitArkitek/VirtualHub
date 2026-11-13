"""Find the collider that blocks the top of each stair flight.

    python tools/diagnose_stairs.py <colliders.json>

Treads are small horizontal surfaces climbing in steps. For each flight's top
tread this prints every segment that would stop a body standing there — the
amber strip in the viewer, and whatever else is in the way.
"""
import json
import math
import sys

m = json.load(open(sys.argv[1], encoding="utf-8"))
segs = m.get("segments") or []
surfs = m.get("surfaces") or []


def area(t):
    return (t["maxX"] - t["minX"]) * (t["maxZ"] - t["minZ"])


def centre(t):
    return ((t["minX"] + t["maxX"]) / 2, (t["minZ"] + t["maxZ"]) / 2)


def seg_dist(s, x, z):
    x1, z1, x2, z2 = s["x1"], s["z1"], s["x2"], s["z2"]
    dx, dz = x2 - x1, z2 - z1
    n = dx * dx + dz * dz
    u = 0.0 if n < 1e-12 else max(0.0, min(1.0, ((x - x1) * dx + (z - z1) * dz) / n))
    return math.hypot(x - (x1 + dx * u), z - (z1 + dz * u))


candidates = [t for t in surfs
              if 0.2 < area(t) < 4.0 and t.get("p") and len(t["p"]) >= 6
              and (t["maxX"] - t["minX"]) <= 3.0 and (t["maxZ"] - t["minZ"]) <= 3.0]
candidates.sort(key=lambda t: t["y"])
print(f"step-sized surfaces: {len(candidates)}   segments: {len(segs)}")

# group into flights: a rise of 0.1..0.4 m and a nearby centre
flights, cur = [], []
for t in candidates:
    if not cur:
        cur = [t]
        continue
    prev = cur[-1]
    rise = t["y"] - prev["y"]
    dx = abs(centre(t)[0] - centre(prev)[0])
    dz = abs(centre(t)[1] - centre(prev)[1])
    if 0.08 <= rise <= 0.40 and dx < 2.5 and dz < 2.5:
        cur.append(t)
    else:
        if len(cur) >= 3:
            flights.append(cur)
        cur = [t]
if len(cur) >= 3:
    flights.append(cur)

print(f"flights found: {len(flights)}\n")
for i, fl in enumerate(flights):
    top = fl[-1]
    tx, tz = centre(top)
    print(f"flight {i}: {len(fl)} steps, y {fl[0]['y']:.2f} -> {top['y']:.2f}  "
          f"top tread at ({tx:.1f}, {tz:.1f}) area {area(top):.2f}")
    blockers = []
    for s in segs:
        if s.get("solid") is False:
            continue
        if not (s["y0"] < top["y"] + 1.70 and s["y1"] > top["y"] + 0.05):
            continue
        d = seg_dist(s, tx, tz)
        if d <= 1.2:
            blockers.append((d, s))
    for d, s in sorted(blockers, key=lambda p: p[0])[:6]:
        h = s["y1"] - s["y0"]
        kind = "amber" if h < 2.0 else "blue"
        print(f"    {kind:5s} h={h:.2f} y {s['y0']:.2f}..{s['y1']:.2f} "
              f"{d:.2f} m away  len={math.dist((s['x1'], s['z1']), (s['x2'], s['z2'])):.2f}"
              f"  cls={s.get('cls', '-')}")
    # what is next to the top tread, height-wise
    above = [t for t in surfs if abs(t["y"] - top["y"]) < 0.9 and t is not top]
    print(f"    surfaces within 0.9 m of the top tread: "
          f"{[(round(t['y'], 2), round(area(t), 1)) for t in above][:6]}")
    print()
