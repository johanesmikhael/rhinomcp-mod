"""Tool to execute a Rhino command."""
from mcp.server.fastmcp import Context
from rhinomcp.server import mcp

@mcp.tool()
async def run_rhino_command(ctx: Context, command: str) -> str:
    """Execute a Rhino command by name.
    
    Args:
        command: The English name of the Rhino command to run (e.g., "_Line", "_Sphere", "CM_OpenFeatureTree").
        
    Returns:
        Result message indicating success or failure.
    """
    try:
        from rhinomcp.server import rhino_connected, send_to_rhino
        
        if not rhino_connected():
            return "Error: Not connected to Rhino. Start Rhino and run mcpmodstart first."
        
        if not command:
            return "Error: Command name is required."
        
        result = send_to_rhino({
            "type": "run_command",
            "params": {
                "command": command
            }
        })
        
        if result.get("error"):
            return f"Error: {result['error']}"
        
        return result.get("message", f"Command '{command}' executed.")
        
    except Exception as e:
        return f"Error running command: {str(e)}"
