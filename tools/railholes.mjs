// Finds real holes in railing barriers: gaps between an element's collider pieces that have
// rendered railing geometry in them.  node tools/railholes.mjs <exportDir>
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2] || 'runtime/export';
const man = JSON.parse(readFileSync(join(dir, 'colliders.json'), 'utf8'));
const glb = readFileSync(join(dir, 'scene.glb'));
const dv = new DataView(glb.buffer, glb.byteOffset, glb.byteLength);
let off = 12, json = null, bin = null;
while (off < glb.length) {
  const len = dv.getUint32(off, true), type = dv.getUint32(off + 4, true);
  const body = glb.subarray(off + 8, off + 8 + len);
  if (type === 0x4e4f534a) json = JSON.parse(body.toString('utf8'));
  else if (type === 0x004e4942) bin = body;
  off += 8 + len + ((4 - (len % 4)) % 4);
}
const bvView = new DataView(bin.buffer, bin.byteOffset, bin.byteLength);
const CELL = 0.5;
const grid = new Map();
const key = (cx, cz) => cx * 73856093 ^ cz * 19349663;
for (const mesh of json.meshes || []) {
  for (const prim of mesh.primitives || []) {
    const acc = json.accessors[prim.attributes.POSITION];
    const bv = json.bufferViews[acc.bufferView];
    const base = (bv.byteOffset || 0) + (acc.byteOffset || 0);
    const stride = bv.byteStride || 12;
    for (let i = 0; i < acc.count; i++) {
      const p = base + i * stride;
      const x = bvView.getFloat32(p, true), y = bvView.getFloat32(p + 4, true), z = bvView.getFloat32(p + 8, true);
      const k = key(Math.floor(x / CELL), Math.floor(z / CELL));
      let list = grid.get(k);
      if (!list) grid.set(k, (list = []));
      list.push(x, y, z);
    }
  }
}

/** Is there rendered geometry within pad of this plan point, between y0 and y1? */
function geometryAt(x, z, y0, y1, pad = 0.45) {
  const cells = Math.ceil(pad / CELL) + 1;
  for (let cx = Math.floor(x / CELL) - cells; cx <= Math.floor(x / CELL) + cells; cx++) {
    for (let cz = Math.floor(z / CELL) - cells; cz <= Math.floor(z / CELL) + cells; cz++) {
      const list = grid.get(key(cx, cz));
      if (!list) continue;
      for (let i = 0; i < list.length; i += 3) {
        const vy = list[i + 1];
        if (vy < y0 - 0.25 || vy > y1 + 0.25) continue;
        const dx = list[i] - x, dz = list[i + 2] - z;
        if (dx * dx + dz * dz <= pad * pad) return true;
      }
    }
  }
  return false;
}

// group colliders by element, then chain pieces into runs by matching endpoints
const byElement = new Map();
for (const s of man.segments) {
  if (s.sourceId == null) continue;
  if (!byElement.has(s.sourceId)) byElement.set(s.sourceId, []);
  byElement.get(s.sourceId).push(s);
}

const holes = [];
for (const [id, segs] of byElement) {
  // endpoints rounded to 1 cm, so a piece's end matches the next piece's start
  const r = (v) => Math.round(v * 100);
  const starts = new Map();
  const ends = new Map();
  for (const s of segs) {
    const a = `${r(s.x1)},${r(s.z1)}`, b = `${r(s.x2)},${r(s.z2)}`;
    if (!starts.has(a)) starts.set(a, []);
    starts.get(a).push(s);
    if (!ends.has(b)) ends.set(b, []);
    ends.get(b).push(s);
  }
  // free ends: a point where a piece ends but no piece starts (dangling)
  const free = [];
  for (const [pt, list] of ends) if (!starts.has(pt)) for (const s of list) free.push({ x: s.x2, z: s.z2, y0: s.y0, y1: s.y1 });
  for (const [pt, list] of starts) if (!ends.has(pt)) for (const s of list) free.push({ x: s.x1, z: s.z1, y0: s.y0, y1: s.y1 });
  for (let i = 0; i < free.length; i++) {
    for (let j = i + 1; j < free.length; j++) {
      const a = free[i], b = free[j];
      const dist = Math.hypot(b.x - a.x, b.z - a.z);
      if (dist < 0.35 || dist > 12) continue;

      // the two ends of a spiral railing are a long way apart in plan and a flight apart in
      // height; that seam is not a hole, the next flight's railing starts there
      const y0 = Math.min(a.y0, b.y0), y1 = Math.max(a.y1, b.y1);
      if (Math.abs(a.y0 - b.y0) > 0.5) continue;
      // walk the chord: a real hole has rendered railing in between
      let hits = 0;
      const steps = Math.max(4, Math.round(dist / 0.35));
      for (let k = 1; k < steps; k++) {
        const t = k / steps;
        if (geometryAt(a.x + (b.x - a.x) * t, a.z + (b.z - a.z) * t, y0, y1)) hits++;
      }
      if (hits >= Math.ceil((steps - 1) * 0.4)) {
        holes.push({ id, a, b, dist, hits, steps });
      }
    }
  }
}

const byId = new Map();
for (const h of holes) {
  if (!byId.has(h.id)) byId.set(h.id, []);
  byId.get(h.id).push(h);
}
console.log(`railholes: ${byElement.size} railing elements, ${holes.length} gaps with railing geometry inside`);
for (const [id, list] of byId) {
  list.sort((p, q) => q.dist - p.dist);
  for (const h of list.slice(0, 3)) {
    console.log(
      `  ${id}  ${h.dist.toFixed(2)} m gap  (${h.a.x.toFixed(2)},${h.a.z.toFixed(2)}) y ${h.a.y0.toFixed(2)}..${h.a.y1.toFixed(2)} → ` +
      `(${h.b.x.toFixed(2)},${h.b.z.toFixed(2)}) y ${h.b.y0.toFixed(2)}..${h.b.y1.toFixed(2)}  geometry ${h.hits}/${h.steps - 1}`
    );
  }
}
console.log(byId.size ? `\n${byId.size} railings have a hole with railing in it` : '\nno holes with railing in them');
