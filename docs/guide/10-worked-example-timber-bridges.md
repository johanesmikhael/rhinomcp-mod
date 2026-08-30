# Worked example: the two timber bridges

<!-- run: 2026-08-30, plugin 0.4.0 -->

This example evaluates two 24 m timber bridges on two pads. The first is x-braced in plan and
elevation. The second uses rigid portal frames without diagonal bracing. Both are stable under
self-weight, but the sway probe shows different stiffness behaviour.

| | `timber_bridge_xbraced.3dm` | `timber_bridge.3dm` |
| --- | --- | --- |
| elements | 104 on `TRUSS` + 2 on `PAD` | 48 `BEAM`, 28 `PORTAL`, 12 `PLANK`, 2 `PAD` |
| mass | 37.2 t | 35.8 t |
| rules stored | `PAD\|TRUSS` contact, `TRUSS\|TRUSS` pin | 9: pins between beams and planks, fixed wherever a portal meets anything, contact on the pads |

![The x-braced bridge](img/bridges-xbraced-iso.png)
![The portal-framed bridge](img/bridges-portal-iso.png)

**1. Open the document.**

```python
open_file("RhinoAndGHFiles/timber_bridge_xbraced.3dm", close_current=True)
get_document_info(detail="inventory", limit=10)
```

```text
{"meta_data": {"name": "timber_bridge_xbraced.3dm", "units": "Millimeters", "tolerance": 0.001},
 "object_count": 104, "objects_truncated": true,
 "objects": [{"id": "00892d31-...", "name": "DGB_1_01", "type": "Brep", "layer": "TRUSS"}, ...],
 "layers": [{"name": "TRUSS"}, {"name": "PAD"}]}
```

Object names identify the member family: `TOP` and `BOT` chords, `POST`, `RAFT`, `FLOOR`,
`PLNA`/`PLNB` purlins, `DGA`/`DGB` diagonals, and `XFA`/`XFB` cross bracing.

**2. Verify mass.** Every element in both files already has mass assigned. Check the values
without overwriting them:

```python
assign_mass(density=2400, overwrite=False)    # assigns nothing; reports what is there
```

Nothing is written, so `assigned` is empty and `assigned_mass_kg` is 0. Every element lands in
`skipped`, each carrying the mass it already holds, and `total_mass_kg` is what the scope
weighs: 37231 for the x-braced file and 35794 for the portal one - the same totals
`evaluate_stability` reports, which is the point of checking here first. To assign mass from
scratch, call `assign_mass(density=...)` with the appropriate timber density.

**3. Inspect joint rules.** The rules are stored in each document. List them with:

```python
assign_joint_type()
```

```text
x-braced: {"rules": [{"a": "layer:PAD",   "b": "layer:TRUSS", "joint_type": "contact"},
                     {"a": "layer:TRUSS", "b": "layer:TRUSS", "joint_type": "pin"}], "stale_rules": 0}
portal:   {"rules": [{"a": "ground:",      "b": "layer:PAD",    "joint_type": "contact"},
                     {"a": "layer:BEAM",   "b": "layer:BEAM",   "joint_type": "pin"},
                     {"a": "layer:BEAM",   "b": "layer:PAD",    "joint_type": "contact"},
                     {"a": "layer:BEAM",   "b": "layer:PLANK",  "joint_type": "pin"},
                     {"a": "layer:BEAM",   "b": "layer:PORTAL", "joint_type": "fixed"},
                     {"a": "layer:PAD",    "b": "layer:PORTAL", "joint_type": "contact"},
                     {"a": "layer:PLANK",  "b": "layer:PLANK",  "joint_type": "pin"},
                     {"a": "layer:PLANK",  "b": "layer:PORTAL", "joint_type": "pin"},
                     {"a": "layer:PORTAL", "b": "layer:PORTAL", "joint_type": "fixed"}], "stale_rules": 0}
```

The braced truss uses pinned connections at its nodes and contact bearings on its pads. In the
portal bridge, every connection involving a portal member is fixed.

**4. Inspect the connectivity graph.**

```python
graph_display(enabled=True)
get_connectivity_graph()
```

![The x-braced bridge with its graph: 104 elements, 609 contacts; pins blue at the nodes, contact green on the pads](img/bridges-xbraced-graph.png)
![The portal bridge with its graph: fixed amber where portals meet beams and planks, pins blue between beams and planks](img/bridges-portal-graph.png)

The graph counts contacts between element pairs. The evaluation clusters contacts at the same
node into 77 joints for the braced bridge and 206 for the portal bridge. Every bearing is shown
at full brightness because each contact matches a rule. Before evaluation, check for nodes with
no edges and for bearing patches drawn on the side of a pad beneath a supported member
([08 - limitations](08-stability.md#limitations)).

**5. Evaluate stability.**

```python
evaluate_stability(mode="elements")
```

| | x-braced | portal |
| --- | --- | --- |
| `stable` / `verdict` | true / `stable` | true / `stable` |
| `joint_count` and types | 77: 24 contact, 53 pin | 206: 24 contact, 70 pin, 112 fixed |
| `max_pin_displacement_m` | 0.00305 | 0.00073 |
| `mechanism_threshold_m` | 0.136 (span 27.3 m) | 0.136 |
| `settled_displacement_m` | 0.00123 | 0.00044 |
| `contact_joints_sided` / `_open` | 48 / 16 | 48 / 0 |
| `joint_forces_summary.max_tension_n` | 14109 (a pin) | 8387 (a fixed joint) |
| `ground_sites_summary.fz_total_n` | 340583 | 351052 |

Both bridges are stable, with maximum pin displacement more than an order of magnitude below
the mechanism threshold. The worst pin displacement in the braced bridge is about four times
that of the portal bridge. Sixteen of its 48 contact bearings open as the diagonals pull on
their chords. No bearings open in the portal bridge, which carries sway through bending. Its
highest joint tension occurs at a fixed connection and is about 60% of the braced bridge's
highest pin tension.

**6. Read detailed results.** Query the most-tensioned joints and the elements they connect:

```python
get_stability_report(section="joint_forces", limit=5)
get_stability_report(section="joint_forces", joint_type="contact", sort="shear_n", limit=5)
get_stability_report(section="nodes", limit=5)
```

```text
x-braced, joint_forces by tension: [{"body": 43, "guid": "0700897d-...", "joint_type": "pin", "with": [46, 47, 49, 59, 70, 99],
                                     "force_n": 14125.5, "tension_n": 14108.7, "shear_n": 689.5, "bearing_points": 1}, ...]
```

`body` 43 is `BOT_1_03`, a bottom chord connected to six members at the node. For the portal
bridge, the same query returns fixed joints at the portal legs with `bearing_points` greater
than 1. Fixed joints preserve the distributed bearing; pins reduce it to one point.

**7. Measure sway stiffness.** The probe first settles the bridge, then applies 5% of the
carried weight along x and y and reports the resulting stiffness. The braced bridge remains in
motion after the default 0.5 seconds, so this example uses a longer duration.

```python
evaluate_stability(mode="elements", lateral_load_fraction=0.05, duration_seconds=1.5)
```

| `sway` | x-braced | portal |
| --- | --- | --- |
| `sway_stiffness_x_n_per_m` (along the span) | 4.67e7 | 2.95e8 |
| `sway_stiffness_y_n_per_m` (across) | 3.67e7 | 3.28e7 |
| `softest_direction` | y | y |
| `notional_load_n` | 18256 | 17551 |
| `settled` at the end of the run | false | true |

Transverse stiffness is similar: 3.7e7 N/m for the braced bridge and 3.3e7 N/m for the portal
bridge. Longitudinally, the portal bridge is about six times stiffer. Its 112 fixed joints
resist sway through bending, while the pinned braced truss racks until the diagonals engage.
The braced bridge reports `settled: false`, so its stiffness was measured while motion was
continuing and can change with a longer `duration_seconds`. Use `detail="full"` or query the
`sway` report section to inspect drift ratios.

**8. Display the evaluated pose.**

```python
evaluate_stability(mode="elements", display=True)
```

This draws each element at its evaluated position in grey over the original geometry. The
displacements in both bridge examples are too small to distinguish at this view scale. The
stair example shows a more visible displacement ([08](08-stability.md)). Run
`mcpmodstabilitydisplay Off` to hide the overlay.

**9. Clear cached data.** `-mcpmodclearcache` removes the stored graph, settled poses, and
masses from the document. Joint rules remain until removed with
`assign_joint_type(clear=True)`.
