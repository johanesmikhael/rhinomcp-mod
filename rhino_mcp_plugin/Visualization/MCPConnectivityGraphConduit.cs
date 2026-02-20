using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace RhinoMCPModPlugin;

internal sealed class MCPConnectivityGraphConduit : DisplayConduit
{
    private readonly Color _edgeColor = Color.FromArgb(180, 255, 120, 40);
    private readonly Color _nodeColor = Color.FromArgb(240, 80, 180, 255);

    protected override void DrawForeground(DrawEventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            return;
        }

        var graph = MCPConnectivityGraphBuilder.Compute(doc);
        if (graph.Nodes.Count == 0)
        {
            e.Display.Draw2dText("MCP Graph ON | no visible objects", Color.White, new Point2d(20, 40), false, 14);
            return;
        }

        foreach (var edge in graph.Edges)
        {
            var a = graph.Nodes[edge.A];
            var b = graph.Nodes[edge.B];
            e.Display.DrawLine(a.Center, b.Center, _edgeColor, 2);
        }

        foreach (var node in graph.Nodes)
        {
            e.Display.DrawPoint(node.Center, PointStyle.RoundSimple, 3, _nodeColor);
        }

        e.Display.Draw2dText(
            $"MCP Graph ON | nodes: {graph.Nodes.Count} edges: {graph.Edges.Count}",
            Color.White,
            new Point2d(20, 40),
            false,
            14);
    }
}

internal static class MCPConnectivityGraphBuilder
{
    private const int MaxNodes = 160;
    private const int MinComponentSize = 2;
    private const double NearbyDistanceFactor = 12.0;

    public static MCPConnectivityGraph Compute(RhinoDoc doc)
    {
        var tolerance = doc.ModelAbsoluteTolerance * 2.0;
        var nodes = new List<Node>(MaxNodes);

        foreach (var obj in doc.Objects)
        {
            if (nodes.Count >= MaxNodes)
            {
                break;
            }

            if (obj == null || obj.IsDeleted || !obj.Visible || obj.Geometry == null)
            {
                continue;
            }
            if (!IsGraphSupportedGeometry(obj.Geometry))
            {
                continue;
            }

            var bbox = obj.Geometry.GetBoundingBox(true);
            if (!bbox.IsValid)
            {
                continue;
            }

            nodes.Add(new Node
            {
                ObjectId = obj.Id,
                Name = obj.Name ?? string.Empty,
                Center = bbox.Center,
                BoundingBox = bbox,
                Geometry = obj.Geometry
            });
        }

        var edges = new List<Edge>();
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                if (!TryGetContactPoint(nodes[i], nodes[j], tolerance, out var contactPoint))
                {
                    continue;
                }

                edges.Add(new Edge { A = i, B = j, ContactPoint = contactPoint });
            }
        }

        var nearbyDistance = tolerance * NearbyDistanceFactor;
        return FilterByComponentProximity(nodes, edges, nearbyDistance, MinComponentSize, tolerance);
    }

    private static MCPConnectivityGraph FilterByComponentProximity(
        IReadOnlyList<Node> nodes,
        IReadOnlyList<Edge> edges,
        double nearbyDistance,
        int minComponentSize,
        double tolerance)
    {
        if (nodes.Count == 0)
        {
            return new MCPConnectivityGraph(nodes, edges, tolerance);
        }

        var adjacency = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            adjacency[i] = new List<int>();
        }

        foreach (var edge in edges)
        {
            adjacency[edge.A].Add(edge.B);
            adjacency[edge.B].Add(edge.A);
        }

        var visited = new bool[nodes.Count];
        var include = new bool[nodes.Count];
        var componentBoxes = new List<BoundingBox>();

        for (var start = 0; start < nodes.Count; start++)
        {
            if (visited[start])
            {
                continue;
            }

            var component = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                foreach (var next in adjacency[current])
                {
                    if (visited[next])
                    {
                        continue;
                    }

                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            if (component.Count < minComponentSize)
            {
                continue;
            }

            var hasEdge = false;
            for (var i = 0; i < component.Count; i++)
            {
                if (adjacency[component[i]].Count > 0)
                {
                    hasEdge = true;
                    break;
                }
            }

            if (!hasEdge)
            {
                continue;
            }

            var unionBox = nodes[component[0]].BoundingBox;
            for (var i = 0; i < component.Count; i++)
            {
                var nodeIndex = component[i];
                include[nodeIndex] = true;
                unionBox.Union(nodes[nodeIndex].BoundingBox);
            }

            componentBoxes.Add(unionBox);
        }

        if (componentBoxes.Count == 0)
        {
            return new MCPConnectivityGraph(nodes, edges, tolerance);
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (include[i])
            {
                continue;
            }

            foreach (var componentBox in componentBoxes)
            {
                if (BoundingBoxDistance(nodes[i].BoundingBox, componentBox) <= nearbyDistance)
                {
                    include[i] = true;
                    break;
                }
            }
        }

        var remap = new int[nodes.Count];
        Array.Fill(remap, -1);
        var filteredNodes = new List<Node>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!include[i])
            {
                continue;
            }

            remap[i] = filteredNodes.Count;
            filteredNodes.Add(nodes[i]);
        }

        var filteredEdges = new List<Edge>();
        foreach (var edge in edges)
        {
            if (!include[edge.A] || !include[edge.B])
            {
                continue;
            }

            filteredEdges.Add(new Edge
            {
                A = remap[edge.A],
                B = remap[edge.B],
                ContactPoint = edge.ContactPoint
            });
        }

        return new MCPConnectivityGraph(filteredNodes, filteredEdges, tolerance);
    }

    private static bool TryGetContactPoint(in Node a, in Node b, double tolerance, out Point3d contactPoint)
    {
        // Broad-phase reject only. Final decision is based on actual geometry.
        if (BoundingBoxDistance(a.BoundingBox, b.BoundingBox) > tolerance * 4.0)
        {
            contactPoint = Point3d.Unset;
            return false;
        }

        return TryGetGeometryContactPoint(a.Geometry, b.Geometry, tolerance, out contactPoint);
    }

    private static double BoundingBoxDistance(BoundingBox a, BoundingBox b)
    {
        var dx = AxisGap(a.Min.X, a.Max.X, b.Min.X, b.Max.X);
        var dy = AxisGap(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y);
        var dz = AxisGap(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double AxisGap(double minA, double maxA, double minB, double maxB)
    {
        if (maxA < minB)
        {
            return minB - maxA;
        }

        if (maxB < minA)
        {
            return minA - maxB;
        }

        return 0.0;
    }

    private static bool TryGetGeometryContactPoint(GeometryBase a, GeometryBase b, double tolerance, out Point3d contactPoint)
    {
        if (a is Mesh ma && b is Mesh mb)
        {
            var lines = Intersection.MeshMeshFast(ma, mb);
            if (TryGetRepresentativePoint(lines, out contactPoint))
            {
                return true;
            }
        }

        if (TryGetBrepFamily(a, out var ba) && TryGetBrepFamily(b, out var bb))
        {
            var ok = Intersection.BrepBrep(ba, bb, tolerance, out var curves, out var points);
            if (ok && TryGetRepresentativePoint(points, curves, out contactPoint))
            {
                return true;
            }
        }

        contactPoint = Point3d.Unset;
        return false;
    }

    private static bool TryGetRepresentativePoint(Point3d[] points, Curve[] curves, out Point3d contactPoint)
    {
        var samples = new List<Point3d>();
        if (points != null)
        {
            foreach (var p in points)
            {
                if (p.IsValid)
                {
                    samples.Add(p);
                }
            }
        }

        if (curves != null)
        {
            foreach (var curve in curves)
            {
                if (curve == null)
                {
                    continue;
                }

                samples.Add(curve.PointAtNormalizedLength(0.5));
            }
        }

        return TryAveragePoints(samples, out contactPoint);
    }

    private static bool TryGetRepresentativePoint(Line[] lines, out Point3d contactPoint)
    {
        var samples = new List<Point3d>();
        if (lines != null)
        {
            foreach (var line in lines)
            {
                if (!line.IsValid)
                {
                    continue;
                }

                samples.Add(line.PointAt(0.5));
            }
        }

        return TryAveragePoints(samples, out contactPoint);
    }

    private static bool TryAveragePoints(IReadOnlyList<Point3d> points, out Point3d averagePoint)
    {
        if (points == null || points.Count == 0)
        {
            averagePoint = Point3d.Unset;
            return false;
        }

        var sumX = 0.0;
        var sumY = 0.0;
        var sumZ = 0.0;
        var count = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!p.IsValid)
            {
                continue;
            }

            sumX += p.X;
            sumY += p.Y;
            sumZ += p.Z;
            count++;
        }

        if (count == 0)
        {
            averagePoint = Point3d.Unset;
            return false;
        }

        averagePoint = new Point3d(sumX / count, sumY / count, sumZ / count);
        return true;
    }

    private static bool IsGraphSupportedGeometry(GeometryBase geometry)
    {
        return geometry is Mesh || TryGetBrepFamily(geometry, out _);
    }

    private static bool TryGetBrepFamily(GeometryBase geometry, out Brep brep)
    {
        switch (geometry)
        {
            case Brep b:
                brep = b;
                return true;
            case Extrusion extrusion:
                brep = extrusion.ToBrep();
                return brep != null;
            case Surface surface:
                brep = surface.ToBrep();
                return brep != null;
            default:
                brep = null;
                return false;
        }
    }

}

internal sealed class MCPConnectivityGraph
{
    public MCPConnectivityGraph(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges, double tolerance)
    {
        Nodes = nodes;
        Edges = edges;
        Tolerance = tolerance;
    }

    public IReadOnlyList<Node> Nodes { get; }
    public IReadOnlyList<Edge> Edges { get; }
    public double Tolerance { get; }
}

internal struct Node
{
    public Guid ObjectId;
    public string Name;
    public Point3d Center;
    public BoundingBox BoundingBox;
    public GeometryBase Geometry;
}

internal struct Edge
{
    public int A;
    public int B;
    public Point3d ContactPoint;
}

internal static class MCPConnectivityGraphController
{
    private static readonly MCPConnectivityGraphConduit Conduit = new();
    private static bool _enabled;

    public static bool IsEnabled => _enabled;

    public static void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            RhinoApp.WriteLine($"MCP graph already {(enabled ? "ON" : "OFF")}.");
            return;
        }

        _enabled = enabled;
        Conduit.Enabled = enabled;
        RhinoDoc.ActiveDoc?.Views.Redraw();
        RhinoApp.WriteLine($"MCP connectivity graph {(enabled ? "enabled" : "disabled")}.");
    }

    public static void Toggle()
    {
        SetEnabled(!_enabled);
    }
}
