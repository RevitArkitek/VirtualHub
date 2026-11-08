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

  group.userData.count = (segments || []).filter((s) => s.solid !== false).length;
  group.userData.kinds = [...buckets.keys()];
  return group;
}
