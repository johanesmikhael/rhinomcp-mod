"""Tool to get Rhino command history."""
from mcp.server.fastmcp import Context
from rhinomcp.server import mcp

@mcp.tool()
async def get_rhino_log(ctx: Context, lines: int = 20) -> str:
    """Get recent entries from the Rhino command history.
    
    Args:
        lines: Number of recent lines to return (default: 20, max: 100).
        
    Returns:
        Recent command history entries as a formatted string.
    """
    try:
        from rhinomcp.server import rhino_connected, send_to_rhino
        
        if not rhino_connected():
            return "Error: Not connected to Rhino. Start Rhino and run mcpmodstart first."
        
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
        
        return "\n".join(entries)
        
    except Exception as e:
        return f"Error getting log: {str(e)}"
