from mcp.server.fastmcp import Context
import json
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def open_file(
    ctx: Context,
    path: str,
    close_current: bool = False,
    save_current: bool = False,
) -> str:
    """
    Open a Rhino .3dm file.

    Parameters:
    - path: Absolute or relative path to the file to open.
    - close_current: Close current document before opening (default False).
    - save_current: Save current document before closing if close_current is True (default False).
    """
    try:
        rhino = get_rhino_connection()
        result = rhino.send_command(
            "open_file",
            {
                "path": path,
                "close_current": close_current,
                "save_current": save_current,
            },
        )
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"Error opening file: {str(e)}")
        return f"Error opening file: {str(e)}"
