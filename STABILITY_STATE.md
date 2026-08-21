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
7. ~~The relaxed pinned mode reports a false positive on the unbraced bridge.~~ **Fixed by
   deletion.** `pinned` is now an alias for the dynamic solver and the 407-line relaxed path
   is gone; the regression case passes without its assertion moving. See
   `SIMPLIFICATION_PLAN.md`.
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

### It does not scale, and the ceiling is ~66 elements

Measured on N-panel versions of the test bridge, all 2 m members:

| panels | span | elements | steps | wall | settled |
| --- | --- | --- | --- | --- | --- |
| 2 | 4 m | 18 | 51k | 3.2 s | yes |
| 3 | 6 m | 26 | 51k | 7.5 s | yes |
| 5 | 10 m | 42 | 110k | 9.6 s | yes |
| 8 | 16 m | 66 | 554k | 60 s | yes, at 0.36 s |
| 12 | 24 m | 98 | 767k | 97 s | **no** |
| 18 | 36 m | 146 | - | >120 s | - |

Cost grows as about **N^2.1**: per-step work is linear in elements, but the step count also
grows, because settling time is set by the structure's lowest natural frequency (`f1 ~ 1/L^2`)
while `dt` stays pinned by the stiffest member. Local and global timescales diverge - the
classic stiff-ODE problem.

**A run that has not settled no longer claims stability.** At 24 m the assembly was still
moving when time ran out, 5.2 mm and growing, and it reported `stable`. That means only "it
had not fallen yet" - the same budget-dependence this mode exists to remove. `stable` now
requires settled and below the limit; otherwise the verdict is `inconclusive` and the
stiffness probe is skipped, since a secant stiffness about a structure that never reached
equilibrium is meaningless. The 24 m case does not settle even at a 2 s cap.

**Convergence replaced waiting**, and lifted the ceiling to ~146 elements. A damped response
does not have to be simulated to be bounded: its increments shrink geometrically, so once a
window of samples agrees on a ratio the limit is `d + delta*r/(1-r)`. If that is under the
threshold the structure cannot reach it however long the run continues. Validated where it
fires - 98 elements went 97 s to 12 s, and an 8-panel bridge projected 1.61 mm against a
known settled 1.595 mm.

Three false convergences had to be killed first, each of which would have reported a moving
structure as stable:

- **Kinetic-energy turnovers** fire at the stiffest *local* mode - one every two steps -
  and say nothing about where the structure is going. Converged in 1385 steps, predicted
  0.29 mm where the truth was 0.60. Convergence is now read from the measured displacement.
- **Terminal velocity looks like convergence** between neighbouring samples. A falling
  structure damped by viscosity has *constant* increments, a ratio of one that noise pushes
  under any limit set just below one. Decay is now measured across a 7-sample window, where
  constant increments give a ratio of exactly one.
- **The final sample spans a shorter interval** than the rest, so its increment is smaller
  purely from being measured over less time. Three unrelated models "converged" at a ratio
  of 0.31 the moment their budget ran out. Only uniform intervals now feed the test.

Remaining ways to go faster: raise `TimestepSafety` from 0.1 (about 2.5x); selective mass
scaling, standard in explicit FE; implicit integration, the real fix and the largest job.

## The dynamic mode does not simulate free-body motion

**Two members hanging in mid-air with nothing holding them up move 2.82 mm in half a second.
Free fall is 1226 mm.** Committed as a failing regression case, `free_fall_two_members`,
which asserts the *distance* rather than the verdict - the verdict comes out "unstable"
regardless, because even 2.82 mm clears the threshold such a small assembly gets, and
passing on that would hide the defect.

The cause is structural. A body's frame particle carries its best-fit frame and is updated
projectively rather than integrated, while the particles carrying its mass are held to that
frame by a penalty of 3.6e8 N/m. They can depart from it by only `mg/k`, about 1.5 micron,
and the frame then follows at a quarter of that per step, so a free body falls at the
solver's update rate rather than at `g`.

What this does and does not invalidate:

- **Deformation dynamics is sound** and separately validated - sag against hand statics,
  sway stiffness convergent and linear, projections matching settled values to 0.1%.
- **Gross rigid-body motion is not modelled.** An element toppling off its support or a
  fragment dropping away does not accelerate. An unstable verdict can currently be reached
  only by deformation crossing the limit.
- The claim that "a mechanism is caught because it accelerates under gravity" is therefore
  **not true of this implementation**, whatever its merits as physics.

The fix is to give each body's frame its mass and inertia and integrate it, with the pins
supplying constraint forces - rigid-body dynamics in place of a fitted frame.

### The rigid-body integrator: built, verified for falling, not yet calibrated

`integrator: "rigid_bodies"` (`StabilityRigidBodies.cs`) makes the body the primitive: mass,
centre of mass, inertia from its own mesh, position, orientation, linear and angular
velocity, obeying `F = ma` and Euler's equations. Pins are springs pulling every body meeting
there toward their common point, applied at the attachment so they deliver moment as well as
force. `RelaxationCompensation` has no place in it - a spring of stiffness k delivers k times
its extension, with no quarter-correction to cancel.

**It falls correctly.** Two members dropped in mid-air track `0.5*g*t^2` to one part in ten
thousand, against 0.2% of it for the particle integrator. Regression case
`free_fall_two_members` asserts the *time* to cross the limit, 0.045 s, which measures the
acceleration directly; `free_fall_two_members_particles` is the same drop on the default
integrator, committed failing.

Two bugs were found and fixed on the way, both of which would have quietly reintroduced the
defect being fixed:

- **Damping belongs to the joint, not the body.** Applied to a body's absolute velocity it
  is air drag: it resists free fall and imposes a terminal velocity of `mg/c`. The assembly
  descended at 0.095 m/s, covering 10.5 mm where gravity asks for 76.6. Real structural
  damping is internal, so it now opposes each attachment's velocity relative to its joint -
  a body falling freely has nothing moving relative to anything and loses nothing.
- **Convergence must be monotone.** Without that test a ringing structure - increments
  alternating in sign while shrinking on average - projected 41.7 mm of sag where the truth
  was nearer 1.

**Diagnosis: it was never a stiffness problem.** Left to run, the rigid-body response
*oscillates* between 0.02 and 0.39 mm and does not decay - the same amplitude after 3 s and
896k steps as after 0.5 s. The peak is the same order as the particle model's 0.62 mm, so
the joint stiffness was roughly right all along; the "1000x soft" sway was the static run
being measured mid-oscillation. One problem, not two.

Two fixes since, both real:

- **Kinetic energy must include rotation.** These members are pinned at both ends and much
  of their energy is angular, so an energy built from linear velocity alone turns over at
  the wrong moments - and kinetic damping acts on exactly those turnovers.
- **A pin needs friction.** A member pinned at two points can spin about the axis through
  them, a freedom with no stiffness that the pinned idealisation grants deliberately, and
  joint damping cannot reach it: a body spinning about that axis has *zero velocity at the
  very points where the damping acts*. Once started it never stops. Pin friction, sized
  against the body's own rotational scale and acting only on rotation, brought the unbraced
  sag from 22.1 mm to 4.24 mm and the braced from 0.739 to 0.656 against a particle
  reference of 0.623 - within 5%.

**It settles now, and the cause was the step, not the damping.** Halving told the two
apart: at a tenth of the stability limit the assembly never settled however long it ran; at
a fortieth it settles the same model in 168k steps. The step was returning about as much
energy as the damping removed, which is why damping saturated - zeta of 0.9, essentially
critical, decayed the response only to 0.60 over half a second. `TimestepSafety` on this
path is 0.025, and it is opened as `timestep_safety` so the trade can be re-measured rather
than trusted.

Where it stands against the validated particle answers:

| | rigid-body | particle | difference |
| --- | --- | --- | --- |
| braced sag | 0.656 mm | 0.623 mm | +5% |
| braced sway `Ky` | 6.11e8 | 6.94e8 | -12% |
| braced sway `Kx` | 8.91e8 | 2.32e9 | -62% |
| **unbraced sag** | **3.869 mm** | 0.595 mm | **6.5x** |

Braced is close; unbraced is not, and **it is not established which of the two is right**.
The unbraced bridge is the one carrying the infinitesimal mechanisms, whose stiffness comes
entirely from second-order geometry - and a rigid-body model updates that geometry honestly
where the particle model holds each body to a fitted frame that may be stiffening the very
mode in question. The larger deflection may be the more correct one. Settling that needs an
independent check, not a preference.

**It is not the default, because its joints are not calibrated.** Where the particle model
shares a particle outright - an exact pin - this one uses springs, and the assembly comes out
far softer than deflections already checked against hand statics: sway stiffness around
3.8e5 N/m against the particle model's 4.7e8, and the kinetic-damping static runs do not
settle for these bodies. Making it default would trade a defect that is understood and
documented for numbers that are not. Each end spring is already at `2k` so the pair delivers
`EA/L` along a member; that was necessary and is not sufficient. The remaining work is the
joint model and the static settling, not the integrator.
