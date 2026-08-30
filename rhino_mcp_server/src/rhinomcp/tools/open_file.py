from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict


@mcp.tool()
def open_file(
    ctx: Context,
    path: str,
    close_current: bool = False,
    save_current: bool = False,
) -> Dict[str, Any]:
    """
    Open a Rhino .3dm file and make it the document every other tool acts on.

    Prefer close_current=True when switching files. Rhino can hold several
    documents at once, but every tool here works on the active one, and an open
    that leaves an extra document behind does not reliably make the new file
    active - the call is then refused rather than left pointing at the wrong
    model. Closing as you go leaves Rhino one document to activate, which is
    reliable. It also discards the current document's unsaved changes unless
    save_current=True, so save first if it matters.

    Parameters:
    - path: Absolute or relative path to the file to open.
    - close_current: Close the previous document once the new one is open
      (default False). The new file is opened first either way, so Rhino is
      never left with no document at all.
    - save_current: Save the previous document before closing it, when
      close_current is True (default False).

    Returns the opened path and name, and active_path / active_name for the
    document now in effect. They agree, or the call fails.
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
        return result
    except Exception as e:
        logger.error(f"Error opening file: {str(e)}")
        return {"error": str(e)}
