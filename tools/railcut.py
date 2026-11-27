#!/usr/bin/env python3
"""Cut the exported GLB with a plane and report what actually crosses a line in the model.

Use it to answer "is there really a railing on this line?" without opening Revit and without
trusting vertex proximity: a rail modelled as one long quad has vertices only at its ends, and a
floor triangle can span the whole cut with no vertex in it, so testing *nearby vertices* reports
empty space where a railing is drawn. Triangles are intersected here instead.

    python tools/railcut.py <scene-dir> --z -12.5 [--x -3:3] [--y 36:38] [--top 8]

Prints, per material, the crossing points: count, x/y ranges, and — with --x and --y windows —
a clustering by x so you can see which rails the cut passes through.

Coordinates are glTF: metres, x = Revit X, y = up, z = -Revit Y.
"""
import argparse
import json
import struct


def load(scene):
    with open(f'{scene}/scene.glb', 'rb') as f:
        f.read(12)
        length, _ = struct.unpack('<II', f.read(8))
        gltf = json.loads(f.read(length))
        length, _ = struct.unpack('<II', f.read(8))
        binary = f.read(length)
    return gltf, binary


def accessor(gltf, binary, index):
    acc = gltf['accessors'][index]
    view = gltf['bufferViews'][acc['bufferView']]
    base = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
    stride = view.get('byteStride', 12)
    return [struct.unpack_from('<fff', binary, base + k * stride) for k in range(acc['count'])]


def indices(gltf, binary, index):
    acc = gltf['accessors'][index]
    view = gltf['bufferViews'][acc['bufferView']]
    base = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
    fmt = {5121: 'B', 5123: 'H', 5125: 'I'}[acc['componentType']]
    size = struct.calcsize(fmt)
    return [struct.unpack_from('<' + fmt, binary, base + k * size)[0] for k in range(acc['count'])]


def parse_range(text):
    lo, _, hi = text.partition(':')
    return float(lo), float(hi)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('scene')
    ap.add_argument('--z', type=float, required=True, help='plane position along glTF z')
    ap.add_argument('--x', help='x window lo:hi, filters the reported crossings')
    ap.add_argument('--y', help='y (height) window lo:hi')
    ap.add_argument('--top', type=int, default=12, help='materials to list')
    args = ap.parse_args()

    xwin = parse_range(args.x) if args.x else None
    ywin = parse_range(args.y) if args.y else None

    gltf, binary = load(args.scene)
    names = {i: m.get('name') for i, m in enumerate(gltf.get('materials', []))}
    hits = {}

    for mesh in gltf['meshes']:
        for prim in mesh['primitives']:
            verts = accessor(gltf, binary, prim['attributes']['POSITION'])
            order = (indices(gltf, binary, prim['indices'])
                     if 'indices' in prim else list(range(len(verts))))
            name = str(names.get(prim.get('material')))
            for t in range(0, len(order) - 2, 3):
                a, b, c = verts[order[t]], verts[order[t + 1]], verts[order[t + 2]]
                da, db, dc = a[2] - args.z, b[2] - args.z, c[2] - args.z
                for p, q, dp, dq in ((a, b, da, db), (b, c, db, dc), (c, a, dc, da)):
                    if (dp > 0) == (dq > 0) or dp == dq:
                        continue
                    s = dp / (dp - dq)
                    x = p[0] + (q[0] - p[0]) * s
                    y = p[1] + (q[1] - p[1]) * s
                    if xwin and not xwin[0] <= x <= xwin[1]:
                        continue
                    if ywin and not ywin[0] <= y <= ywin[1]:
                        continue
                    hits.setdefault(name, []).append((x, y))

    print(f'cut at z={args.z}' + (f'  x{xwin}' if xwin else '') + (f'  y{ywin}' if ywin else ''))
    for name, points in sorted(hits.items(), key=lambda kv: -len(kv[1]))[:args.top]:
        xs = sorted(p[0] for p in points)
        ys = sorted(p[1] for p in points)
        print(f'  {name[:28]:30s} n{len(points):5d}  x {xs[0]:7.2f}..{xs[-1]:6.2f}'
              f'   y {ys[0]:6.2f}..{ys[-1]:6.2f}')

        clusters = []
        for x in xs:
            if clusters and x - clusters[-1][-1] <= 0.25:
                clusters[-1].append(x)
            else:
                clusters.append([x])
        if len(clusters) > 1:
            print('      clusters: ' + ', '.join(f'{c[0]:.2f}(n{len(c)})' for c in clusters[:10]))


if __name__ == '__main__':
    main()
