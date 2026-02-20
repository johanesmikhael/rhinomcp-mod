from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, List, Dict


@mcp.tool()
def modify_objects(
    ctx: Context,
    objects: List[Dict[str, Any]],
    all: bool = None
) -> Dict[str, Any]:
    """
    Modify multiple objects at once in the Rhino document.
    
    Parameters:
    - objects: List[ObjectModifySpec]
      ObjectModifySpec schema:
      - id or name: required selector
      - new_name: optional string
      - new_color: optional [r, g, b]
      - layer: optional string
      - translation: optional [x, y, z]
      - rotation_matrix: optional 3x3 matrix
      - invert_rotation_matrix: optional bool
      - scale: optional [x, y, z]
      - visible: optional bool
    - all: Optional boolean to modify all objects, if true, only one object is required in the objects dictionary

    Each object can have the following parameters:
    - id: The id of the object to modify
    - new_color: Optional [r, g, b] color values (0-255) for the object
    - translation: Optional [x, y, z] translation vector
    - rotation_matrix: Optional 3x3 rotation matrix (world axes, pivot at bbox center)
    - invert_rotation_matrix: Optional boolean.
      If true, applies inverse(rotation_matrix) (transpose for proper rotation matrices).
      Primary use: align orientation back to world axes. If rotation_matrix is each
      object's current pose rotation R, this applies R^-1.
    - scale: Optional [x, y, z] scale factors
    - visible: Optional boolean to set visibility

    Returns:
    A message indicating the modified objects.
    """
    try:
        if (not objects) and not all:
            return {"error": "objects must be a non-empty list unless all=true"}

        if objects:
            for index, entry in enumerate(objects):
                if not isinstance(entry, dict):
                    return {"error": f"objects[{index}] must be a dictionary"}
                if "id" not in entry and "name" not in entry:
                    return {"error": f"objects[{index}] requires 'id' or 'name'"}

        # Get the global connection
        rhino = get_rhino_connection()
        command_params = {}
        command_params["objects"] = objects
        if all:
            command_params["all"] = all
        return rhino.send_command("modify_objects", command_params)
    except Exception as e:
        logger.error(f"Error modifying objects: {str(e)}")
        return {"error": str(e)}
