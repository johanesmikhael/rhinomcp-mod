from mcp.server.fastmcp import Context
from rhinomcp import get_rhino_connection, mcp, logger, rhinoscriptsyntax_json
from typing import Any, List, Dict


# @mcp.tool()
def get_rhinoscript_python_function_names(ctx: Context, categories: List[str]) -> List[str]:
    """
    Return the RhinoScriptsyntax Function Names for specified categories.

    Parameters:
    - categories: A list of categories of the RhinoScriptsyntax to get.

    Returns:
    - A list of function names that are available in the specified categories.

    The following categories are available:
    - application
    - block
    - compat
    - curve
    - dimension
    - document
    - geometry
    - grips
    - group
    - hatch
    - layer
    - light
    - line
    - linetype
    - material
    - mesh
    - object
    - plane
    - pointvector
    - selection
    - surface
    - toolbar
    - transformation
    - userdata
    - userinterface
    - utility
    - view
    """
    try:
        function_names: List[str] = []
        for i in rhinoscriptsyntax_json:
            if i["ModuleName"] in categories:
                function_names.extend([func["Name"] for func in i["functions"]])
                
        # return the related functions
        return function_names
    except Exception as e:
        logger.error(f"Error executing code: {str(e)}")
        return []
