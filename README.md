# RhinoMCP Mod

RhinoMCP Mod is a derivative work based on the original [RhinoMCP](https://github.com/jingcheng-chen/rhinomcp).

## Let your AI agents see the geometry

<img src="images/screenshot.png" alt="Geometry and Topological context" width="400">


This repository extends Rhino MCP with deeper geometric and topological context for AI-assisted design in Rhino3D.

### 1. Improved Geometry Understanding

Compared to baseline object metadata, this mod exposes richer geometric semantics (via tools like `get_object_info` / `get_objects_info`):

- Compact scene inventory via `get_document_info(detail="inventory")`
- Local-first detailed geometry with optional world duplicates
- `pose.world_from_local` frames for lines, curves/polylines, breps, and extrusions
- Planarity-aware curve/polyline summaries
- OBB-oriented summaries for complex solids (brep/extrusion)
- Geometry details suitable for downstream reasoning

Rhino visualization command for this geometry cache:

- Rhino command: `mcpmodobb` (toggle OBB + projection profile display)
- Rhino command: `mcpmodclearcache` (clear cached pose/OBB user strings)

### 2. Viewport Capture and Views

The agent can look at the model rather than only read it.

- MCP tool: `capture_view` - frames the targets, captures a PNG, and returns the image with
  compact metadata

```python
capture_view(view="front", ids=[...], display_mode="Shaded")
capture_view(view="isometric", all_visible=True, resolution="high")
capture_view(camera_location=[x, y, z], camera_target=[x, y, z], lens_mm=50)
```

`view` takes `perspective`, `isometric`, `top`, `front` or `right`; targets are `ids`,
`selected` or `all_visible`, framed with `fit` and `padding`. `display_mode` is any Rhino
display mode name - `Shaded`, `Rendered`, `Wireframe`, `Technical`. `resolution` is `low`
(640x480), `medium` (960x720), `high` (1280x900) or `print` (2560x1800), with `width`/`height`
overrides up to 3840; overlay text and markers scale with the size. `background` is
`viewport`, `white` or `transparent`. `camera_location`, `camera_target`, `camera_up` and
`lens_mm` place the camera explicitly; `draw_grid` and `draw_axes` are off by default.
`preserve_view` defaults to true, restoring the camera, projection, lens, frustum and display
mode afterwards, so a capture does not disturb what is on screen.

Views and camera state:

- MCP tool: `get_viewport_info` - camera, projection, lens, display mode and active state for
  every viewport
- MCP tool: `zoom_to_objects` - zoom to given or currently selected objects
- MCP tool: `save_named_view`, `restore_named_view`, `get_named_views`, `delete_named_view` -
  named views stored in the document

### 3. Added Topological Context

This mod adds a connectivity graph pipeline:

- MCP tool: `get_connectivity_graph`
- Rhino command: `mcpmodgraph`
- Rhino command: `mcpmodgraphexport` (write the computed graph to a JSON file)

The graph returns compact node/edge topology (including representative contact points), so AI can reason about adjacency/connectivity instead of isolated objects.

The computed graph is cached in document user text under `rhinomcp-mod:connectivity-graph`, so it survives save/reopen. A fingerprint of the graph-relevant document state (candidate object ids + quantized bounding boxes + tolerance) is stored with it; the stored graph is reused only while that fingerprint matches, otherwise it is recomputed and rewritten. `get_connectivity_graph` reports which path was taken in `source` (`document_text_cache` or `computed`). `mcpmodclearcache` (without `SelectedOnly`) removes the stored graph.

### Scene Inspection Contract

Use `get_document_info` as the first scene-index call. Its default is intentionally compact:

```text
get_document_info(detail="inventory", limit=100, offset=0, include_bbox=true)
```

For focused edits, scope the inventory to a world axis-aligned bounding box:

```text
get_document_info(detail="inventory", bbox=[[0,0,0],[100,100,30]], bbox_mode="intersects")
```

Supported `detail` values:

- `inventory`: id, name, type, layer, and optional world axis-aligned `bbox`.
- `summary`: inventory fields plus compact descriptors such as point counts, planarity, face counts, and material/color.
- `full`: legacy per-object document payload. Use sparingly; prefer `get_objects_info` for detailed geometry of selected targets.

Pagination and truncation fields:

- `object_count`: total object count in the document.
- `objects_returned`: number of objects in this response.
- `objects_truncated`: true when more objects are available.
- `objects_offset` / `objects_limit`: page position and page size.
- `spatial_filter`: present when `bbox` is supplied; includes normalized world AABB,
  `bbox_mode`, and matched object count.

For detailed geometry, first identify target ids/names from `inventory` or `summary`, then call `get_objects_info(objects=[...], geometry_detail="obb_pose")`.

- `geometry_detail="bbox"`: world AABB only, cheapest detailed lookup.
- `geometry_detail="obb_pose"`: default detailed mode. Curves/lines return local coordinates + pose; solids return OBB extents + pose.
- `include_world=true`: re-add world-space duplicates such as `world_points`, `world_start`/`world_end`, and `obb.world_corners`.

Use `max_geometry_points` with `detail="full"` only for legacy document payloads.

Viewport captures preserve the active Rhino view by default. `capture_view(..., preserve_view=true)`
temporarily applies the requested camera, projection, lens, fit, and display mode, captures the
bitmap, then restores the original viewport state. Set `preserve_view=false` only when the capture
is intentionally meant to become the new active view. Perspective/isometric presets use 50 mm
unless `lens_mm` is supplied explicitly; they never inherit a lens value from a parallel viewport.

### 4. Pose-Aware and Batch Transform Workflows

This mod adds stronger pose operations for reliable editing pipelines:

- Batch tools for modify/rotate/copy operations: `modify_objects`, `rotate_objects`, `copy_objects`
- Pose rebasing without moving geometry: `rebase_objects_pose`
- Pose reset: `reset_objects_pose`
- Rotation helpers such as `invert_rotation_matrix`
- Geometry-detected poses are recomputed after transforms so OBBs remain minimal; explicitly
  rebased poses are tagged separately and remain attached to the object through later transforms.


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
      "args": ["rhinomcp-mod"]
    }
  }
}
```

Claude Code: `claude mcp add rhino -- uvx rhinomcp-mod`. Running from source, the start
order, verification and troubleshooting: [`docs/guide/01-setup.md`](docs/guide/01-setup.md).

## Guide

Every feature as an MCP tool and as a Rhino command, with what comes back and what to look at:
[`docs/guide/`](docs/guide/README.md).

| area | page |
| --- | --- |
| overview: feature map, the stability pipeline, the two routes, what is stored on the document | [00](docs/guide/00-overview.md) |
| setup and session tools | [01](docs/guide/01-setup.md) |
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
