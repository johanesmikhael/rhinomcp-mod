from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict, List


@mcp.tool()
def rotate_object(
    ctx: Context,
    id: str = None,
    name: str = None,
    rotation_matrix: List[List[float]] = None,
    invert_rotation_matrix: bool = False,
    pivot: List[float] = None
) -> Dict[str, Any]:
    """
    Rotate an existing object in the Rhino document.

    Parameters:
    - id: The id of the object to rotate
    - name: The name of the object to rotate
    - rotation_matrix: 3x3 rotation matrix, about world axes
    - invert_rotation_matrix: Optional boolean.
      If true, applies inverse(rotation_matrix) (transpose for proper rotation matrices).
      Primary use: align orientation back to world axes. If rotation_matrix is the
      object's current pose rotation R, this applies R^-1.
    - pivot: [x, y, z] pivot point in world coordinates
    """
    try:
        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if id is not None:
            params["id"] = id
        if name is not None:
            params["name"] = name
        if rotation_matrix is None:
            return {"error": "rotation_matrix is required"}
        if pivot is None:
            return {"error": "pivot is required"}
        params["rotation_matrix"] = rotation_matrix
        if invert_rotation_matrix:
            params["invert_rotation_matrix"] = invert_rotation_matrix
        params["pivot"] = pivot

        return rhino.send_command("rotate_object", params)
    except Exception as e:
        logger.error(f"Error rotating object: {str(e)}")
        return {"error": str(e)}
