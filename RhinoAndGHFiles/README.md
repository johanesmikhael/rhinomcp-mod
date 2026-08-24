# Test files

Four models with answers that are known independently of the solver, for checking that a
build works and for seeing what the three joint types actually do. Open one and run
`mcpmodevaluatestability`, or over MCP:

    evaluate_stability(mode="pinned")

`mcpmodgraph` draws the connectivity overlay on top: which elements touch, the bearing
surface each joint is built over, and what type each one resolves to.

| file | what it is | expected |
| --- | --- | --- |
| `stair_jointtypes.3dm` | three blocks, each set 100 mm forward | **stable** as contact, **unstable** as pin |
| `stair_toppling.3dm` | the same stair at 300 mm | **unstable** - the centre of mass clears the bearing |
| `pavilion_jointtypes.3dm` | four walls in a pinwheel, roof set on top | **stable**, 44 contact joints |
| `bridge_jointtypes.3dm` | 10 m truss on two pads, 47 elements | **stable**, 29 joints, 36 contact and 17 pin |

Run the stair twice. Same three blocks either way:

    evaluate_stability(mode="pinned", joint_type="contact")   # stable
    evaluate_stability(mode="pinned", joint_type="pin")       # unstable

Contact bears over the measured surface and pushes without pulling, so the stack stands
with 150 mm of margin. Pin collapses each bearing to its centre, and a body held at one
point can rotate about it, so the same stack is a mechanism. Both are correct answers to
different questions about how the blocks are connected.

Only `bridge_jointtypes.3dm` carries joint-type rules of its own - `PAD | TRUSS` contact and
`TRUSS | TRUSS` pin, stored in the document - so it needs no setup at all. The other three
take the default, which is `contact`.

All four carry mass on every element. Nothing else is required to evaluate them.

## Rebuilding them

    python3 scripts/dev/build_demo_files.py

Draws three of these from the regression suite's own case definitions and saves them here,
which keeps the demo files and the tested cases in step. It writes to whatever document
Rhino has open and clears it first, so point Rhino at a scratch file before running it.

`bridge_jointtypes.3dm` is not rebuilt by that script; it was drawn by hand.

## The rest

- `example.3dm`, `example.gh` - the original RhinoMCP sample scene.
- `material_library.3dm` - materials for the material tools.
