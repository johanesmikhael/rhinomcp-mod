from mcp.server.fastmcp import Context
from rhinomcp import get_rhino_connection, mcp, logger
from typing import Dict, Any

@mcp.tool()
def get_object_info(
    ctx: Context,
    id: str = None,
    name: str = None,
    geometry_detail: str = "obb_pose",
    include_world: bool = False,
) -> Dict[str, Any]:
    """
    Get detailed information about a specific object in the Rhino document.
    The information contains the object's id, name, type, and geometry info.
    You can either provide the id or the object_name of the object to get information about.
    If both are provided, the id will be used.

    Returns:
    - A dictionary containing the object's information
    - The dictionary will have the following keys:
        - "id": The id of the object
        - "name": The name of the object
        - "type": The type of the object
        - "layer": The layer of the object
        - "material": The material of the object
        - "color": The color of the object
        - "geometry": The geometry info of the object (includes summary for Brep/Extrusion when available)
        - geometry_detail="bbox":
            - "geometry.bbox": world axis-aligned bounding box
            - "geometry.bbox_frame": "world_aabb"
        - geometry_detail="obb_pose" (default):
            - Lines: local endpoints + pose; world endpoints only when include_world=True
            - Curves/Polylines: local points + pose; world points only when include_world=True
            - Breps/Extrusions/Meshes: OBB extents + pose; world corners only when include_world=True
    
    Parameters:
    - id: The id of the object to get information about
    - name: The name of the object to get information about
    - geometry_detail: "bbox" for world AABB only, "obb_pose" for pose-aware detailed geometry.
    - include_world: Include world-space duplicates such as world points and world corners.
    """
    try:
        rhino = get_rhino_connection()
        return rhino.send_command(
            "get_object_info",
            {
                "id": id,
                "name": name,
                "geometry_detail": geometry_detail,
                "include_world": include_world,
            }
        )

    except Exception as e:
        logger.error(f"Error getting object info from Rhino: {str(e)}")
        return {
            "error": str(e)
        }
