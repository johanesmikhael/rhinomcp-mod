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

## On calibration

**A stiffness nobody checked against arithmetic was wrong by a factor of two, in both
integrators, for different reasons.** Three 2 m columns of 3.611e7 N/m under a 196 kN block
must shorten by W/3k = 1.810 mm. The particle path reported 3.627 - a ratio of 2.003 - because
a member's two ends are held to its frame by two springs in series, and two springs of
strength S deliver S/2. The rigid-body path was diluted the same amount by a different
mechanism: pulling each body toward the *average* of what meets at a joint is a spring of half
the stated stiffness when two bodies meet there. Every sway stiffness the evaluator ever
reported was half of what it claimed.

**Neither disagreement between the two paths would have found it**, because both were wrong by
the same factor. Cross-checking two implementations detects only the errors they do not share.

**A closed form beats a hand calculation of the real model.** The blocking question had been
posed as "hand-calculate the braced bridge's midspan sag" - a 45-member Warren girder by
virtual work, laborious, approximate, and worth one number. Three columns and a block took
minutes to write, run in seconds, and located the defect *and* its cause. Build the smallest
model that has an exact answer, not a small version of the real one.

**A dashpot sized per body breaks Newton's third law.** The rigid-body joints damped each body
against its own mass, so a 5 t block and a 5.4 kg column meeting at one pin got coefficients
twenty-five times apart and the forces at the two ends of that pin were not equal and
opposite. The assembly wound itself up: 17 mm of steady drift where the static answer was
0.45, at a rate that barely changed when the load was quartered. **Switching damping off is the
diagnostic** - the same model then oscillated cleanly about 0.49 mm, which said the spring was
right and the dashpot was not. A coefficient that is a property of the *joint* sums to zero
over the bodies meeting there and can only remove energy.

**A damping ratio is a fraction of critical for some particular mode, and the mode has to be
named.** Sized against the lightest body at a joint, a nominal 2% came out at 0.08% for the
assembly's own mode - nine seconds of settling for a run lasting half of one. Sized against
the heaviest it lands where it was meant to. The same mistake in the other direction
over-damped the particle path, where 30% left the structure at a quarter of its static
deflection after half a second.

**`max_pin_displacement` is a peak, not a settled value.** A suddenly applied load overshoots
to twice its static deflection, and that is correct physics, not error - so a well-damped
integrator and an over-damped one report different things about the same structure. Assert a
calibration on a settled figure; the peak belongs to the verdict.

**An axis-aligned box is not a member.** `MemberAxialStiffness` took the longest edge of the
world AABB as the member's length, which is exact for a member lying along an axis and wrong
for every other one: a 2 m member tilted into the x-z diagonal has a 1.41 m box, and since
k goes as 1/L^2 it reported 6.25e8 N/m where the truth was 3.61e8. Same member, same mass,
1.7 times stiffer for being rotated. The greatest distance between any two vertices is
orientation-independent by construction and is L to a fraction of a percent for anything
slender enough to call a member.

**Three bugs in one day shared a shape**: a quantity correct in the one orientation or
configuration it was first tested in, and silently wrong elsewhere - the end springs, the
per-body dashpot, the member length. None was caught by an existing case. Every one was caught
the first time a case varied the thing being assumed. That is an argument about test coverage
rather than about any of them.

**The graph measures contact properly and then averages it away.** `TryGetBearingCentroid`
samples the real region where two meshes come within the contact gap, at a spacing taken from
the smaller element, and returns the centroid. The extent is computed and discarded, and
`TryBuildContactPatch` downstream tries to rebuild it from bounding boxes. Before reaching for
a better box, check whether the geometry was already measured somewhere upstream.

**A stiffness ceiling looked free and was not.** Cost is set by the stiffest body - the step
is 2/omega against it - and on the splayed-leg case a 5000 kg block sat at 6.55e10 N/m over
legs at 3.61e7, eighteen hundred times softer, contributing about a twentieth of a percent of
the deflection and all of the timestep. Holding every body within 100x of the softest should
therefore have been invisible and bought the square root of what was clipped. The closed-form
cases said otherwise at once: the splayed legs went to 3.45 mm against an exact 0.603, and the
two-storey stack 36% soft, while the rigid path was unaffected. Reverted, cause not yet found.
**The arithmetic for why it should be safe was sound and the answer still moved**, which is the
whole argument for having a case with an exact answer rather than a plausible one.

**A damper on absolute rotation cannot coexist with a verdict about toppling.** Pin friction
was applied to a body's whole angular velocity and sized as a fraction of critical for the
*joint* mode, where omega is tens of thousands. Against the overturning mode, where it is
about three, that is four orders of magnitude too much: a 192 kg cap overhanging its pedestal
by 250 mm carries 570 N m of overturning moment against 3.5e4 N m s of drag, a terminal
0.016 rad/s, about 2 mm in half a second - so it read as standing. It is the logbook's own
linear-damping defect in rotation, and it was invisible for as long as no joint could open,
because nothing in the suite ever asked this solver to let something fall over. **The defect
and the feature that exposes it arrived in the same session, which is the argument for adding
the case before trusting the answer.**

**And it cannot be rescued by acting on relative motion instead.** That is the cure for a
linear dashpot, and it fails here: a body rotating about a single pin has zero velocity at the
pin, which is exactly why the absolute version was reached for. The freedom is real - a member
held at points on one line spins about that line and no joint can see it - so the friction is
kept, but only about that line, and only where the attachments actually are collinear. A body
held at three points off a line has no such freedom and must be free to topple.

**Removing a fictitious damper is a re-measurement of the real one.** The rigid path's damping
ratio was 20% while the spin term was quietly doing most of the settling. With it gone the
one-storey stack drifted to 0.552 mm against an exact 0.453 and the splayed one to 11 against
0.603 - not divergence, because halving the timestep barely moved it, but a mode nothing was
damping. At 100% both land inside their bands and the splayed case sits closer to the closed
form than it ever had. A constant calibrated against a model containing a defect measures the
defect too.

**A dry bearing rings, and a verdict has to have somewhere to put that.** The solver could
conclude three ways - it settled, it converged, it crossed the threshold - and all three
assume the motion dies or runs away. A contact joint dissipates only while it is closed, so a
block rocking on one still rings after half a second and the run ended undecided, which was
read as not stable for a stack with a +150 mm margin whose motion never reached two thirds of
the limit. What separates it from a mechanism is direction, not distance: a mechanism creeps
one way, a rocking block reverses. Reversals plus a peak that is not growing, and no new tuned
number.

**A best fit through a region assumes the region is one surface.** The bearing plane was
fitted to the sampled contact points, which is right whenever two faces lie on each other and
meaningless the moment they do not: a square-cut diagonal landing on a flat pad is sampled on
its own inclined end *and* on the pad's top, and the fitted plane splits the difference at 45
degrees - a direction neither surface points in. As a contact joint that sheds the vertical
load those members carry and pushes them sideways, so a braced bridge walked off its supports,
112 mm against a 61 mm limit, while the same bridge welded or pinned stood at half a
millimetre. **The controls are what located it**: all-welded and all-pin both stood, so it was
not the pinned truss; turning the imperfection off and raising the damping changed nothing, so
it was neither a jolt artefact nor ringing. That left the bearings, and the normals said so
outright.

**The fix is to take the direction from the surfaces, and to refuse when there is none.** A
normal from the dominant face pair rather than from a point cloud, and no extent at all when
the two faces are more than 20 degrees from parallel, because a member touching along one edge
has a contact point and no bearing plane. Inventing a direction there is worse than admitting
none: a contact with no normal falls back to welded, which is wrong in a way that shows up in
`contact_joints_sided` rather than wrong in a way that moves a verdict.

**Ranking the faces by sample count picked the wrong pair.** A 150 mm column standing on a pad
is sampled far more often down its four sides - each contributing a row along the bottom
edge - than across the small square actually bearing, so its "dominant" face came out
horizontal-pointing and the column lost the extent it had always had. What identifies a
bearing is that *two* surfaces lie on each other, so the pair is what has to be searched, not
each body's busiest face independently. The geometry tier caught this on the first run after
the change.

**An assertion that a measurement is right cannot fail on a measurement that was never made.**
The extent case had checked the bearing normal was vertical since it was written, and the 45
degree fit sailed past it for weeks - because that case only ever drew elements sitting square
on a pad, and the check runs per measured rectangle. It needed the opposite assertion too: a
diagonal that must report *no* bearing. Every case in the tier said "this is right"; none said
"this must not be there".

**A cached measurement is not a measurement.** Three joints in a model reported zero samples,
which said the sampler had never run - so two hypotheses were built on that and both were
wrong, and a correct fix was written, tested against the same cache, judged a failure and
reverted. The graph was being served from an in-memory cache that does not store sample
counts, so every reading was a zero that meant "not recorded" rather than "not found". Forcing
a recompute showed 115 and 134 samples on those joints and named the real cause in one line.
**Before diagnosing from an instrument, check the instrument is connected**: the tell was that
deleting the document's stored graph changed nothing, which should have been read as "this
number is not coming from where I think" rather than as evidence about geometry.

**A test that cannot fail for the reason you are testing proves nothing.** The one-sided
fallback was correct when it was first written. It was reverted because it appeared to change
nothing - and it appeared to change nothing because the cached result it was compared against
could not change. Two hours and a revert for a fix that worked.

**A pin is not a weaker weld.** The three joint types describe how the *measured bearing* is
used, and a pin is the one that throws it away: it collapses the joint to its centre, which is
the freedom it exists to grant. That is fine for a bolted shoe and wrong for anything acting
along a line. Two CLT panels screwed edge to edge over 4000 mm, modelled as a pin, hinge about
the single point they are left with and drop a panel the weld would have carried - 45 mm
against a 41 mm limit, where the same model welded stands at 5. Weaker in the ordering does
not mean weaker in every direction, and the ordering is about what a joint *carries*, not
about how much of the geometry survives.

**Where the real detail sits between two types, take the weaker.** A verdict here is a lower
bound or it is nothing: overstating a connection makes a structure look stiffer and more
redundant than it is, and the failure that hides is the one nobody sees coming, while
understating costs a sound structure being called marginal. Applies where there is judgement -
a screwed spline is neither of the types available - and not where there is knowledge: a pad
cast into its column really is a moment connection, and refusing to say so is not caution but
a worse model. **Run both ends when it matters**; the distance between them is what the detail
is worth, and a single run at the optimistic end reports a sound deck and shows nothing.

**A defect can be held up by something nobody modelled deliberately.** A CLT panel left short
of its bearing came out stable, because the panels beside it were touching along their edges,
that pair had no rule, and the default welded them into a deck that carried it. The joint that
decided the verdict was the one nobody had mentioned. It became a case rather than a
workaround - splined deck catches the panel, loose deck drops it - but the lesson is that an
unstated joint is not an absent joint, and the default is doing work whether or not anyone
thought about it.

**An unstable verdict's displacement is not a margin.** The run stops the moment motion
crosses the mechanism limit, so a failing case always reports a figure barely above it -
41.5 mm against 40.9 - however hard it is failing. Reading that as a 1.5% margin is reading
the stopping rule, not the structure. What separates decisive from marginal is how long it
took to get there: 36k steps against the sound model's full 167k.

**The type ordering is about what a joint carries, not about how much it restrains.**
Contact is weakest in the ordering and *stiffer in rotation than pin*, because a pin discards
the measured bearing while a contact keeps it: a 700 mm pad under a 400 mm column resists
rotation with k d^2 for as long as it is in compression. So a frame with nothing but dry
bearings stands under its own weight and sways no more than the same frame cast in - 1.44e8
against 1.45e8 N/m - while the same frame with *pinned* bases is a genuine mechanism at 44 mm.
Weakest-governs therefore does not mean softest-governs, and "fails safe" holds for tension
and not for every direction at once. Worth knowing before trusting the ordering to be
conservative.

**A wrong verdict can pass for a season because a wrong reason agreed with it.** The
dry-stacked frame was asserted unstable, with a hand-written reason calling it a four-hinge
mechanism, and it passed - until the imperfection was turned off and it stood without moving
at all. There were never any hinges: every joint was a bearing, and a bearing is not a hinge.
The case had been green because the kick made it fail and the reason made that sound
inevitable. **A case whose reason cites a mechanism should name the hinges.**

**An imperfection applied as a velocity is a nudge to a truss and a shove to a stack.** It is
applied that way deliberately - displacing particles stores the flaw as strain and the
structure rings from an energy hundreds of times the load - but a span/1000 offset comes out
as 0.43 m/s, and what that does depends entirely on what the joints are. A truss whose joints
hold in tension rings and settles. A dry-stacked pavilion cannot: friction has no way to put
back what slides, so every body keeps the ground it loses. The pinwheel read unstable at
50 mm of accumulated sliding; with the imperfection off, nothing moved at all - 0.02 mm across
the whole model. The verdict was measuring the kick. **Ask a bearing structure about a load,
not about an impulse**: the notional lateral fraction is a load, and it separated the
compositions cleanly where the imperfection had buried them.

**Overlapping solids are not a joint.** Four pavilion walls written as centre lines and a
thickness shared 200 x 200 mm of volume at every corner where one met another, and the graph
duly found contacts there. The pinwheel then read unstable at 155 mm while the parallel
arrangement stood, which is the opposite of the physics - a composition that faces two ways
cannot be worse than one facing one way. Stating each wall as its plan rectangle makes
abutting explicit and the error impossible to write. **A model that fails in a direction the
physics forbids is a modelling error until proven otherwise**, and it is worth checking the
geometry before the solver.

## On numerics

**A test model's cost is set by its heaviest, stubbiest body, not by the physics under test.**
Joint stiffness is `(E/rho) m / L^2`, the ground anchor is ten times the stiffest of them, and
the explicit step is `2/omega` against that - so a 20 t loading block pinned the timestep at a
value the 5.4 kg columns being measured never needed. Quartering the load quartered the cost
and left the closed-form answer just as resolvable. Size the load down until the residual, not
the patience, is the limit.

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
