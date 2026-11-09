import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { CollisionWorld } from './collision.js';
import { buildColliderView } from './colliderview.js';

const UP = new THREE.Vector3(0, 1, 0);
const RADIUS = 0.20;       // player capsule radius, metres. 0.30 was too fat once railing colliders landed on the stairs: on a 0.9 m flight it left a 0.2 m corridor, so you could not climb. 0.20 leaves ~0.65 m on the same stair.
const HEAD = 1.72;         // capsule height above the floor
const WALK = 1.65;         // m/s
const RUN = 3.4;
const SNAP = Math.PI / 6;  // 30 degrees
const DEAD = 0.18;
const BOOST = 3.0;         // left trigger held: movement speed multiplier (walk, fly, climb)

const app = {
  ready: false,
  scene: new THREE.Scene(),
  rig: new THREE.Group(),
  camera: new THREE.PerspectiveCamera(70, 1, 0.05, 400),
  world: null,
  manifest: null,
  fly: false,
  colliderView: null,
  collidersVisible: false,
  needsRecentre: false,
  floorY: 0,
  yawLatch: 0,
  stats: { frames: 0, blocked: 0, tris: 0 },
};

// ---------------------------------------------------------------- renderer
const canvas = document.getElementById('view');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'high-performance' });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.0;
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
// The scene is static, so the shadow map is rendered once and then frozen.
renderer.shadowMap.autoUpdate = false;
renderer.xr.enabled = true;
renderer.xr.setReferenceSpaceType('local-floor');

// ---------------------------------------------------------------- lighting
app.scene.background = new THREE.Color(0x151b22);
const hemi = new THREE.HemisphereLight(0xdce9ff, 0x3a3226, 0.55);
app.scene.add(hemi);
const ambient = new THREE.AmbientLight(0xffffff, 0.18);
app.scene.add(ambient);
const sun = new THREE.DirectionalLight(0xfff2e2, 1.35);
sun.position.set(6, 12, 8);
sun.castShadow = true;
sun.shadow.mapSize.set(2048, 2048);
sun.shadow.bias = -0.0005;
sun.shadow.normalBias = 0.02;
app.scene.add(sun);
app.scene.add(sun.target);
const fill = new THREE.DirectionalLight(0xa9c4ff, 0.35);
fill.position.set(-8, 6, -6);
app.scene.add(fill);

app.rig.add(app.camera);
app.camera.position.set(0, 1.6, 0);
app.scene.add(app.rig);

// ---------------------------------------------------------------- environment
// Without an environment map, metallic and smooth materials have nothing to reflect and render
// black. A tiny procedural sky (gradient dome + one bright panel) gives IBL for free: no asset,
// no download, and it costs one off-screen PMREM bake at startup.
function buildEnvironment(renderer) {
  const envScene = new THREE.Scene();

  const geo = new THREE.SphereGeometry(60, 24, 16);
  const colors = new Float32Array(geo.attributes.position.count * 3);
  const sky = new THREE.Color(0xcfe0f2);
  const horizon = new THREE.Color(0xb9b4ac);
  const ground = new THREE.Color(0x2a2620);
  const p = geo.attributes.position;
  const c = new THREE.Color();
  for (let i = 0; i < p.count; i++) {
    const t = p.getY(i) / 60;              // -1 (down) .. 1 (up)
    if (t >= 0) c.copy(horizon).lerp(sky, Math.pow(t, 0.7));
    else c.copy(horizon).lerp(ground, Math.pow(-t, 0.5));
    colors[i * 3] = c.r; colors[i * 3 + 1] = c.g; colors[i * 3 + 2] = c.b;
  }
  geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  envScene.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({
    vertexColors: true, side: THREE.BackSide, toneMapped: false,
  })));

  // a bright panel standing in for a window, so specular highlights have a direction
  const panel = new THREE.Mesh(
    new THREE.PlaneGeometry(26, 16),
    new THREE.MeshBasicMaterial({ color: 0xffffff, toneMapped: false })
  );
  panel.position.set(0, 14, -26);
  panel.lookAt(0, 4, 0);
  envScene.add(panel);

  const pmrem = new THREE.PMREMGenerator(renderer);
  pmrem.compileEquirectangularShader();
  const target = pmrem.fromScene(envScene, 0.02);
  pmrem.dispose();
  geo.dispose();
  return target.texture;
}

app.scene.environment = buildEnvironment(renderer);
app.scene.environmentIntensity = 0.9;

// ---------------------------------------------------------------- inputs
const keys = new Set();
addEventListener('keydown', (e) => {
  keys.add(e.code);
  if (e.code === 'KeyF') toggleFly();
  if (e.code === 'KeyR') respawn();
  if (e.code === 'KeyC') toggleColliders();
});
addEventListener('keyup', (e) => keys.delete(e.code));

// desktop mouse look (so the scene is testable without a headset)
let dragging = false, lastX = 0, lastY = 0, pitch = 0, yaw = 0;
canvas.addEventListener('pointerdown', (e) => { dragging = true; lastX = e.clientX; lastY = e.clientY; });
addEventListener('pointerup', () => (dragging = false));
addEventListener('pointermove', (e) => {
  if (!dragging || renderer.xr.isPresenting) return;
  yaw -= (e.clientX - lastX) * 0.0032;
  pitch -= (e.clientY - lastY) * 0.0032;
  pitch = Math.max(-1.45, Math.min(1.45, pitch));
  lastX = e.clientX; lastY = e.clientY;
  app.rig.rotation.y = yaw;
  app.camera.rotation.set(pitch, 0, 0);
});

// ---------------------------------------------------------------- XR controllers
const controllers = [];
for (let i = 0; i < 2; i++) {
  const c = renderer.xr.getController(i);
  const line = new THREE.Line(
    new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -0.6)]),
    new THREE.LineBasicMaterial({ color: 0x9fd0ff, transparent: true, opacity: 0.55 })
  );
  c.add(line);
  c.addEventListener('connected', (e) => { c.userData.handedness = e.data.handedness; });
  c.addEventListener('selectstart', () => {
    if (c.userData.handedness === 'right') toggleFly();
  });

  // left grip, not the trigger: the left trigger is held for boost, so a press-to-toggle there
  // fought with locomotion. The grip is the one input nothing else uses during a walkthrough.
  c.addEventListener('squeezestart', () => {
    if (c.userData.handedness === 'left') toggleColliders();
  });
  app.rig.add(c);
  controllers.push(c);
}

const tmpV = new THREE.Vector3();
const tmpV2 = new THREE.Vector3();
const tmpHead = new THREE.Vector3();

/**
 * Where the head is, in world space. Almost everywhere the player's position is needed, the
 * head is what to use: in a headset you stand somewhere inside your play area, not at its
 * origin, so the rig alone is not where the player is.
 */
function headWorld() {
  return app.camera.getWorldPosition(tmpHead);
}

/** Head displacement from the rig origin, in world space. Zero on the desktop. */
function headOffset() {
  const head = headWorld();
  return { x: head.x - app.rig.position.x, z: head.z - app.rig.position.z };
}

/**
 * Moves the rig so the head sits over the rig origin.
 *
 * Without this the capsule and the body are two different places: walk into a railing and you
 * stop short of it by however far you happen to stand from the play-area origin — a constant
 * clearance that follows you around and reads as colliders being in the wrong place.
 */
function recentreOnHead() {
  if (!renderer.xr.isPresenting) return;
  const offset = headOffset();
  app.rig.position.x += offset.x;
  app.rig.position.z += offset.z;
}

// ---------------------------------------------------------------- session
const btn = document.getElementById('enter');
btn.addEventListener('click', async () => {
  try {
    const session = await navigator.xr.requestSession('immersive-vr', {
      optionalFeatures: ['local-floor', 'bounded-floor', 'hand-tracking', 'layers'],
    });
    await renderer.xr.setSession(session);
    app.needsRecentre = true;      // the first XR frame supplies the head pose
    session.addEventListener('end', () => { app.camera.position.set(0, 1.6, 0); document.body.classList.remove('presenting'); });
    document.body.classList.add('presenting');
  } catch (err) {
    console.error(err);
    document.getElementById('hint').textContent =
      'XR session failed: ' + (err && err.message ? err.message : err);
  }
});

function toggleFly() {
  app.fly = !app.fly;
  if (!app.fly && app.world) {
    // dropping out of fly at height should land you, not leave you hovering
    const g = app.world.groundAt(app.rig.position.x, app.rig.position.z, app.rig.position.y + 0.45, 0.45, 500);
    if (g !== null) {
      app.floorY = g;
      app.rig.position.y = g;
    }
  }
  flash(app.fly ? 'FLY' : 'WALK');
}
/**
 * Shows or hides the collision world as translucent boxes.
 *
 * Turning it on makes the invisible visible: a railing that stops you two metres early, or a
 * barrier with a hole in it, is obvious once the boxes are drawn over the model.
 */
function toggleColliders(force = null) {
  if (!app.colliderView) return;
  const next = force === null ? !app.collidersVisible : !!force;
  app.collidersVisible = next;
  app.colliderView.visible = next;
  flash(next ? 'COLLIDERS ON (C)' : 'COLLIDERS OFF');
}

function respawn(opts = {}) {
  const m = app.manifest;
  app.rig.position.set(m.spawn[0], Math.max(0, m.spawn[1] - m.eyeHeight), m.spawn[2]);
  app.floorY = app.rig.position.y;
  yaw = Math.atan2(-m.spawnForward[0], -m.spawnForward[2]);
  app.rig.rotation.y = yaw;
  pitch = 0;
  app.camera.rotation.set(0, 0, 0);
  if (renderer.xr.isPresenting) recentreOnHead();
  if (!opts.silent) flash('RESPAWN');
}
function flash(t) {
  const el = document.getElementById('toast');
  el.textContent = t;
  el.classList.add('show');
  clearTimeout(flash._t);
  flash._t = setTimeout(() => el.classList.remove('show'), 900);
}

// ---------------------------------------------------------------- movement
/**
 * Applies one movement step. Kept separate from the render loop so the headless
 * test harness can drive it deterministically.
 *
 * `boost` is the fast-navigation multiplier (left trigger held) and scales every axis that
 * moves the rig — travel and climb alike — but never the snap turn, which stays one 30°
 * step so fast navigation cannot spin someone by accident.
 */
function step(dt, wish, turn, boost = false) {
  if (!app.world) return;

  // horizontal basis from where the head is actually looking
  const dir = app.camera.getWorldDirection(tmpV);
  dir.y = 0;
  if (dir.lengthSq() < 1e-6) dir.set(0, 0, -1);
  dir.normalize();
  const right = tmpV2.crossVectors(dir, UP).normalize();

  const speed = ((keys.has('ShiftLeft') || keys.has('ShiftRight')) ? RUN : WALK) * (boost ? BOOST : 1);
  const dx = (dir.x * wish.y + right.x * wish.x) * speed * dt;
  const dz = (dir.z * wish.y + right.z * wish.x) * speed * dt;

  if (!app.fly) {
    const p = app.rig.position;
    const before = { x: p.x, z: p.z };

    // Resolve the capsule at the head, then move the rig by whatever correction was needed.
    // Resolving the rig origin instead leaves you stopped a body's width away from every wall.
    const offset = headOffset();
    const head = { x: p.x + offset.x + dx, z: p.z + offset.z + dz };

    p.x += dx;
    p.z += dz;

    const blocked = app.world.resolve(head, RADIUS, p.y, p.y + HEAD);
    if (blocked) app.stats.blocked++;

    p.x += head.x - (p.x + offset.x);
    p.z += head.z - (p.z + offset.z);

    // keep the capsule out of walls even if the player spawned inside one
    if (before.x === p.x && before.z === p.z && (dx || dz)) app.stats.blocked++;

    // ground snapping: highest walkable slab under us within one stair riser
    const g = app.world.groundAt(head.x, head.z, app.floorY, 0.45, 3.0);
    if (g !== null && Math.abs(g - app.floorY) > 1e-4) {
      app.floorY += (g - app.floorY) * Math.min(1, dt * 12);
      p.y = app.floorY;
    } else if (g !== null) {
      p.y = g;
    }
  } else {
    app.rig.position.x += dx;
    app.rig.position.z += dz;
    if (wish.fly !== 0) app.rig.position.y += wish.fly * speed * dt;
    app.floorY = app.rig.position.y;
  }

  if (turn) snapTurn(turn * SNAP);
}

function snapTurn(angle) {
  // +angle rotates the rig about +Y, which is a *left* turn (right-handed, counter-clockwise
  // seen from above). Callers that think in "turn right" pass a negative angle — see
  // turnFromAxis().
  const head = app.camera.getWorldPosition(new THREE.Vector3());
  const delta = head.clone().sub(app.rig.position);
  delta.applyAxisAngle(UP, angle);
  app.rig.position.copy(head).sub(delta);
  app.rig.rotation.y += angle;
  if (!renderer.xr.isPresenting) yaw = app.rig.rotation.y;
}

/**
 * Right stick X axis → rotation to hand snapTurn().
 *
 * WebXR reports +x for "pushed right", and a positive rotation about +Y turns you *left*, so
 * the sign flips here: push the stick right and you turn right.
 */
function turnFromAxis(ax) {
  return -Math.sign(ax);
}

function readThumbsticks(sources = renderer.xr.getSession()?.inputSources ?? []) {
  let wish = { x: 0, y: 0 };
  let turn = 0;
  let rightY = 0;   // right stick Y, forward positive. Fly mode: the climb axis.
  let boost = false;   // left trigger held: fast navigation
  for (const src of sources) {
    const gp = src.gamepad;
    if (!gp || !gp.axes || gp.axes.length < 4) continue;
    const ax = gp.axes[2], ay = gp.axes[3];
    if (src.handedness === 'right') {
      // Climb and snap turn are independent: the stick can do both at once, so the turn latch
      // gates the turn only and never the Y axis.
      if (Math.abs(ay) >= DEAD) rightY = -ay;
      // Snap turn latch so one push = one 30 degree step. Release is checked *before* the
      // deadzone skip, because letting go parks the stick at (0,0) — the case that a
      // deadzone-first skip would hide, leaving the latch stuck and the stick dead for the
      // rest of the session.
      if (Math.abs(ax) < 0.4) {
        app.yawLatch = 0;
      } else if (Math.abs(ax) > 0.7 && app.yawLatch === 0) {
        turn = turnFromAxis(ax);
        app.yawLatch = Math.sign(ax);
      }
    } else {
      // Left hand — and any source the runtime does not label — travels the rig, and its
      // index trigger (xr-standard button 0) is the fast-navigation boost.
      if (gp.buttons && gp.buttons[0] && gp.buttons[0].pressed) boost = true;
      if (Math.abs(ax) >= DEAD || Math.abs(ay) >= DEAD) wish = { x: ax, y: -ay };
    }
  }
  return { wish, turn, rightY, boost };
}

// ---------------------------------------------------------------- loop
const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
  const dt = Math.min(clock.getDelta(), 0.05);

  // the head pose only exists inside an XR frame, so the first frame after starting is the
  // earliest the rig can be lined up under the player
  if (app.needsRecentre && renderer.xr.isPresenting) {
    recentreOnHead();
    app.needsRecentre = false;
  }
  if (renderer.xr.isPresenting) {
    const { wish, turn, rightY, boost } = readThumbsticks();
    // Fly: left stick travels (forward/back and strafe) exactly as it does on foot, and the
    // right stick's Y is the climb axis — push forward to rise, pull back to descend. Right
    // stick X stays snap turn, so you can climb and turn at the same time.
    if (app.fly) step(dt, { x: wish.x, y: wish.y, fly: rightY }, turn, boost);
    else step(dt, { x: wish.x, y: wish.y, fly: 0 }, turn, boost);
  } else {
    const f = (keys.has('KeyW') ? 1 : 0) - (keys.has('KeyS') ? 1 : 0);
    const s = (keys.has('KeyD') ? 1 : 0) - (keys.has('KeyA') ? 1 : 0);
    const fly = (keys.has('KeyE') ? 1 : 0) - (keys.has('KeyQ') ? 1 : 0);
    step(dt, { x: s, y: f, fly }, 0);
    app.camera.position.y = 1.6;
  }
  app.stats.frames++;
  renderer.render(app.scene, app.camera);
  updateHud();
});

let hudT = 0;
function updateHud() {
  if (performance.now() - hudT < 250) return;
  hudT = performance.now();
  const p = app.rig.position;
  document.getElementById('hud').textContent =
    `${p.x.toFixed(1)}, ${p.y.toFixed(1)}, ${p.z.toFixed(1)} m` +
    `   ${app.stats.tris.toLocaleString()} tris` +
    (app.fly ? '   FLY' : '') +
    (app.collidersVisible ? '   COLLIDERS' : '');
}

// ---------------------------------------------------------------- load
async function load() {
  const manifest = await fetch('./scene/colliders.json', { cache: 'no-store' }).then((r) => {
    if (!r.ok) throw new Error('colliders.json: HTTP ' + r.status);
    return r.json();
  });
  app.manifest = manifest;
  app.world = new CollisionWorld(manifest.segments, manifest.surfaces, manifest.terrain);

  // the collision world, drawable on demand; ?colliders turns it on at load, which is what makes
  // a screenshot of a suspect railing reproducible
  app.colliderView = buildColliderView(manifest.segments, {
    surfaces: manifest.surfaces,
    terrain: manifest.terrain,
    openings: manifest.openings_applied,
  });
  app.colliderView.visible = false;
  app.scene.add(app.colliderView);
  if (new URLSearchParams(location.search).has('colliders')) toggleColliders(true);

  // Baked light supersedes the analytic ambient fill: the vertex colours already carry sky
  // visibility and one bounce, so leaving the hemisphere at full strength would light the room
  // twice and wash the bake out.
  if (manifest.bakedLight) {
    hemi.intensity *= 0.25;
    ambient.intensity *= 0.25;
    app.scene.environmentIntensity *= 0.6;
  }

  const gltf = await new GLTFLoader().loadAsync('./scene/scene.glb');
  let tris = 0;
  gltf.scene.traverse((o) => {
    if (o.isMesh) {
      o.frustumCulled = true;
      o.castShadow = true;
      o.receiveShadow = true;
      const g = o.geometry;
      tris += (g.index ? g.index.count : g.attributes.position.count) / 3;
      const m = o.material;
      if (m) {
        // Spike preview: the vertex colour carries the whole baked irradiance, so the surface must
        // not be lit a second time by the renderer's lights. Unlit material, no tone mapping —
        // the bake already carries a display curve.
        if (app.manifest.bakedLight) {
          o.material = new THREE.MeshBasicMaterial({
            map: m.map, color: m.color, vertexColors: true, side: m.side, toneMapped: false,
          });
          return;
        }
        m.envMapIntensity = 0.8;
        // Textures are the difference between a coloured massing model and a building: give them
        // the correct colour space, and let the GPU read them at the glancing angles a headset
        // looks along at floors and walls.
        const aniso = renderer.capabilities.getMaxAnisotropy();
        if (m.map) {
          m.map.colorSpace = THREE.SRGBColorSpace;
          m.map.anisotropy = aniso;
        }
        if (m.normalMap) m.normalMap.anisotropy = aniso;
        m.needsUpdate = true;
      }
    }
  });
  app.stats.tris = Math.round(tris);
  app.scene.add(gltf.scene);

  // fit the single shadow map to the model, then freeze it
  const box = new THREE.Box3().setFromObject(gltf.scene);
  if (!box.isEmpty()) {
    const c = box.getCenter(new THREE.Vector3());
    const size = Math.min(box.getSize(new THREE.Vector3()).length(), 80);
    sun.position.copy(c).add(new THREE.Vector3(size * 0.4, size * 0.75, size * 0.35));
    sun.target.position.copy(c);
    sun.target.updateMatrixWorld();
    const sc = sun.shadow.camera;
    sc.left = -size / 2; sc.right = size / 2;
    sc.top = size / 2; sc.bottom = -size / 2;
    sc.near = 0.5; sc.far = size * 2.5;
    sc.updateProjectionMatrix();
  }

  respawn({ silent: true });
  document.getElementById('loading').classList.add('gone');
  renderer.shadowMap.needsUpdate = true;
  app.ready = true;
  window.__vr = debugApi();
}

// ---------------------------------------------------------------- test hooks
function debugApi() {
  return {
    app,
    get position() { return app.rig.position.clone(); },
    setPose(x, z, yawDeg = 0, y = null) {
      app.rig.position.set(x, y ?? app.rig.position.y, z);
      app.floorY = app.rig.position.y;
      yaw = (yawDeg * Math.PI) / 180;
      app.rig.rotation.y = yaw;
      app.camera.rotation.set(0, 0, 0);
      pitch = 0;
    },
    /** Walk in a straight line for `seconds`, as the movement code would. */
    walkWard(seconds, fwd = 1, strafe = 0, dt = 1 / 72) {
      const n = Math.round(seconds / dt);
      for (let i = 0; i < n; i++) step(dt, { x: strafe, y: fwd, fly: 0 }, 0);
      return app.rig.position.clone();
    },
    groundAt: (x, z) => app.world.groundAt(x, z, app.floorY),
    get blocked() { return app.stats.blocked; },
    toggleColliders,
    get collidersVisible() { return app.collidersVisible; },
    get colliderCount() { return app.colliderView ? app.colliderView.userData.count : 0; },

    /** Heading the camera is actually looking along, degrees (0 = -Z, positive = left). */
    heading() {
      const d = app.camera.getWorldDirection(new THREE.Vector3());
      return (Math.atan2(-d.x, -d.z) * 180) / Math.PI;
    },
    /**
     * Push the thumbsticks through the real readThumbsticks() → step() path with no headset.
     * `right` is the right stick's X axis, `left` is [x, y]; opts: `rightY` (right stick Y,
     * forward positive), `trigger` (left index trigger held), `dt`. Call with right = 0 to
     * release the snap-turn latch, as letting go of the stick does.
     */
    simulateStick(right = 0, left = [0, 0], { dt = 1 / 72, rightY = 0, trigger = false } = {}) {
      const sources = [
        { handedness: 'left', gamepad: { axes: [0, 0, left[0], left[1]], buttons: [{ pressed: trigger }] } },
        { handedness: 'right', gamepad: { axes: [0, 0, right, rightY], buttons: [{ pressed: false }] } },
      ];
      const { wish, turn, rightY: climb, boost } = readThumbsticks(sources);
      if (app.fly) step(dt, { x: wish.x, y: wish.y, fly: climb }, turn, boost);
      else step(dt, { x: wish.x, y: wish.y, fly: 0 }, turn, boost);
      return { turn, wish, climb, boost, headingDeg: this.heading(), latch: app.yawLatch };
    },
  };
}

addEventListener('resize', () => {
  app.camera.aspect = window.innerWidth / window.innerHeight;
  app.camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
});

if (navigator.xr) {
  navigator.xr.isSessionSupported('immersive-vr').then((ok) => {
    // On a desktop the hint is the only place the controls are listed, so it has to keep
    // saying them — the preview button opens this page with no headset anywhere in sight.
    document.getElementById('hint').textContent = ok
      ? 'Headset detected — put it on and press Enter VR.  Desktop: WASD, drag to look, F to fly, R to respawn, C for colliders.'
      : 'Desktop preview — WASD to walk, drag to look, F to fly, Q/E down and up, R to respawn, C for colliders.';
    // Clicking Enter VR on a machine with no XR device can only fail, so don't invite it.
    btn.disabled = !ok;
  }).catch(() => { btn.disabled = false; });
} else {
  document.getElementById('hint').textContent =
    'No WebXR in this browser — desktop preview: WASD to walk, drag to look, F to fly, Q/E down and up, R to respawn, C for colliders.';
}

load().catch((e) => {
  console.error(e);
  document.getElementById('loading').textContent = 'Load failed: ' + e.message;
});
