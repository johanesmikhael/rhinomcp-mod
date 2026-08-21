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


## 0.3.0-beta.1: Experimental Assembly Stability

This prerelease introduces an assembly stability workflow with three evaluation modes. They answer different questions, and none subsumes the others - run more than one.

| Mode | Bodies | Joints | Answers |
| --- | --- | --- | --- |
| `welded` (default) | the whole scope as one rigid body | none; graph edges are not simulated | does the assembly as a whole settle, slide or tip under gravity? |
| `multi_body_contact` | one per element | bearing surfaces carrying compression and no tension, with friction | can an element rotate off its support, lift, or slide? |
| `multi_body_pinned` | one per element | the graph's contact points, shared and so bilateral | is the assembly a mechanism? |

Welded is an upper bound: it silently supplies every moment connection the real assembly lacks, so it passes structures that a dry stack would not hold. Contact is the closest of the three to dry-stacked masonry and the only one that can fail a single element rather than the whole scope. Pinned holds in tension, so it cannot see an element toppling off another.

Read the limitations at the end of this section before trusting any verdict.

Stability evaluation requires Rhino 8 with Grasshopper/Kangaroo installed. The plugin loads Rhino's installed `KangarooSolver.dll` at runtime. Developers can override its build/runtime location with `KangarooSolverPath` and `RHINOMCP_KANGAROO_PATH`, respectively; the Yak package does not ship a private Kangaroo copy.

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

#### Option C: Assign Mass over MCP

The `assign_mass` tool assigns mass without prompting, which is what makes the workflow scriptable - the Rhino commands above stop per object or per layer and cannot be driven from an MCP client.

Scope by `ids`, `names`, `layer`, or `selected`; omit every scope argument to take the whole document. Then either give a `density` in kg/m^3, and each object's mass follows from its own closed volume, or give one `mass` in kg applied to every object in scope. Objects with no computable volume are reported under `skipped` with the reason rather than guessed at.

Unit handling is important:

- Metric documents accept mass in `kg` and density in `kg/m³`.
- Imperial documents accept pound-mass (`lbm`, never pound-force) and density in `lbm/ft³`.
- New object mass is converted immediately and stored as tagged canonical `kg`; layer density retains its explicit input-unit tag. Untagged legacy values remain readable with a warning. To preserve the earlier behavior, only legacy feet documents infer untagged values as imperial; other legacy documents infer metric values.
- Documents with `None`, `Unset`, or custom units cannot be normalized reliably and are rejected by stability and density-derived mass evaluation.

### 3. Evaluate Assembly Stability

Run:

```text
mcpmodevaluatestability
```

The command first asks for the evaluation mode - `Welded`, `Contact`, or `PinnedJoints` - and then runs the corresponding solver over the selected scope. Solver geometry, floor elevation, tolerances, and mass are normalized internally to meters and kilograms, and gravity defaults to standard gravity (`9.80665 m/s²`). The returned displacement, transform, floor elevation, and explicit length parameters remain in the active Rhino document's units.

When omitted, the stability threshold (`0.01 m`), solver threshold (`0.001 m`), and particle-assignment tolerance (`0.000001 m`) are converted into document units at runtime. Rigid and floor strengths remain Kangaroo tuning weights. Invalid graph nodes, missing or non-positive mass, unsupported or non-finite units/values, and invalid iteration counts fail explicitly rather than being classified as instability.

Choose a stability threshold that is appropriate for the model's scale and units. The assembly is classified as stable when its normalized maximum displacement does not exceed the normalized threshold. Results expose displacement in both document units and meters.

The MCP `evaluate_stability` tool exposes the same solver parameters through `mode`, allowing an AI client such as Claude to adjust them for a particular case.

#### Multi-body parameters

The multi-body modes report per-element displacement and rotation, name the element that moved furthest, and carry no assembly transform or support margin - there is no single transform to report. Contact mode also reports each bearing surface: how many of its springs carry load, the compression across it, its corners, and where that compression acts.

Contact stiffness is derived from the load each bearing surface carries and needs no tuning. An absolute stiffness is not a material property in this solver but the size of the pseudo-time step: Kangaroo blends goals by weight, so pinning the stiffness ties the rate of collapse to the model's mass and bearing area, and the same structure can read as stable or as toppling depending on the number chosen. The knobs are therefore stated as lengths:

- `joint_penetration` - how far a bearing surface may close under its own load. This sets the per-step motion directly.
- `ground_settlement` - how far a body may settle into the ground under its own load. Separate from the joints because the ground is a soil and the joints are not.
- `contact_strength` and `floor_strength` - pin the joints or the ground to an absolute modulus instead, for study rather than for use.
- `torque_gain` - how much of a patch's eccentric compression becomes rotation of the bodies it joins.

### Limitations

These are measured against hand-computed statics, not estimated:

- **Contact mode absorbs marginal eccentricity.** A joint whose resultant falls well outside its bearing surface topples correctly; one only marginally outside settles into a tilted equilibrium and reads as stable. On three-block stairs, 112 mm of eccentricity past the patch edge topples and 75 mm does not.
- **Pinned mode makes almost everything a mechanism.** Each graph edge merges into one shared particle, and a body pinned at a single point is free to rotate about it. Kangaroo offers no middle ground - one shared point rotates freely, two give a hinge, three or more weld - so with one contact point per edge, a dry stack comes apart regardless of its geometry. Treat an unstable pinned verdict as a statement about the joint model, not about the structure.
- **Joints cannot be typed individually.** A mode applies one joint model to every edge in the scope, so a structure mixing bolted, pinned and dry-bearing connections cannot be expressed.
- **Self-weight only.** No lateral load, no strength or crushing limit, so a design can pass on stability while its bearing stress is absurd.
- **A body that leaves its support falls through the ground.** Ground bearing is built only for points that start at floor level. The verdict is unaffected; the trajectory afterwards is meaningless.
- **Contact patches are computed once, from the initial pose,** as the overlap of the two elements' bounding boxes. This is exact for axis-aligned boxes and an over-estimate of the bearing area for rotated ones.

This normalization makes equivalent metric and imperial models comparable. It does not turn Kangaroo's dynamic-relaxation result into an engineering-certified structural analysis.

### 4. Display the Evaluated Result

Run the following command and choose `On` or `Off` to control the evaluated-geometry display:

```text
mcpmodstabilitydisplay
```

The display visualizes the geometry cached from the latest stability evaluation, in every mode. It does not modify the original Rhino objects.

## Credits

- Original project and concept: [Jingcheng Chen](https://github.com/jingcheng-chen/rhinomcp)
