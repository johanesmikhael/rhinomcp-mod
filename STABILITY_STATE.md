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
   equilibrium; it does not fall. With physical stiffness a mechanism creeps: the unbraced
   bridge needs ~5250 solver iterations (62 s) to be caught and reads *stable* at the
   default budget. This is the single biggest open defect.
2. **Contact mode's blind band.** Marginal eccentricity is absorbed as elastic tilt.
   Measured on 3-body stairs: -112 mm topples, -75 mm does not. `torque_gain` (default
   0.25) is the suspect term and remains an arbitrary constant.
3. **No automatic cross-checking.** Every defect found so far was caught by running a
   second, independent method by hand (statics margin, rank test, hand arithmetic).
4. **Trend labels mislead.** `rotation_trend: steady` is reported while rotation is still
   growing, because the test asks about *acceleration*. Rename or report both windows.
5. **Imperial units untested** end to end. Millimetres and metres are verified.

## Agreed next steps, in order

1. Regression suite from the cases below.
2. Dynamics prototype on **pinned only** — own integrator over the same goals
   (`force = Weighting * Move`, `a = F/m`), real timestep. Kangaroo's `PhysicalSystem`
   integrates velocity but applies kinetic damping (`Velocity *= 0.9` on reversal, else
   zeroed), so it is an equilibrium finder and cannot be used as a simulator directly.
   `AddParticle(Point3d, double m)` does carry mass.
3. Extend to contact if it holds — that is where it pays, since real dynamics removes the
   blind band and the `torque_gain` fudge.
4. Leave welded on relaxation as the unchanged reference; it has an independent
   `support_margin` cross-check that agrees.

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
