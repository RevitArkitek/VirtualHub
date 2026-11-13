"""What is actually modelled around a point, read from the exported GLB.

    python tools/probe_glb.py <scene.glb> <x> <z> [radius]

Prints the triangles within `radius` of (x, z): how high they sit, whether they
face up (a floor) or sideways (a face), so a collider can be checked against the
geometry it claims to represent. Viewer space: x = IngeTrazo X, z = -IngeTrazo Y,
y = up.
"""
import json
import math
import struct
import sys
from pathlib import Path

path = Path(sys.argv[1])
cx, cz = float(sys.argv[2]), float(sys.argv[3])
radius = float(sys.argv[4]) if len(sys.argv) > 4 else 1.5

data = path.read_bytes()
jlen = struct.unpack_from("<I", data, 12)[0]
doc = json.loads(data[20:20 + jlen].decode("utf-8"))
boff = 20 + jlen
blen, btype = struct.unpack_from("<I4s", data, boff)
bin0 = boff + 8
blob = data[bin0:bin0 + blen]


def accessor(i):
    a = doc["accessors"][i]
    bv = doc["bufferViews"][a["bufferView"]]
    start = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    n = a["count"]
    fmt = {5126: "f", 5125: "I", 5123: "H", 5121: "B"}.get(a["componentType"])
    if fmt is None:
        return []
    comps = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
    size = {"f": 4, "I": 4, "H": 2, "B": 1}[fmt]
    stride = bv.get("byteStride") or comps * size
    out = []
    for k in range(n):
        out.append(struct.unpack_from("<" + fmt * comps, blob, start + k * stride))
    return out


floors = []
faces = []
for mesh in doc["meshes"]:
    for prim in mesh["primitives"]:
        pos = accessor(prim["attributes"]["POSITION"])
        idx = ([v[0] for v in accessor(prim["indices"])] if "indices" in prim
               else list(range(len(pos))))
        for i in range(0, len(idx) - 2, 3):
            a, b, c = pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]]
            mx = (a[0] + b[0] + c[0]) / 3
            mz = (a[2] + b[2] + c[2]) / 3
            if math.hypot(mx - cx, mz - cz) > radius:
                continue
            u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]]
            v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]]
            n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2],
                 u[0] * v[1] - u[1] * v[0]]
            ln = math.sqrt(sum(q * q for q in n)) or 1.0
            n = [q / ln for q in n]
            ys = [a[1], b[1], c[1]]
            rec = (min(ys), max(ys), n[1] > 0.85, abs(n[1]) < 0.34)
            (floors if rec[2] else faces).append((rec, mx, mz, a, b, c))

print(f"triangles within {radius} m of ({cx:.1f}, {cz:.1f}): "
      f"{len(floors)} up-facing, {len(faces)} other")

heights = {}
for (mn, mx, up, vert), *_ in floors:
    heights[round((mn + mx) / 2, 2)] = heights.get(round((mn + mx) / 2, 2), 0) + 1
print("floor heights here:", dict(sorted(heights.items())))

print("\nvertical faces (min..max y, how far along):")
seen = set()
for (mn, mx, up, vert), mx_, mz_, a, b, c in sorted(faces, key=lambda r: r[0][0]):
    if not vert or mx - mn < 0.10:
        continue
    key = (round(mn, 1), round(mx, 1), round(mx_, 1), round(mz_, 1))
    if key in seen:
        continue
    seen.add(key)
    print(f"  y {mn:6.2f}..{mx:6.2f}  h={mx - mn:5.2f}  at ({mx_:.1f}, {mz_:.1f})")
