"""Tool to list all loaded plugins in Rhino."""
from mcp.server.fastmcp import Context
from rhinomcp.server import mcp

@mcp.tool()
async def list_plugins(ctx: Context) -> str:
    """List all loaded plugins in Rhino with their names, IDs, and load status.

    Returns a formatted string with plugin information.
    """
    try:
        from rhinomcp.server import rhino_connected, send_to_rhino
        
        if not rhino_connected():
            return "Error: Not connected to Rhino. Start Rhino and run mcpmodstart first."
        
        result = send_to_rhino({
            "type": "list_plugins",
            "params": {}
        })
        
        if result.get("error"):
            return f"Error: {result['error']}"
        
        plugins = result.get("plugins", [])
        if not plugins:
            return "No plugins loaded."
        
        lines = ["Loaded Plugins:"]
        for i, plugin in enumerate(plugins, 1):
            name = plugin.get("name", "Unknown")
            plugin_id = plugin.get("id", "N/A")
            version = plugin.get("version", "N/A")
            status = plugin.get("status", "Loaded")
            lines.append(f"  {i}. {name} (v{version})")
            lines.append(f"     ID: {plugin_id}")
            lines.append(f"     Status: {status}")
        
        return "\n".join(lines)
        
    except Exception as e:
        return f"Error listing plugins: {str(e)}"
