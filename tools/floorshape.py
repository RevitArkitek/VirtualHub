#!/usr/bin/env python3
"""Describe — and optionally check — the walkable plan shape of a floor from the exported scene.

Two questions, one tool, neither needing Revit:

    python tools/floorshape.py <scene-dir> --y 10.973
        prints the plan coverage of the render's own upward-facing geometry at that height, so a
        donut slab (a ring around a stairwell) is visible as the ring it is rather than as the
        box the collider used to claim.

    python tools/floorshape.py <scene-dir> --y 10.973 --compare
        cross-checks the manifest's surfaces at that height against the render: `#` is floor both
        agree on, `F` is floor the collider claims and the model does not have (the phantom slab
        that fills a stairwell), `M` is floor the model has and the collider lost.

A little mismatch at the edges is normal — the outline is thinned to a 5 cm chord tolerance and
the render is the full tessellation — so read the counts, not a handful of edge cells.

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


def positions(gltf, binary, index):
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


def upward_triangles_at(gltf, binary, y, band):
    """Every triangle level enough to stand on, within +-band of y, as (ax,az,bx,bz,cx,cz)."""
    out = []
    for mesh in gltf.get('meshes', []):
        for prim in mesh.get('primitives', []):
            attrs = prim.get('attributes', {})
            if 'POSITION' not in attrs:
                continue
            acc = gltf['accessors'][attrs['POSITION']]
            mn, mx = acc.get('min'), acc.get('max')
            if not mn or not mx or mx[1] < y - band or mn[1] > y + band:
                continue                      # cannot hold a triangle at this height
            pos = positions(gltf, binary, attrs['POSITION'])
            idx = indices(gltf, binary, prim['indices']) if 'indices' in prim else list(range(len(pos)))
            for t in range(0, len(idx) - 2, 3):
                a, b, c = pos[idx[t]], pos[idx[t + 1]], pos[idx[t + 2]]
                if min(a[1], b[1], c[1]) < y - band or max(a[1], b[1], c[1]) > y + band:
                    continue
                # normal.y by the plan cross product: positive means the winding faces up
                ny = (b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0])
                if abs(ny) < 1e-9:
                    continue
                out.append((a[0], a[2], b[0], b[2], c[0], c[2]))
    return out


def in_triangle(t, x, z):
    ax, az, bx, bz, cx, cz = t
    d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
    if abs(d) < 1e-12:
        return False
    u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d
    v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d
    return u >= 0 and v >= 0 and u + v <= 1


def in_polygon(flat, x, z):
    """Crossing number, matching pointInPolygon in web/src/collision.js."""
    n = len(flat) // 2
    inside = False
    j = n - 1
    for i in range(n):
        xi, zi = flat[i * 2], flat[i * 2 + 1]
        xj, zj = flat[j * 2], flat[j * 2 + 1]
        if (zi > z) != (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi:
            inside = not inside
        j = i
    return inside


def surface_contains(s, x, z):
    """The same test the viewer's CollisionWorld makes: rect, then outline, then holes."""
    minx, minz, maxx, maxz = s.get('minX'), s.get('minZ'), s.get('maxX'), s.get('maxZ')
    if None in (minx, minz, maxx, maxz):
        return False
    if not (minx <= x <= maxx and minz <= z <= maxz):
        return False
    if s.get('p') and not in_polygon(s['p'], x, z):
        return False
    for hole in s.get('h') or []:
        if in_polygon(hole, x, z):
            return False
    return True


def polygon_area(flat):
    area = 0.0
    n = len(flat) // 2
    for i in range(n):
        j = (i + 1) % n
        area += flat[i * 2] * flat[j * 2 + 1] - flat[j * 2] * flat[i * 2 + 1]
    return abs(area) / 2


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('scene', help='published scene folder (holds scene.glb and colliders.json)')
    ap.add_argument('--y', type=float, required=True, help='floor height to look at, metres')
    ap.add_argument('--cell', type=float, default=2.0, help='grid size for the map, metres')
    ap.add_argument('--band', type=float, default=0.05, help='height tolerance, metres')
    ap.add_argument('--compare', action='store_true', help='cross-check against colliders.json')
    args = ap.parse_args()

    gltf, binary = load(args.scene)
    tris = upward_triangles_at(gltf, binary, args.y, args.band)
    print(f'{len(tris)} upward triangles within {args.band} m of y={args.y}')
    if not tris:
        return

    xs = [v for t in tris for v in (t[0], t[2], t[4])]
    zs = [v for t in tris for v in (t[1], t[3], t[5])]
    x0, x1, z0, z1 = min(xs), max(xs), min(zs), max(zs)
    print(f'extent x {x0:.2f}..{x1:.2f}  ({x1 - x0:.2f} m)   z {z0:.2f}..{z1:.2f}  ({z1 - z0:.2f} m)')

    surfaces = []
    if args.compare:
        with open(f'{args.scene}/colliders.json', 'r', encoding='utf-8') as f:
            manifest = json.load(f)
        surfaces = [s for s in manifest.get('surfaces', [])
                    if abs(s.get('y', 0) - args.y) <= args.band]
        print(f'{len(surfaces)} collider surfaces at this height')
        for s in surfaces:
            holes = s.get('h') or []
            outline = polygon_area(s['p']) - sum(polygon_area(h) for h in holes) if s.get('p') else 0.0
            box = (s['maxX'] - s['minX']) * (s['maxZ'] - s['minZ'])
            print(f"  y {s['y']:8.3f}  outline {outline:7.1f} m2  box {box:7.1f} m2  "
                  f"holes {len(holes)}  points {len(s.get('p') or []) // 2}")

    cs = args.cell
    nx = int((x1 - x0) / cs) + 1
    nz = int((z1 - z0) / cs) + 1

    both = phantom = missing = 0
    rows = []
    for iz in range(nz):
        z = z0 + (iz + 0.5) * cs
        row = []
        for ix in range(nx):
            x = x0 + (ix + 0.5) * cs
            rend = any(in_triangle(t, x, z) for t in tris)
            if not args.compare:
                row.append('#' if rend else '.')
                continue
            claim = any(surface_contains(s, x, z) for s in surfaces)
            if rend and claim:
                both += 1
                row.append('#')
            elif claim:
                phantom += 1
                row.append('F')
            elif rend:
                missing += 1
                row.append('M')
            else:
                row.append('.')
        rows.append(''.join(row))

    print(f'plan coverage, {cs} m cells, # = floor, . = no floor  (+x right, +z down)'
          if not args.compare else
          f'plan coverage, {cs} m cells: # agree, F collider only, M model only  (+x right, +z down)')
    for row in rows:
        print(row)

    if args.compare:
        cell_area = cs * cs
        print(f'agree {both} cells ({both * cell_area:.0f} m2), '
              f'phantom {phantom} ({phantom * cell_area:.0f} m2), '
              f'missing {missing} ({missing * cell_area:.0f} m2)')


if __name__ == '__main__':
    main()
