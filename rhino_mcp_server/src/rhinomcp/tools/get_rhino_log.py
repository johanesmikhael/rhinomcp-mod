"""Tool to get Rhino command history."""
from mcp.server.fastmcp import Context
from rhinomcp.server import mcp

@mcp.tool()
async def get_rhino_log(ctx: Context, lines: int = 20) -> str:
    """Get recent entries from Rhino's command line pane.

    Covers what commands printed, not only the command names, so it is the
    way to read output from commands run via run_rhino_command. It also
    carries the MCP server's own connection chatter.

    Args:
        lines: Number of recent lines to return (default: 20, max: 100).

    Returns:
        The most recent command line entries as a formatted string.
    """
    try:
        from rhinomcp.server import send_to_rhino
        
        lines = min(max(1, lines), 100)
        
        result = send_to_rhino({
            "type": "get_log",
            "params": {
                "lines": lines
            }
        })
        
        if result.get("error"):
            return f"Error: {result['error']}"
        
        entries = result.get("entries", [])
        if not entries:
            return "No log entries found."

        text = "\n".join(entries)
        if result.get("truncated"):
            total = result.get("total_lines", 0)
            text = f"[showing last {len(entries)} of {total} lines]\n{text}"
        return text
        
    except Exception as e:
        return f"Error getting log: {str(e)}"
