"""Apply the current slab-edge rule to an existing manifest.

    python tools/refilter_manifest.py <colliders.json> [out.json]

Answers "would the new rule have removed the thing that blocked me?" without
re-exporting: the rule reads only segments and surfaces, both of which are in
the file. Segments are re-split where only part of a run is a floor edge.
"""
import importlib.util
import json
import sys
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
tools = types.ModuleType("tools")
base = types.ModuleType("tools.base")


class Tool:
    pass


base.Tool = Tool
sys.modules["tools"] = tools
sys.modules["tools.base"] = base

spec = importlib.util.spec_from_file_location("ingetrazo_vr", ROOT / "ingetrazo_vr.py")
vr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vr)

src = Path(sys.argv[1])
m = json.loads(src.read_text(encoding="utf-8"))
surfaces = m.get("surfaces") or []
walk = vr._walk_index(surfaces, m.get("terrain") or [])

kept, dropped, trimmed = [], [], 0
for s in m.get("segments") or []:
    # viewer space back to IngeTrazo world: (X, Y, Z) -> (X, -Z, Y)
    q0 = (s["x1"], -s["z1"], s["y0"])
    q1 = (s["x2"], -s["z2"], s["y0"])
    pieces = vr._wall_pieces(q0, q1, s["y0"], s["y1"], walk, vr.MIN_RUN_LEN)
    if not pieces:
        dropped.append(s)
        continue
    if len(pieces) > 1 or pieces[0][0] != q0 or pieces[0][1] != q1:
        trimmed += 1
    for p0, p1 in pieces:
        seg = dict(s)
        seg.update(x1=round(p0[0], 4), z1=round(-p0[1], 4),
                   x2=round(p1[0], 4), z2=round(-p1[1], 4))
        kept.append(seg)

total = len(m.get("segments") or [])
print(f"segments {total} -> {len(kept)}  "
      f"({len(dropped)} dropped whole, {trimmed} trimmed)")

by_h = {}
for s in dropped:
    by_h.setdefault(round(s["y1"] - s["y0"], 2), []).append(s)
for h, group in sorted(by_h.items()):
    print(f"  dropped {len(group)} of height {h} m:")
    for s in group[:4]:
        print(f"    y {s['y0']:.2f}..{s['y1']:.2f} "
              f"({s['x1']:.1f},{s['z1']:.1f})->({s['x2']:.1f},{s['z2']:.1f})")

if len(sys.argv) > 2:
    m["segments"] = kept
    Path(sys.argv[2]).write_text(json.dumps(m, separators=(",", ":")),
                                 encoding="utf-8")
    print("written:", sys.argv[2])
