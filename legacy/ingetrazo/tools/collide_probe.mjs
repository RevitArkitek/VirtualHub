// Checks an IngeTrazo collider manifest against the real collision engine.
//   node tools/collide_probe.mjs <colliders.json>
// Walls must stop a body, the spawn must be standable, and the ground must be
// where the manifest says it is.
import { readFileSync } from 'node:fs';
import { CollisionWorld } from '../../RevitVR/vr-web/src/collision.js';

const file = process.argv[2];
if (!file) { console.error('usage: node tools/collide_probe.mjs <colliders.json>'); process.exit(2); }
const m = JSON.parse(readFileSync(file, 'utf8'));
const world = new CollisionWorld(m.segments, m.surfaces, m.terrain);

const R = 0.2, EYE = m.eyeHeight ?? 1.6;
let pass = 0, fail = 0;
const check = (name, cond, detail = '') => {
  if (cond) { pass++; console.log(`  ok   ${name}`); }
  else { fail++; console.log(`  FAIL ${name}  ${detail}`); }
};

console.log(`manifest: ${m.name}  ${m.segments.length} segments  ${m.surfaces.length} surfaces  ${m.terrain.length} terrain`);

// ---- spawn is standable -------------------------------------------------
const [sx, sy, sz] = m.spawn;
const feet = sy - EYE;
const ground = world.groundAt(sx, sz, feet + 0.05);
check('ground under the spawn', ground !== null && Math.abs(ground - feet) < 0.12,
      `ground=${ground} feet=${feet}`);
const p = { x: sx, z: sz };
world.resolve(p, R, feet + 0.05, feet + EYE);
const pushed = Math.hypot(p.x - sx, p.z - sz);
check('spawn is not inside a wall', pushed < 0.05, `pushed ${pushed.toFixed(2)} m`);

// ---- every wall stops a body walking into it ----------------------------
let blockedCount = 0;
const misses = [];
for (const s of m.segments) {
  const dx = s.x2 - s.x1, dz = s.z2 - s.z1;
  const len = Math.hypot(dx, dz);
  if (len < 1e-6) continue;
  const ux = dx / len, uz = dz / len;
  const nx = -uz, nz = ux;                       // left normal
  const cx = s.x1 + dx / 2, cz = s.z1 + dz / 2;
  const fy = s.y0 + 0.1, hy = s.y1 - 0.1;
  if (hy - fy < 0.2) continue;                   // too thin vertically to walk into
  // start 0.9 m clear of the wall, walk 1.8 m straight through it
  const from = { x: cx + nx * 0.9, z: cz + nz * 0.9 };
  const q = { x: from.x, z: from.z };
  const steps = 60;
  for (let i = 0; i < steps; i++) {
    q.x += (-nx * 1.8) / steps;
    q.z += (-nz * 1.8) / steps;
    world.resolve(q, R, fy, hy);
  }
  const v = (q.x - cx) * nx + (q.z - cz) * nz;   // signed distance across the wall
  if (v > (s.thickness ?? 0.1) / 2) blockedCount++;
  else misses.push(`(${s.x1.toFixed(2)},${s.z1.toFixed(2)})→(${s.x2.toFixed(2)},${s.z2.toFixed(2)}) crossed to ${v.toFixed(2)}`);
}
console.log(`  walls stopping a body: ${blockedCount}/${m.segments.length}`);
check('every wall run stops you', misses.length === 0, misses.slice(0, 4).join(' | '));

// ---- a body on the floor does not fall through --------------------------
let floors = 0;
for (const s of m.surfaces.slice(0, 40)) {
  const x = (s.minX + s.maxX) / 2, z = (s.minZ + s.maxZ) / 2;
  if (s.p) {
    let inside = false;
    const n = s.p.length / 2;
    for (let i = 0, j = n - 1; i < n; j = i++) {
      const xi = s.p[i * 2], zi = s.p[i * 2 + 1], xj = s.p[j * 2], zj = s.p[j * 2 + 1];
      if ((zi > z) !== (zj > z) && x < xi + ((z - zi) / (zj - zi)) * (xj - xi)) inside = !inside;
    }
    if (!inside) continue;
  }
  const g = world.groundAt(x, z, s.y + 0.05);
  if (g !== null && Math.abs(g - s.y) < 0.02) floors++;
}
check('surfaces are walkable ground', floors > 0, `${floors} tested`);

console.log();
console.log(fail ? `FAILED: ${fail}` : `all ${pass} collision checks passed`);
process.exit(fail ? 1 : 0);
