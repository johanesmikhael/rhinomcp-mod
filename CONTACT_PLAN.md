# Measuring a bearing exactly, for every way two solids can meet

## The requirement

A contact has to be measured in all three states two elements can be drawn in, and for every
combination of geometry kinds they can be drawn as.

| state | offset between the meeting faces | today |
| --- | --- | --- |
| 1. almost touching | `0 < d <= gap` (gap is 1 mm) | measured |
| 2. perfect touching | `d = 0`, coplanar | measured |
| 3. overlapping | `d < 0`, one buried in the other | **joint found, bearing not measured** |

| pair | today |
| --- | --- |
| Brep - Brep | sampled |
| Brep - Mesh | sampled |
| Mesh - Mesh | sampled |

State 2 is the one to keep in mind: it used to fail as well. Coplanar faces do not *intersect*,
so `MeshMeshFast` returned nothing and a deck resting exactly on a beam read as unsupported.
Sampling was introduced to fix precisely that - and sampling is why state 3 breaks, because it
measures **surface-to-surface distance**, which falls to zero as two things touch and grows
again once they overlap. State 3 sits on the far side of a cliff whose summit is state 2.

## Why not another patch

Three attempts on 2026-08-23, all reverted:

1. spacing taken from the overlap region instead of the body diagonals
2. accepting samples that lie inside the other solid
3. the same, gated to fire only when proximity found nothing

Each fixed the case in front of it and moved a validated number somewhere else - the splayed
legs to 0.704 mm against an exact 0.603, the unbraced bridge's sway, a wall on a pad measuring
181 mm where it is 150. The pattern is the diagnosis: **sampling approximates a region with a
point grid, so every rule about it is a heuristic about where points happen to land.** Truss
members meeting at a node genuinely interpenetrate, so any rule about overlap reaches them too.

## The method

The bearing between two flat faces is a **polygon intersection**. Take the signed offset `d`
between the two face planes along their common normal:

```
-burial <= d <= gap
```

One condition covers all three states. In every case:

- the bearing plane is the **mean plane** of the pair, so a roof buried 20 mm in walls whose
  tops are at 2500 lands at 2490 because that is where the mid-surface is, not because of a rule
- the bearing polygon is the **boolean intersection of the two outlines** projected onto it

Nothing is special-cased, which is why it cannot have the fix-one-break-another behaviour.

## Geometry kinds

Everything reduces to the same intermediate, so the downstream code is written once:

```
PlanarRegion { Plane Plane; Curve Outline; double Area; BoundingBox Box; }
```

- **Brep**: faces where `face.TryGetPlane(out plane)` succeeds; outline from the outer loop.
  `Extrusion` and `SubD` via `ToBrep()`.
- **Mesh**: group faces by quantised plane (normal to ~2 decimals plus offset), keep connected
  groups, and take each group's boundary - the edges used by exactly one face in the group -
  as a closed polyline. This is the part that does not exist yet and is the bulk of the work.
- **Mixed**: one side's regions come from Brep faces and the other's from mesh groups. Same
  pairing, same intersection.

A node that yields no planar regions (a genuinely curved surface) falls back to the sampler,
and the result says which method produced it.

## Steps

### A. Build it alongside, change nothing

1. Region extraction for Brep and for Mesh, cached on the graph node beside `ProxyMesh`.
2. Pair regions: normals antiparallel within the existing 20 degrees, `-burial <= d <= gap`,
   plan bounding boxes overlapping. Bbox prefilter before any curve work.
3. Project both outlines onto the mean plane; `Curve.CreateBooleanIntersection`.
4. `AreaMassProperties` on the result for centroid and second moments, so the principal axes
   are exact rather than a PCA over samples. Largest-area pair governs; report how many
   regions were found.
5. Emit as `contact_extent_exact` **beside** the existing `contact_extent`. No behaviour change.

Then compare the two across every suite model and the pavilion. **That comparison is the
deliverable of step A** - it is the measurement that should have come before any of the three
patches.

### B. Switch

Prefer exact; fall back to sampling for curved faces and skew contacts. Every extent reports
`method: "planar" | "sampled"`, so a fallback is never invisible.

### C. Tighten the tests

- `bearing_extent` tolerance from 5% to ~0.5%: exactness is now the claim.
- One geometry, three states, same expected footprint: a wall and a slab at +0.5 mm, 0 mm and
  -20 mm.
- One geometry, three kinds: the same pair as Brep-Brep, Brep-Mesh, Mesh-Mesh.
- A column on a pad must read exactly 400 x 400. The sampler gave 453, 536, 543 and 544 on
  four identical joints.
- `DIAGONAL` must still report **no** bearing. An edge meeting a face has no parallel region
  pair, so it falls out by construction rather than needing the 20 degree guard.

## Reporting, which is half the point

- `penetration_depth` per joint. Deliberate overlap and a wall driven through a slab look
  identical to the geometry; the depth is what tells them apart, and it should be visible
  rather than silently accepted as a bearing.
- **Mass is double-counted in an overlap** - both solids claim the shared volume when mass
  comes from density. Small overlaps are negligible; report it and let the engineer decide.
- `contact_extent_unmeasured` already reports samples and face counts per failed joint. Keep it.

## Risks

- **The suite will move.** Exact bearings differ from approximate ones and the micro tier
  asserts closed forms. If those shift, re-derive them; do not re-baseline.
- Coplanar boolean robustness - mitigated by projecting both outlines onto one plane first.
- L-shaped or multi-region bearings do not fit one rectangle. Take the largest and report the
  count rather than silently merging.
- Performance: region pairing is O(regions squared) per candidate pair, bbox-prefiltered. Fine
  for boxes; add a face RTree if a real model needs it.

## Working notes that cost time today

- **The graph is served from an in-memory cache** that does not store sample counts, so a
  cached read shows `samples = 0` for everything and looks like a sampler that never ran. Force
  a recompute by pinning a scope: `graph_display(ids=[...])` marks it dirty. Deleting the
  document string is *not* enough.
- Deploy cycle: save the document, `pkill -x Rhinoceros`, `dotnet build -t:Rebuild`,
  `open -a "Rhino 8" <file>`, then poll `get_document_info` until it answers.
- Baselines to hold: **fast 17/17, systems 8/8, geometry 1/1, micro 6/8** (two known failures),
  slow at baseline.

## Files

- `rhino_mcp_plugin/Visualization/MCPConnectivityGraphConduit.cs` - extraction, pairing,
  intersection, `ContactExtent`
- `rhino_mcp_plugin/Functions/GetConnectivityGraph.cs` - the reported fields
- `scripts/stability_regression/cases.py` - the geometry tier cases

---

# Step A result: the two measurements compared

Built by `scripts/stability_regression/compare_bearings.py`, over every model the suite
draws. The exact measurement is emitted beside the sampled one; nothing reads it yet.

**Coverage.** 226 joints across 20 models. Both methods measure 219 of them. The exact
method measures 4 that sampling missed entirely and misses 4 that sampling reported.

**Where both answer, sampling is the one that is wrong**, and by more than the 5% the
geometry tier allows for:

| model | joint | sampled | exact | drawn |
| --- | --- | --- | --- | --- |
| hybrid | `COLUMN_01-PAD_01` | 544.3 x 544.3 | 400.0 x 400.0 | 400 x 400 |
| hybrid | `COLUMN_11-PAD_11` | 542.6 x 542.6 | 400.0 x 400.0 | 400 x 400 |
| hybrid | `COLUMN_00-PAD_00` | 536.0 x 536.0 | 400.0 x 400.0 | 400 x 400 |
| hybrid | `COLUMN_10-PAD_10` | 453.4 x 453.4 | 400.0 x 400.0 | 400 x 400 |
| micro | `COL_1_1-BLOCK_0` | 201.2 x 201.2 | 150.0 x 150.0 | 150 x 150 |
| hybrid | `PANEL_3-BEAM_1` | 1500.0 x 107.9 | 1500.0 x 100.0 | 1500 x 100 |
| geometry | `WALL-PAD` | 1150.3 x 152.0 | 1150.0 x 150.0 | 1150 x 150 |
| geometry | `WALL_ROT30-PAD` | 1150.1 x 151.0 | 1150.0 x 150.0 | 1150 x 150 |

Four identical column-on-pad joints sampled at 453, 536, 542 and 544 for one drawn 400 -
the spread is the grid landing differently under each. Exact reads 400.0 x 400.0 on all
four. The rotated wall is exact at 1150.0 x 150.0, which is the case an axis-aligned box
gets 370% wrong, so the frame is right and not only the size.

**Sampling degenerates to a line on butt joints.** `COL_0_2-PAD`, `M_01-M_00`, `B-A` and
every other end-to-end meeting sampled as `150.0 x 0.0` with a normal 90 degrees off:
a rectangle of zero width, which carries no moment about one axis at all. Exact reads
150 x 150. This is not a precision difference, it is a joint the solver was told is a
hinge when it is a full bearing.

**Interpenetration, which is what this was for.** Every one of the bridge's chords is
drawn on its centreline and therefore sits half sunk into its pad. Sampling read
`M_00-PAD_0` as 1001.7 x 102.2 **on a horizontal normal** - a vertical bearing measured
sideways. Exact reads 1000.0 x 75.0 at `penetration_depth` 75, which is the geometry:
the chord hangs half off the pad edge, so half its 150 width bears.

**What exact refuses.** `DIAGONAL` on a pad, the splayed legs under their block, and the
three skew brace crossings: no parallel region pair exists, so no bearing is reported. That
is by construction rather than by the 20-degree guard, which is what the plan wanted. It
also means step B cannot simply switch - the splayed-leg case has a bearing today and would
lose it, so the fallback to sampling is required, not optional.

## Two defects the comparison found, both in the new code

Neither would have been visible without building the table, which is the argument for the
table.

1. **The plan prefilter compared boxes in 3D**, so it rejected pairs for being exactly the
   distance apart that the offset test had just accepted. Every chord-to-pad bearing on the
   bridge came back unmeasured. A's box is now slid along the normal onto B's plane first.
2. **Burial was bounded by a bounding-box projection.** A brace drawn on a diagonal has a
   150 mm section and a box measuring 2978 mm along its own normal, so two braces nearly
   three metres apart paired as a bearing. Burial is now bounded by the body's thickness at
   that face, read from its opposite face: 150 either way the member is drawn.

## What step B still has to decide

- Two crossing members that interpenetrate share a **volume**, not a face. Exact reports
  the mid-plane of the shared box (`BR_0-M_00`, 181 x 128 at 150 mm penetration); sampling
  reports a 138.6 x 28.7 sliver. Neither is obviously right and the case deserves stating
  before either is trusted.
- `bridge_braced` `BR_4-PAD_1`: exact 1489.2 x 150.0 against sampled 1414.5 x 154.8. The
  brace lands skew on the pad, so the exact rectangle is the bounding box of a genuinely
  non-rectangular polygon. `polygon_area` beside `area` is what says so.
