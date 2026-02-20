from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict, List


@mcp.tool()
def copy_object(
    ctx: Context,
    id: str = None,
    translation: List[float] = None
) -> str:
    """
    Copy an existing object in the Rhino document.

    Parameters:
    - id: The id of the object to copy
    - translation: Optional [x, y, z] translation vector for the copy
    """
    try:
        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if id is not None:
            params["id"] = id
        if translation is not None:
            params["translation"] = translation

        result = rhino.send_command("copy_object", params)
        return f"Copied object: {result['name']}"
    except Exception as e:
        logger.error(f"Error copying object: {str(e)}")
        return f"Error copying object: {str(e)}"
