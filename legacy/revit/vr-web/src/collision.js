/**
 * Collision against analytic wall segments (metres, glTF space: X right, Y up, Z forward).
 *
 * Why not mesh collision: a real Revit model is hundreds of thousands of triangles. Sweeping a
 * capsule against that every frame will not hold 72 fps on a standalone Quest. Instead the Revit
 * add-in emits one 2D box per wall run, with door openings already cut out, so a whole floor of
 * walls is a few hundred cheap 2D tests behind a spatial hash.
 */

const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

export class CollisionWorld {
  /**
   * @param {Array} segments [{x1,z1,x2,z2,thickness,y0,y1,solid}]
   * @param {Array} surfaces [{minX,minZ,maxX,maxZ,y}]
   */
  constructor(segments = [], surfaces = [], terrain = [], cellSize = 4) {
    this.segments = [];
    this.surfaces = (surfaces || []).map((s) => ({
      y: s.y,
      minX: s.minX ?? -Infinity,
      minZ: s.minZ ?? -Infinity,
      maxX: s.maxX ?? Infinity,
      maxZ: s.maxZ ?? Infinity,
      // a stair tread is a polygon, a floor slab is a rectangle
      poly: Array.isArray(s.p) && s.p.length >= 6 ? s.p : null,
    }));

    // Sloped ground. Triangles are indexed into a coarse grid because a site mesh can run to
    // tens of thousands of them and testing all of it every frame would not hold 72 fps.
    this.terrain = [];
    this.terrainGrid = new Map();
    this.terrainCell = 8;
    for (const t of terrain || []) this._addTerrainTriangle(t);

    this.cellSize = cellSize;
    this.grid = new Map();
    this.stats = { tests: 0, gridCells: 0 };

    for (const s of segments) {
      if (s.solid === false) continue;
      const dx = s.x2 - s.x1;
      const dz = s.z2 - s.z1;
      const len = Math.hypot(dx, dz);
      if (len < 1e-4) continue;
      const ux = dx / len, uz = dz / len;
      const seg = {
        cx: s.x1 + dx / 2,
        cz: s.z1 + dz / 2,
        ux, uz,
        // left normal of the wall direction
        nx: -uz, nz: ux,
        halfLen: len / 2,
        halfT: Math.max(s.thickness ?? 0.2, 0.02) / 2,
        y0: s.y0 ?? -1e9,
        y1: s.y1 ?? 1e9,
      };
      // A hair of mitre closes float gaps where two runs meet at a corner. It used to be a full
      // half-thickness, which silently doubled the size of short, thick boxes — fine for long
      // walls, badly wrong for a table or a square column.
      seg.halfLen += 0.01;
      const idx = this.segments.length;
      this.segments.push(seg);
      this._insert(seg, idx);
    }
    this.stats.gridCells = this.grid.size;
  }

  _cellKey(cx, cz) { return cx * 73856093 ^ cz * 19349663; }

  _insert(seg, idx) {
    const r = seg.halfLen + seg.halfT;
    const cs = this.cellSize;
    const x0 = Math.floor((seg.cx - r) / cs), x1 = Math.floor((seg.cx + r) / cs);
    const z0 = Math.floor((seg.cz - r) / cs), z1 = Math.floor((seg.cz + r) / cs);
    for (let cx = x0; cx <= x1; cx++) {
      for (let cz = z0; cz <= z1; cz++) {
        const k = this._cellKey(cx, cz);
        let list = this.grid.get(k);
        if (!list) this.grid.set(k, (list = []));
        list.push(idx);
      }
    }
  }

  _near(x, z) {
    return this.grid.get(this._cellKey(Math.floor(x / this.cellSize), Math.floor(z / this.cellSize))) || EMPTY;
  }

  /**
   * Pushes a vertical capsule out of every wall it overlaps.
   * Mutates `pos` in place and returns true if anything blocked.
   * @param {{x:number,z:number}} pos
   * @param {number} radius   player radius, metres
   * @param {number} feetY    bottom of the capsule
   * @param {number} headY    top of the capsule
   */
  resolve(pos, radius, feetY, headY) {
    let blocked = false;
    for (let iter = 0; iter < 3; iter++) {
      let moved = false;
      const near = this._near(pos.x, pos.z);
      for (let i = 0; i < near.length; i++) {
        const s = this.segments[near[i]];
        this.stats.tests++;
        if (s.y0 >= headY || s.y1 <= feetY) continue; // different storey

        const rx = pos.x - s.cx, rz = pos.z - s.cz;
        const u = rx * s.ux + rz * s.uz;
        const v = rx * s.nx + rz * s.nz;

        // already clear on this axis? cheap early out
        if (Math.abs(u) > s.halfLen + radius) continue;
        if (Math.abs(v) > s.halfT + radius) continue;

        const pu = clamp(u, -s.halfLen, s.halfLen);
        const pv = clamp(v, -s.halfT, s.halfT);
        let du = u - pu;
        let dv = v - pv;
        const d2 = du * du + dv * dv;

        if (d2 > radius * radius) continue;

        if (d2 > 1e-10) {
          const d = Math.sqrt(d2);
          const push = radius - d;
          du /= d; dv /= d;
          pos.x += (s.ux * du + s.nx * dv) * push;
          pos.z += (s.uz * du + s.nz * dv) * push;
        } else {
          // centre is inside the box: escape along the shallower axis
          const penU = s.halfLen + radius - Math.abs(u);
          const penV = s.halfT + radius - Math.abs(v);
          if (penV <= penU) {
            const sign = v >= 0 ? 1 : -1;
            pos.x += s.nx * sign * penV;
            pos.z += s.nz * sign * penV;
          } else {
            const sign = u >= 0 ? 1 : -1;
            pos.x += s.ux * sign * penU;
            pos.z += s.uz * sign * penU;
          }
        }
        moved = blocked = true;
      }
      if (!moved) break;
    }
    return blocked;
  }

  /** Indexes one terrain triangle into the spatial grid. */
  _addTerrainTriangle(t) {
    const tri = {
      ax: t[0], ay: t[1], az: t[2],
      bx: t[3], by: t[4], bz: t[5],
      cx: t[6], cy: t[7], cz: t[8],
    };
    tri.minX = Math.min(tri.ax, tri.bx, tri.cx);
    tri.maxX = Math.max(tri.ax, tri.bx, tri.cx);
    tri.minZ = Math.min(tri.az, tri.bz, tri.cz);
    tri.maxZ = Math.max(tri.az, tri.bz, tri.cz);

    const index = this.terrain.length;
    this.terrain.push(tri);

    const cs = this.terrainCell;
    for (let cx = Math.floor(tri.minX / cs); cx <= Math.floor(tri.maxX / cs); cx++) {
      for (let cz = Math.floor(tri.minZ / cs); cz <= Math.floor(tri.maxZ / cs); cz++) {
        const k = this._cellKey(cx, cz);
        let list = this.terrainGrid.get(k);
        if (!list) this.terrainGrid.set(k, (list = []));
        list.push(index);
      }
    }
  }

  _terrainNear(x, z) {
    return this.terrainGrid.get(this._cellKey(Math.floor(x / this.terrainCell), Math.floor(z / this.terrainCell))) || EMPTY;
  }

  /**
   * Highest walkable ground at (x,z) that is no more than `maxStep` above `fromY` and no more
   * than `maxDrop` below it — from slabs and treads (one height each) and from terrain
   * triangles (height interpolated).
   * Returns null when standing over nothing (caller keeps the current height).
   */
  groundAt(x, z, fromY, maxStep = 0.45, maxDrop = 3.0) {
    let best = null;
    for (const s of this.surfaces) {
      if (x < s.minX || x > s.maxX || z < s.minZ || z > s.maxZ) continue;
      if (s.y > fromY + maxStep) continue;
      if (s.y < fromY - maxDrop) continue;
      if (s.poly && !pointInPolygon(s.poly, x, z)) continue;
      if (best === null || s.y > best) best = s.y;
    }

    // sloped ground: height varies across the triangle, so interpolate
    const near = this._terrainNear(x, z);
    for (let i = 0; i < near.length; i++) {
      const h = triangleHeightAt(this.terrain[near[i]], x, z);
      if (h === null) continue;
      if (h > fromY + maxStep) continue;
      if (h < fromY - maxDrop) continue;
      if (best === null || h > best) best = h;
    }

    return best;
  }

  /** True when a straight step from a to b is unobstructed (used by teleport). */
  pathClear(ax, az, bx, bz, radius, feetY, headY) {
    const d = Math.hypot(bx - ax, bz - az);
    const steps = Math.max(1, Math.ceil(d / (radius * 0.75)));
    const p = { x: ax, z: az };
    for (let i = 1; i <= steps; i++) {
      p.x = ax + ((bx - ax) * i) / steps;
      p.z = az + ((bz - az) * i) / steps;
      if (this.resolve(p, radius, feetY, headY)) return false;
    }
    return true;
  }
}

const EMPTY = [];

/** Point in a flat [x,z,x,z,...] polygon, by crossing number. */
function pointInPolygon(p, x, z) {
  let inside = false;
  const n = p.length / 2;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    const xi = p[i * 2], zi = p[i * 2 + 1];
    const xj = p[j * 2], zj = p[j * 2 + 1];
    if ((zi > z) !== (zj > z) && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) inside = !inside;
  }
  return inside;
}

/**
 * Height of a triangle at (x,z) by barycentric interpolation, or null when the point is outside
 * it. This is what lets a hillside be walked instead of being flattened to one plane.
 */
function triangleHeightAt(t, x, z) {
  if (x < t.minX || x > t.maxX || z < t.minZ || z > t.maxZ) return null;

  const v0x = t.bx - t.ax, v0z = t.bz - t.az;
  const v1x = t.cx - t.ax, v1z = t.cz - t.az;
  const v2x = x - t.ax, v2z = z - t.az;

  const den = v0x * v1z - v1x * v0z;
  if (Math.abs(den) < 1e-12) return null;      // degenerate in plan

  const u = (v2x * v1z - v1x * v2z) / den;
  const v = (v0x * v2z - v2x * v0z) / den;
  if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) return null;

  return t.ay + u * (t.by - t.ay) + v * (t.cy - t.ay);
}
