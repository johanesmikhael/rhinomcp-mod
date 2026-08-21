# Logbook: lessons from building the stability evaluator

Written after the sessions that produced the three modes, the regression suite, the dynamic
solver and the rigid-body integrator. These are the things that cost real time and that a
later reader - or a later me - would otherwise learn again the same way.

## On finding defects

**Every defect was found by two independent methods disagreeing. Not once did the solver
notice.** Welded mode's own analytic `support_margin` said +50 mm while its solver rotated
the block 51 degrees. The pinned mode's clustering said 23 nodes where the geometry has 17.
A rank test said four mechanisms where the integrator said none. The solver reported all of
these with equal confidence. This is the entire argument for the regression suite and for
building cross-checks *in* rather than running them by hand when something feels wrong.

**Ground truth derived by inference is still inference.** "The rank test finds 4 mechanisms"
became "the bridge is unstable" in my head, and went into the suite as truth. It was wrong: a
rank test counts modes, it does not predict collapse, and these were infinitesimal
mechanisms that stiffen at second order. Three independent checks were needed to overturn an
answer the suite was asserting. Label what was measured, not what it seemed to imply.

**A test can pass for the wrong reason.** The free-fall case reported "unstable" correctly
while the motion was wrong by a factor of 400 - with nothing supporting it, even 2.82 mm of
travel cleared the threshold. Asserting the verdict would have hidden the defect that
motivated the whole rigid-body rewrite. Assert the physical quantity, not the conclusion.

**Check the premise before believing the result.** Twice a case was mislabelled, not
miscomputed: the "one-pad bridge that must fall" is a cantilever built into its remaining pad
(face contact, not a pin), and the "unsupported" truss was silently anchored because
`floor_z` auto-places the floor at the assembly's own underside. Both times the solver was
right and the expectation was wrong.

## On arbitrary constants

**They migrate.** Contact stiffness was an arbitrary constant deciding the verdict; sizing it
from the load moved the arbitrariness into the iteration budget; making the run settle moved
it into the timestep. Each fix was real and each revealed the next. Expect the sequence rather
than expecting to be finished.

**A constant tuned between two failure modes is hiding both.** `floor_strength = 1e5` was
picked because a stiff floor made real failures develop too slowly to see and a soft one
tipped sound structures. It was a compromise between two wrong answers, and it read *every*
case in the sweep as unstable. Sizing it as `K = W / settlement` removed the choice.

**Statistical thresholds move when unrelated things change.** The node-merge radius was a
knee found in each body's own contact points, so adding five diagonals to the bottom plane
split seven nodes those diagonals never touch, three of them at the ridge. The physical
scales were already known - contact spreads over a member's thickness, joints are a member
length apart - and did not need discovering.

## On relaxation versus dynamics

**Kangaroo's `PhysicalSystem` is an equilibrium finder, not a simulator**, and its own `Step`
says so: `Position += Velocity` with no timestep and no mass - `Particle.Mass` is never read -
then kinetic damping. Structures creep toward equilibrium instead of falling, so "unstable"
has to be inferred from how far something crept inside a budget.

**But relaxation is the right tool for a static answer.** The dynamic mode uses both
deliberately: real-time integration for the verdict, dynamic relaxation with kinetic damping
for the stiffness measurement. Using one for the other's question is what caused the original
problem, in both directions - real time took tens of periods to ring out a static answer.

**`Weighting * Move` is a force.** Kangaroo's `Unary` carries an applied force as `Move`
against a weight of exactly one. Dividing the accumulated sum by mass rather than by
accumulated weight is the whole difference between Newton's second law and a relaxation step.

## On modelling

**Symmetric loads do not excite antisymmetric modes.** Integrated from perfect geometry the
braced and unbraced bridges moved identically to the micron. Real structures are not built
perfect and the codes say by how much - so the imperfection is a modelling assumption, not a
numerical trick to break symmetry.

**An imperfection must be stress-free.** Displacing particles stores the flaw as strain in
springs of 3.6e8 N/m - about 26 kJ against the 81 J gravity does over the same distance - and
the structure rings from an energy 300 times the load it is meant to carry. Applied as a
velocity, `v = sqrt(2*g*delta)`, it costs nothing.

**Damping belongs where dissipation happens.** Applied to a body's absolute velocity it is
air drag: it resists free fall and imposes a terminal velocity of `mg/c`, which quietly
reintroduced the exact defect the rigid-body integrator had just fixed. Real structural
damping is internal and opposes *relative* motion.

**Some freedoms nothing can damp.** A member pinned at two points spins freely about the axis
through them, and joint damping cannot touch it: a body spinning about that axis has zero
velocity at the very points where the damping acts. It needed pin friction, sized against the
body's own rotational scale.

## On numerics

**Marginal stability looks exactly like missing damping.** The rigid-body response held its
amplitude after 896k steps and damping saturated - zeta of 0.9, essentially critical, decayed
it only to 0.60 over half a second, which is not how a near-critically damped structure
behaves. The step was returning as much energy as damping removed. **Halving the timestep
tells the two apart in one run**; do that before touching the damping model.

**A measurement below the solver's own residual is noise.** At the codes' 0.5% notional load
the stiff direction moved 0.2 micron and its reported stiffness tracked the residual
shrinking rather than the structure - 9.7e8, 1.9e9, 2.4e9 as the run lengthened. Check that
the signal clears the floor, and check linearity rather than assuming it.

**Do not compare samples measured over different intervals.** The final sample of a run spans
a shorter interval than the rest, so its increment is smaller purely from being measured over
less time. Three unrelated models "converged" at a ratio of 0.31 the instant their budget ran
out - two of them falling.

**Terminal velocity mimics convergence.** A structure falling against viscous damping has
constant increments - a ratio of one, which noise pushes under any limit set just below one.
Decay has to be measured across a window, where constant increments give exactly one, and the
series must be monotone: a ringing structure shrinking on average projected 41.7 mm of sag
where the truth was nearer 1.

## On the tooling

**`dotnet build` deploys into Rhino unless `-p:DeployToRhino=false`.** `--output` looks like
isolation and is not; the PostBuild copy still overwrites the `.rhp` Rhino has open, and the
next MCP call fails with "Bad IL range." Ignoring this cost several restarts.

**`RigidMesh.PIndex[0]` is the body particle**, not the first listed point. An off-by-one
there had the motion metric reading 2.807 m while the real per-body pin displacement was
0.026 m.

**A client timeout leaves Rhino's handler wedged** - process alive, socket accepting, never
responding. It needs a restart. Worth fixing before running long evaluations routinely.
