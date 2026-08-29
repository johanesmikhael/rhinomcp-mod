# Stability evaluator — state of the work

Branch `JointTypes`. **The section below supersedes the rest of this file where they
disagree** - everything after it was written on 2026-08-21 against branch
`MultiBodyStability` and parts of it are now wrong rather than merely old. Written to
survive a context reset: what the modes do, what is trustworthy, what is not, and the
cases whose answers are known independently of the solver.

---

# Since 2026-08-24

Suite: **fast 17/17, systems 11/11, geometry 4/4, micro 7/8** - micro's one red is
`free_fall_two_members_particles`, committed failing on purpose.

## Bearings are measured, not sampled

The solver builds joints over the polygon two flat faces actually share, on the mean plane
between them. `PlanarBearing.cs`. One condition, `-burial <= d <= gap`, covers all three
states two solids can be drawn in - nearly touching, touching, overlapping - and Brep, mesh
and mixed pairs all reduce to one `PlanarRegion` intermediate.

Three kinds of contact, from one rule about the two faces:

| faces | kind | what the solver gets |
| --- | --- | --- |
| near parallel | `planar` | the shared polygon on the mean plane |
| crossing, no overlap | `line` | the line they cross along - **a hinge about itself**, since a zero half-width collapses that axis to one bearing point |
| crossing and overlapping | `buried` | the surface inside the shared volume |

`bearing_source` = `sampled` | `exact` (default) | `buried`. Sampling remains for curved
faces, which have no flat region to intersect. **Buried is opt-in**: its area grows with how
far the drawing goes through itself, and it takes the splayed-leg case from 0.661 mm to 1.097
against a closed form of 0.603.

Never bisect two face normals for a skew contact - 12.5 degrees of error at 25 degrees of
skew, and historically it walked a truss 112 mm off its supports. The normal comes from the
face the line runs *inside*.

## A joint nobody named is a `contact`

Was `welded`, which is the strongest assumption available applied where the least is known -
it reports toppling structures as standing. `pin` is not the safe end either: it hangs, and it
discards the bearing, so a stack becomes a mechanism hinged at points that exist nowhere in
the drawing. Contact is the only one of the three that describes any two things merely found
touching.

## Joints have a capacity

`assign_joint_type(joint_type="pin", capacity_kn=12, layer="Truss")`. Absent means unlimited,
which is what every joint was before. **Tension only**, **per bearing point** - which is what
gives it a moment capacity, by the mechanism contact already uses. It yields rather than
breaking. `joints_with_capacity`, `joints_at_capacity`, and `capacity_n` / `reached_capacity`
per joint.

**Read `peak_point_tension_n`, never the net.** A cantilever's connection sits in net
compression at -7.1 kN while one of its bearing points is pulled at 24.5.

## Joint forces are reported

`joint_forces` on the rigid path: `force_n`, `tension_n` (tension positive, measured *across
the bearing plane* and not along a member), `shear_n`, `bearing_points`,
`peak_point_tension_n`, `capacity_n`, `reached_capacity`. Captured on the last step of the
verdict run - not the sway runs, which reload the structure laterally.

Verified against statics: three columns under an off-centre block read 15420 / 15420 / 18424
where equilibrium demands 15323 / 15323 / 18387.

## Two clustering defects, both fixed

1. **Joint type is resolved per link, before merging**, and links that answer differently are
   never merged. A link is a fact about the model; a node is something the clustering
   invented. Previously a truss support merged four bolted connections with a bearing on a
   pad and made them all contact, and the truss came apart at 0.6 m/s.
2. **Two contacts with the body's own middle between them are on opposite faces**, so two
   joints, whatever the distance. The radius rule assumes slenderness; a plate's smallest
   dimension *is* the gap between its faces. This fixed `axial_two_storeys_rigid_bodies`,
   red since the rigid path existed: 0.785 mm against a closed-form 0.928, now 0.942.

Both can only **split** nodes that were wrongly merged, which is why nothing else moved.

## The verdict no longer depends on how long you watched

Motion is sampled at a fixed cadence in simulated time, not a fixed count of 32 however long
the run. The old behaviour was not even monotonic - one bridge read 3.0 mm inconclusive over
half a second, 10.8 stable over two, 5.1 inconclusive over five, 5.1 stable over ten.

**`inconclusive` reports as not stable**, so a structure soft in the direction its mechanisms
move - and therefore slow - can be judged before it has swung once. Duration is a cap, not a
price: the run stops as soon as it can conclude.

## Open, in the order I would take them

1. **Coverage.** ~35 cases, mostly bridges and stacks plus a pavilion and a hybrid. Every
   defect found on 2026-08-24 came from varying something previously held fixed.
2. **Joint stiffness is fixed at 2k per end** rather than shared along a member's load path,
   so a member's stiffness depends on how many joints it happens to have - a property of the
   mesh, not the member.
3. **Force visualisation.** All the data now exists in `joint_forces`; this is a drawing job,
   not a physics one. Colour by sense, scale by magnitude against the model's own maximum,
   draw through `WriteMultiBodyDisplay` / `MCPStabilityConduit`.
4. **Mass is double-counted in overlaps** when it comes from `assign_mass(density=...)`.
   Roughly 4% for a centreline-drawn truss, and it mostly cancels.

---


## The modes

| mode | bodies | joints | question |
| --- | --- | --- | --- |
| `welded` | whole scope as one rigid body | none | does the assembly tip or slide? |
| `pinned_dynamic` | one per element | whatever the joint rules say they are, integrated in real seconds | is it a mechanism, how far does it move, and how stiff is it? |
| `contact` | one per element | all bearings: `joint_type="contact"` on the rigid path | can an element rotate off, lift, slide? |
| `pinned` | alias for `pinned_dynamic` | - | the relaxed solver is deleted; see `SIMPLIFICATION_PLAN.md` |

**`contact` is a joint type now, not a solver.** It was a separate relaxed solver with its own
contact stiffness, its own pseudo-time step and a `torque_gain` that existed only because
Kangaroo's projective step has no moments. The multi-body integrator answers the same question
from Newton's and Euler's equations - the bearing pushes and does not pull, the moment falls
out of `r x F` - and reproduces every verdict the old one was trusted for, at identical step
counts, so the mode is kept as a name for a model whose joints are all bearings and means
exactly that. `StabilityContactGoal.cs`, `SolveContactFromGraph`, `torque_gain` and
`contact_strength` are gone; two arbitrary constants went with them.

`pinned_dynamic` carries two integrators, and **`rigid_bodies` is now the default**. It falls
correctly, builds each joint over the measured bearing, and is the only path that can express
a joint type at all - the particle path holds a body's points to a fitted frame at one
strength and joins elements by sharing a particle, so every joint there is a point pin and any
joint type is silently discarded.

`integrator: "particles"` remains for the two things it is still better at: it reproduces the
closed-form axial deflection to about 1% where the rigid path is 15% soft on a two-storey
stack (0.785 mm against an exact 0.928, cause still open), and it is an independent second
implementation. That second point is worth less than it sounds - both paths were wrong by
exactly 2x on joint stiffness for months, because they shared the mistake, and it was the
closed-form micro cases rather than their agreement that caught it.

Two sway calibrations name `particles` explicitly rather than following the default, because
that is where they were measured: the braced bridge reads 9.0e8 N/m on the rigid path against
1.13e9 on the particle one, and nothing establishes which is right. The unbraced bridge
reports no sway at all on the rigid path - it does not settle inside the half-second run, and
sway is only measured after settling.

Each integrator carries its own `damping_ratio` default - 2% for particles, 20% for rigid
bodies - because a damping ratio is a fraction of critical for some particular mode and the
two do not damp the same one. Sharing one number is how the rigid path came to be quietly
under-damped.

Welded is an upper bound: it supplies every moment connection the real assembly lacks.

## What is now physically grounded

- **Contact stiffness** is derived from the load each bearing surface carries
  (`P_tributary / delta`), and each body's total goal weight is held to `carried / delta`
  so joint count does not set the clock. Knobs are stated as lengths
  (`joint_penetration`, `ground_settlement`), never as a modulus.
- **Pinned stiffness** is the member's own axial stiffness `k = EA/L`, with the section
  recovered from mass (`A = m/(rho L)`, so `k = (E/rho) m / L^2`).
  - Only the ratio `E/rho` was ever used, so it is now one parameter, `specific_stiffness`,
    defaulting to steel's 2.68e7 m2/s2. Worth seeing rather than hiding: C24 spruce is
    2.62e7, within two percent, which is why a timber member sized for the same load
    deflects about as much as a steel one. A model that never states its material is
    already close for both.
  - `joint_stiffness_n_per_m` states the figure outright instead. It applies to **every**
    member in the scope, which is the thing to know before using it: derived, a pad or slab
    comes out hundreds of times stiffer than the columns on it, and stating one value makes
    them all equally soft. On the three-column micro case that is a 6.4x larger deflection,
    and it is the topology rather than an error.
  - `rigid_strength` no longer reaches the pinned joints. It meant "how rigid is a body" in
    welded mode and "how stiff is a joint" here, two questions sharing one name.
- **Each end spring is `2k`, because two sit in series along a member** (`EndSpringsInSeries`).
  Measured, not assumed: three 2 m columns of 3.611e7 N/m under a 196 kN block must shorten
  `W/3k` = 1.810 mm, and the solver reported 3.627 - a ratio of 2.003. The path from pin to
  pin runs through the body's frame via two springs, and two springs of strength `S` deliver
  `S/2`. **Every stiffness this evaluator reported before 2026-08-22 was half of what it
  claimed.** The rigid-body path needed the same factor for a different reason: pulling each
  body toward the *average* of what meets at a joint is a spring of half the stated stiffness
  when two bodies meet there, now undone by a gain of `n/(n-1)`.
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
5. ~~**Imperial units untested** end to end.~~ **Tested 2026-08-29.**
   `scripts/stability_regression/imperial_crosscheck.py` builds nine regression cases in
   millimetres, scales the document to feet and evaluates again: every verdict and every
   SI-reported number agrees to 1e-3 on the contact, pinned and dynamic paths, and
   `assign_mass(density)` in feet reproduces the stated kilograms. The one verdict it
   flipped was not a unit bug - see item 6. Two residuals remain, neither touching a
   verdict: the bearing graph meshes document-unit geometry with `FastRenderMesh`
   (`MCPConnectivityGraphConduit`), so bearing samples and hence node diameters and the
   tension/shear split at a joint move by up to ~7% between documents while joint force
   magnitudes hold to 0.2%; and the particle integrator merges particles on a 1 um grid,
   so `shared_particles` and its sway stiffness shift ~3% with the scaled coordinates.
   Meshing in metre space for the bearing sampling, as the solver already does, would
   remove the first. Contact detection itself is in multiples of the document's
   absolute tolerance by design, so a document converted with `AdjustModelUnitSystem`
   must have its tolerance rescaled too - the cross-check does.
6. ~~**`floor_strength` is not a subgrade modulus.**~~ **It is now.** It used to be divided
   by the summed tributary areas of the vertices standing on the floor, which included each
   corner's share of the side faces above it - and that share depends on how the mesher
   triangulated those faces, which is not stable across transforms. The same 0.3 x 0.4 m
   footing measured 0.47 m2 in millimetres and 0.54 m2 after a scale to feet, and the welded
   verdict of the +121 mm cantilever flipped with it. A standing vertex now carries only
   the floor faces meeting at it, split evenly per quad, so `ground_bearing_area_m2` is the
   footprint (0.12 m2) and `floor_strength` is W / (settlement x footprint) in Pa/m.
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

## A wall is a wall on the rigid-body path

`integrator: "rigid_bodies"` now builds each joint over the bearing region the graph measured,
rather than at its centre point. Measured on a block carried by three 2 m columns, then with
two of those legs replaced by a 1150 mm wall of the same combined `EA/L`:

| | `Kx` | `Ky` | ratio |
| --- | --- | --- | --- |
| three columns | - | - | reads unstable |
| wall + one column | 7.60e6 | 5.38e5 | **14.1** |

Stiff in the wall's own plane, soft across it, which is why buildings need shear walls in two
directions. The same two models on the default particle path give 2.49e7 / 2.50e7 and
2.27e7 / 2.29e7 - no difference at all, because a joint there is a point and a point has no
lever arm.

The joint points are two-point Gauss positions, `half/sqrt(3)` either side of centre on each
in-plane axis, each carrying its share of the joint's stiffness. That is exact rather than
convenient: four points of `k/4` sum to `k` in translation and to `k L^2 / 12` in moment, which
is the analytic rotational stiffness of a uniformly loaded elastic bearing. Corners would have
given `k L^2 / 4`, three times too stiff. A bearing with no width in one direction - a member
cut square standing at an angle on a flat pad - collapses to two points and restrains rotation
about the line it touches along and not about the other axis.

**The axial answer did not move**, which is the check that the stiffness share is right:
`axial_one_storey_rigid_bodies` still reproduces `W/3k` after the joint was spread over four
points.

### Why only this path

Kangaroo's `RigidMesh` takes one strength for all of a body's points, so the particle solver
cannot give one joint four points at `k/4` and another two at `k/2` - and a line contact and a
face contact genuinely need different counts. `StabilityRigidBodies` carries stiffness per
site and expresses it directly. Bearing extent and the choice of integrator are therefore the
same question, which was not obvious until the design was worked out.

Particles remain the default, so nothing about the ordinary answer has changed.

## Joints are named, not guessed

`joint_type` on `pinned_dynamic` + `integrator: "rigid_bodies"` says what every joint in the
assembly is. Geometry cannot tell you: a screwed panel and a dry-stacked one look identical to
an intersection test. Three types, and the type decides how the *measured bearing* is used
rather than adding behaviour of its own:

| type | joint points | carries |
| --- | --- | --- |
| `contact` | the full extent, one-sided + friction | compression and moment until it opens |
| `pin` | one, at the centre | force, no moment |
| `welded` (default) | the full extent | force and moment, always |

Ordered weakest to strongest, so where two elements disagree the weaker governs - a hinge
assumed where a moment connection exists reads softer and more mechanism-prone than the truth,
which fails safe, and unlike "last rule wins" it does not depend on the order rules were given
in. There is deliberately no `free`: it would be a correction to the graph rather than a
construction type, and under weakest-wins one such rule on an element would silently delete
every joint it has.

`contact` is the honest floor and `welded` the optimistic ceiling, so running both brackets a
verdict. One geometry, three answers, all correct for what was asked:

| stair, +150 mm margin | verdict |
| --- | --- |
| `welded` | stable, settles in 0.031 s |
| `contact` | stable, rocks and stays inside two thirds of the limit |
| `pin` | **unstable** - three blocks on three points is a mechanism at any margin |

**The physics is the relaxed contact solver's, moved.** Each bearing point pushes and never
pulls: the joint spring is split along the measured bearing normal, the tensile half dropped
when the faces separate, and the tangential half capped at `mu = 0.6` times what is actually
being pressed. The moment then follows for free - points drop out one at a time as the load
leaves them, so an element sheds its far edge and overturns on the near one at the rate
`r x F` dictates. `torque_gain` is deliberately not ported: it existed because Kangaroo's
projective step has no moments, so the fraction of eccentric compression that became rotation
had to be dialled in by hand.

Verified against the cases the relaxed solver has been answering for weeks, which is the gate
on folding the two together: `stair3_step100` stable, `stair3_step300` unstable,
`pedestal_eccentric` unstable, all now reproduced by joint type on the rigid path.

Where a joint has no measured bearing region - found by proximity rather than by two faces
meeting - `contact` has no direction to open along and falls back to welded. The result says
so: `contact_joints_sided` against `joint_count`.

### Stating it: `assign_joint_type`

An engineer states a rule, not four hundred joints, and the unit they state it in is a **pair
of element classes**: "beam to column is welded", "truss to truss is pinned". So that is the
unit the tool takes.

```
assign_joint_type(joint_type="welded", layer="Beams", with_layer="Columns")
assign_joint_type(joint_type="pin",    layer="Truss")        # all its joints
assign_joint_type(joint_type="contact", ids=[...])           # these elements
assign_joint_type(joint_type="pin", ids=[beam], with_ids=[column])   # this joint
assign_joint_type(clear=True, layer="Beams", with_layer="Columns")
```

Either side of a pair rule can be named by layer or by object, the same choice Rhino gives
everywhere else, and the tighter rule wins: **two objects**, then **an object and a layer**,
then **two layers**, then an **element rule**, then `evaluate_stability`'s own `joint_type` as
the **default**. That ordering is what makes "this beam meets that column as a pin" statable
at all - without it the tighter case would have to move every joint between the two classes
with it. Specificity, not order: a rule table is not a script.

Where two element rules meet and no pair rule covers them the weaker governs, for the same
reason the type ordering exists at all.

Names are resolved to object ids as the rule is written, since a name can be changed or
duplicated later while the rule has to keep meaning the element it was written for. Tokens are
stored prefixed - `layer:Beams`, `id:<guid>` - so a layer named after a GUID cannot be mistaken
for the object of that name.

A rule naming an object outlives that object - it is in the document text and the object is
not - so deleting the beam leaves a rule that matches nothing and says nothing. Two of them
accumulated here inside one afternoon of rebuilding test scenes. Every call reports them with
a `stale` field saying which side is missing, `assign_joint_type()` with no arguments lists
them and counts them, and `assign_joint_type(prune=True)` removes them and returns what it
removed. Not removed on sight: a deleted object can be undone, and a rule dropped in between
would not come back with it.

Element rules go on the object beside its mass, in the same user string, so they travel with a
copy. Pair rules go in document user text, because there is nowhere on a beam to record what
it does when it meets a column. Each is order-insensitive: naming Beams/Columns and
Columns/Beams writes and matches the same rule, and the rule reports itself the same way round
whichever order the graph listed the joint's two bodies in - that order is arbitrary and a
label built from it would flip between runs of the same model.

### Seeing it: the graph overlay

`graph_display(enabled=True, contact_extent=True)` turns on the connectivity overlay and
colours every joint by the type it resolves to - **welded amber, pin blue, contact green** -
using the same rule table the evaluator reads, through the same code, so what is drawn and
what is solved cannot drift apart. A joint drawn **dim** took the default because no rule names
it; a bright one was named. Both solve identically, and that is exactly why the difference has
to be visible: it is the only way to answer "did my rule reach this joint".

It exists as an MCP tool because the `mcpmodgraph` command asks for its scope at the command
line, so driving it over the socket blocks forever and needs a Rhino restart. An agent could
build a model, resolve its joints, and never see the picture that would show whether the
joints it resolved were the ones it meant. `capture_view` renders the overlay, so the check is
available without a human at the screen.

Every node in the result reports its `joint_type` and the `joint_type_rule` that decided it
(`pair:A|B`, `element:both`, `element:one`, `default`). A verdict that changed because a rule
matched more joints than intended has to be diagnosable without re-deriving the rules by hand.

### Lower bound, or nothing

Where the real detail sits between two of the three types, state the weaker one. Overstating a
connection makes a structure look stiffer and more redundant than it is, and the failure that
hides is the one nobody sees coming; understating costs a sound structure being called
marginal, which is the affordable error.

That applies to judgement, not to knowledge. A pad cast into its column really is a moment
connection and saying so is a better model, not a bolder one. A screwed spline between CLT
panels is the other case: it transfers shear along the joint and little moment, `welded` keeps
the whole 4000 mm line and adds full moment continuity, `pin` keeps no line at all - so the
spline is between them and neither is it, and the weaker is what gets stated.

**Run both ends when the detail matters.** The `systems` tier carries the same defective panel
at both, and they disagree: the lower bound drops it, the upper bound carries it. That
disagreement is the finding - it says the verdict rests entirely on a line of screws being as
good as continuous timber. A single run at the optimistic end reports a sound deck and shows
nothing.

**A pin is not a weaker weld.** It is the type that throws the measured bearing away, which is
the freedom it exists to grant. Modelled as a pin, two panels screwed over 4000 mm hinge about
the single point they are left with. Weaker in the ordering does not mean weaker in every
direction - and the sharpest form of that is `contact`, the weakest type, being *stiffer in
rotation* than `pin`, because it keeps the bearing. A frame on dry bearings stands and sways
no more than the same frame cast in; the same frame on pinned bases is a mechanism. The
ordering is about what a joint carries, not about how much it restrains, so weakest-governs
is a rule about tension rather than a guarantee of the softest structure.

`joint_type_rules` in the fast tier walks all four branches on one stair - bottom block on one
layer, the two above on another, so the lower joint and the upper joint belong to different
class pairs and a rule that matches one has to leave the other alone. It asserts the resolved
types *and* the verdicts, since the type reaching the report is not the same claim as the type
reaching the solver.

## Contacts still reduce to a point on the particle path

The connectivity graph emits **one bearing point per element pair**, so every joint in the
pinned modes is a point pin however large the real contact is. Clustering cannot recover what
was never emitted.

Measured. A three-legged tower - pad, three 2 m columns on a triangle, block on top - gives
`Kx` 2.49e7, `Ky` 2.50e7. Replacing two of those legs with a 1150 mm load-bearing wall of the
same combined `EA/L` gives 2.27e7 and 2.29e7: no stiffer, and the cluster dump shows why - four
nodes, one at the wall's top and one at its bottom. The wall is modelled as a pin-ended strut.

A real wall stands in its own plane and sways out of it, which is why buildings need shear
walls in two directions. This mode cannot express that: it reports the same near-isotropic
softness either way, and no arrangement of walls will change it.

**`contact` mode does see it.** The same wall comes back as a patch of 0.1725 m2 spanning the
full 1150 mm against the column's 0.0225 m2, because that mode works from bearing patches with
tributary areas rather than from graph points. So the two modes disagree about what a wall is,
and the one that gets it right is the one that cannot report stiffness.

Worth knowing before trusting a pinned sway number on anything with a wall, a slab or a wide
bearing in it. It is also an argument for where the joint model should end up: patches, with a
point pin as the degenerate case, rather than the reverse.

### The three-legged tower is a mechanism, and the model does not say so

Three pin-ended bars are three constraints; a rigid body needs six. The block keeps sway in x,
sway in y and twist about z, and with all three legs parallel their length changes only at
second order under each. The same family as the unbraced bridge.

The model reports lateral stiffness of 2.5e7 - soft, 66x below the braced bridge and 4x below
the tower's own axial stiffness, but not the near-zero the count implies. Under the notional
load a true mechanism should have moved about 69 mm before second-order stiffening caught it;
it moved 0.18. **The count and the measurement disagree by two orders**, and which is wrong is
not established. It is the same question the unbraced bridge raised and is the strongest
remaining reason to distrust a pinned sway figure in absolute terms.

The micro cases use this tower deliberately and safely: with `lateral_load_fraction` and
`imperfection_fraction` both zero, gravity vertical and the geometry symmetric, nothing excites
the modes and the only motion is the axial squash being measured. Their `stable=True` is the
expected verdict under those parameters, not a structural judgement.

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
Both horizontal directions, disturbance off. Result: **1.13e9 N/m unbraced against 1.66e9
braced in y**, the direction the modes move, and near 5e9 in x for both.

Those figures doubled on 2026-08-22 when `EndSpringsInSeries` landed. The y direction moved by
2.4 rather than 2.0, which is the expected signature rather than a discrepancy: this is a
secant stiffness measured on a mechanism that stiffens quadratically as it moves, so halving
the sway makes it relatively stiffer still, while the linear x direction moved by 2.1. The
ratio the comparison rests on did not move - braced over unbraced, 1.46 against the old 1.48.

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

### Screening cheaply: skip the sway probe

Measuring sway means settling the assembly, then settling it again under load along each
horizontal axis - three further integrations on top of the verdict run. `lateral_load_fraction`
0 skips them:

| model | probe on | verdict only |
| --- | --- | --- |
| braced bridge, 47 bodies, particles | 14.3 s | **2.3 s** |
| wall model, rigid bodies, timestep_safety 0.4 | 6.0 s | **1.1 s** |
| wall model, rigid bodies, default safety | 92 s | **1.1 s** |

Verdict unchanged throughout. For a first pass over many configurations this is the cheapest
speed-up there is and it trades no correctness at all - it removes a number, not an answer.
Run the survivors again with the probe on.

The ordering costs nothing, because a model that collapses skips the probe already: it only
runs when the verdict run settled or converged without crossing the threshold. Failures are
therefore fast, and only structures that stand pay for the extra passes.

What the probe buys, when it is wanted, is the difference between braced and merely
not-yet-fallen. Four of the test bridge's modes are infinitesimal mechanisms that stand under
self-weight, and the sway figure is the only thing separating that bridge from a properly
braced one.

### Cost is set by the stiffest body, not by the element count

Measured 2026-08-22, after the stiffness corrections:

| model | integrator | wall time | steps | dt |
| --- | --- | --- | --- | --- |
| wall + column + pad + block, 4 bodies | particles | 51.4 s | 1,002,496 | 1.25e-7 |
| the same 4 bodies | rigid bodies | 92.4 s | 3,588,504 | 1.04e-7 |
| braced bridge, 47 bodies | particles | 14.0 s | 61,172 | 6.55e-7 |
| the same 47 bodies | rigid bodies | over 120 s, client timeout | - | - |

**The four-body model is 3.7 times slower than the forty-seven-body one.** A 4147 kg pad and a
5000 kg block are stiff, stubby bodies - `k = (E/rho) m / L^2` grows with mass and falls with
length squared - and the explicit step is `2/omega` against the stiffest of them, whatever the
rest of the model looks like. It also takes three times as long to settle. Element count barely
enters.

So the "about 10 s per evaluation" below holds for assemblies shaped like the bridge: many
light, slender members. It does not hold for a few heavy, squat ones, and pads exist only to be
stiff - they pin the timestep while contributing nothing to the answer.

**That claim about the rigid path was wrong and is corrected below.** It read "the integrator
that can see a wall cannot yet do a truss", from a run at default settings with the sway probe
on. Both were the settings rather than the integrator:

| braced bridge, 47 bodies | time | verdict | sag |
| --- | --- | --- | --- |
| particles, verdict only | 12.4 s | stable | 0.345 mm |
| rigid bodies, verdict only, timestep_safety 0.4 | **4.2 s** | stable | 0.142 mm |
| rigid bodies, verdict only, timestep_safety 0.2 | 8.6 s | stable | 0.144 mm |
| rigid bodies, sway on, timestep_safety 0.4 | 44.8 s | stable | Ky 9.49e8 |

**The rigid path is three times faster than the default on this model**, not slower, and its sag
is converged in the timestep - 0.142 against 0.144 when the step is halved - so the fast setting
is not buying speed with accuracy. Its sway also came much closer to the particle path once
joints gained extent: Ky 9.49e8 against about 1.14e9, 17% apart, where the two used to differ by
6.5x on unbraced sag.

What remains open is sag: 0.345 mm against 0.142, a factor of 2.4, with no independent answer
for either. The micro cases pin axial stiffness and say nothing about a Warren girder's midspan
deflection.

Two untried speed levers, both aimed at the measured cause: capping each body's stiffness
relative to the softest member in the model, since a pad 500 times stiffer than what it carries
pins the step and changes no answer; and sweeping `timestep_safety` across the whole suite
rather than trusting one model.

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

  **It now acts only about that axis, and only where there is one.** Applied to the whole
  angular velocity it is a rotational air drag, and sized as a fraction of critical for the
  *joint* mode - omega in the tens of thousands - it over-damps the overturning mode, where
  omega is about three, by four orders of magnitude: a 192 kg cap overhanging its pedestal by
  250 mm carries 570 N m of overturning moment against 3.5e4 N m s of drag, so it drifts
  0.016 rad/s and reads as standing. Nothing could ever topple. The freedom it exists for is
  specific - a body whose attachments all lie on one line - and a body held at three points
  off a line has no such freedom, because the joint dashpots already damp every rotation it
  has. Acting on *relative* rotation instead, the usual cure, does not work here: a body
  turning about a single pin has zero velocity at the pin, which is why the absolute version
  was reached for in the first place.

  Removing it re-measured the real damping. The rigid path's `damping_ratio` was 20% while
  this term was quietly doing most of the settling; without it the one-storey stack drifted to
  0.552 mm against an exact 0.453 and the splayed one to 11 mm against 0.603 - not divergence,
  since halving the timestep barely moved it. At 100% both land inside their bands, 0.467 and
  0.661, the splayed case closer to the closed form than it ever was. That is what the micro
  tier now runs the rigid path at.

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
