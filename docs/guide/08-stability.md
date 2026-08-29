# Stability evaluation

<!-- run: 2026-08-29, plugin 0.3.1 -->

| task | mcp | rhino command |
| --- | --- | --- |
| elements as separate bodies, joined where they bear | `evaluate_stability(mode="pinned")` | `mcpmodevaluatestability` > Elements |
| the scope as one rigid body: does it tip or slide | `evaluate_stability(mode="welded")` | `mcpmodevaluatestability` > Assembly |
| every joint a dry bearing, whatever the rules say | `evaluate_stability(mode="contact")` | `mcpmodevaluatestability` > Elements > Contact |
| default type for joints no rule names | `evaluate_stability(mode="pinned", joint_type="pin")` | the `Joint type where no rule names one` prompt |
| a subset | `evaluate_stability(mode="pinned", ids=[...])`, `layer=`, `bbox=`, `selected=True` | pre-select, or pick at the first prompt |
| measure sway stiffness too | `evaluate_stability(mode="pinned", lateral_load_fraction=0.05)` | Custom > `Sway probe` |
| see the settled pose | `evaluate_stability(mode="pinned", display=True)` | `mcpmodstabilitydisplay On` |
| the whole report in one answer | `evaluate_stability(mode="pinned", detail="full")` | - |

An assembly is evaluated as separate rigid bodies resting on one another, joined where the
geometry says they touch, under gravity. The question is whether it stands: whether it is a
mechanism, whether an element rotates off its support, whether a stack topples. Requires
Rhino 8 with Grasshopper; the plugin loads Rhino's own `KangarooSolver.dll` for the assembly
mode.

Before evaluating: mass on every element and rules where the default is wrong
([07](07-mass-joint-types.md)); the graph checked ([06](06-connectivity-graph.md)) - a joint the
graph never found is not solved, and a bearing measured on the wrong face restrains the wrong
rotation.

## Modes

| mode | bodies | joints | answers |
| --- | --- | --- | --- |
| `pinned` | one per element | what the rules say; `contact` where nothing is said | is it a mechanism; how far does it move; how stiff is it |
| `contact` | one per element | all bearings, rules ignored | can anything rotate off, lift, or slide |
| `welded` | the whole scope, one body | none | does the assembly tip or slide as a whole |

`pinned` is the general one and the default in Rhino's `Elements`. `contact` is `pinned` with
every joint forced to a bearing, for a dry-stacked reading of a model that has rules.
`welded` is a different thing from the `fixed` joint type: it supplies every moment
connection the real assembly lacks, so it passes structures a dry stack would not hold. It is
a cheap upper bound, and its `support_margin_m` - the centre of mass against the bearing
footprint - is closed-form. Neither subsumes the other.

```python
evaluate_stability(mode="pinned")                         # the model as its rules describe it
evaluate_stability(mode="pinned", joint_type="pin")       # unnamed joints as pins instead of bearings
evaluate_stability(mode="contact")                        # every joint a bearing
evaluate_stability(mode="welded")                         # one body; tips or not
```

On the stair demo: `stable` as contact, `unstable` as pin. Both are right about different
connections: a bearing pushes without pulling and spreads over the measured face; a pin holds
one point and a body held at one point rotates about it.

![The 300 mm stair after a pinned evaluation with display on: the bodies drawn grey over the original geometry where the run stopped - 6 mm into a topple, past the 4 mm mechanism threshold, verdict unstable](img/stability-stair-settled.png)

The run ends as soon as the verdict is settled - here 47 ms in, when the top block had moved
6 mm against a threshold of 4 - so the drawn pose is where the fall was caught, not where it
would end. A stable assembly's pose is its rest position.

## Parameters

Everything has a default sized from the document; pass a parameter to study a deliberately
different model, not to make a sound one pass.

**Scope.** `ids`, `names`, `layer` (one or a list), `bbox` + `bbox_mode` (`intersects`,
`contains_center`, `contained`), `selected=True`. Nothing means the whole document. A scope
that matches nothing, or that would truncate the graph, fails rather than guessing. A scope
that leaves out the pads its columns stand on is evaluated standing on the ground: the floor
is placed at the underside of the scope unless `floor_z` is given.

**Joint solver** (`pinned`, `contact`):

| parameter | default | what it is |
| --- | --- | --- |
| `joint_type` | `contact` | type for joints no rule names |
| `duration_seconds` | 0.5 | how long to simulate; a mechanism with a tenth of gravity available covers 50 mm in 0.32 s, and the run exits early once motion has settled or is clearly diverging |
| `damping_ratio` | 0.2 | fraction of critical at each joint, against relative motion there |
| `joint_penetration` | 0.1 mm, in document units | how far a bearing may close under its own load; sizes joint stiffness where none is stated |
| `joint_stiffness_n_per_m` | derived per member from its mass and length | one axial stiffness for every joint in the scope; what a joint test gives |
| `lateral_load_fraction` | 0 (off) | the sway probe: settle, then push sideways with this fraction of the carried weight along x and along y, and report stiffness. Six times the cost; the verdict is unchanged |
| `bearing_source` | `exact` | `buried` also uses the surface inside overlapping solids - off because its area grows with a modelling artefact |
| `integrator` | `rigid_bodies` | `particles` is the earlier point-joint model, kept as a reference; it cannot represent joint types |

**Assembly solver** (`welded`):

| parameter | default | what it is |
| --- | --- | --- |
| `ground_settlement` | 0.1 mm, in document units | how far the assembly may settle into the ground under its weight; sizes the floor spring |
| `ground_support_stiffness_n_per_m` | from weight and settlement | states the floor spring outright instead |
| `rigid_strength` | from the floor | how rigid the single body is; below the floor stiffness the floor deforms the body |
| `stability_threshold` | 10 mm, in document units | displacement counted as unstable |
| `current_step`, `solver_substeps` | 50, 1 | the relaxation budget |

**Both:** `floor_z` (document units; default the scope's underside), `gravity` (9.80665 m/s²),
`display` (draw the settled pose), `detail` (`summary` or `full`; [09](09-reading-results.md)).

Geometry, tolerances and mass are normalised internally to metres and kilograms; lengths in
the result that end in `_m` are metres, the rest are the document's units. Missing or
non-positive mass, invalid graph nodes and non-finite values fail explicitly rather than
reading as instability.

## The command

`mcpmodevaluatestability` asks, in order:

```text
Select objects to evaluate (Enter = whole document) ( All  Pinned ): <pick, All, or Pinned for the graph's scope>
Model the scope as ( Assembly  Elements ): Elements
Joint type where no rule names one (2 rules will override it) ( Contact  Pin  Fixed ): Contact
Stability parameter mode ( Defaults  Custom ): Defaults
Display evaluated geometry cache? ( On  Off ): On
```

`Defaults` in Elements mode sets nothing but gravity and leaves the rest to the solver's own
sizing. `Custom` asks for the parameters that mode reads:

```text
Elements:  Floor level ( Auto  Manual )
           Duration (s) = 0.5
           Damping ratio (fraction of critical at a joint) = 0.2
           Joint penetration (Millimeters; sizes joint stiffness where none is stated) = 0.1
           Joint stiffness (N/m, 0 = derived per member) = 0
           Sway probe (fraction of weight, 0 = off) = 0
           Gravity (m/s²) = 9.80665
Assembly:  Floor level, Current step, Stability threshold, Rigid strength (0 = auto),
           Floor strength (0 = auto), Gravity, Assign tolerance, Displacement threshold,
           Solver substeps, Ground settlement
```

Enter accepts the shown default. The result prints on the command line - verdict, bodies and
joints by type, worst pin against the mechanism threshold, run length, contact and capacity
counts, the three most-tensioned joints, sway if probed - and, with display on, the settled
bodies are drawn grey over the original geometry, which is not modified.
`mcpmodstabilitydisplay Off` hides them.

## Bearings

A joint is built over the polygon two flat faces actually share, on the mean plane between
them. One rule covers the three states two solids can be drawn in:

| the two faces | what the solver gets |
| --- | --- |
| near parallel | the shared polygon |
| crossing, no overlap | the line they cross along - a hinge about itself |
| crossing and overlapping | the surface inside the shared volume, off unless `bearing_source="buried"` |

Curved faces have no flat region to intersect and are sampled instead. The bearing is spread
over points that reproduce a uniformly loaded elastic face in force and in moment; a `pin`
collapses it to one point. The graph overlay draws exactly this ([06](06-connectivity-graph.md)).

## Limitations

Measured against hand-computed statics:

- **Self-weight only.** No live or lateral load beyond the sway probe, no strength or crushing
  limit, so a design can pass on stability while its bearing stress is absurd.
- **A verdict has a duration.** `duration_seconds` is real simulated time, and the run exits
  early on settling or divergence; a body that has not settled by the end reads
  `inconclusive`, which is not `unstable`.
- **Contact absorbs marginal eccentricity.** A joint whose resultant falls well outside its
  bearing topples; one only marginally outside settles into a tilted equilibrium and reads as
  stable. On three-block stairs, 112 mm past the bearing edge topples and 75 mm does not.
- **A body that leaves its support falls through the ground.** Ground bearing is built for
  points that start at floor level. The verdict holds; the trajectory afterwards is
  meaningless.
- **A run that diverges reports no verdict.** `verdict` is `inconclusive`, `diverged` is true,
  `diverged_reason` carries the speed that triggered it.
- **A member seated into a support keeps one bearing face.** A chord dropped into a pad rests
  on the pad's top and bears against its side; the larger shared area is kept. Where the side
  is larger the joint's normal points sideways and the member's weight rests on friction, so
  the verdict can change with support position. Visible in the graph as a bearing patch on a
  vertical face under a member that sits on top of it.
- **Joint stiffness is per end** and is not shared along a member's load path.
- **Overlapping bodies double-count mass** from `assign_mass(density=...)`, since each
  element's own volume includes the overlap - about 4% for a centreline truss.
- **The welded floor is sized from the footprint.** `ground_settlement` moves the verdict of
  a marginal assembly: at 1 mm the +121 mm cantilever demo reads unstable, at 0.1 mm stable.

None of this makes the result a certified structural analysis.
