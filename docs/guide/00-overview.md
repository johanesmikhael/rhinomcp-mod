# Overview

rhinomcp-mod consists of a Rhino 8 plugin and an MCP server. It can read and edit the open
document, measure contact between elements, and evaluate an assembly's stability under
gravity. The server exposes these operations to MCP clients, and the plugin provides an
equivalent Rhino command for each feature.

## Feature areas

| area | what it does | page |
| --- | --- | --- |
| scene inspection | list the document with paging or a bounding-box scope; return per-object geometry as an oriented box with a pose or as three orthographic outlines | [02](02-scene-inspection.md) |
| geometry, layers, materials | create, copy, modify, delete, and select geometry; manage layers, layer states, and materials | [03](03-geometry-layers-materials.md) |
| pose and transforms | rotate about a pivot, invert a rotation, rebase or reset the canonical pose, and display the OBB | [04](04-pose-transforms.md) |
| views and capture | frame objects and capture a PNG in any display mode; set an explicit camera; manage named views and viewport state | [05](05-views-capture.md) |
| connectivity graph | identify which elements touch, locate each contact, measure its bearing surface, display an overlay, and cache the result | [06](06-connectivity-graph.md) |
| mass and joint types | assign mass from density or as a stated value; classify connections as contact, pin, or fixed, with an optional tension capacity | [07](07-mass-joint-types.md) |
| stability evaluation | evaluate the scope as one rigid body or as separate bodies joined at measured bearings; report stability, displacement, and stiffness | [08](08-stability.md) |
| results and report | return an evaluation summary and page through the complete per-joint, per-node, and per-step report | [09](09-reading-results.md) |
| session and files | open and close documents, run a Rhino command, read the command line, start and stop the listener | [01](01-setup.md#10-session-tools) |

## The stability pipeline

Use the following sequence for a stability evaluation. Each step is available through both an
MCP tool and a Rhino command.

| step | mcp | rhino command |
| --- | --- | --- |
| 1. mass on every element | `assign_mass(density=...)` | `mcpmodassignlayerdensity` then `mcpmodmassfromlayerdensity`, or `mcpmodassignmass` |
| 2. joint rules where the default is wrong | `assign_joint_type(...)` | `mcpmodassignjointtype` |
| 3. graph: check what will be solved | `graph_display(enabled=True)`, `get_connectivity_graph()` | `mcpmodgraph` |
| 4. evaluate | `evaluate_stability(mode="elements")` | `mcpmodevaluatestability` |
| 5. read | the returned summary; `get_stability_report(section=...)` | the command line |
| 6. see the settled pose | `evaluate_stability(display=True)` | `mcpmodstabilitydisplay` |
| 7. clear what was stored | - | `mcpmodclearcache` |

![The x-braced timber bridge with its connectivity graph drawn: 104 elements, 77 joints, contact green and pin blue](img/bridges-xbraced-graph.png)

The worked example runs all seven on the two timber bridges: [10](10-worked-example-timber-bridges.md).

<a id="two-routes"></a>

## MCP tools and Rhino commands

| | mcp tool | rhino command |
| --- | --- | --- |
| caller | an MCP client, or a script over the socket | a person at the command line, or a script through `run_rhino_command` |
| parameters | keyword arguments | prompts, in a fixed order; option tokens after the name answer them |
| scope | `ids`, `names`, `layer`, `bbox`, `selected`, or the whole document | pre-selection, `All`, or a pick |
| result | JSON | lines on the command line; overlays in the viewport |
| only here | `detail=`, `get_stability_report`, `bbox` scoping, `capture_view` | `mcpmodgraphexport`, `mcpmodobb`, `mcpmodassignlayerdensity`, `mcpmodclearcache` |

Every feature page begins with a `task / mcp / rhino command` table. MCP examples use `python`
fences, and Rhino command prompts use `text` fences.

## What is stored on the document

| where | key | holds | cleared by |
| --- | --- | --- | --- |
| each object | `rhinomcp.stability.v1` | mass in kg, element joint type | `mcpmodclearcache` |
| each object | `rhinomcp.pose.v1`, `rhinomcp.obb.v1` | detected pose and oriented box | `mcpmodclearcache` |
| each object | `rhinomcp.after_eva.v1` | its settled pose from the last evaluation | `mcpmodclearcache` |
| document | `rhinomcp.stability.joint_types.v1` | the pair rules | `assign_joint_type(clear=True)` |
| document | `rhinomcp-mod:connectivity-graph` | the graph, with a fingerprint of the geometry it was computed from | `mcpmodclearcache` |
| document | `rhinomcp-mod:stability-report` | the last evaluation's complete report | the next evaluation |

This data is stored in the `.3dm`. Copying an object preserves its mass and element rule.
