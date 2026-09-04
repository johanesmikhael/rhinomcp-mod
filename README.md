# RhinoMCP Mod

RhinoMCP Mod is a derivative work based on the original [RhinoMCP](https://github.com/jingcheng-chen/rhinomcp).

## Let your AI agents see the geometry

<img src="images/screenshot.png" alt="Geometry and Topological context" width="400">


This repository extends Rhino MCP with deeper geometric and topological context for AI-assisted design in Rhino3D.

### What it adds

Compared with the baseline, the mod exposes the geometry and the topology an agent needs to
reason about an assembly rather than about isolated objects. Each area has its own page in
the guide, with the calls, the responses and the Rhino command beside them.

- **Geometry understanding** - a compact scene inventory, oriented boxes with
  `pose.world_from_local` frames for lines, curves, breps and extrusions, planarity-aware
  curve summaries, and three orthographic outlines for shapes an oriented box cannot tell
  apart: [02](docs/guide/02-scene-inspection.md), [04](docs/guide/04-pose-transforms.md)
- **Viewport capture** - `capture_view` frames targets and returns a PNG in any display mode,
  at any size up to 3840 px, from a preset or a stated camera, restoring the viewport
  afterwards; named views and viewport state alongside it:
  [05](docs/guide/05-views-capture.md)
- **Topological context** - `get_connectivity_graph` and the `mcpmodgraph` overlay: which
  elements touch, where, and the bearing surface measured between them, cached on the
  document: [06](docs/guide/06-connectivity-graph.md)
- **Pose-aware editing** - batch `modify_objects` / `rotate_objects` / `copy_objects`,
  `rebase_objects_pose` to fix a canonical pose without moving anything, and
  `reset_objects_pose` to return to it: [04](docs/guide/04-pose-transforms.md)
- **Assembly stability** - mass, joint types, and an evaluation of whether the thing stands:
  [07](docs/guide/07-mass-joint-types.md) to [10](docs/guide/10-worked-example-timber-bridges.md)

Rhino commands for the caches these build: `mcpmodobb` (oriented boxes and profiles),
`mcpmodgraph` (connectivity), `mcpmodclearcache` (clear stored poses, boxes and graph).

## Installation

1. Rhino 8: `Tools > Package Manager`, search `rhinomcp-mod`, install. The listener starts with
   the plugin; `mcpmodversion` confirms it.
2. `uv`: `brew install uv` (macOS) or `powershell -c "irm https://astral.sh/uv/install.ps1 | iex"` (Windows).
3. Claude Desktop, in `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rhino": {
      "command": "uvx",
      "args": ["--with", "mcp[cli]<2", "rhinomcp-mod"]
    }
  }
}
```

The `mcp[cli]<2` pin is required through 0.4.0. `mcp` 2.x renamed `FastMCP` to `MCPServer` and
removed `mcp.server.fastmcp`, which the server imports; without the pin `uvx` resolves 2.x and the
server exits at import with `ModuleNotFoundError: No module named 'mcp.server.fastmcp'`.

Claude Code: `claude mcp add rhino -- uvx --with 'mcp[cli]<2' rhinomcp-mod`. Running from source,
the start order, verification and troubleshooting: [`docs/guide/01-setup.md`](docs/guide/01-setup.md).

## Guide

Every feature as an MCP tool and as a Rhino command, with what comes back and what to look at:
[`docs/guide/`](docs/guide/README.md).

| area | page |
| --- | --- |
| overview: feature map, the stability pipeline, the two routes, what is stored on the document | [00](docs/guide/00-overview.md) |
| setup and session tools | [01](docs/guide/01-setup.md) |
| scene inspection: the inventory, paging, a region, per-object geometry, selection | [02](docs/guide/02-scene-inspection.md) |
| geometry, layers, materials | [03](docs/guide/03-geometry-layers-materials.md) |
| pose and transforms: rotate, reset, rebase, the OBB overlay | [04](docs/guide/04-pose-transforms.md) |
| views and capture: framing, cameras, display modes, size, named views | [05](docs/guide/05-views-capture.md) |
| connectivity graph: measured bearings, the overlay, the cache | [06](docs/guide/06-connectivity-graph.md) |
| mass and joint types: contact, pin, fixed; rules; capacity | [07](docs/guide/07-mass-joint-types.md) |
| stability evaluation: modes, parameters, the command's prompts, limitations | [08](docs/guide/08-stability.md) |
| reading results and paging the report | [09](docs/guide/09-reading-results.md) |
| worked example: the two timber bridges | [10](docs/guide/10-worked-example-timber-bridges.md) |
| reference: every tool and command | [11](docs/guide/11-reference.md) |

## Assembly stability

An assembly is evaluated as separate rigid bodies resting on one another, joined where the
geometry says they touch, under gravity. The question it answers is whether the thing stands
up: whether it is a mechanism, whether an element rotates off its support, whether a stack
topples. Each joint is built over the bearing surface measured between two elements and typed
`contact`, `pin` or `fixed` by rules stated per layer pair, element pair or element; the
result carries the verdict, how far it moved, the force at every joint, and - with the sway
probe - how stiff it is in each direction.

Requires Rhino 8 with Grasshopper/Kangaroo: the plugin loads Rhino's installed
`KangarooSolver.dll` at runtime (override with `KangarooSolverPath` or
`RHINOMCP_KANGAROO_PATH`); the Yak package ships no private copy.

Demo models with answers known independently of the solver:
[`RhinoAndGHFiles/`](RhinoAndGHFiles/README.md). Limitations: [guide 08](docs/guide/08-stability.md#limitations).
None of this makes the result a certified structural analysis.

## Development notes

How the stability evaluator got its numbers - the state of each mode, the defects found
along the way, and the plans they came from - is in [`docs/dev/`](docs/dev/README.md).

## Credits

- Original project and concept: [Jingcheng Chen](https://github.com/jingcheng-chen/rhinomcp)
