using Newtonsoft.Json.Linq;
using System;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetConnectivityGraph(JObject parameters)
    {
        var doc = Rhino.RhinoDoc.ActiveDoc
            ?? throw new System.InvalidOperationException("No active Rhino document.");

        var graph = MCPConnectivityGraphBuilder.Compute(doc);

        var nodes = new JArray();
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            nodes.Add(new JObject
            {
                ["i"] = i,
                ["name"] = node.Name ?? string.Empty,
                ["guid"] = node.ObjectId.ToString()
            });
        }

        var edges = new JArray();
        foreach (var edge in graph.Edges)
        {
            edges.Add(new JArray(
                edge.A,
                edge.B,
                RoundPoint(edge.ContactPoint)));
        }

        return new JObject
        {
            ["n"] = nodes,
            ["e"] = edges,
            ["node_count"] = graph.Nodes.Count,
            ["edge_count"] = graph.Edges.Count,
            ["tolerance"] = graph.Tolerance
        };
    }

    private static JArray RoundPoint(Rhino.Geometry.Point3d point)
    {
        return new JArray(
            Math.Round(point.X, 2),
            Math.Round(point.Y, 2),
            Math.Round(point.Z, 2));
    }
}
