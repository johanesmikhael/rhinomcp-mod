from mcp.server.fastmcp import Context
import json
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, List, Dict

@mcp.tool()
def create_object(
    ctx: Context,
    type: str = "BOX",
    name: str = None,
    color: List[int]= None,
    params: Dict[str, Any] = {},
    translation: List[float]= None,
    rotation: List[float]= None,
    rotation_matrix: List[List[float]] = None,
    scale: List[float]= None,
) -> str:
    """
    Create a new object in the Rhino document.
    
    Parameters:
    - type: Object type ("POINT", "LINE", "POLYLINE", "CIRCLE", "ARC", "ELLIPSE", "CURVE", "BOX", "SPHERE", "CONE", "CYLINDER", "PIPE", "SURFACE")
    - name: Optional name for the object
    - color: Optional [r, g, b] color values (0-255) for the object
    - params: Type-specific parameters dictionary (see documentation for each type)
    - translation: Optional [x, y, z] translation vector
    - rotation_matrix: Optional 3x3 rotation matrix (world axes, pivot at bbox center)
    - scale: Optional [x, y, z] scale factors (world axes, pivot at bbox center)

    The params dictionary is type-specific.
    For POINT, the params dictionary should contain the following keys:
    - x: x coordinate of the point
    - y: y coordinate of the point
    - z: z coordinate of the point

    For LINE, the params dictionary should contain the following keys:
    - start: [x, y, z] start point of the line
    - end: [x, y, z] end point of the line

    For POLYLINE, the params dictionary should contain the following keys:
    - points: List of [x, y, z] points that define the polyline

    For CIRCLE, the params dictionary should contain the following keys:
    - center: [x, y, z] center point of the circle
    - radius: Radius of the circle

    For ARC, the params dictionary should contain the following keys:
    - center: [x, y, z] center point of the arc
    - radius: Radius of the arc
    - angle: Angle of the arc in degrees

    For ELLIPSE, the params dictionary should contain the following keys:
    - center: [x, y, z] center point of the ellipse
    - radius_x: Radius of the ellipse along X axis
    - radius_y: Radius of the ellipse along Y axis

    For CURVE, the params dictionary should contain the following keys:
    - points: List of [x, y, z] control points that define the curve
    - degree: Degree of the curve (default is 3, if user asked for smoother curve, degree can be higher)
    If the curve is closed, the first and last points should be the same.

    For BOX, the params dictionary should contain the following keys:
    - width: Width of the box along X axis of the object
    - length: Length of the box along Y axis of the object
    - height: Height of the box along Z axis of the object

    For SPHERE, the params dictionary should contain the following key:
    - radius: Radius of the sphere

    For CONE, the params dictionary should contain the following keys:
    - radius: Radius of the cone
    - height: Height of the cone
    - cap: Boolean to indicate if the cone should be capped at the base, default is True
    - axis: Optional axis of the cone ("x", "y", or "z"), default is "z"

    For CYLINDER, the params dictionary should contain the following keys:
    - radius: Radius of the cylinder
    - height: Height of the cylinder
    - cap: Boolean to indicate if the cylinder should be capped at the base, default is True
    - axis: Optional axis of the cylinder ("x", "y", or "z"), default is "z"

    Centering convention:
    - BOX, SPHERE, CONE, and CYLINDER are created centered at the origin by default.
    - Use translation/rotation/scale to place and orient them as needed.

    For SURFACE, the params dictionary should contain the following keys:
    - count : ([number, number]) Number of points in u and v directions
    - points: List of [x, y, z] points defining a rectangular grid
      with exactly count[0] * count[1] points
    - degree: ([number, number], optional) Surface degree in u and v
      (defaults to [3, 3], clamped to valid Rhino limits)
    - closed: ([bool, bool], optional) Closed flags in u and v (default [False, False])
    
    Returns:
    A message indicating the created object name.
    
    Examples of params:
    - POINT: {"x": 0, "y": 0, "z": 0}
    - LINE: {"start": [0, 0, 0], "end": [1, 1, 1]}
    - POLYLINE: {"points": [[0, 0, 0], [1, 1, 1], [2, 2, 2]]}
    - CIRCLE: {"center": [0, 0, 0], "radius": 1.0}
    - CURVE: {"points": [[0, 0, 0], [1, 1, 1], [2, 2, 2]], "degree": 3}
    - BOX: {"width": 1.0, "length": 1.0, "height": 1.0}
    - SPHERE: {"radius": 1.0}
    - CONE: {"radius": 1.0, "height": 1.0, "cap": True, "axis": "z"}
    - CYLINDER: {"radius": 1.0, "height": 1.0, "cap": True, "axis": "z"}
    - SURFACE: {"count": (3, 3), "points": [[0, 0, 0], [1, 0, 0], [2, 0, 0], [0, 1, 0], [1, 1, 0], [2, 1, 0], [0, 2, 0], [1, 2, 0], [2, 2, 0]], "degree": (3, 3), "closed": (False, False)}
    """
    try:
        # Get the global connection
        rhino = get_rhino_connection()

        command_params = {
            "type": type,
            "params": params
        }

        if translation is not None: command_params["translation"] = translation
        if rotation is not None and rotation_matrix is None:
            raise ValueError("rotation is deprecated; please provide rotation_matrix instead.")
        if rotation is not None: command_params["rotation"] = rotation
        if rotation_matrix is not None: command_params["rotation_matrix"] = rotation_matrix
        if scale is not None: command_params["scale"] = scale

        if name: command_params["name"] = name
        if color: command_params["color"] = color

        # Create the object
        result = result = rhino.send_command("create_object", command_params)  
        
        return f"Created {type} object: {result['name']}"
    except Exception as e:
        logger.error(f"Error creating object: {str(e)}")
        return f"Error creating object: {str(e)}"
 
