# rhinomcp-mod guide

Rhino 8 driven through Claude: the document read and edited over MCP, the way its elements
touch measured, and an assembly's stability under gravity evaluated. Each feature has an MCP
tool and a Rhino command; every page shows both.

| page | covers |
| --- | --- |
| [00 overview](00-overview.md) | feature areas, the stability pipeline, the two routes, what is stored on the document |
| [01 setup](01-setup.md) | plugin, `uv`, Claude Desktop, Claude Code, from source, verify, troubleshooting, session tools |
| [02 scene inspection](02-scene-inspection.md) | the inventory, paging, a region, per-object geometry, selection |
| [03 geometry, layers, materials](03-geometry-layers-materials.md) | the primitive types, copy and modify, layers and their states, materials |
| [04 pose and transforms](04-pose-transforms.md) | what a pose is, rotate, reset, rebase, the OBB overlay |
| [05 views and capture](05-views-capture.md) | framing, cameras, display modes, size, named views |
| [06 connectivity graph](06-connectivity-graph.md) | nodes, edges, measured bearings, the overlay, the cache |
| [07 mass and joint types](07-mass-joint-types.md) | mass from density or stated; contact, pin, fixed; rules, capacity, pruning |
| [08 stability](08-stability.md) | modes, parameters, the command's prompts, bearings, limitations |
| [09 reading results](09-reading-results.md) | the summary, the stored report, paging it |
| [10 worked example](10-worked-example-timber-bridges.md) | the two timber bridges end to end |
| [11 reference](11-reference.md) | every tool and command with its counterpart |

Demo models with known answers: [`RhinoAndGHFiles/`](../../RhinoAndGHFiles/README.md).

Images under `img/` are produced by `scripts/dev/build_guide_images.py` against a running
Rhino; rerun it after a change to what the plugin draws.
