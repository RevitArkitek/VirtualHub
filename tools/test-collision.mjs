// Headless acceptance test for the collision engine.
//   node tools/test-collision.mjs runtime/export/colliders.json
// Asserts the behaviour the whole product rests on: walls stop you, doorways do not.
import { readFileSync } from 'node:fs';
import { CollisionWorld } from '../web/src/collision.js';

const file = process.argv[2] || 'runtime/export/colliders.json';
const manifest = JSON.parse(readFileSync(file, 'utf8'));
const world = new CollisionWorld(manifest.segments, manifest.surfaces);

const R = 0.3, FEET = 0, HEAD = 1.7;
let pass = 0, fail = 0;

function check(name, cond, detail = '') {
  if (cond) { pass++; console.log(`  ok   ${name}`); }
  else { fail++; console.log(`  FAIL ${name}  ${detail}`); }
}

// Walk a body towards a target in small steps, letting collision resolve each step.
function walk(from, to, steps = 400) {
  return walkAt(from, to, steps, FEET, HEAD);
}

// Same, for a body standing on a storey rather than on the ground.
function walkAt(from, to, steps, feet, head) {
  const p = { x: from[0], z: from[1] };
  for (let i = 0; i < steps; i++) {
    p.x += (to[0] - from[0]) / steps;
    p.z += (to[1] - from[1]) / steps;
    world.resolve(p, R, feet, head);
  }
  return p;
}

console.log(`collision test: ${manifest.segments.length} segments, ${manifest.surfaces.length} surfaces`);

// 1. north wall at z=0 must stop the player short of it (wall half-thickness 0.1 + radius 0.3)
const north = walk([3, 2], [3, -3]);
check('north wall blocks', north.z > 0.1 && north.z < 0.1 + R + 0.02, `ended at z=${north.z.toFixed(3)}`);

// 2. the doorway (x 2.0..2.9) must let the player through the south wall at z=4
const door = walk([2.45, 3], [2.45, 6]);
check('doorway is passable', door.z > 4.5, `ended at z=${door.z.toFixed(3)}`);

// 3. the wall 1m to the right of the doorway must not be
const beside = walk([4.5, 3], [4.5, 6]);
check('wall beside doorway blocks', beside.z < 4.0, `ended at z=${beside.z.toFixed(3)}`);

// 4. walking diagonally into a corner must not tunnel through
const corner = walk([1.0, 1.0], [0.35, 0.35]);
check('inner corner holds', corner.x > 0.1 && corner.z > 0.1, `ended at (${corner.x.toFixed(2)}, ${corner.z.toFixed(2)})`);

// 5. partition at x=6 blocks travel between the rooms except at its doorway
const partition = walk([5.0, 3.0], [8.0, 3.0]);
check('partition x=6 blocks', partition.x < 6.0, `ended at x=${partition.x.toFixed(3)}`);

// 5b. the interior doorway (z 1.4..2.3) connects the two rooms
const innerDoor = walk([5.0, 1.85], [8.0, 1.85]);
check('interior doorway is passable', innerDoor.x > 6.5, `ended at x=${innerDoor.x.toFixed(3)}`);

// 6. a body already inside a wall gets ejected instead of sticking
const inside = { x: 6.0, z: 3.0 };
const wasBlocked = world.resolve(inside, R, FEET, HEAD);
check('inside-wall body ejects', wasBlocked && Math.abs(inside.x - 6.0) > 0.1,
  `moved to (${inside.x.toFixed(2)}, ${inside.z.toFixed(2)})`);

// 7. ground snapping finds the slab
check('ground snap = 0', world.groundAt(3, 3, 1.6) === 0);
check('no ground off-slab', world.groundAt(-50, -50, 1.6) === null);

// 8. height filtering: a wall whose sill is above the player must not block
const upper = new CollisionWorld([{ x1: 0, z1: 0, x2: 10, z2: 0, thickness: 0.2, y0: 2.5, y1: 5 }], []);
// body starts overlapping the wall footprint; downstairs it must be left alone
const under = { x: 5, z: 0.2 };
upper.resolve(under, R, 0, 1.7);
check('wall above head does not block', Math.abs(under.z - 0.2) < 1e-6, `z=${under.z}`);

// 9. mixed storey: the same wall must eject the body once it is on the upper floor
const upstairs = { x: 5, z: 0.2 };
upper.resolve(upstairs, R, 2.5, 4.2);
check('same wall blocks upstairs', Math.abs(upstairs.z - 0.2) > 0.15, `ejected to z=${upstairs.z.toFixed(2)}`);

// ---------------------------------------------------------------- stairs
// A flight built the way the exporter emits one: each tread is a polygon at its own height.
const RISER = 0.18, GOING = 0.28, STEPS = 12;
const stairSurfaces = [{ y: 0, minX: -0.6, maxX: 0.6, minZ: 0, maxZ: 1 }];
for (let i = 0; i < STEPS; i++) {
  const zFar = -GOING * (i + 1), zNear = -GOING * i;
  stairSurfaces.push({
    y: RISER * (i + 1),
    minX: -0.6, maxX: 0.6, minZ: zFar, maxZ: zNear,
    p: [-0.6, zNear, 0.6, zNear, 0.6, zFar, -0.6, zFar],
  });
}
const stairs = new CollisionWorld([], stairSurfaces);

let y = 0, z = 0.5, biggestStep = 0;
for (let i = 0; i < 500; i++) {
  z -= 0.01;
  const g = stairs.groundAt(0, z, y, 0.45, 3.0);
  if (g !== null && g !== y) { biggestStep = Math.max(biggestStep, g - y); y = g; }
  if (z < -GOING * STEPS) break;
}
check('stairs: climbs the whole flight', Math.abs(y - RISER * STEPS) < 0.02,
  `ended at y=${y.toFixed(2)}, expected ${(RISER * STEPS).toFixed(2)}`);
check('stairs: no step exceeds the climb limit', biggestStep <= 0.45, `biggest jump ${biggestStep.toFixed(2)} m`);

for (let i = 0; i < 500; i++) {
  z += 0.01;
  const g = stairs.groundAt(0, z, y, 0.45, 3.0);
  if (g !== null && g !== y) y = g;
  if (z > 0.5) break;
}
check('stairs: descends back to the landing', Math.abs(y) < 0.02, `back at y=${y.toFixed(2)}`);

// a polygon tread must not be walkable outside its outline
const diamond = new CollisionWorld([], [
  { y: 1, minX: -1, maxX: 1, minZ: -1, maxZ: 1, p: [0, -1, 1, 0, 0, 1, -1, 0] },
]);
check('polygon tread: centre is walkable', diamond.groundAt(0, 0, 1) === 1);
check('polygon tread: corner of its box is not', diamond.groundAt(0.9, 0.9, 1) === null);

// ---------------------------------------------------------------- terrain
// A 10% slope rising in +Z: y=0 at z=0, y=1 at z=10. Flat planes cannot express this.
const slope = new CollisionWorld([], [], [
  [0, 0, 0, 10, 0, 0, 10, 1, 10],
  [0, 0, 0, 10, 1, 10, 0, 1, 10],
]);
check('terrain: flat at the bottom edge', Math.abs(slope.groundAt(5, 0, 0) - 0) < 0.01,
  `got ${slope.groundAt(5, 0, 0)}`);
check('terrain: height interpolates mid-slope', Math.abs(slope.groundAt(5, 2.5, 0.2) - 0.25) < 0.01,
  `got ${slope.groundAt(5, 2.5, 0.2)}`);
check('terrain: top edge reached', Math.abs(slope.groundAt(5, 10, 1) - 1) < 0.01,
  `got ${slope.groundAt(5, 10, 1)}`);
check('terrain: outside the mesh is not ground', slope.groundAt(-5, 5, 0.5) === null);

let ty = 0, tz = 0.1, biggestRise = 0;
for (let i = 0; i < 400; i++) {
  tz += 0.025;
  const g = slope.groundAt(5, tz, ty, 0.45, 3.0);
  if (g !== null) { biggestRise = Math.max(biggestRise, g - ty); ty = g; }
  if (tz >= 9.9) break;
}
check('terrain: walking uphill follows the surface', Math.abs(ty - 0.99) < 0.03, `ended at y=${ty.toFixed(2)}`);
check('terrain: gradient is smooth, not stepped', biggestRise < 0.05, `biggest rise ${biggestRise.toFixed(3)} m`);

// ---------------------------------------------------------------- oriented obstacles
// Furniture arrives as a rotated box. Walking into its face must stop at that face, not at the
// world-axis bounding box that a naive fit would produce.
const ang30 = 30 * Math.PI / 180;
const ux30 = Math.cos(ang30), uz30 = Math.sin(ang30);
const halfLen = 0.8, halfT = 0.4;
const oriented = new CollisionWorld([{
  x1: -ux30 * halfLen, z1: -uz30 * halfLen,
  x2: ux30 * halfLen, z2: uz30 * halfLen,
  thickness: 2 * halfT, y0: 0, y1: 0.75,
}], []);

const nx30 = -uz30, nz30 = ux30;                  // the box's own normal
const start = { x: -nx30 * 3, z: -nz30 * 3 };
const body = { x: start.x, z: start.z };
for (let i = 0; i < 300; i++) {
  body.x += (0 - start.x) / 300;
  body.z += (0 - start.z) / 300;
  oriented.resolve(body, 0.3, 0, 1.7);
}
const stopped = Math.hypot(body.x, body.z);
check('oriented obstacle: stops at its own face', Math.abs(stopped - (halfT + 0.3)) < 0.05,
  `centre distance ${stopped.toFixed(2)}, expected ${(halfT + 0.3).toFixed(2)}`);

const aabbHalf = halfLen * Math.abs(ux30) + halfT * Math.abs(nx30);
check('oriented obstacle: tighter than a world-axis box', stopped < aabbHalf,
  `oriented ${stopped.toFixed(2)} vs axis-aligned ${aabbHalf.toFixed(2)}`);

// ---------------------------------------------------------------- railings
// The demo's landing carries a guard rail: a 1 m barrier at landing height (1.7 m), which is the
// shape a Revit railing collider arrives in. It has to stop you walking off the mezzanine while
// leaving the floor beneath it and the stair route beside it alone.
const rail = manifest.segments.find(
  (s) => Math.abs(s.y0 - 1.7) < 0.02 && Math.abs(s.y1 - 2.7) < 0.02);
check('demo scene carries a railing collider', !!rail,
  'no segment with a landing-height barrier range');

const offEdge = walkAt([9.5, 3.4], [9.5, 2.2], 400, 1.7, 3.4);
check('railing stops you walking off the landing', offEdge.z > 3.05,
  `ended at z=${offEdge.z.toFixed(3)}`);

// A railing is a barrier with the railing's height range, not the storey's: standing under a
// mezzanine guard rail must not be blocked by it, and standing on the mezzanine must be.
const storeyRail = new CollisionWorld([{
  x1: 0, z1: 0, x2: 4, z2: 0, thickness: 0.06, y0: 3.0, y1: 4.0,
}], []);
const beneath = { x: 2, z: 0.02 };
storeyRail.resolve(beneath, R, 0, 1.72);
check('railing one storey up does not block you below', Math.abs(beneath.z - 0.02) < 1e-6,
  `moved to z=${beneath.z}`);

const onTop = { x: 2, z: 0.02 };
storeyRail.resolve(onTop, R, 3.0, 4.72);
check('the same railing blocks you on the mezzanine', Math.abs(onTop.z - 0.02) > 0.15,
  `ejected to z=${onTop.z.toFixed(2)}`);

// and the stair route along the railing stays walkable, all the way up to the landing
let sy = 0, sx = 6.2;
for (let i = 0; i < 360; i++) {
  sx += 0.01;
  const g = world.groundAt(sx, 3.35, sy, 0.45, 3.0);
  if (g !== null) sy = g;
}
check('stairs beside the railing still climb to the landing', Math.abs(sy - 1.7) < 0.02,
  `ended at y=${sy.toFixed(2)}`);

console.log(`\n${pass} passed, ${fail} failed   (${world.stats.tests} narrow-phase tests)`);
console.log(`grid: ${world.segments.length} segments in ${world.stats.gridCells} cells`);
process.exit(fail ? 1 : 0);
