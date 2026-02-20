from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, List, Dict


@mcp.tool()
def create_objects(
    ctx: Context,
    objects: List[Dict[str, Any]]
) -> str:
    """
    Create multiple objects at once in the Rhino document.
    
    Parameters:
    - objects: List[ObjectCreateSpec]
      ObjectCreateSpec schema:
      - type: required string
      - params: required dictionary
      - name: optional string (recommended for readable output keys)
      - color: optional [r, g, b]
      - translation: optional [x, y, z]
      - rotation_matrix: optional 3x3 matrix
      - scale: optional [x, y, z]

    Each object should have the following values:
    - type: Object type ("POINT", "LINE", "POLYLINE", "BOX", "SPHERE", etc.)
    - name: Optional name for the object
    - color: Optional [r, g, b] color values (0-255) for the object
    - params: Type-specific parameters dictionary (see documentation for each type in create_object() function)
    - translation: Optional [x, y, z] translation vector
    - rotation: Optional [x, y, z] rotation in radians
    - scale: Optional [x, y, z] scale factors

    Returns:
    A message indicating the created objects.
    
    Examples of params:
    [
        {
            "type": "POINT",
            "name": "Point 1",
            "params": {"x": 0, "y": 0, "z": 0}
        },
        {
            "type": "LINE",
            "name": "Line 1",
            "params": {"start": [0, 0, 0], "end": [1, 1, 1]}
        },
        {
            "type": "POLYLINE",
            "name": "Polyline 1",
            "params": {"points": [[0, 0, 0], [1, 1, 1], [2, 2, 2]]}
        },
        {
            "type": "CURVE",
            "name": "Curve 1",
            "params": {"points": [[0, 0, 0], [1, 1, 1], [2, 2, 2]], "degree": 3}
        },
        {
            "type": "BOX",
            "name": "Box 1",
            "color": [255, 0, 0],
            "params": {"width": 1.0, "length": 1.0, "height": 1.0},
            "translation": [0, 0, 0],
            "rotation": [0, 0, 0],
            "scale": [1, 1, 1]
        },
        {
            "type": "SPHERE",
            "name": "Sphere 1",
            "color": [0, 255, 0],
            "params": {"radius": 1.0},
            "translation": [0, 0, 0],
            "rotation": [0, 0, 0],
            "scale": [1, 1, 1]
        }
    ]
    """
    try:
        if not objects:
            return "Error creating objects: objects must be a non-empty list"

        # Get the global connection
        rhino = get_rhino_connection()
        command_params = {}
        for index, obj in enumerate(objects):
            if not isinstance(obj, dict):
                return f"Error creating objects: objects[{index}] must be a dictionary"
            if "type" not in obj:
                return f"Error creating objects: objects[{index}].type is required"
            if "params" not in obj or not isinstance(obj["params"], dict):
                return f"Error creating objects: objects[{index}].params must be a dictionary"

            key = str(obj.get("name", f"object_{index}"))
            command_params[key] = obj
        result = rhino.send_command("create_objects", command_params)
  
        
        return f"Created {len(result)} objects"
    except Exception as e:
        logger.error(f"Error creating object: {str(e)}")
        return f"Error creating object: {str(e)}"
