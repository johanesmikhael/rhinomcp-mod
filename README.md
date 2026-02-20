# RhinoMCP Mod

RhinoMCP Mod is a derivative work based on the original [RhinoMCP](https://github.com/jingcheng-chen/rhinomcp) project by Jingcheng Chen.

- Original project: `jingcheng-chen/rhinomcp`
- This repository adapts and extends the original implementation.

## Derivative Work Notice

This project includes derivative code and ideas from the original RhinoMCP repository. Please review attribution and license requirements in `LICENSE` and `NOTICE`.

## What Is Different In This Mod

This fork is focused on **better geometry understanding** and **topological context** for AI-driven design in Rhino.

### 1. Improved Geometry Understanding

Compared to baseline object metadata, this mod exposes richer geometric semantics (via tools like `get_object_info` / `get_objects_info`):

- Local and world representations for supported geometry
- `pose.world_from_local` frames for lines, curves/polylines, breps, and extrusions
- Planarity-aware curve/polyline summaries
- OBB-oriented summaries for complex solids (brep/extrusion)
- Geometry details suitable for downstream reasoning, not just display

### 2. Added Topological Context

This mod adds a connectivity graph pipeline:

- MCP tool: `get_connectivity_graph`
- Rhino command: `mcpmodgraph`

The graph returns compact node/edge topology (including representative contact points), so AI can reason about adjacency/connectivity instead of isolated objects.

### 3. Pose-Aware and Batch Transform Workflows

This mod adds stronger pose operations for reliable editing pipelines:

- Single + batch tools for modify/rotate/copy operations
- Pose rebasing without moving geometry: `rebase_object_pose`, `rebase_objects_pose`
- Pose reset controls: `reset_object_pose`, `reset_objects_pose`
- Rotation helpers such as `invert_rotation_matrix`


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

### 4. Start and Verify

1. Start Rhino and run `mcpmodstart`.
2. Optional: run `mcpmodgraph` to toggle connectivity graph display.
3. Open Claude Desktop.
4. Confirm Rhino tools appear in Claude (hammer/tools icon).

## Screenshots (To Add)

- Rhino Package Manager install screen (macOS) for `rhinomcp-mod`
- Rhino Package Manager install screen (Windows) for `rhinomcp-mod`
- `mcpmodstart` command in Rhino
- `mcpmodgraph` connectivity graph visualization
- Claude Desktop MCP config screen
- Claude tools enabled screen

## Credits

- Original project and concept: [Jingcheng Chen](https://github.com/jingcheng-chen)
- Upstream repository: [jingcheng-chen/rhinomcp](https://github.com/jingcheng-chen/rhinomcp)
