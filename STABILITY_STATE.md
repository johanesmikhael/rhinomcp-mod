# Stability evaluator — state of the work

Branch `MultiBodyStability`, current as of 2026-08-21. Written to survive a context
reset: what the three modes do, what is trustworthy, what is not, and the cases whose
answers are known independently of the solver.

## The three modes

| mode | bodies | joints | question |
| --- | --- | --- | --- |
| `welded` | whole scope as one rigid body | none | does the assembly tip or slide? |
| `multi_body_contact` | one per element | bearing patches, compression only, friction 0.6 | can an element rotate off, lift, slide? |
| `multi_body_pinned` | one per element | shared particles at clustered nodes | is it a mechanism? |

Welded is an upper bound: it supplies every moment connection the real assembly lacks.

## What is now physically grounded

- **Contact stiffness** is derived from the load each bearing surface carries
  (`P_tributary / delta`), and each body's total goal weight is held to `carried / delta`
  so joint count does not set the clock. Knobs are stated as lengths
  (`joint_penetration`, `ground_settlement`), never as a modulus.
- **Pinned stiffness** is the member's own axial stiffness `k = EA/L`, with the section
  recovered from mass (`A = m/(rho L)`, so `k = (E/rho) m / L^2`). Defaults: steel,
  E = 210 GPa, rho = 7850, overridable via `youngs_modulus` / `material_density`.
- **Kangaroo's factor of four**: `RigidMesh.Calculate` proposes `Move = 0.25 * error`, so
  equilibrium sits at `error = 4F/Strength`. Pass `4k` to realise `k`. Verified on a single
  anchored body: predicted 0.40 mm, measured 0.547 mm with ground springs in series.
- **Pinned verdict measures pins, not bodies.** A member pinned at exactly two nodes spins
  freely about that axis; that is a freedom of the idealisation, not of the structure.
- **Node clustering**: the graph emits one bearing point per element *pair*, not per node.
  `ClusterJointsIntoNodes` gathers them per body using the largest gap in the single-linkage
  merge distances (equivalent to a radius sweep's plateau, read exactly off the MST),
  clamped by the element's own section.
- **Bearing points** come from sampling the region where two meshes are within the contact
  gap, symmetric in the pair, rather than from a tolerant intersection.

## What is NOT trustworthy yet

1. **Collapse detection depends on the iteration budget.** Relaxation converges toward
   equilibrium; it does not fall, so a mechanism creeps instead of collapsing and the
   verdict depends on how long it is allowed to creep. Still the single biggest open
   defect, but no longer unmeasured: the slow tier of the regression suite records, per
   case, the smallest budget that reaches the right answer, and fails when one rises.
   Both slow cases now sit at the default budget, where the unbraced bridge previously
   needed ~5250 iterations (62 s).
2. **Contact mode's blind band.** Marginal eccentricity is absorbed as elastic tilt.
   Measured on 3-body stairs: -112 mm topples, -75 mm does not. `torque_gain` (default
   0.25) is the suspect term and remains an arbitrary constant.
3. **No automatic cross-checking.** Every defect found so far was caught by running a
   second, independent method by hand (statics margin, rank test, hand arithmetic).
4. **Trend labels mislead.** `rotation_trend: steady` is reported while rotation is still
   growing, because the test asks about *acceleration*. Rename or report both windows.
5. **Imperial units untested** end to end. Millimetres and metres are verified.
6. **`floor_strength` is not a subgrade modulus.** It is divided by the summed tributary
   areas of the vertices standing on the floor, which include those corners' share of the
   side faces meeting there - a 0.3 x 0.4 m pedestal base sums to ~0.47 m2, not 0.12. The
   product `ground_support_stiffness_n_per_m` is the quantity with physical meaning.
7. **The unbraced bridge is caught by the divergence trend, not by displacement.** Its pin
   displacement is 1.47 mm against a 60.8 mm limit, barely different from the braced case's
   1.60 mm. The verdict is right; the path to it is the weaker of the two.
8. **Pinned node clustering finds 23 nodes on the unbraced bridge where the geometry has
   17** (12 bottom + 5 ridge). Unexplained.

## Agreed next steps, in order

1. ~~Regression suite from the cases below.~~ **Done** - `scripts/stability_regression/`.
   Cases are built from code, not loaded from a .3dm, so geometry and answer cannot drift
   apart, and are scoped by GUID because an existing layer makes `LayerTable.Add` return -1
   and every object lands on layer 0. Two tiers: fast asserts the verdict at the default
   budget, slow sweeps `solver_substeps` and baselines the ceiling. 10/10 passing.
2. Dynamics prototype on **pinned only** — own integrator over the same goals
   (`force = Weighting * Move`, `a = F/m`), real timestep. Kangaroo's `PhysicalSystem`
   integrates velocity but applies kinetic damping (`Velocity *= 0.9` on reversal, else
   zeroed), so it is an equilibrium finder and cannot be used as a simulator directly.
   `AddParticle(Point3d, double m)` does carry mass.
3. Extend to contact if it holds — that is where it pays, since real dynamics removes the
   blind band and the `torque_gain` fudge.
4. Welded is **no longer** the untouched reference - that premise did not survive the
   suite. Its fixed 1e5 subgrade modulus decided the verdict on its own and read every
   cantilever case as unstable, margin or no margin. The floor is now sized as
   `K = W / settlement` from the load standing on it, at the same 0.1 mm the contact mode
   uses, and welded discriminates across its own tipping point: +121 mm stable, -40 mm
   unstable. The independent `support_margin` cross-check matches hand statics to 0.1 mm
   and did *not* agree with the solver before this fix - it was what exposed it.

Timestep note: stability wants `dt < 2 sqrt(m/k)`; at k = 1e9, m = 100 kg that is ~0.6 ms,
so 0.5 s of simulation is ~800 steps — affordable.

## Regression cases, with answers established independently

Ground truth is hand statics (centre of mass vs the actual overlap polygon) or a
rigid-body rank test, never the solver.

| case | independent answer | solver should say |
| --- | --- | --- |
| Stack A, 10 random blocks | worst joint margin **+157 mm** | stable |
| Stack B, 6-block cantilever stair, step 150 | **-150 mm**, COM 75 mm outside base | unstable |
| 3-block stair, step 250 | -75 mm | unstable (**currently missed**) |
| 3-block stair, step 275 / 300 / 325 | -112 / -150 / -187 mm | unstable (caught) |
| Pedestal + eccentric block | -250 mm | unstable, 3.48 m, 113 deg |
| Bridge, unbraced | **4 mechanisms** (rank test) | unstable (needs 5250 iters today) |
| Bridge, braced with 5 diagonals | **0 mechanisms**; sag ~1.8 mm by hand | stable, measured 1.70 mm |

Bridge model: `bridge test.3dm`, mm document. Two 2x2x0.3 m pads at x = 0 and 10 m;
triangular-prism Warren girder, depth sqrt(2) m so every member is exactly 2000 mm;
40 members at 54 kg (SHS 150x150x6), plus 5 bottom-plane diagonals at 2828 mm, 76.4 kg.
Members are drawn as 150 mm solid boxes but massed as the hollow section - which is why
deriving area from mass matters.

Key structural finding: a square panel cannot be braced by a member of its own edge
length (the diagonal is edge x sqrt(2)), so an all-2 m lattice cannot brace its own
bottom plane. The bridge needs either 4-5 diagonals at 2828 mm, or a true octet module.

## Traps that cost time

- **Unscoped `evaluate_stability` used to read a stale cached graph.** Fixed in `e65f65b`,
  but the habit stands: pass a scope after changing geometry.
- **`RigidMesh.PIndex[0]` is the body particle**; listed points start at index 1.
- **Rebuilding the .rhp while Rhino has it loaded corrupts the running plugin** — symptom
  is "Bad IL range." on every MCP call. Always quit Rhino before `dotnet build` into
  `bin/Debug`; type-check by building a copied tree in the scratchpad.
