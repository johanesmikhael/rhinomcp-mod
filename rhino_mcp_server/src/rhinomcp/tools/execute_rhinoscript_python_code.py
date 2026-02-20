from mcp.server.fastmcp import Context
import json
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, List, Dict


# @mcp.tool()
def execute_rhinoscript_python_code(ctx: Context, code: str) -> Dict[str, Any]:
    """
    Execute arbitrary RhinoScript code in Rhino.
    
    Parameters:
    - code: The RhinoScript code to execute

    GUIDE: 
    
    1. To get any output from the script, you should use the python `print` function.
    2. You can get a list of all possible functions names that can be used by using the get_rhinoscript_python_function_names tool.
    3. You can get the details of a specific function by using the get_rhinoscript_python_code_guide tool.

    Example:
    - Your task is: "Create a loft surface between two curves."
    - get_rhinoscript_python_function_names(["surface", "curve"])
    - This will return the function names that are necessary for creating the code.
    - get_rhinoscript_python_code_guide("AddLoftSrf")
    - This will return the syntax of the code that are necessary for creating the code.

    Any changes made to the document will be undone if the script returns failure.

    DO NOT HALLUCINATE, ONLY USE THE SYNTAX THAT IS SUPPORTED BY RHINO.GEOMETRY OR RHINOSCRIPT.
    
    """
    try:
        # Get the global connection
        rhino = get_rhino_connection()
        
        return rhino.send_command("execute_rhinoscript_python_code", {"code": code})

    except Exception as e:
        logger.error(f"Error executing code: {str(e)}")
        return {"success": False, "message": str(e)}
