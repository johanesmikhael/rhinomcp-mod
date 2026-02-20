from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict, List


@mcp.tool()
def rotate_objects(
    ctx: Context,
    objects: List[Dict[str, Any]],
    all: bool = None
) -> Dict[str, Any]:
    """
    Rotate multiple objects at once in the Rhino document.

    Parameters:
    - objects: List[ObjectRotateSpec]
      ObjectRotateSpec schema:
      - id or name: required selector
      - rotation_matrix: required 3x3 matrix
      - pivot: required [x, y, z]
      - invert_rotation_matrix: optional bool
    - all: Optional boolean to rotate all objects; if true, only one object is required in the objects list

    Each object can have the following parameters:
    - id: The id of the object to rotate
    - name: The name of the object to rotate
    - rotation_matrix: 3x3 rotation matrix, about world axes
    - invert_rotation_matrix: Optional boolean. If true, applies inverse(rotation_matrix).
    - pivot: [x, y, z] pivot point in world coordinates

    Returns:
    A message indicating the rotated objects.
    """
    try:
        if (not objects) and not all:
            return {"error": "objects must be a non-empty list unless all=true"}

        rhino = get_rhino_connection()
        for index, entry in enumerate(objects or []):
            if not isinstance(entry, dict):
                return {"error": f"objects[{index}] must be a dictionary"}
            if "id" not in entry and "name" not in entry:
                return {"error": f"objects[{index}] requires 'id' or 'name'"}
            if "rotation_matrix" not in entry:
                return {"error": f"objects[{index}].rotation_matrix is required"}
            if "pivot" not in entry:
                return {"error": f"objects[{index}].pivot is required"}
        command_params: Dict[str, Any] = {"objects": objects}
        if all:
            command_params["all"] = all
        return rhino.send_command("rotate_objects", command_params)
    except Exception as e:
        logger.error(f"Error rotating objects: {str(e)}")
        return {"error": str(e)}
