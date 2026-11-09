import * as THREE from 'three';

/**
 * Draws the collision world as translucent boxes over the model.
 *
 * The analytic collider is invisible by design, which makes a walkthrough hard to debug: you
 * cannot tell whether you were stopped by the railing you see, by a barrier a metre away from it,
 * or by nothing at all. This turns colliders.json back into geometry — one box per segment, at
 * the height range and thickness the collision engine actually uses.
 *
 * Colour is by kind, so a railing that is really a wall-sized box, or a barrier in open floor,
 * is obvious at a glance.
 */

const KINDS = {
  railing: { color: 0xff2b2b, opacity: 0.34 },   // the ones that keep going wrong
  wall: { color: 0x59b6ff, opacity: 0.20 },
  obstacle: { color: 0xffb020, opacity: 0.28 },
  other: { color: 0x9aa4b2, opacity: 0.22 },
};

// Where you can stand is drawn as a green plane: a floor polygon, or a terrain
// triangle (a floor with a stairwell hole is triangulated, so that is where the
// storey above a stair actually lives).
// A wall run the exporter dropped because a tag or a layer said "opening".
// Drawn so a tag that did nothing, or one that ate a wall, is visible.
const OPENED = { color: 0xff4de0, opacity: 0.34 };

const GROUND = {
  surface: { color: 0x35d05a, opacity: 0.18 },
  terrain: { color: 0x1f9e44, opacity: 0.22 },
};

/** A flat green plane per floor polygon, triangulated so openings and concave
 *  outlines stay true to the plan. */
function groundFromPolygons(surfaces) {
  const positions = [];
  for (const s of surfaces || []) {
    const flat = Array.isArray(s.p) && s.p.length >= 6 ? s.p : null;
    if (!flat) continue;
    const contour = [];
    for (let i = 0; i < flat.length; i += 2) {
      contour.push(new THREE.Vector2(flat[i], flat[i + 1]));
    }
    let faces = [];
    try {
      faces = THREE.ShapeUtils.triangulateShape(contour, []);
    } catch (e) {
      faces = [];
    }
    if (!faces.length) continue;
    const y = s.y + 0.005;              // a hair above the model's own floor
    for (const [a, b, c] of faces) {
      for (const idx of [a, b, c]) {
        const p = contour[idx];
        positions.push(p.x, y, p.y);
      }
    }
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  geo.computeVertexNormals();
  return geo;
}

/** A green plane per terrain triangle — sloped ground, and floors with holes. */
function groundFromTerrain(terrain) {
  const positions = [];
  for (const t of terrain || []) {
    if (!t || t.length < 9) continue;
    positions.push(t[0], t[1] + 0.005, t[2],
                   t[3], t[4] + 0.005, t[5],
                   t[6], t[7] + 0.005, t[8]);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  geo.computeVertexNormals();
  return geo;
}

function groundMesh(geo, style, name, count) {
  if (!count) { geo.dispose(); return null; }
  const material = new THREE.MeshBasicMaterial({
    color: style.color,
    transparent: true,
    opacity: style.opacity,
    depthWrite: false,
    // No depth test on purpose: this is a debug overlay, and a floor's finish
    // layer sits a few centimetres above the analytic surface the engine uses.
    // Depth-tested, the green plane hides under the model's own floor exactly
    // where you most want to see it. It draws over the model and under the
    // collider boxes (renderOrder).
    depthTest: false,
    side: THREE.DoubleSide,
    toneMapped: false,
  });
  const mesh = new THREE.Mesh(geo, material);
  mesh.name = name;
  mesh.frustumCulled = false;
  mesh.renderOrder = 999;               // under the collider boxes, over the model
  return mesh;
}

/**
 * Railings are the only segments the exporter stamps with a sourceId. Walls and furniture are
 * told apart by height: a wall spans a storey, a table does not.
 */
export function classifySegment(s) {
  if (s.sourceId !== undefined && s.sourceId !== null) return 'railing';
  const height = (s.y1 ?? 0) - (s.y0 ?? 0);
  if (height >= 2.0) return 'wall';
  if (height > 0) return 'obstacle';
  return 'other';
}

export function buildColliderView(segments, options = {}) {
  const group = new THREE.Group();
  group.name = 'collider-debug';
  group.renderOrder = 999;

  const unit = new THREE.BoxGeometry(1, 1, 1);
  const buckets = new Map();

  for (const s of segments || []) {
    if (s.solid === false) continue;
    const dx = s.x2 - s.x1;
    const dz = s.z2 - s.z1;
    const length = Math.hypot(dx, dz);
    if (length < 1e-4) continue;

    const kind = classifySegment(s);
    let list = buckets.get(kind);
    if (!list) buckets.set(kind, (list = []));
    list.push({ s, dx, dz, length });
  }

  const matrices = new THREE.Matrix4();
  const quat = new THREE.Quaternion();
  const euler = new THREE.Euler();
  const pos = new THREE.Vector3();
  const scale = new THREE.Vector3();

  for (const [kind, list] of buckets) {
    const style = KINDS[kind] || KINDS.other;
    const material = new THREE.MeshBasicMaterial({
      color: style.color,
      transparent: true,
      opacity: options.opacity ?? style.opacity,
      depthWrite: false,
      side: THREE.DoubleSide,
      toneMapped: false,
    });

    const mesh = new THREE.InstancedMesh(unit, material, list.length);
    mesh.name = `colliders-${kind}`;
    mesh.frustumCulled = false;
    mesh.renderOrder = 1000;

    list.forEach(({ s, dx, dz, length }, i) => {
      // local +X runs along the segment: rotation.y = θ maps +X to (cos θ, 0, -sin θ)
      euler.set(0, Math.atan2(-dz, dx), 0);
      quat.setFromEuler(euler);

      pos.set(
        (s.x1 + s.x2) / 2,
        ((s.y0 ?? 0) + (s.y1 ?? 0)) / 2,
        (s.z1 + s.z2) / 2
      );

      scale.set(
        length,
        Math.max((s.y1 ?? 0) - (s.y0 ?? 0), 0.02),
        Math.max(s.thickness ?? 0.2, 0.03)
      );

      matrices.compose(pos, quat, scale);
      mesh.setMatrixAt(i, matrices);
    });

    mesh.instanceMatrix.needsUpdate = true;
    group.add(mesh);
  }

  // Magenta boxes: wall runs removed as openings, so a door tag or layer you
  // just applied proves itself (or shows it removed the wrong thing).
  const opened = options.openings || [];
  if (opened.length) {
    const material = new THREE.MeshBasicMaterial({
      color: OPENED.color,
      transparent: true,
      opacity: OPENED.opacity,
      depthWrite: false,
      side: THREE.DoubleSide,
      toneMapped: false,
    });
    const mesh = new THREE.InstancedMesh(unit, material, opened.length);
    mesh.name = 'colliders-openings';
    mesh.frustumCulled = false;
    mesh.renderOrder = 1000;
    opened.forEach((o, i) => {
      const dx = o.x2 - o.x1;
      const dz = o.z2 - o.z1;
      const length = Math.hypot(dx, dz);
      euler.set(0, Math.atan2(-dz, dx), 0);
      quat.setFromEuler(euler);
      pos.set((o.x1 + o.x2) / 2, ((o.y0 ?? 0) + (o.y1 ?? 0)) / 2,
              (o.z1 + o.z2) / 2);
      scale.set(Math.max(length, 0.05),
                Math.max((o.y1 ?? 0) - (o.y0 ?? 0), 0.05), 0.12);
      matrices.compose(pos, quat, scale);
      mesh.setMatrixAt(i, matrices);
    });
    mesh.instanceMatrix.needsUpdate = true;
    group.add(mesh);
  }

  // Green planes: where the collision engine will let you stand. Pass
  // `surfaces` and `terrain` from the manifest to see them.
  const surfaces = options.surfaces || [];
  const terrain = options.terrain || [];
  const surfaceMesh = groundMesh(groundFromPolygons(surfaces), GROUND.surface,
                                 'colliders-surfaces', surfaces.length);
  if (surfaceMesh) group.add(surfaceMesh);
  const terrainMesh = groundMesh(groundFromTerrain(terrain), GROUND.terrain,
                                 'colliders-terrain', terrain.length);
  if (terrainMesh) group.add(terrainMesh);

  group.userData.count = (segments || []).filter((s) => s.solid !== false).length;
  group.userData.surfaces = surfaces.length;
  group.userData.terrain = terrain.length;
  group.userData.openings = opened.length;
  group.userData.kinds = [...buckets.keys()];
  return group;
}
