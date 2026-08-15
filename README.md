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

### 2. Added Topological Context

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

### 3. Pose-Aware and Batch Transform Workflows

This mod adds stronger pose operations for reliable editing pipelines:

- Single + batch tools for modify/rotate/copy operations
- Pose rebasing without moving geometry: `rebase_object_pose`, `rebase_objects_pose`
- Pose reset controls: `reset_object_pose`, `reset_objects_pose`
- Rotation helpers such as `invert_rotation_matrix`
- Geometry-detected poses are recomputed after transforms so OBBs remain minimal; explicitly
  rebased poses are tagged separately and remain attached to the object through later transforms.


## Basic Installation

### 1. Install RhinoMCP Mod Plugin (Mac and Windows)

1. Open Rhino.
2. Go to `Tools > Package Manager`.
3. Search for `rhinomcp-mod`.
4. Click `Install`.
5. In Rhino command line, run `mcpmodstart`.

### 2. Install uv

#### macOS

```bash
brew install uv
```

#### Windows (PowerShell)

```powershell
powershell -c "irm https://astral.sh/uv/install.ps1 | iex"
```

### 3. Configure Claude Desktop MCP

Use this config in your Claude Desktop MCP config file:

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

#### macOS Claude config path

`~/Library/Application Support/Claude/claude_desktop_config.json`

#### Windows Claude config path

`%APPDATA%\\Claude\\claude_desktop_config.json`

After saving config, restart Claude Desktop.

## Development Setup (Local Source)

For active development, use a separate MCP entry (for example `rhino-dev`) so it does not conflict with the published `uvx rhinomcp-mod` setup.

### 1. Run the MCP server from local source

Use `uv run` from your local `rhino_mcp_server` folder:

```json
{
  "mcpServers": {
    "rhino-dev": {
      "command": "uv",
      "args": [
        "--directory",
        "/absolute/path/to/rhinomcp_mod/rhino_mcp_server",
        "run",
        "rhinomcp-mod"
      ]
    }
  }
}
```

Replace `/absolute/path/to/rhinomcp_mod` with your local checkout path.

### 2. Build and load local plugin

1. Build `rhino_mcp_plugin/rhinomcp.sln` in `Debug` or `Release`.
2. Load the generated `.rhp` from `rhino_mcp_plugin/bin/<Configuration>/net7.0/` in Rhino.
3. Run `mcpmodstart`.

### 3. Enable only one server entry

Keep only one server enabled at a time (`rhino` or `rhino-dev`) to avoid duplicate connections.

### 4. Start and Verify

1. Start Rhino and run `mcpmodstart`.
2. Optional: run `mcpmodobb` to toggle OBB + projection profile visualization.
3. Optional: run `mcpmodgraph` to toggle connectivity graph display.
4. Optional: run `mcpmodgraphexport` to save the computed connectivity graph as JSON.
5. Optional: run `mcpmodclearcache` to clear cached pose/OBB user strings.
6. Open Claude Desktop.
7. Confirm Rhino tools appear in Claude (hammer/tools icon).


## 20260815 Update

This release introduces an initial assembly stability workflow. All objects represented by the connectivity graph are combined into a single rigid body and evaluated as one assembly. This provides a simple whole-assembly stability test; it does not yet simulate relative movement between individual parts.

### 1. Identify and Maintain the Connectivity Graph

Run the following Rhino command to enable connectivity-graph mode:

```text
mcpmodgraph
```

While this mode is on, supported Rhino object changes—including adding, copying, deleting, restoring, replacing, transforming, and changing object attributes—automatically invalidate and rebuild the graph. The latest graph is stored in the Rhino document under `rhinomcp-mod:connectivity-graph`


This automatic document update was missing in earlier builds and has now been fixed. Turning `mcpmodgraph` off stops automatic graph rebuilding and persistence until the mode is enabled again.

### 2. Assign Mass for Stability Evaluation

The mass-assignment workflow iterates over the nodes stored in `rhinomcp-mod:connectivity-graph`. Mass can be assigned in either of the following ways.

#### Option A: Assign Mass Directly

Run:

```text
mcpmodassignmass
```

This command iterates over every graph node and prompts for the mass of its corresponding Rhino object.

To assign mass only to nodes that do not already have a positive mass, run:

```text
mcpmodassignmissingmass
```

#### Option B: Calculate Mass from Layer Density

First, assign a material density to each relevant layer:

```text
mcpmodassignlayerdensity
```

Then place each object on the appropriate material layer and run:

```text
mcpmodmassfromlayerdensity
```

The command calculates each object's volume and derives its mass from the density stored on its layer.

Unit handling is important:

- For a Rhino model in feet, density is entered in `lb/ft³`, volume is evaluated in `ft³`, and mass is stored in `lb`.
- For other Rhino model units, density is entered in `kg/m³`. The calculated model-space volume—for example, `mm³` in a millimetre model—is converted to `m³` before mass is calculated in `kg`.

### 3. Evaluate Assembly Stability

Run:

```text
mcpmodevalutatestablity
```

The command combines the graph assembly into one rigid body and runs the stability solver. Parameters such as rigid-body strength, floor strength, stability threshold, solver threshold, and solver iterations may need to be adjusted for each model.

Choose a stability threshold that is appropriate for the model's scale and units. The assembly is classified as stable when its maximum simulated displacement does not exceed this threshold.

The MCP `evaluate_stability` tool exposes the same solver parameters, allowing an AI client such as Claude to adjust them for a particular case.

### 4. Display the Evaluated Result

Run the following command and choose `On` or `Off` to control the evaluated-geometry display:

```text
mcpmodstablilitydisplay
```

The display visualizes the geometry cached from the latest stability evaluation. It does not modify the original Rhino objects.

## Credits

- Original project and concept: [Jingcheng Chen](https://github.com/jingcheng-chen/rhinomcp)
