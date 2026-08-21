using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace RhinoMCPModPlugin;

internal sealed class MCPConnectivityGraphConduit : DisplayConduit
{
    private readonly Color _edgeColor = Color.FromArgb(180, 255, 120, 40);
    private readonly Color _nodeColor = Color.FromArgb(240, 80, 180, 255);
    private readonly Color _contactColor = Color.FromArgb(255, 255, 240, 90);
    private readonly Color _isolatedColor = Color.FromArgb(255, 255, 60, 60);

    protected override void DrawForeground(DrawEventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            return;
        }

        // Scope is pinned by the mcpmodgraph command, not read from the live selection:
        // you select, run the command, and the graph stays put while you deselect and
        // orbit. With no pinned scope the whole document is graphed, which will truncate.
        var scope = MCPConnectivityGraphController.PinnedScope ?? GraphScope.All;
        var graph = MCPConnectivityGraphController.GetOrComputeGraph(doc, persist: false, scope: scope);
        var scopeLabel = scope.IsWholeDocument
            ? "whole document"
            : $"pinned {scope.Ids?.Count ?? 0} objects";

        if (graph.Nodes.Count == 0)
        {
            e.Display.Draw2dText(
                $"MCP Graph ON | scope: {scopeLabel} | nothing in scope",
                Color.White,
                new Point2d(20, 40),
                false,
                14);
            return;
        }

        var degree = new int[graph.Nodes.Count];
        foreach (var edge in graph.Edges)
        {
            degree[edge.A]++;
            degree[edge.B]++;
        }

        foreach (var edge in graph.Edges)
        {
            var a = graph.Nodes[edge.A].Center;
            var b = graph.Nodes[edge.B].Center;
            var contact = edge.ContactPoint;

            if (contact.IsValid)
            {
                // Elbow through the contact point: shows which parts meet AND where they
                // touch. A centre-to-centre line hides the location, which is the part
                // that actually matters when checking a joint.
                e.Display.DrawLine(a, contact, _edgeColor, 2);
                e.Display.DrawLine(contact, b, _edgeColor, 2);
                e.Display.DrawPoint(contact, PointStyle.X, 5, _contactColor);
            }
            else
            {
                e.Display.DrawLine(a, b, _edgeColor, 2);
            }
        }

        var isolated = 0;
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var connected = degree[i] > 0;
            if (!connected)
            {
                isolated++;
            }

            e.Display.DrawPoint(
                graph.Nodes[i].Center,
                connected ? PointStyle.RoundSimple : PointStyle.RoundControlPoint,
                connected ? 3 : 6,
                connected ? _nodeColor : _isolatedColor);
        }

        e.Display.Draw2dText(
            $"MCP Graph | scope: {scopeLabel} | nodes {graph.Nodes.Count} " +
            $"edges {graph.Edges.Count} isolated {isolated}",
            Color.White,
            new Point2d(20, 40),
            false,
            14);

        if (graph.Truncated)
        {
            e.Display.Draw2dText(
                $"TRUNCATED: {graph.ExaminedCount} of {graph.CandidateCount} examined - " +
                "select a sub-assembly to see the rest",
                _isolatedColor,
                new Point2d(20, 60),
                false,
                14);
        }
    }
}

/// <summary>
/// Restricts which document objects enter the graph. Scoping happens before node
/// collection, so the node cap applies to the scoped set rather than to the whole
/// document - that is what makes an untruncated graph of one assembly possible.
/// An empty scope means the whole document.
/// </summary>
internal sealed class GraphScope
{
    public static readonly GraphScope All = new();

    public HashSet<Guid> Ids { get; init; }
    public HashSet<string> Layers { get; init; }
    public BoundingBox? Bbox { get; init; }
    public string BboxMode { get; init; } = "intersects";
    public bool SelectedOnly { get; init; }

    public bool IsWholeDocument =>
        Ids == null && Layers == null && Bbox == null && !SelectedOnly;

    /// <summary>Stable identity of this scope, used to key caches.</summary>
    public string Key
    {
        get
        {
            if (IsWholeDocument)
            {
                return "all";
            }

            var builder = new StringBuilder();
            if (Ids != null)
            {
                builder.Append("ids:");
                foreach (var id in Ids.OrderBy(x => x))
                {
                    builder.Append(id.ToString("N")).Append(',');
                }
            }

            if (Layers != null)
            {
                builder.Append("layers:");
                foreach (var layer in Layers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    builder.Append(layer).Append(',');
                }
            }

            if (Bbox.HasValue)
            {
                var box = Bbox.Value;
                builder.Append("bbox:")
                    .Append(box.Min.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(box.Min.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(box.Min.Z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(box.Max.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(box.Max.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(box.Max.Z.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(BboxMode);
            }

            if (SelectedOnly)
            {
                builder.Append("selected");
            }

            return builder.ToString();
        }
    }

    public bool Matches(RhinoObject obj, BoundingBox bbox)
    {
        if (Ids != null && !Ids.Contains(obj.Id))
        {
            return false;
        }

        if (Layers != null)
        {
            var layerName = SafeLayerName(obj);
            if (layerName == null || !Layers.Contains(layerName))
            {
                return false;
            }
        }

        if (SelectedOnly && obj.IsSelected(false) == 0)
        {
            return false;
        }

        if (Bbox.HasValue && !BboxMatches(Bbox.Value, bbox, BboxMode))
        {
            return false;
        }

        return true;
    }

    private static string SafeLayerName(RhinoObject obj)
    {
        try
        {
            var doc = obj.Document ?? RhinoDoc.ActiveDoc;
            return doc?.Layers[obj.Attributes.LayerIndex]?.FullPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ContainsPoint(BoundingBox container, Point3d point)
    {
        return point.X >= container.Min.X && point.X <= container.Max.X &&
               point.Y >= container.Min.Y && point.Y <= container.Max.Y &&
               point.Z >= container.Min.Z && point.Z <= container.Max.Z;
    }

    private static bool BboxMatches(BoundingBox query, BoundingBox candidate, string mode)
    {
        return mode switch
        {
            "contained" => ContainsPoint(query, candidate.Min) && ContainsPoint(query, candidate.Max),
            "contains_center" => ContainsPoint(query, candidate.Center),
            _ => query.Min.X <= candidate.Max.X && query.Max.X >= candidate.Min.X &&
                 query.Min.Y <= candidate.Max.Y && query.Max.Y >= candidate.Min.Y &&
                 query.Min.Z <= candidate.Max.Z && query.Max.Z >= candidate.Min.Z
        };
    }
}

internal static class MCPConnectivityGraphBuilder
{
    // Runaway guard, not a working limit. With the RTree broad phase the cost is roughly
    // linear in object count, so ordinary models are graphed whole; truncation should now
    // be rare rather than routine. Response size on the MCP path is a separate concern -
    // an agent facing a huge graph should scope the request, not rely on a silent cap.
    private const int MaxNodes = 20000;
    private const int MinComponentSize = 2;
    private const double NearbyDistanceFactor = 12.0;

    public static MCPConnectivityGraph Compute(RhinoDoc doc, GraphScope scope = null)
    {
        scope ??= GraphScope.All;
        var tolerance = doc.ModelAbsoluteTolerance * 5.0;
        // Not preallocated to MaxNodes: that is a runaway guard, not an expected size.
        var nodes = new List<Node>();

        // Every candidate is counted even once the cap is reached, so callers can be
        // told the graph is partial instead of reading a truncated graph as complete.
        var candidateCount = 0;
        foreach (var (obj, bbox) in EnumerateCandidates(doc, scope))
        {
            candidateCount++;
            if (nodes.Count >= MaxNodes)
            {
                continue;
            }

            nodes.Add(new Node
            {
                ObjectId = obj.Id,
                Name = obj.Name ?? string.Empty,
                Center = bbox.Center,
                BoundingBox = bbox,
                Geometry = obj.Geometry,
                ProxyMesh = BuildProxyMesh(obj.Geometry, tolerance)
            });
        }

        // Broad phase via RTree instead of testing every pair. The old double loop was
        // O(n^2) - 596 objects is 177k pairs - which is the only reason a node cap was
        // ever needed. Here each object only tests against boxes that actually overlap it.
        var edges = new List<Edge>();
        // The RTree slack must match the gate the narrow phase uses, or pairs that would
        // qualify as touching are discarded before they are ever tested. See ContactGap.
        var searchSlack = ContactGap(tolerance);

        var tree = new RTree();
        for (var i = 0; i < nodes.Count; i++)
        {
            tree.Insert(nodes[i].BoundingBox, i);
        }

        var candidates = new List<int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var searchBox = nodes[i].BoundingBox;
            searchBox.Inflate(searchSlack);

            candidates.Clear();
            var current = i;
            tree.Search(searchBox, (sender, args) =>
            {
                // Only j > i, so each pair is considered once.
                if (args.Id > current)
                {
                    candidates.Add(args.Id);
                }
            });

            foreach (var j in candidates)
            {
                if (!TryGetContactPoint(nodes[i], nodes[j], tolerance, out var contactPoint))
                {
                    continue;
                }

                edges.Add(new Edge { A = i, B = j, ContactPoint = contactPoint });
            }
        }

        var nearbyDistance = tolerance * NearbyDistanceFactor;
        var examinedCount = nodes.Count;
        var graph = FilterByComponentProximity(nodes, edges, nearbyDistance, MinComponentSize, tolerance);
        graph.ExaminedCount = examinedCount;
        graph.CandidateCount = candidateCount;
        graph.NodeLimit = MaxNodes;
        graph.Truncated = candidateCount > MaxNodes;
        return graph;
    }

    /// <summary>
    /// Cheap digest of everything <see cref="Compute"/> reads from the document.
    /// Same fingerprint =&gt; recomputing would produce the same graph, so a stored
    /// graph can be reused instead of re-running the geometry intersections.
    /// </summary>
    public static string ComputeFingerprint(RhinoDoc doc, GraphScope scope = null)
    {
        if (doc == null)
        {
            return null;
        }

        scope ??= GraphScope.All;

        var tolerance = doc.ModelAbsoluteTolerance * 5.0;
        var quantum = Math.Max(tolerance * 0.1, 1e-9);

        var builder = new StringBuilder();
        builder.Append("v2|").Append(tolerance.ToString("R", CultureInfo.InvariantCulture))
            .Append("|").Append(scope.Key);

        // Covers every candidate, not just the first MaxNodes: a change beyond the cap
        // still changes which objects the cap admits, so it must invalidate the cache.
        var count = 0;
        foreach (var (obj, bbox) in EnumerateCandidates(doc, scope))
        {
            count++;
            builder.Append('|').Append(obj.Id.ToString("N"));
            AppendQuantized(builder, bbox.Min, quantum);
            AppendQuantized(builder, bbox.Max, quantum);
        }

        builder.Append("|#").Append(count);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static IEnumerable<(RhinoObject Object, BoundingBox BoundingBox)> EnumerateCandidates(
        RhinoDoc doc,
        GraphScope scope)
    {
        scope ??= GraphScope.All;
        foreach (var obj in doc.Objects)
        {
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

            if (!scope.Matches(obj, bbox))
            {
                continue;
            }

            yield return (obj, bbox);
        }
    }

    private static void AppendQuantized(StringBuilder builder, Point3d point, double quantum)
    {
        builder.Append(':')
            .Append((long)Math.Round(point.X / quantum)).Append(',')
            .Append((long)Math.Round(point.Y / quantum)).Append(',')
            .Append((long)Math.Round(point.Z / quantum));
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
        if (BoundingBoxDistance(a.BoundingBox, b.BoundingBox) > ContactGap(tolerance))
        {
            contactPoint = Point3d.Unset;
            return false;
        }

        return TryGetGeometryContactPoint(a, b, tolerance, out contactPoint);
    }

    /// <summary>
    /// How far apart two elements may sit and still be treated as bearing on one another.
    /// </summary>
    /// <remarks>
    /// The geometric tolerance is far too tight for this. At a document tolerance of
    /// 0.001 mm the old gate came to 0.02 mm, while a catalogue column 2730.9 mm tall,
    /// placed from a height rounded to 2731, leaves a 0.1 mm gap under the beam it is
    /// meant to carry - five times the gate, so the pair was discarded before contact was
    /// ever tested. Nothing about that gap is a modelling error; it is what snapping to
    /// rounded dimensions produces.
    ///
    /// The threshold is therefore a construction tolerance rather than a numerical one: a
    /// millimetre, which is finer than anything a reuse catalogue is cut to and far below
    /// the size of any real gap between elements that are genuinely apart.
    /// </remarks>
    private static double ContactGap(double tolerance)
    {
        // Deliberately not tolerance * 4. That term is twenty times the document's absolute
        // tolerance, and absolute tolerance conventionally tracks the unit system: 0.001 mm
        // in a millimetre model but 0.001 m in a metre one. The same physical building
        // therefore got a 1 mm contact gap in millimetres and a 20 mm one in metres, and a
        // measured ladder confirmed it - gaps of 2 mm and 5 mm, correctly rejected in the
        // millimetre document, were both accepted as contact in the metre one.
        //
        // The gap is a construction tolerance, so it is stated as one. The document's own
        // tolerance is kept only as a floor, since nothing finer than that is resolvable.
        return Math.Max(
            Functions.DocumentUnits.AbsoluteTolerance(),
            Functions.DocumentUnits.Millimetres(ContactGapMillimetres));
    }

    /// <summary>
    /// How close two elements must come to count as bearing on one another, as a real
    /// length. Finer than anything a reuse catalogue is cut to, and far below any gap
    /// between elements that are genuinely apart.
    /// </summary>
    public const double ContactGapMillimetres = 1.0;

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

    private static bool TryGetGeometryContactPoint(in Node a, in Node b, double tolerance, out Point3d contactPoint)
    {
        if (a.ProxyMesh != null && b.ProxyMesh != null)
        {
            var lines = Intersection.MeshMeshFast(a.ProxyMesh, b.ProxyMesh);
            if (TryGetRepresentativePoint(lines, out contactPoint))
            {
                return true;
            }
        }

        if (TryGetBrepFamily(a.Geometry, out var ba) && TryGetBrepFamily(b.Geometry, out var bb))
        {
            var ok = Intersection.BrepBrep(ba, bb, tolerance, out var curves, out var points);
            if (ok && TryGetRepresentativePoint(points, curves, out contactPoint))
            {
                return true;
            }
        }

        // Both tests above look for an intersection, and two elements that merely touch do
        // not intersect: coplanar faces meeting at zero overlap give MeshMeshFast nothing
        // to return, and BrepBrep never runs for mesh objects at all. A deck modelled
        // resting exactly on a beam therefore read as unsupported, which is how a structure
        // whose beams sat precisely on its column tops came back with every column
        // connected to nothing above it.
        //
        // The broad phase has already accepted this pair as being within contact distance,
        // so the narrow phase should not now reject it for failing to overlap. Fall back to
        // asking how close the two actually come.
        if (a.ProxyMesh != null && b.ProxyMesh != null &&
            TryGetProximityContactPoint(a.ProxyMesh, b.ProxyMesh, ContactGap(tolerance), out contactPoint))
        {
            return true;
        }

        contactPoint = Point3d.Unset;
        return false;
    }

    /// <summary>
    /// Closest approach between two meshes, as a contact when it is within
    /// <paramref name="maxGap"/>. The reported point is the midpoint of the closest pair,
    /// which for a face-to-face bearing lies in the shared surface.
    /// </summary>
    private static bool TryGetProximityContactPoint(
        Mesh a, Mesh b, double maxGap, out Point3d contactPoint)
    {
        contactPoint = Point3d.Unset;
        if (a.Vertices.Count == 0 || b.Vertices.Count == 0)
        {
            return false;
        }

        // Walk the vertices of the coarser mesh against the finer one: the closest approach
        // between two convex-ish elements is attained at a vertex of one of them, and
        // testing the coarser side keeps a finely tessellated neighbour from dominating the
        // cost.
        var walkA = a.Vertices.Count <= b.Vertices.Count;
        var source = walkA ? a : b;
        var target = walkA ? b : a;

        // Two elements resting face to face tie at zero gap across their whole overlap, so
        // keeping the first minimum found reports whichever vertex the walk happened to
        // reach first - always a corner of the box, since those are the vertices there are.
        // A bearing surface labelled at its corner is misleading on screen and, in the
        // pinned mode, wrong: that point is where the two bodies get pinned. Average the
        // tied set instead, which puts the label in the middle of the surface actually in
        // contact.
        var tie = Math.Max(RhinoMath.ZeroTolerance, Functions.DocumentUnits.AbsoluteTolerance());
        var bestGap = double.MaxValue;
        var sum = Point3d.Origin;
        var tiedCount = 0;
        foreach (var vertex in source.Vertices)
        {
            var point = new Point3d(vertex.X, vertex.Y, vertex.Z);
            var onTarget = target.ClosestPoint(point);
            if (!onTarget.IsValid)
            {
                continue;
            }

            var gap = point.DistanceTo(onTarget);
            if (gap > bestGap + tie)
            {
                continue;
            }

            var midpoint = (point + onTarget) * 0.5;
            if (gap < bestGap - tie)
            {
                // A genuinely closer approach: everything gathered so far was not contact.
                bestGap = gap;
                sum = midpoint;
                tiedCount = 1;
                continue;
            }

            bestGap = Math.Min(bestGap, gap);
            sum += midpoint;
            tiedCount++;
        }

        if (tiedCount == 0 || bestGap > maxGap)
        {
            return false;
        }

        var averaged = sum / tiedCount;
        if (!averaged.IsValid)
        {
            return false;
        }

        contactPoint = averaged;
        return true;
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

    private static Mesh BuildProxyMesh(GeometryBase geometry, double tolerance)
    {
        if (geometry is Mesh mesh)
        {
            return mesh;
        }

        Brep brep = null;
        if (geometry is Brep b)
        {
            brep = b;
        }
        else if (geometry is Extrusion extrusion)
        {
            brep = extrusion.ToBrep();
        }
        else if (geometry is Surface surface)
        {
            brep = surface.ToBrep();
        }

        if (brep == null)
        {
            return null;
        }

        var meshing = MeshingParameters.FastRenderMesh;
        meshing.MinimumEdgeLength = Math.Max(RhinoMath.ZeroTolerance, tolerance * 0.25);
        meshing.MaximumEdgeLength = 0.0;
        meshing.SimplePlanes = true;

        var meshParts = Mesh.CreateFromBrep(brep, meshing);
        if (meshParts == null || meshParts.Length == 0)
        {
            return null;
        }

        if (meshParts.Length == 1)
        {
            return meshParts[0];
        }

        var combined = new Mesh();
        foreach (var part in meshParts.Where(part => part != null))
        {
            combined.Append(part);
        }

        if (!combined.IsValid || combined.Faces.Count == 0)
        {
            return null;
        }

        return combined;
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

    /// <summary>Objects in the document that qualified as graph candidates.</summary>
    public int CandidateCount { get; set; }

    /// <summary>
    /// Candidates actually admitted as nodes and tested for contact, before the
    /// component-proximity filter dropped isolated ones. Distinct from Nodes.Count,
    /// which is the post-filter result.
    /// </summary>
    public int ExaminedCount { get; set; }

    /// <summary>Node cap applied while collecting candidates.</summary>
    public int NodeLimit { get; set; }

    /// <summary>
    /// True when candidates exceeded <see cref="NodeLimit"/>, so objects were never
    /// examined and absent edges do not mean absent contact.
    /// </summary>
    public bool Truncated { get; set; }
}

internal struct Node
{
    public Guid ObjectId;
    public string Name;
    public Point3d Center;
    public BoundingBox BoundingBox;
    public GeometryBase Geometry;
    public Mesh ProxyMesh;
}

internal struct Edge
{
    public int A;
    public int B;
    public Point3d ContactPoint;
}

internal enum GraphCacheSource
{
    None,
    Computed,
    MemoryCache,
    DocumentText
}

internal static class MCPConnectivityGraphController
{
    private static readonly MCPConnectivityGraphConduit Conduit = new();
    private static readonly object SyncRoot = new();
    private static bool _enabled;
    private static bool _eventsHooked;
    private static bool _dirty = true;
    private static uint _cachedDocRuntimeSerial;
    private static MCPConnectivityGraph _cachedGraph;
    private static GraphCacheSource _cachedSource = GraphCacheSource.None;
    private static string _cachedFingerprint;
    private static string _cachedScopeKey;
    private static bool _cachedGraphPersisted;
    private static bool _persistQueued;
    private static RhinoDoc _pendingPersistDoc;

    public static bool IsEnabled => _enabled;

    /// <summary>
    /// Scope the display is pinned to, captured by the mcpmodgraph command. Null means
    /// the whole document. Pinning rather than tracking the live selection lets the user
    /// select, run the command, then deselect and keep looking at the same graph.
    /// </summary>
    public static GraphScope PinnedScope { get; set; }

    /// <summary>Where the graph currently held in memory came from.</summary>
    public static GraphCacheSource LastSource => _cachedSource;

    public static void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            RhinoApp.WriteLine($"MCP graph already {(enabled ? "ON" : "OFF")}.");
            return;
        }

        _enabled = enabled;
        Conduit.Enabled = enabled;
        if (enabled)
        {
            EnsureEventsHooked();
            MarkDirty();
            QueueGraphPersistence(RhinoDoc.ActiveDoc);
        }
        RhinoDoc.ActiveDoc?.Views.Redraw();
        RhinoApp.WriteLine($"MCP connectivity graph {(enabled ? "enabled" : "disabled")}.");
    }

    public static void Toggle()
    {
        SetEnabled(!_enabled);
    }

    /// <param name="persist">
    /// When true (default) a freshly computed graph is written to document user text.
    /// Callers running inside a display pipeline pass false: modifying the document
    /// during a redraw is not safe.
    /// </param>
    public static MCPConnectivityGraph GetOrComputeGraph(
        RhinoDoc doc,
        bool persist = true,
        GraphScope scope = null)
    {
        scope ??= GraphScope.All;
        var scopeKey = scope.Key;

        lock (SyncRoot)
        {
            if (doc == null)
            {
                _cachedSource = GraphCacheSource.None;
                return new MCPConnectivityGraph(Array.Empty<Node>(), Array.Empty<Edge>(), 0.0);
            }

            // Invalidation used to be hooked only by the display toggle, so _dirty stayed
            // false forever for callers that never enabled the conduit and every request
            // got the first graph ever built. Hooking here makes _dirty authoritative for
            // every caller; it is idempotent and has no effect while the conduit is off.
            EnsureEventsHooked();

            // Only one graph is held in memory, so it is reused only for the same scope.
            // Alternating scopes recompute rather than returning another scope's graph.
            var sameDocument = _cachedDocRuntimeSerial == doc.RuntimeSerialNumber &&
                string.Equals(_cachedScopeKey, scopeKey, StringComparison.Ordinal);

            // The dirty flag only tracks document edits, and selection raises none of the
            // hooked events. So it is trustworthy for the whole-document graph but not for
            // a scoped one, where changing the selection changes the answer with no edit.
            // Scoped requests always fall through to the fingerprint, which does see it.
            if (_cachedGraph != null && sameDocument && !_dirty && _cachedFingerprint != null &&
                scope.IsWholeDocument)
            {
                // Events are wired, so a clean flag is trustworthy and the fingerprint
                // scan can be skipped. This keeps redraws cheap.
                if (persist && !_cachedGraphPersisted)
                {
                    MCPConnectivityGraphStore.Save(doc, _cachedGraph, _cachedFingerprint);
                    _cachedGraphPersisted = true;
                }

                _cachedSource = GraphCacheSource.MemoryCache;
                return _cachedGraph;
            }

            // Second line of defence: any document change that the events do not raise
            // still shows up as a different fingerprint.
            var fingerprint = MCPConnectivityGraphBuilder.ComputeFingerprint(doc, scope);

            if (_cachedGraph != null && sameDocument &&
                string.Equals(_cachedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // A graph first computed for the display conduit was intentionally not
                // written to the document; persist it now that a caller allows it.
                if (persist && !_cachedGraphPersisted)
                {
                    MCPConnectivityGraphStore.Save(doc, _cachedGraph, _cachedFingerprint);
                    _cachedGraphPersisted = true;
                }

                _dirty = false;
                _cachedSource = GraphCacheSource.MemoryCache;
                return _cachedGraph;
            }

            var currentDocumentWasInvalidated = _dirty && sameDocument;

            if (!currentDocumentWasInvalidated && scope.IsWholeDocument &&
                MCPConnectivityGraphStore.TryLoad(doc, fingerprint, out var storedGraph))
            {
                _cachedGraph = storedGraph;
                _cachedDocRuntimeSerial = doc.RuntimeSerialNumber;
                _dirty = false;
                _cachedSource = GraphCacheSource.DocumentText;
                _cachedFingerprint = fingerprint;
                _cachedScopeKey = scopeKey;
                _cachedGraphPersisted = true;
                return _cachedGraph;
            }

            _cachedGraph = MCPConnectivityGraphBuilder.Compute(doc, scope);
            _cachedDocRuntimeSerial = doc.RuntimeSerialNumber;
            _dirty = false;
            _cachedSource = GraphCacheSource.Computed;
            _cachedFingerprint = fingerprint;
            _cachedScopeKey = scopeKey;
            _cachedGraphPersisted = false;

            if (persist && scope.IsWholeDocument)
            {
                MCPConnectivityGraphStore.Save(doc, _cachedGraph, fingerprint);
                _cachedGraphPersisted = true;
            }

            return _cachedGraph;
        }
    }

    /// <summary>Drops the in-memory cache and the stored document-text copy.</summary>
    public static void ClearStoredGraph(RhinoDoc doc)
    {
        lock (SyncRoot)
        {
            MCPConnectivityGraphStore.Clear(doc);
            _cachedGraph = null;
            _cachedSource = GraphCacheSource.None;
            _cachedFingerprint = null;
            _cachedScopeKey = null;
            _cachedGraphPersisted = false;
            _dirty = true;
        }
    }

    private static void EnsureEventsHooked()
    {
        if (_eventsHooked)
        {
            return;
        }

        RhinoDoc.AddRhinoObject += OnGraphAffectingObjectEvent;
        RhinoDoc.DeleteRhinoObject += OnGraphAffectingObjectEvent;
        RhinoDoc.UndeleteRhinoObject += OnGraphAffectingObjectEvent;
        RhinoDoc.ReplaceRhinoObject += OnGraphAffectingReplaceEvent;
        RhinoDoc.ModifyObjectAttributes += OnGraphAffectingAttributesEvent;
        _eventsHooked = true;
    }

    private static void OnGraphAffectingObjectEvent(object sender, RhinoObjectEventArgs e)
    {
        MarkDirtyAndRedraw(sender as RhinoDoc);
    }

    private static void OnGraphAffectingReplaceEvent(object sender, RhinoReplaceObjectEventArgs e)
    {
        MarkDirtyAndRedraw(sender as RhinoDoc);
    }

    private static void OnGraphAffectingAttributesEvent(object sender, RhinoModifyObjectAttributesEventArgs e)
    {
        MarkDirtyAndRedraw(sender as RhinoDoc);
    }

    private static void MarkDirtyAndRedraw(RhinoDoc doc)
    {
        MarkDirty();
        if (_enabled)
        {
            var activeDoc = doc ?? RhinoDoc.ActiveDoc;
            QueueGraphPersistence(activeDoc);
            activeDoc?.Views.Redraw();
        }
    }

    private static void QueueGraphPersistence(RhinoDoc doc)
    {
        if (doc == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            _pendingPersistDoc = doc;
            if (_persistQueued)
            {
                return;
            }

            _persistQueued = true;
            RhinoApp.Idle += OnPersistGraphIdle;
        }
    }

    private static void OnPersistGraphIdle(object sender, EventArgs e)
    {
        RhinoDoc doc;
        lock (SyncRoot)
        {
            RhinoApp.Idle -= OnPersistGraphIdle;
            _persistQueued = false;
            doc = _pendingPersistDoc;
            _pendingPersistDoc = null;
        }

        if (doc == null)
        {
            return;
        }

        try
        {
            // Object add/delete/replace events can fire while Rhino is still editing
            // its document tables. Persist on Idle, after that operation completes.
            GetOrComputeGraph(doc, persist: true);
            doc.Views.Redraw();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Failed to refresh connectivity graph: {ex.Message}");
        }
    }

    private static void MarkDirty()
    {
        lock (SyncRoot)
        {
            _dirty = true;
        }
    }
}
