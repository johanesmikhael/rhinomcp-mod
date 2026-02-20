from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any


@mcp.tool()
def get_connectivity_graph(
    ctx: Context
) -> Dict[str, Any]:
    """
    Get a selective connectivity graph for currently visible Rhino objects.

    Returns a compact undirected graph:
    - n: list of node records: {"i": index, "name": object_name, "guid": object_guid}
    - e: list of undirected edges as [i, j, [x, y, z]] into n
      where [x, y, z] is the representative contact point (rounded to 2 decimals)
    - Includes connected components plus nearby unattached objects
      based on component union-bbox proximity (fixed internal rule)
    - node_count / edge_count
    - tolerance: tolerance used by graph computation
    """
    try:
        rhino = get_rhino_connection()
        return rhino.send_command("get_connectivity_graph", {})
    except Exception as e:
        logger.error(f"Error getting connectivity graph: {str(e)}")
        return {"error": str(e)}
