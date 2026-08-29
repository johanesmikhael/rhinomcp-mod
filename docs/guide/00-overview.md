# Overview

rhinomcp-mod is a Rhino 8 plugin and an MCP server. The plugin reads and edits the open
document, measures how its elements touch, and evaluates whether an assembly of them stands
under gravity. The server exposes that to Claude as tools. Every feature is also a Rhino command.

## Feature areas

| area | what it does | page |
| --- | --- | --- |
| scene inspection | inventory of the document with paging and a bounding-box scope; per-object geometry as an oriented box with a pose, or three orthographic outlines | [02](02-scene-inspection.md) |
| geometry, layers, materials | create the primitive types, copy, modify, delete; select; layers and layer states; materials | [03](03-geometry-layers-materials.md) |
| pose and transforms | rotate about a pivot, invert a rotation, rebase or reset the canonical pose; the OBB display | [04](04-pose-transforms.md) |
| views and capture | frame objects and capture a PNG in any display mode, with an explicit camera if wanted; named views; viewport state | [05](05-views-capture.md) |
| connectivity graph | which elements touch, where, and the bearing surface measured at each contact; drawn as an overlay; cached on the document | [06](06-connectivity-graph.md) |
| mass and joint types | mass per element from density or stated; rules naming each connection contact, pin or fixed, with an optional tension capacity | [07](07-mass-joint-types.md) |
| stability evaluation | the scope as one welded body, or as separate rigid bodies joined at the measured bearings, under gravity; does it stand, how far does it move, how stiff is it | [08](08-stability.md) |
| results and report | the summary the evaluation returns; the complete per-joint, per-node and per-step report, paged | [09](09-reading-results.md) |
| session and files | open and close documents, run a Rhino command, read the command line, start and stop the listener | [01](01-setup.md#9-session-tools) |

## The stability pipeline

In order. Each step has a tool and a command; either does the same work.

| step | mcp | rhino command |
| --- | --- | --- |
| 1. mass on every element | `assign_mass(density=...)` | `mcpmodassignlayerdensity` then `mcpmodmassfromlayerdensity`, or `mcpmodassignmass` |
| 2. joint rules where the default is wrong | `assign_joint_type(...)` | `mcpmodassignjointtype` |
| 3. graph: check what will be solved | `graph_display(enabled=True)`, `get_connectivity_graph()` | `mcpmodgraph` |
| 4. evaluate | `evaluate_stability(mode="pinned")` | `mcpmodevaluatestability` |
| 5. read | the returned summary; `get_stability_report(section=...)` | the command line |
| 6. see the settled pose | `evaluate_stability(display=True)` | `mcpmodstabilitydisplay` |
| 7. clear what was stored | - | `mcpmodclearcache` |

![The x-braced timber bridge with its connectivity graph drawn: 104 elements, 77 joints, contact green and pin blue](img/bridges-xbraced-graph.png)

The worked example runs all seven on the two timber bridges: [10](10-worked-example-timber-bridges.md).

## Two routes

| | mcp tool | rhino command |
| --- | --- | --- |
| who drives | Claude, or a script over the socket | a person at the command line, or a script through `run_rhino_command` |
| parameters | keyword arguments | prompts, in a fixed order; option tokens after the name answer them |
| scope | `ids`, `names`, `layer`, `bbox`, `selected`, or the whole document | pre-selection, `All`, or a pick |
| result | JSON | lines on the command line; overlays in the viewport |
| only here | `detail=`, `get_stability_report`, `bbox` scoping, `capture_view` | `mcpmodgraphexport`, `mcpmodobb`, `mcpmodassignlayerdensity`, `mcpmodclearcache` |

Every feature page opens with a `task / mcp / rhino command` table and then walks the MCP route
in `python` fences and the command's prompts in `text` fences for the same task.

## What is stored on the document

| where | key | holds | cleared by |
| --- | --- | --- | --- |
| each object | `rhinomcp.stability.v1` | mass in kg, element joint type | `mcpmodclearcache` |
| each object | `rhinomcp.pose.v1`, `rhinomcp.obb.v1` | detected pose and oriented box | `mcpmodclearcache` |
| each object | `rhinomcp.after_eva.v1` | its settled pose from the last evaluation | `mcpmodclearcache` |
| document | `rhinomcp.stability.joint_types.v1` | the pair rules | `assign_joint_type(clear=True)` |
| document | `rhinomcp-mod:connectivity-graph` | the graph, with a fingerprint of the geometry it was computed from | `mcpmodclearcache` |
| document | `rhinomcp-mod:stability-report` | the last evaluation's complete report | the next evaluation |

All of it travels with the `.3dm`. A copied object keeps its mass and element rule.
