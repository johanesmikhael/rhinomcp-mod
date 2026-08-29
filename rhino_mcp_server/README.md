# RhinoMCP-mod - Rhino Model Context Protocol Integration

RhinoMCP-mod connects Rhino to Claude AI through the Model Context Protocol (MCP), allowing Claude to directly interact with and control Rhino. This integration enables prompt assisted 3D modeling in Rhino 3D.

Please visit Github for complete information:

[Github](https://github.com/johanesmikhael/rhinomcp-mod)

Version 0.3.0 adds stability evaluation for assemblies of discrete elements. Each element is a rigid body with its own mass; the joints between them are measured from where the geometry actually bears and typed as contact (bears in compression only, with friction), pin or fixed, with an optional tension capacity. The evaluator answers whether the assembly stands, how far it moves and how stiff it is, and reports the force at every joint. Rhino-facing lengths stay in document units - metric or imperial - while the solver works internally in metres, kilograms and SI gravity.
