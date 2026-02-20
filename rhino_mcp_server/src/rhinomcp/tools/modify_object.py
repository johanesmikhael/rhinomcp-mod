from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, List, Dict


@mcp.tool()
def modify_object(
    ctx: Context,
    id: str = None,
    name: str = None,
    new_name: str = None,
    new_color: List[int] = None,
    layer: str = None,
    translation: List[float] = None,
    rotation_matrix: List[List[float]] = None,
    invert_rotation_matrix: bool = False,
    scale: List[float] = None,
    visible: bool = None
) -> Dict[str, Any]:
    """
    Modify an existing object in the Rhino document.
    
    Parameters:
    - id: The id of the object to modify
    - name: The name of the object to modify
    - new_name: Optional new name for the object
    - new_color: Optional [r, g, b] color values (0-255) for the object
    - layer: Optional layer name or guid to move the object to
    - translation: Optional [x, y, z] translation vector
    - rotation_matrix: Optional 3x3 rotation matrix (world axes, pivot at bbox center)
    - invert_rotation_matrix: Optional boolean.
      If true, applies inverse(rotation_matrix) (transpose for proper rotation matrices).
      Primary use: align orientation back to world axes. If rotation_matrix is the
      object's current pose rotation R, this applies R^-1.
    - scale: Optional [x, y, z] scale factors (world axes, pivot at bbox center)
    - visible: Optional boolean to set visibility
    """
    try:
        # Get the global connection
        rhino = get_rhino_connection()
        
        params : Dict[str, Any] = {}
        
        if id is not None:
            params["id"] = id
        if name is not None:
            params["name"] = name
        if new_name is not None:
            params["new_name"] = new_name
        if new_color is not None:
            params["new_color"] = new_color
        if layer is not None:
            params["layer"] = layer
        if translation is not None:
            params["translation"] = translation
        if rotation_matrix is not None:
            params["rotation_matrix"] = rotation_matrix
        if invert_rotation_matrix:
            params["invert_rotation_matrix"] = invert_rotation_matrix
        if scale is not None:
            params["scale"] = scale
        if visible is not None:
            params["visible"] = visible

        return rhino.send_command("modify_object", params)
    except Exception as e:
        logger.error(f"Error modifying object: {str(e)}")
        return {"error": str(e)}
