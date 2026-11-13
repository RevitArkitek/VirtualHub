# 001 — baked indirect light (lightmap feasibility)

**Question.** Given a small closed room with one window, when sky visibility and a diffuse bounce
are baked, does the interior read as *lit from the window* rather than evenly ambient — and what
would a shipping implementation cost?

**Why it matters.** Occlusion (tier 1) can only ever darken. The Enscape look comes from light that
arrives indirectly: a sunlit floor brightening the ceiling, a room falling off away from an
opening. If that cannot be baked cheaply, the whole tier-2 plan is wrong.

## Approach

Cheapest experiment that could kill the idea: bake into the storage we already have — per-vertex
`COLOR_0` — instead of building an unwrapper, an atlas and a second texture set up front.

- `IndirectLight` (Core): cosine-sampled path tracer over the same BVH the occlusion bake uses.
  Per vertex: 16 primary rays, up to 3 bounces, one shadow ray per bounce. Sky is a two-colour
  gradient; the sun is directional and shadowed.
- Demo scene gained a closed room: a ceiling and a 2.0 × 1.2 m window opening, so there is a
  room to fill and an opening to fill it through.
- The result is normalised against open-sky radiance and tone-curve compressed (γ 0.75) for the
  preview, because raw radiance spans ~0.03 (back room) to 1.0 (outside).

Reproduce:

```
dotnet run --project tools/demogen -- runtime/bake --bake-preview
dotnet run --project server/RevitVr.Server -- --app web --scene runtime/bake
```

The viewer renders a fully baked preview when the manifest says `bakedLight: true` (unlit
materials, lights off — the vertex colour carries everything).

## Results

| Measure | Value |
|---|---|
| Vertices baked | 6,972 |
| Rays | 16 primary × 3 bounces + shadow rays |
| Bake time | **0.56 s** (20-core CPU, single scene) |
| Throughput | ~300k rays/s |
| Triangles after subdivision | 458 → 10,292 |
| GLB growth | 1.04 → 1.40 MB (no new textures) |

Visual: the room now falls off away from the window and the wall around the opening is brighter —
it reads as an interior lit through a hole rather than a uniformly lit box. The difference is
obvious side by side (`docs/spike-baked-light.png`).

## Verdict: PARTIAL

The lighting idea works. The storage choice does not.

### What worked

- Path-traced bounce light at 3 bounces is enough for a room to read correctly.
- Cost is negligible: 0.56 s for a demo room on the CPU, no GPU, no new dependency.
- The BVH from tier 1 does the whole job; nothing new is needed to trace.

### What didn't

- **Vertex colour cannot carry light.** In three.js (and glTF generally) `COLOR_0` multiplies the
  *diffuse albedo*, so it scales the real-time sun as well as the ambient. Baking light into it
  means either double-darkening the sunlit areas or disabling the sun entirely — the preview here
  is unlit for exactly that reason. Occlusion is fine in this slot (it is a multiplier); light is
  not.
- **Per-vertex resolution is too coarse.** Gradients show as faceting and blotches at ~0.4 m
  spacing, and separate elements (wall segments around the window) have unaligned vertices, so
  their shading does not meet: visible seams.
- **No crisp beam.** A window's light pattern needs texel resolution; vertices cannot hold an edge.

### Surprises

- A single bounce bakes a closed room almost black: a ceiling is lit by the floor, not the sky, so
  the second bounce is what makes the image. The first version of this bake was 3× too dark purely
  from stopping at one bounce.
- The room also needs tone-curve compression before 8-bit storage or it is either crushed or washed
  out; the bake and the display transform are separate problems.

## Recommendation for the real build

1. **Bake into a lightmap, not vertex colour.** three.js `lightMap` contributes to *indirect*
   diffuse only, so it composes with the real-time sun instead of multiplying it — which is the
   correct behaviour and the thing this spike proved vertex colours cannot do.
2. **glTF has no lightmap slot**, so the map rides in a sidecar next to the scene (the pattern
   `colliders.json` already uses): `lightmaps.json` naming the atlas and the per-primitive rect.
   UV1 comes from the exporter as `TEXCOORD_1`, which three maps to `uv1`.
3. **Budget from the measured throughput.** At ~300k rays/s: a 1024² atlas (≈16M rays) is about a
   minute per room — feasible. A 4096² atlas is ~45 minutes — not feasible without a GPU baker or
   tiling. Start at 1024², per floor, with the view crop deciding what gets baked.
4. **Unwrap coherently.** xatlas (or equivalent) over the merged geometry, not per triangle and not
   per element — the seams this spike shows came from elements being unwrapped and interpolated
   independently.
5. **Keep the sun real-time.** Bake sky + bounce only, so the existing shadow map still provides
   crisp direct light and the bake provides what the sun cannot reach.

Not worth building: a real-time GI renderer in the browser for this class of headset, as previously
estimated.
