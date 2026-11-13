// Walks a body along a straight line through a manifest, using the real engine.
//   node tools/walk_probe.mjs <colliders.json> x0 z0 x1 z1 feetY [headY] [radius]
// Prints where the body ended up and how far along the line it got; a body
// stopped at a wall ends short of 1.0.
import { readFileSync } from 'node:fs';
import { CollisionWorld } from '../../web/src/collision.js';

const [, , file, sx, sz, tx, tz, fy, hy, r] = process.argv;
if (!file) { console.error('usage: node tools/walk_probe.mjs <colliders.json> x0 z0 x1 z1 feetY [headY] [radius]'); process.exit(2); }
const m = JSON.parse(readFileSync(file, 'utf8'));
const world = new CollisionWorld(m.segments, m.surfaces, m.terrain);
const from = [+sx, +sz], to = [+tx, +tz];
const feet = +fy, head = hy ? +hy : feet + 1.7, radius = r ? +r : 0.2;

const p = { x: from[0], z: from[1] };
const steps = 400;
for (let i = 0; i < steps; i++) {
  p.x += (to[0] - from[0]) / steps;
  p.z += (to[1] - from[1]) / steps;
  world.resolve(p, radius, feet, head);
}
const dx = to[0] - from[0], dz = to[1] - from[1];
const total = Math.hypot(dx, dz);
const got = Math.hypot(p.x - from[0], p.z - from[1]);
const along = total ? ((p.x - from[0]) * dx + (p.z - from[1]) * dz) / total : 0;
console.log(`from (${from[0]}, ${from[1]}) toward (${to[0]}, ${to[1]})  feet ${feet}`);
console.log(`  ended at (${p.x.toFixed(2)}, ${p.z.toFixed(2)})  ` +
            `travelled ${got.toFixed(2)} of ${total.toFixed(2)} m  ` +
            `(progress ${(total ? along / total : 1 * 100).toFixed(0)}%)`);
console.log(along >= total - 0.15 ? '  THROUGH' : '  BLOCKED');
