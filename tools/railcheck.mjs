#!/usr/bin/env node
// Checks railing colliders against the geometry that actually renders.
//
//   node tools/railcheck.mjs [exportDir]
//
// Why this exists: a railing collider can be perfectly well-formed and still be in the wrong
// place. Railing.GetPath() reports the path at the level's elevation, not the railing's, so an
// earlier exporter put every upper-floor barrier one storey below its railing — you walked
// through the railing upstairs and were stopped in open space downstairs. Counting boxes does not
// catch that; comparing them with the render mesh does.
//
// The test is deliberately loose: a box must overlap *some* rendered geometry within 0.45 m of
// its line and within its height range. Geometry from a wall behind a railing will happily
// satisfy it, so a pass is weak evidence while a failure is strong.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2] || 'runtime/export';
const manifest = JSON.parse(readFileSync(join(dir, 'colliders.json'), 'utf8'));
const glb = readFileSync(join(dir, 'scene.glb'));

// ---- read positions out of the GLB -------------------------------------------------------
const dv = new DataView(glb.buffer, glb.byteOffset, glb.byteLength);
let off = 12, json = null, bin = null;
while (off < glb.length) {
  const len = dv.getUint32(off, true), type = dv.getUint32(off + 4, true);
  const body = glb.subarray(off + 8, off + 8 + len);
  if (type === 0x4e4f534a) json = JSON.parse(body.toString('utf8'));
  else if (type === 0x004e4942) bin = body;
  off += 8 + len + ((4 - (len % 4)) % 4);
}
if (!json || !bin) { console.error('not a GLB: ' + join(dir, 'scene.glb')); process.exit(2); }

const binView = new DataView(bin.buffer, bin.byteOffset, bin.byteLength);
const CELL = 0.5;
const grid = new Map();
const key = (cx, cz) => cx * 73856093 ^ cz * 19349663;

let vertexCount = 0;
for (const mesh of json.meshes || []) {
  for (const prim of mesh.primitives || []) {
    const acc = json.accessors[prim.attributes.POSITION];
    const bv = json.bufferViews[acc.bufferView];
    // binView is already offset to the binary chunk, so only the view/accessor offsets apply
    const base = (bv.byteOffset || 0) + (acc.byteOffset || 0);
    const stride = bv.byteStride || 12;
    for (let i = 0; i < acc.count; i++) {
      const p = base + i * stride;
      const x = binView.getFloat32(p, true);
      const y = binView.getFloat32(p + 4, true);
      const z = binView.getFloat32(p + 8, true);
      const k = key(Math.floor(x / CELL), Math.floor(z / CELL));
      let list = grid.get(k);
      if (!list) grid.set(k, (list = []));
      list.push(x, y, z);                       // flat triples keep this cheap
      vertexCount++;
    }
  }
}

/** Vertical span of rendered geometry within `pad` of the segment's plan line, or null. */
function geometryNear(x1, z1, x2, z2, pad = 0.45) {
  const dx = x2 - x1, dz = z2 - z1, L2 = dx * dx + dz * dz;
  if (L2 < 1e-12) return null;

  let lo = null, hi = null, n = 0;
  const cx0 = Math.floor((Math.min(x1, x2) - pad) / CELL) - 1;
  const cx1 = Math.floor((Math.max(x1, x2) + pad) / CELL) + 1;
  const cz0 = Math.floor((Math.min(z1, z2) - pad) / CELL) - 1;
  const cz1 = Math.floor((Math.max(z1, z2) + pad) / CELL) + 1;

  for (let cx = cx0; cx <= cx1; cx++) {
    for (let cz = cz0; cz <= cz1; cz++) {
      const list = grid.get(key(cx, cz));
      if (!list) continue;
      for (let i = 0; i < list.length; i += 3) {
        const x = list[i], y = list[i + 1], z = list[i + 2];
        const t = Math.max(0, Math.min(1, ((x - x1) * dx + (z - z1) * dz) / L2));
        const ex = x - (x1 + t * dx), ez = z - (z1 + t * dz);
        if (ex * ex + ez * ez > pad * pad) continue;
        n++;
        if (lo === null || y < lo) lo = y;
        if (hi === null || y > hi) hi = y;
      }
    }
  }
  return n ? { lo, hi, n } : null;
}

// ---- check every railing box -------------------------------------------------------------
// Railing colliders are the ones stamped with the element they came from; walls and furniture
// carry no sourceId. Selecting on the stamp rather than on the 0.1 m railing thickness matters:
// a footprint box carries the fitted rectangle's width instead, which is exactly the shape most
// worth checking.
const MAX_RAILING_HEIGHT = 1.6;   // metres — nothing in a railing is taller than this
const railings = manifest.segments.filter((s) => s.sourceId != null);
const missing = [], below = [], above = [], tall = [], enclosing = [];

for (const s of railings) {
  const where = `(${s.x1.toFixed(2)}, ${s.z1.toFixed(2)})→(${s.x2.toFixed(2)}, ${s.z2.toFixed(2)})`;
  const h = s.y1 - s.y0;
  if (h > MAX_RAILING_HEIGHT) tall.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} — ${h.toFixed(2)} m tall, too big for a railing`);

  const g = geometryNear(s.x1, s.z1, s.x2, s.z2);
  if (!g) { missing.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} — nothing rendered within 0.45 m`); continue; }
  if (s.y1 < g.lo - 0.15) below.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} vs geometry y ${g.lo.toFixed(2)}..${g.hi.toFixed(2)} — barrier BELOW the railing`);
  else if (s.y0 > g.hi + 0.15) above.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} vs geometry y ${g.lo.toFixed(2)}..${g.hi.toFixed(2)} — barrier ABOVE the railing`);
}

// ---- no railing collider may stand in the middle of a walking surface ---------------------
// A barrier is meant to run along the edge of a floor or a tread, never across it. A footprint
// rectangle fitted over a curved railing breaks that rule spectacularly: three boxes in the Silo
// enclosed every one of its 420 spiral-stair treads, so the whole stair tower was impassable.
const surfaces = manifest.surfaces || [];
function planOf(s) {
  if (s.p && s.p.length >= 6) {
    const poly = [];
    for (let i = 0; i + 1 < s.p.length; i += 2) poly.push([s.p[i], s.p[i + 1]]);
    return poly;
  }
  return [[s.minX, s.minZ], [s.maxX, s.minZ], [s.maxX, s.maxZ], [s.minX, s.maxZ]];
}
function inside(poly, x, z) {
  let c = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, zi] = poly[i], [xj, zj] = poly[j];
    if ((zi > z) !== (zj > z) && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) c = !c;
  }
  return c;
}
function footprint(s) {
  const dx = s.x2 - s.x1, dz = s.z2 - s.z1, L = Math.hypot(dx, dz);
  if (L < 1e-9) return null;
  const t = s.thickness / 2, nx = -dz / L * t, nz = dx / L * t;
  return [[s.x1 + nx, s.z1 + nz], [s.x2 + nx, s.z2 + nz], [s.x2 - nx, s.z2 - nz], [s.x1 - nx, s.z1 - nz]];
}

let enclosedCount = 0;
const enclosedBy = new Map();
for (const surface of surfaces) {
  const poly = planOf(surface);
  const cx = poly.reduce((a, p) => a + p[0], 0) / poly.length;
  const cz = poly.reduce((a, p) => a + p[1], 0) / poly.length;
  for (const s of railings) {
    // the barrier has to rise out of the walking surface, not merely start at it
    if (!(surface.y > s.y0 + 0.05) || surface.y > s.y1) continue;
    const box = footprint(s);
    if (box && inside(box, cx, cz)) {
      enclosedCount++;
      const id = s.sourceId ?? '?';
      if (!enclosedBy.has(id)) enclosedBy.set(id, 0);
      enclosedBy.set(id, enclosedBy.get(id) + 1);
      break;
    }
  }
}
if (enclosedCount > 0) {
  const worst = [...enclosedBy.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5);
  enclosing.push(
    `${enclosedCount} walkable surfaces have a railing collider standing on them (${worst
      .map(([id, n]) => `element ${id}: ${n}`)
      .join(', ')}${enclosedBy.size > worst.length ? ', …' : ''}) — those floors or treads are blocked`
  );
}

console.log(`railcheck: ${railings.length} railing colliders, ${vertexCount.toLocaleString()} render vertices`);
for (const m of [...below, ...above, ...missing, ...tall, ...enclosing]) console.log('  BAD ' + m);

const bad = below.length + above.length + tall.length + enclosing.length;
console.log(
  bad === 0
    ? `\nall ${railings.length} railing colliders are railing-sized, overlap rendered geometry, and leave the floors alone`
    : `\n${bad} of ${railings.length} railing colliders are wrong` +
      (below.length + above.length ? `, ${below.length + above.length} do not match the rendered railing` : '') +
      (tall.length ? `, ${tall.length} are taller than a railing` : '') +
      (missing.length ? `, ${missing.length} sit where nothing renders` : '') +
      (enclosing.length ? `, ${enclosedCount} walkable surfaces are walled off` : '')
);
process.exit(bad === 0 ? 0 : 1);
