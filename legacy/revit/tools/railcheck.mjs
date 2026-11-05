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
// Railing boxes carry a fixed 0.1 m thickness; walls carry the wall's width.
const MAX_RAILING_HEIGHT = 1.6;   // metres — nothing in a railing is taller than this
const railings = manifest.segments.filter((s) => Math.abs((s.thickness ?? 0.2) - 0.1) < 0.005);
const missing = [], below = [], above = [], tall = [];

for (const s of railings) {
  const where = `(${s.x1.toFixed(2)}, ${s.z1.toFixed(2)})→(${s.x2.toFixed(2)}, ${s.z2.toFixed(2)})`;
  const h = s.y1 - s.y0;
  if (h > MAX_RAILING_HEIGHT) tall.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} — ${h.toFixed(2)} m tall, too big for a railing`);

  const g = geometryNear(s.x1, s.z1, s.x2, s.z2);
  if (!g) { missing.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} — nothing rendered within 0.45 m`); continue; }
  if (s.y1 < g.lo - 0.15) below.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} vs geometry y ${g.lo.toFixed(2)}..${g.hi.toFixed(2)} — barrier BELOW the railing`);
  else if (s.y0 > g.hi + 0.15) above.push(`${where} collider y ${s.y0.toFixed(2)}..${s.y1.toFixed(2)} vs geometry y ${g.lo.toFixed(2)}..${g.hi.toFixed(2)} — barrier ABOVE the railing`);
}

console.log(`railcheck: ${railings.length} railing colliders, ${vertexCount.toLocaleString()} render vertices`);
for (const m of [...below, ...above, ...missing, ...tall]) console.log('  BAD ' + m);

const bad = below.length + above.length + tall.length;
console.log(
  bad === 0
    ? `\nall ${railings.length} railing colliders are railing-sized and overlap rendered geometry`
    : `\n${bad} of ${railings.length} railing colliders are wrong` +
      (below.length + above.length ? `, ${below.length + above.length} do not match the rendered railing` : '') +
      (tall.length ? `, ${tall.length} are taller than a railing` : '') +
      (missing.length ? `, ${missing.length} sit where nothing renders` : '')
);
process.exit(bad === 0 ? 0 : 1);
