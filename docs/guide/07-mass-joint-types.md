# Mass and joint types

<!-- run: 2026-08-29, plugin 0.3.1 -->

| task | mcp | rhino command |
| --- | --- | --- |
| mass from density and volume | `assign_mass(density=2400, layer="Blocks")` | `mcpmodassignlayerdensity`, then `mcpmodmassfromlayerdensity` |
| one mass per object | `assign_mass(mass=120, ids=[...])` | `mcpmodassignmass` |
| only where none is set | `assign_mass(density=2400, overwrite=False)` | `mcpmodassignmissingmass` |
| store a density on a layer | - | `mcpmodassignlayerdensity` |
| rule for a layer pair | `assign_joint_type(joint_type="pin", layer="TRUSS", with_layer="TRUSS")` | `mcpmodassignjointtype` > Layers |
| rule for two elements | `assign_joint_type(joint_type="contact", ids=[a], with_ids=[b])` | `mcpmodassignjointtype` > Objects |
| an element's own joints | `assign_joint_type(joint_type="pin", ids=[a])` | `mcpmodassignjointtype` |
| a founded base | `assign_joint_type(joint_type="fixed", layer="PAD", with_ground=True)` | `mcpmodassignjointtype` |
| tension capacity on a rule | `assign_joint_type(..., capacity_kn=40)` | `mcpmodassignjointtype` |
| list the rules | `assign_joint_type()` | `mcpmodassignjointtype` > List |
| drop rules naming nothing | `assign_joint_type(prune=True)` | `mcpmodassignjointtype` > Prune |
| remove every rule | `assign_joint_type(clear=True)` | `mcpmodassignjointtype` > Clear |

## Mass

Nothing is evaluated without it: every element in the scope must carry a positive mass. It is
stored on the object under `rhinomcp.stability.v1` as kilograms, whatever the document's units,
and travels with a copy of the object.

```python
assign_mass(density=2400, layer="Blocks")     # kg/m³; each object's mass from its own closed volume
assign_mass(mass=850, names=["SLAB"])         # kg, the same value on every object in the scope
assign_mass(density=500, overwrite=False)     # fill in what has no mass yet, leave the rest
```

Exactly one of `density` or `mass`. Scope by `ids`, `names`, `layer` (one or a list) or
`selected`; no scope is the whole document. The result lists each object's mass and the volume
used, the scope total, and `skipped` - objects with no computable closed volume when a density
was given. Give those a `mass`.

```text
{"assigned": [{"guid": "...", "name": "BOT_0_01", "mass": 259.2, "mass_unit": "kg", "volume_m3": 0.108}, ...],
 "skipped": [], "total_mass_kg": 37231.4, "density_kg_m3": 2400, "document_length_unit": "Millimeters"}
```

The Rhino commands prompt per object or per layer. In a metric document they take `kg` and
`kg/m³`; in an imperial one, pound-mass (`lbm`, never pound-force) and `lbm/ft³`. Either way
the stored value is kilograms.

```text
mcpmodassignlayerdensity          Layer: Blocks   Density (kg/m³): 2400
mcpmodmassfromlayerdensity        every object on a layer with a density gets density x volume
mcpmodassignmass                  pick objects; Mass for BOT_0_01 in kg <259.2>:
mcpmodassignmissingmass           the same, only for objects with no mass
```

## Joint types

Geometry cannot tell a screwed panel from a dry-stacked one - they look identical to an
intersection test - so the connection is stated. Three types; the type decides how the
measured bearing ([06](06-connectivity-graph.md)) is used:

| type | carries | what it is |
| --- | --- | --- |
| `contact` | compression and moment until it opens; friction across it; no tension | dry masonry, a beam on a corbel, a panel on a pad |
| `pin` | force in three directions, no moment | truss to truss, a single bolt |
| `fixed` | force and moment, both ways, always | a moment connection: beam to column, a rigid plate |

The moment comes from the spread of the bearing. A joint reduced to a point has no lever arm
and resists no rotation, so `pin` collapses the bearing to its centre and the other two keep
its extent.

A joint nobody names is `contact`, the only one of the three that describes two things found
touching. `fixed` is the strongest assumption available; `pin` hangs in tension and discards
the bearing, so a stack of blocks pinned becomes a mechanism hinged at points that exist
nowhere in the drawing.

![The unbraced timber bridge with its graph drawn: pins blue between beams and planks, fixed amber wherever a portal meets anything, contact green on the pads](img/joint-types-portal.png)

## Rules

Rules are stated by element class, not joint by joint, and stored in the document under
`rhinomcp.stability.joint_types.v1`:

```python
assign_joint_type(joint_type="pin", layer="TRUSS", with_layer="TRUSS")       # every truss-to-truss joint
assign_joint_type(joint_type="contact", layer="TRUSS", with_layer="PAD")     # truss on pad
assign_joint_type(joint_type="fixed", layer="BEAM", with_layer="PORTAL", capacity_kn=40)
assign_joint_type(joint_type="contact", ids=[block], with_ids=[pad])         # one joint
assign_joint_type(joint_type="pin", ids=[strut])                             # all of one element's joints
assign_joint_type(joint_type="fixed", layer="PAD", with_ground=True)         # a founded base
```

Layer rules match the layer's leaf name, not its full path. Element rules (an `ids` list with
no `with_`) are stored on the object beside its mass.

Precedence: a pair rule beats an element rule beats the default. Where two elements' own
rules disagree the weaker governs - a hinge assumed where a moment connection exists reports
the structure softer than it is, which fails safe. The result carries each joint's resolved
type and the rule that decided it (`nodes[].joint_type`, `nodes[].joint_type_rule`;
[09](09-reading-results.md)).

**Founded bases.** A base is a bearing too. Anything resting on the floor can lift off it and
slide on it, which is what an unfounded block does. A pad cast into a footing and one set down
on gravel are drawn identically, so `with_ground=True` states the footing the way the other
rules state a joint. Without it an arch spreads at its springings and a post lifts the far
edge of its base under an overhanging load - both correct for something merely set down.

**Capacity.** `capacity_kn` limits tension, per bearing point, which gives a joint a moment
capacity as well as an axial one. A joint that reaches it yields: the pull holds at the limit
and the structure redistributes, or moves. It does not break. Read `peak_point_tension_n` and
never the net force: a cantilever's connection can sit in net compression at -7.1 kN while
one of its bearing points is pulled at 24.5.

## List, prune, clear

```python
assign_joint_type()                 # every rule, with "stale" on any that names an object or layer no longer in the document
assign_joint_type(prune=True)       # remove the stale ones and return them
assign_joint_type(clear=True)       # remove every rule
```

```text
{"rules": [{"a": "layer:PAD", "b": "layer:TRUSS", "joint_type": "contact", "capacity_kn": null},
           {"a": "layer:TRUSS", "b": "layer:TRUSS", "joint_type": "pin", "capacity_kn": null},
           {"a": "id:15ee9192-...", "b": "id:282f4823-...", "joint_type": "contact",
            "stale": "object 15ee9192-... is not in the document; object 282f4823-... is not in the document"}],
 "stale_rules": 1}
```

A rule naming a deleted object is kept, not dropped: the deletion can be undone and the rule
should come back with it. Stale rules match nothing and change no verdict; the evaluate
command's prompt counts them (`4 rules, 2 stale`) so they are not mistaken for live ones.
`prune` removes them on request.

In Rhino, `mcpmodassignjointtype`:

```text
Select elements on one side of the joint ( List  Prune ): <pick, or List / Prune>
Select elements on the other side ( Ground ): <pick, or Ground for a founded base>
Write the rule about ( Layers  Objects ): Layers
Joint type ( Contact  Pin  Fixed  Clear ): Pin
```

`Layers` writes one rule for the two layers the picks are on; `Objects` writes one rule per
object pair. `Clear` removes the rule for that pair.
