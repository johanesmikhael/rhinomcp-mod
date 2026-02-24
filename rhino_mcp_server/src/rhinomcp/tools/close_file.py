from mcp.server.fastmcp import Context
import json
from rhinomcp.server import get_rhino_connection, mcp, logger


@mcp.tool()
def close_file(
    ctx: Context,
    save_changes: bool = False,
    save_path: str = None,
) -> str:
    """
    Close the active Rhino document.

    Parameters:
    - save_changes: Save before closing (default False).
    - save_path: Optional Save As path when save_changes is True.
    """
    try:
        rhino = get_rhino_connection()
        command_params = {"save_changes": save_changes}
        if save_path is not None:
            command_params["save_path"] = save_path

        result = rhino.send_command("close_file", command_params)
        return json.dumps(result, indent=2)
    except Exception as e:
        logger.error(f"Error closing file: {str(e)}")
        return f"Error closing file: {str(e)}"
