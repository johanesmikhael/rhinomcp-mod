from mcp.server.fastmcp import Context
from rhinomcp import get_rhino_connection, mcp, logger, rhinoscriptsyntax_json
from typing import Any, List, Dict



# @mcp.tool()
def get_rhinoscript_python_code_guide(ctx: Context, function_name: str) -> Dict[str, Any]:
    """
    Return the RhinoScriptsyntax Details for a specific function.

    Parameters:
    - function_name: The name of the function to get the details for.

    You should get the function names first by using the get_rhinoscript_python_function_names tool.
    """
    try:
        for module in rhinoscriptsyntax_json:
            for function in module["functions"]:
                if function["Name"] == function_name:
                    return function

        return {"success": False, "message": "Function not found"}

    except Exception as e:
        logger.error(f"Error executing code: {str(e)}")
        return {"success": False, "message": str(e)}
