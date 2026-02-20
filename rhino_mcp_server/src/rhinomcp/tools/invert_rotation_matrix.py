from mcp.server.fastmcp import Context
import json
from rhinomcp.server import mcp, logger
from typing import List


def _validate_rotation_matrix(rotation_matrix: List[List[float]]) -> None:
    if rotation_matrix is None:
        raise ValueError("rotation_matrix is required")
    if len(rotation_matrix) != 3 or any(len(row) != 3 for row in rotation_matrix):
        raise ValueError("rotation_matrix must be a 3x3 matrix")


def _transpose_3x3(matrix: List[List[float]]) -> List[List[float]]:
    return [
        [matrix[0][0], matrix[1][0], matrix[2][0]],
        [matrix[0][1], matrix[1][1], matrix[2][1]],
        [matrix[0][2], matrix[1][2], matrix[2][2]],
    ]


@mcp.tool()
def invert_rotation_matrix(
    ctx: Context,
    rotation_matrix: List[List[float]]
) -> str:
    """
    Invert a 3x3 rotation matrix.

    For a proper rotation matrix R, inverse(R) = transpose(R).

    Parameters:
    - rotation_matrix: 3x3 rotation matrix

    Returns:
    - JSON string containing inverse_rotation_matrix
    """
    try:
        _validate_rotation_matrix(rotation_matrix)
        inverse_rotation_matrix = _transpose_3x3(rotation_matrix)
        return json.dumps({"inverse_rotation_matrix": inverse_rotation_matrix})
    except Exception as e:
        logger.error(f"Error inverting rotation matrix: {str(e)}")
        return f"Error inverting rotation matrix: {str(e)}"
