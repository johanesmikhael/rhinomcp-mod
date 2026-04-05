"""Extended Rhino MCP tools for selection, layers, and materials."""
from rhinomcp.server import mcp

@mcp.tool()
async def get_selected_objects() -> str:
    """Get information about currently selected objects in Rhino."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_selected_objects", {})
    count = result.get("count", 0)
    return f"Selected {count} object(s):\n" + "\n".join(
        f"  - {o['name']} ({o['type']}) on layer '{o['layer']}'" 
        for o in result.get("selected", [])
    )

@mcp.tool()
async def select_objects(ids: list[str] | None = None, names: list[str] | None = None, 
                         layer: str | None = None, type: str | None = None) -> str:
    """Select objects by ID, name, layer, or type."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {}
    if ids: params["ids"] = ids
    if names: params["names"] = names
    if layer: params["layer"] = layer
    if type: params["type"] = type
    result = rhino.send_command("select_objects_by_filter", params)
    return result.get("message", "Selection complete.")

@mcp.tool()
async def deselect_all() -> str:
    """Deselect all objects in the Rhino document."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    rhino.send_command("deselect_all", {})
    return "All objects deselected."

@mcp.tool()
async def zoom_to_objects(ids: list[str] | None = None) -> str:
    """Zoom viewport to selected objects (or currently selected if no IDs provided)."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {"ids": ids} if ids else {}
    result = rhino.send_command("zoom_to_objects", params)
    return result.get("message", "Zoom complete.")

@mcp.tool()
async def get_viewport_info() -> str:
    """Get information about all viewports in the Rhino document."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_viewport_info", {})
    vps = result.get("viewports", [])
    return f"Viewports ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {v['name']} at {v['cameraLocation']}" for v in vps
    )

@mcp.tool()
async def rename_layer(id: str, new_name: str) -> str:
    """Rename a layer by ID.
    
    Args:
        id: Layer GUID.
        new_name: New layer name.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("rename_layer", {"id": id, "new_name": new_name})
    return result.get("message", result.get("error", "Layer renamed."))

@mcp.tool()
async def move_objects_to_layer(ids: list[str], layer: str) -> str:
    """Move objects to a specific layer by name.
    
    Args:
        ids: List of object GUIDs to move.
        layer: Target layer name.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("move_objects_to_layer", {"ids": ids, "layer": layer})
    return result.get("message", result.get("error", "Move complete."))

@mcp.tool()
async def get_layer_states() -> str:
    """Get the current state (visible/locked/color) of all layers."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_layer_states", {})
    layers = result.get("layers", [])
    return f"Layers ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {l['name']} {'🔒' if l['locked'] else ''} {'👁️' if l['visible'] else '🚫'} [{l['color']}]" 
        for l in layers
    )

@mcp.tool()
async def save_layer_state(name: str) -> str:
    """Save the current layer visibility and lock state."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("save_layer_state", {"name": name})
    return result.get("message", result.get("error", "Layer state saved."))

@mcp.tool()
async def restore_layer_state(name: str) -> str:
    """Restore a previously saved layer state."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("restore_layer_state", {"name": name})
    return result.get("message", result.get("error", "Layer state restored."))

@mcp.tool()
async def get_materials() -> str:
    """Get all materials in the Rhino document."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_materials", {})
    mats = result.get("materials", [])
    return f"Materials ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {m['name']} [{m['diffuseColor']}]" for m in mats
    )

@mcp.tool()
async def create_material(name: str = "NewMaterial", r: int = 128, g: int = 128, b: int = 128) -> str:
    """Create a new material with diffuse color."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("create_material", {"name": name, "r": r, "g": g, "b": b})
    return result.get("message", result.get("error", "Material created."))

@mcp.tool()
async def set_object_material(ids: list[str], material_name: str | None = None, material_index: int | None = None) -> str:
    """Assign a material to objects."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {"ids": ids}
    if material_name: params["material_name"] = material_name
    if material_index is not None: params["material_index"] = material_index
    result = rhino.send_command("set_object_material", params)
    return result.get("message", result.get("error", "Material assigned."))

@mcp.tool()
async def get_object_materials(ids: list[str] | None = None) -> str:
    """Get materials assigned to objects. If no IDs, returns all objects."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {}
    if ids: params["ids"] = ids
    result = rhino.send_command("get_object_materials", params)
    objs = result.get("objects", [])
    return f"Object materials ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {o['name']} -> {o['material_name']}" for o in objs
    )
