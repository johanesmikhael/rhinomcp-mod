from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict, List, Optional, Union


@mcp.tool()
def get_connectivity_graph(
    ctx: Context,
    layer: Optional[Union[str, List[str]]] = None,
    ids: Optional[List[str]] = None,
    bbox: Optional[List[List[float]]] = None,
    bbox_mode: str = "intersects",
    selected: bool = False,
) -> Dict[str, Any]:
    """
    Get a selective connectivity graph for currently visible Rhino objects.

    Scope the graph to one assembly whenever the document is larger than a few
    hundred objects. Scoping is applied before the node limit, so a scoped request
    can return a complete graph where an unscoped one would be truncated. Filters
    combine with AND; omit all of them to graph the whole document.

    Parameters:
    - layer: Layer name, or list of names, to include.
    - ids: Object GUIDs to include.
    - bbox: World axis-aligned box filter [[min_x,min_y,min_z],[max_x,max_y,max_z]].
    - bbox_mode: "intersects" (default), "contains_center", or "contained".
    - selected: When True, only currently selected objects.

    Returns a compact undirected graph:
    - n: list of node records: {"i": index, "name": object_name, "guid": object_guid}
    - e: list of undirected edges as [i, j, [x, y, z]] into n
      where [x, y, z] is the representative contact point (rounded to 2 decimals)
    - Includes connected components plus nearby unattached objects
      based on component union-bbox proximity (fixed internal rule)
    - node_count / edge_count
    - candidate_count: objects matching the scope that qualified as graph candidates
    - examined_count: candidates actually tested for contact, capped by node_limit.
      node_count is lower still: it is what survives the component-proximity filter.
    - node_limit: maximum objects the graph will examine
    - truncated: True when candidate_count exceeds node_limit, meaning objects were
      never tested for contact. When truncated is True a missing edge does NOT mean
      the parts are disconnected, and truncation_warning describes what was skipped.
      Narrow the scope and retry rather than trusting a truncated graph.
    - scope: echo of the filters that were applied
    - tolerance: tolerance used by graph computation
    - source: where this response came from - "computed", "memory_cache",
      "document_text_cache", or "none". Caches are validated against a fingerprint of
      every candidate's id and bounding box, so a cached graph reflects current
      geometry. One graph is held in memory at a time, keyed by scope, so alternating
      between scopes recomputes rather than returning a stale result.
    """
    try:
        params: Dict[str, Any] = {}
        if layer is not None:
            params["layer"] = layer
        if ids:
            params["ids"] = ids
        if bbox is not None:
            params["bbox"] = bbox
            params["bbox_mode"] = bbox_mode
        if selected:
            params["selected"] = True

        rhino = get_rhino_connection()
        return rhino.send_command("get_connectivity_graph", params)
    except Exception as e:
        logger.error(f"Error getting connectivity graph: {str(e)}")
        return {"error": str(e)}
