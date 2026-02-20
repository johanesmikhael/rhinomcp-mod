from mcp.server.fastmcp import Context
import json
from rhinomcp import get_rhino_connection, mcp, logger
from typing import Dict, Any

@mcp.tool()
def get_object_info(ctx: Context, id: str = None, name: str = None) -> Dict[str, Any]:
    """
    Get detailed information about a specific object in the Rhino document.
    The information contains the object's id, name, type, all custom user attributes and geometry info.
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
        - "attributes": A dictionary containing all custom user attributes of the object
        - Line geometry:
            - "world_start"/"world_end": endpoints in world XYZ
            - "local_start"/"local_end": endpoints in local frame (origin at midpoint)
            - "pose.world_from_local": line frame (R, t) with origin at midpoint
        - Polyline geometry:
            - "planar": bool
            - "world_points": sampled points in world XYZ
            - "local_points": points in local frame
            - "pose.world_from_local": working plane frame
        - Curve geometry:
            - "planar": bool
            - "world_points": sampled points in world XYZ
            - "local_points": points in local frame
            - "pose.world_from_local": working plane frame
        - Brep geometry:
            - "obb": Oriented bounding box info: "extents" and "world_corners"
            - "surface_edges_local"/"surface_edges_world" (single face)
            - "proj_outline_local_xy"/"proj_outline_world" (multi face)
            - "pose.world_from_local": working plane frame
        - Extrusion geometry:
            - "obb": Oriented bounding box info
            - "pose.world_from_local": working plane frame
    
    Parameters:
    - id: The id of the object to get information about
    - name: The name of the object to get information about
    """
    try:
        rhino = get_rhino_connection()
        return rhino.send_command("get_object_info", {"id": id, "name": name})

    except Exception as e:
        logger.error(f"Error getting object info from Rhino: {str(e)}")
        return {
            "error": str(e)
        }
