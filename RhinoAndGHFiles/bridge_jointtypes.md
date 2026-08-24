# bridge_jointtypes.3dm — stability demo

A 10 m truss bridge set down on two pads. 47 solids: 40 members `M_00..M_39`,
5 braces `BR_0..BR_4`, 2 pads `PAD_0`/`PAD_1`, on layers `TRUSS` and `PAD`.

The file already carries everything the evaluator needs, so it demonstrates the
three joint types separating with no setup:

- masses on every element (8302 kg total)
- two joint-type pair rules in document user text:
  - `PAD | TRUSS` → `contact` — the truss is set down, not fixed
  - `TRUSS | TRUSS` → `pin` — a truss carries axial force, not moment

## Running it

Open the file and run `mcpmodevaluatestability`, or over MCP:

    evaluate_stability(mode="pinned")

Expected: **stable**, 47 bodies clustered into 29 joints,
`joint_type_counts: {contact: 36, pin: 17, welded: 0}`, all 219 graph edges
measured exactly (`bearing_source: "exact"`, no unmeasured contacts).

`mcpmodgraph` draws the connectivity graph coloured by joint type, with the
measured bearing polygon behind each contact.

## What it is useful for showing

- the same structure answering differently per joint type — set
  `joint_type="welded"` or `"contact"` globally to override the rules
- `joint_forces`: per joint the force, its sense, the shear, and
  `peak_point_tension_n` — the number to read, since a joint can sit in net
  compression while one bearing point is pulled
- joint capacity, by adding e.g.
  `assign_joint_type(joint_type="pin", capacity_kn=12, layer="TRUSS")`
