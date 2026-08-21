# Stability evaluator — state of the work

Branch `MultiBodyStability`, current as of 2026-08-21. Written to survive a context
reset: what the three modes do, what is trustworthy, what is not, and the cases whose
answers are known independently of the solver.

## The three modes

| mode | bodies | joints | question |
| --- | --- | --- | --- |
| `welded` | whole scope as one rigid body | none | does the assembly tip or slide? |
| `pinned_dynamic` | one rigid body per element, pinned | real seconds | how far does it move, and how stiff is it? |
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
7. **The relaxed pinned mode reports a false positive on the unbraced bridge**, through its
   divergence trend rather than displacement. Committed as a failing regression case.
8. ~~Pinned node clustering finds 23 nodes where the geometry has 17.~~ **Explained and
   fixed.** 23 = 17 structural nodes, all correct, plus 6 genuine pad contacts (the test
   members are drawn as 150 mm boxes centred on the node axes, so they really do intersect
   the pads). The actual bug was elsewhere and worse: the per-body merge radius was a
   statistical knee, so adding five bottom diagonals split seven nodes the diagonals do not
   touch, three at the ridge. The radius is now the body's own cross-section - contacts
   spread over a member's thickness (111-134 mm measured for a 150 mm section) while two
   joints on one member are a member length apart (2000 mm), so the scales are known and do
   not need discovering. Braced went 32 clusters to 25 with 0 splits, and its spurious
   hinge pairs from 13 to 6, exactly the 7 the splits were creating.

## Agreed next steps, in order

1. ~~Regression suite from the cases below.~~ **Done** - `scripts/stability_regression/`.
   Cases are built from code, not loaded from a .3dm, so geometry and answer cannot drift
   apart, and are scoped by GUID because an existing layer makes `LayerTable.Add` return -1
   and every object lands on layer 0. Two tiers: fast asserts the verdict at the default
   budget, slow sweeps `solver_substeps` and baselines the ceiling. 10/10 passing.
2. ~~Dynamics prototype on **pinned only**~~ **Done** - mode `pinned_dynamic`. — own integrator over the same goals
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

## Infinitesimal mechanisms, and why the bridge stands

The unbraced bridge has four modes by rank test, and it does not collapse. Both are true.

Under the mode a tie's ends separate as `2*sqrt(1 + (0.71t)^2)`: length preserved to first
order, growing at second. These are *infinitesimal* mechanisms, stiffened quadratically by
the five states of self-stress the same rank test reports beside them. **A rank test counts
modes; it does not predict collapse.** Reading "4 mechanisms" as "unstable" was an inference
laid on top of the test, and it was wrong - it is the one place the suite's ground truth had
to be corrected rather than the solver.

Three independent checks agree the structure stands: the integrator settles it at 0.60 mm
(braced: 0.40 mm) and does not run away undamped or over 2 s; 10% of its weight applied
sideways moves it 1 micron further than no load; and 161 of 167 connected body pairs share
exactly one particle, with every exception a member bearing flat on a pad.

So `pinned_dynamic` reports **sway stiffness** as well as a verdict, since "does it fall"
rates that bridge identically to a braced one. Settle, settle again under the notional
horizontal load the codes prescribe, divide the load by the distance between settled shapes.
Both horizontal directions, disturbance off. Result: **4.67e8 N/m unbraced against 6.94e8
braced in y**, the direction the modes move, and about 2.4e9 in x for both.

Those braced figures are post-clustering-fix. Removing seven joints that should never have
existed made the braced bridge *softer* - 7.27e8 to 6.76e8 in y, and its sag 0.40 mm to
0.62 mm. It had been held up in part by constraints that were an artefact of the merge
radius.

### Dynamics notes

- `Weighting * Move` is a force: Kangaroo's `Unary` carries an applied force as `Move`
  against a weight of exactly 1. Dividing the sum by mass rather than by accumulated weight
  is the whole change.
- Timestep is derived, not set: the stability limit of the stiffest spring holding the
  lightest mass. ~6e-7 s here, 828k steps for half a second in 13 s.
- A `RigidMesh`'s first particle keeps the projective update - it carries the body's fitted
  frame, and its `Move` is the fit's correction rather than a force on anything.
- Mass is distributed over each body's particles, so gravity acts where the inertia is.
  Verified: 81415 N against 8302 kg modelled.
- **The imperfection is a modelling assumption, not a numerical trick.** Symmetric gravity
  does not excite an antisymmetric mode, so from perfect geometry both bridges moved
  identically to the micron. Applied as a velocity `v = sqrt(2*g*delta)`, `delta = span/1000`:
  displacing particles instead would store the flaw as strain in 3.6e8 N/m springs, ~26 kJ
  against the 81 J gravity does over the same distance.
- `KangarooSolver.dll` as built against does **not** expose `PhysicalSystem.Particles`, so
  particle assignment is replicated in `StabilityDynamics.AssignParticles`.

### Cost, and the two solvers inside one mode

A full dynamic evaluation runs in **~10 s**, down from 167 s at its worst. Three things got
it there, and only the first is about speed:

1. **The verdict run stops when the structure stops.** Settling is tested on speed, not
   displacement, so it cannot be passed at the top of a swing. 110k steps rather than 767k.
2. **The stiffness runs use kinetic damping, not real time.** A secant stiffness wants an
   equilibrium position and nothing else. At the structure's real 2% it rings for tens of
   periods; viscous damping sized on each particle's *local* stiffness over-damps the slow
   global mode and was four times slower still, without converging. Zeroing all velocities
   whenever kinetic energy turns over is standard dynamic relaxation and settles it fast.
   **This is a static solver, and the verdict never uses it** - which is precisely the
   distinction Kangaroo's `Step` blurs.
3. **The probe load is 5% of weight, not the codes' 0.5%.** At 0.5% the stiff direction
   moved 0.2 micron, under the settling residual, and its reported stiffness tracked the
   residual rather than the structure: 9.7e8, 1.9e9, 2.4e9 as the run was lengthened.
   Linearity is checked rather than assumed - quadrupling the probe to 20% moves the stiff
   direction by 0.2%, while the soft direction drops ~7%, which is the geometric softening
   an infinitesimal mechanism should show.

The client timeout is back to 120 s. Raising it to 900 s was treating the symptom.
