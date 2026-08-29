# RhinoMCP-mod - Rhino Model Context Protocol Integration

RhinoMCP-mod connects Rhino to Claude AI through the Model Context Protocol (MCP), allowing Claude to directly interact with and control Rhino. This integration enables prompt assisted 3D modeling in Rhino 3D.

Please visit Github for complete information:

[Github](https://github.com/johanesmikhael/rhinomcp-mod)

Version 0.3.0 adds whole-assembly stability evaluation. It treats the connectivity graph as one welded rigid body; graph edges are not yet simulated as physical joints. Rhino-facing lengths stay in document units while the solver normalizes geometry and mass internally to meters and kilograms with SI gravity.
