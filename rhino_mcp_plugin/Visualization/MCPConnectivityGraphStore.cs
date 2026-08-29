using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPModPlugin;

/// <summary>
/// Persists the computed connectivity graph in document user text so it survives
/// save/reopen. A fingerprint of the graph-relevant document state is stored with
/// the payload; a stored graph is only reused when that fingerprint still matches.
/// </summary>
internal static class MCPConnectivityGraphStore
{
    public const string DocumentStringKey = "rhinomcp-mod:connectivity-graph";
    // v2 adds candidate/limit/truncation stats. v3 adds each contact's bearing region.
    // Older payloads are rejected rather than loaded: a v2 graph restored into v3 would carry
    // no regions, and every contact would silently report itself as a point - the same
    // failure as computing them and dropping them, arriving by a different route.
    private const int SchemaVersion = 3;

    public static bool TryLoad(RhinoDoc doc, string fingerprint, out MCPConnectivityGraph graph)
    {
        graph = null;
        if (doc == null || string.IsNullOrEmpty(fingerprint))
        {
            return false;
        }

        var raw = doc.Strings.GetValue(DocumentStringKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var payload = JObject.Parse(raw);
            if (payload.Value<int?>("v") != SchemaVersion)
            {
                return false;
            }

            if (!string.Equals(payload.Value<string>("fp"), fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            var tolerance = payload.Value<double?>("tol") ?? 0.0;
            var nodes = new List<Node>();
            foreach (var token in payload["n"] as JArray ?? new JArray())
            {
                if (token is not JObject nodeObject || !TryReadNode(nodeObject, out var node))
                {
                    return false;
                }

                nodes.Add(node);
            }

            var edges = new List<Edge>();
            foreach (var token in payload["e"] as JArray ?? new JArray())
            {
                if (token is not JArray edgeArray || edgeArray.Count < 5)
                {
                    return false;
                }

                var a = edgeArray[0].Value<int>();
                var b = edgeArray[1].Value<int>();
                if (a < 0 || b < 0 || a >= nodes.Count || b >= nodes.Count)
                {
                    return false;
                }

                var restored = new Edge
                {
                    A = a,
                    B = b,
                    ContactPoint = new Point3d(
                        edgeArray[2].Value<double>(),
                        edgeArray[3].Value<double>(),
                        edgeArray[4].Value<double>())
                };

                // The bearing region, when the contact was found by sampling. Eleven numbers
                // follow the point: the rectangle's centre, its two in-plane axes, and its
                // half-lengths. Contacts found by intersection or proximity have none and
                // stop at index 5.
                if (edgeArray.Count >= 16)
                {
                    var origin = new Point3d(
                        edgeArray[5].Value<double>(),
                        edgeArray[6].Value<double>(),
                        edgeArray[7].Value<double>());
                    var axisU = new Vector3d(
                        edgeArray[8].Value<double>(),
                        edgeArray[9].Value<double>(),
                        edgeArray[10].Value<double>());
                    var axisV = new Vector3d(
                        edgeArray[11].Value<double>(),
                        edgeArray[12].Value<double>(),
                        edgeArray[13].Value<double>());
                    var frame = new Plane(origin, axisU, axisV);
                    if (frame.IsValid)
                    {
                        restored.Extent = new ContactExtent
                        {
                            IsValid = true,
                            Frame = frame,
                            HalfU = edgeArray[14].Value<double>(),
                            HalfV = edgeArray[15].Value<double>(),
                            Samples = edgeArray.Count > 16 ? edgeArray[16].Value<int>() : 0
                        };
                    }
                }

                edges.Add(restored);
            }

            // The exact bearings ride in their own array rather than being appended to each
            // edge's positional payload, which is already read by index and would become
            // hard to extend a second time. Index into it by edge order.
            if (payload["ex"] is JArray exactArray)
            {
                for (var i = 0; i < exactArray.Count && i < edges.Count; i++)
                {
                    if (exactArray[i] is not JArray e || e.Count < 14)
                    {
                        continue;
                    }

                    var frame = new Plane(
                        new Point3d(e[0].Value<double>(), e[1].Value<double>(), e[2].Value<double>()),
                        new Vector3d(e[3].Value<double>(), e[4].Value<double>(), e[5].Value<double>()),
                        new Vector3d(e[6].Value<double>(), e[7].Value<double>(), e[8].Value<double>()));
                    if (!frame.IsValid)
                    {
                        continue;
                    }

                    var restoredEdge = edges[i];
                    restoredEdge.Exact = new PlanarBearingResult
                    {
                        IsValid = true,
                        Frame = frame,
                        HalfU = e[9].Value<double>(),
                        HalfV = e[10].Value<double>(),
                        PolygonArea = e[11].Value<double>(),
                        Offset = e[12].Value<double>(),
                        Pieces = e[13].Value<int>(),
                        Pairs = e.Count > 14 ? e[14].Value<int>() : 0,
                        RegionsA = e.Count > 15 ? e[15].Value<int>() : 0,
                        RegionsB = e.Count > 16 ? e[16].Value<int>() : 0,
                        IsLine = e.Count > 17 && e[17].Value<double>() != 0.0,
                        SkewDegrees = e.Count > 18 ? e[18].Value<double>() : 0.0,
                        BisectorDegrees = e.Count > 19 ? e[19].Value<double>() : 0.0,
                        IsBuried = e.Count > 20 && e[20].Value<double>() != 0.0,
                        LineLength = e.Count > 21 ? e[21].Value<double>() : 0.0
                    };
                    edges[i] = restoredEdge;
                }
            }

            graph = new MCPConnectivityGraph(nodes, edges, tolerance)
            {
                CandidateCount = payload.Value<int?>("cc") ?? nodes.Count,
                ExaminedCount = payload.Value<int?>("ec") ?? nodes.Count,
                NodeLimit = payload.Value<int?>("nl") ?? 0,
                Truncated = payload.Value<bool?>("tr") ?? false
            };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the node/edge JSON that both the stored graph and the stability evaluator
    /// consume. Exposed so a freshly computed (and possibly scoped) graph can be handed
    /// straight to the evaluator without a round trip through document text.
    /// </summary>
    public static JObject BuildGraphPayload(RhinoDoc doc, MCPConnectivityGraph graph)
    {
        var nodes = new JArray();
        foreach (var node in graph.Nodes)
        {
            var nodePayload = new JObject
            {
                ["g"] = node.ObjectId.ToString(),
                ["nm"] = node.Name ?? string.Empty,
                ["c"] = PointArray(node.Center),
                ["bb"] = new JArray(
                    node.BoundingBox.Min.X, node.BoundingBox.Min.Y, node.BoundingBox.Min.Z,
                    node.BoundingBox.Max.X, node.BoundingBox.Max.Y, node.BoundingBox.Max.Z)
            };

            var rhinoObject = doc.Objects.FindId(node.ObjectId);
            var stabilityText = rhinoObject?.Attributes?.GetUserString("rhinomcp.stability.v1");
            if (!string.IsNullOrWhiteSpace(stabilityText))
            {
                try
                {
                    var stability = JObject.Parse(stabilityText);
                    var mass = stability.Value<double?>("mass");
                    if (mass.HasValue)
                    {
                        nodePayload["mass"] = mass.Value;
                        var massUnit = stability.Value<string>("mass_unit");
                        if (!string.IsNullOrWhiteSpace(massUnit))
                        {
                            nodePayload["mass_unit"] = massUnit;
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore malformed per-object stability data.
                }
            }

            nodes.Add(nodePayload);
        }

        var edges = new JArray();
        foreach (var edge in graph.Edges)
        {
            var contact = edge.ContactPoint.IsValid ? edge.ContactPoint : Point3d.Origin;
            var payload = new JArray(edge.A, edge.B, contact.X, contact.Y, contact.Z);
            if (edge.Extent.IsValid)
            {
                var frame = edge.Extent.Frame;
                payload.Add(frame.Origin.X);
                payload.Add(frame.Origin.Y);
                payload.Add(frame.Origin.Z);
                payload.Add(frame.XAxis.X);
                payload.Add(frame.XAxis.Y);
                payload.Add(frame.XAxis.Z);
                payload.Add(frame.YAxis.X);
                payload.Add(frame.YAxis.Y);
                payload.Add(frame.YAxis.Z);
                payload.Add(edge.Extent.HalfU);
                payload.Add(edge.Extent.HalfV);
                payload.Add(edge.Extent.Samples);
            }

            edges.Add(payload);
        }

        var exact = new JArray();
        foreach (var edge in graph.Edges)
        {
            if (!edge.Exact.IsValid)
            {
                exact.Add(new JArray());
                continue;
            }

            var frame = edge.Exact.Frame;
            exact.Add(new JArray(
                frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
                frame.XAxis.X, frame.XAxis.Y, frame.XAxis.Z,
                frame.YAxis.X, frame.YAxis.Y, frame.YAxis.Z,
                edge.Exact.HalfU, edge.Exact.HalfV,
                edge.Exact.PolygonArea, edge.Exact.Offset,
                edge.Exact.Pieces, edge.Exact.Pairs,
                edge.Exact.RegionsA, edge.Exact.RegionsB,
                // What kind of contact it is, which the solver needs and not only the report:
                // a line has to reach it as a line, or its zero half-width is read as a
                // rectangle that happens to be thin.
                edge.Exact.IsLine ? 1.0 : 0.0,
                edge.Exact.SkewDegrees, edge.Exact.BisectorDegrees,
                edge.Exact.IsBuried ? 1.0 : 0.0,
                edge.Exact.LineLength));
        }

        return new JObject
        {
            ["ex"] = exact,
            ["tol"] = graph.Tolerance,
            ["cc"] = graph.CandidateCount,
            ["ec"] = graph.ExaminedCount,
            ["nl"] = graph.NodeLimit,
            ["tr"] = graph.Truncated,
            ["n"] = nodes,
            ["e"] = edges
        };
    }

    public static void Save(RhinoDoc doc, MCPConnectivityGraph graph, string fingerprint)
    {
        if (doc == null || graph == null || string.IsNullOrEmpty(fingerprint))
        {
            return;
        }

        var payload = BuildGraphPayload(doc, graph);
        payload["v"] = SchemaVersion;
        payload["fp"] = fingerprint;

        try
        {
            doc.Strings.SetString(DocumentStringKey, payload.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Failed to store connectivity graph in document text: {ex.Message}");
        }
    }

    public static void Clear(RhinoDoc doc)
    {
        if (doc == null)
        {
            return;
        }

        try
        {
            doc.Strings.Delete(DocumentStringKey);
        }
        catch (Exception)
        {
            // Nothing stored, or table rejected the delete; nothing to do.
        }
    }

    private static bool TryReadNode(JObject nodeObject, out Node node)
    {
        node = default;

        if (!Guid.TryParse(nodeObject.Value<string>("g"), out var objectId))
        {
            return false;
        }

        if (nodeObject["c"] is not JArray center || center.Count < 3)
        {
            return false;
        }

        if (nodeObject["bb"] is not JArray bbox || bbox.Count < 6)
        {
            return false;
        }

        node = new Node
        {
            ObjectId = objectId,
            Name = nodeObject.Value<string>("nm") ?? string.Empty,
            Center = new Point3d(center[0].Value<double>(), center[1].Value<double>(), center[2].Value<double>()),
            BoundingBox = new BoundingBox(
                new Point3d(bbox[0].Value<double>(), bbox[1].Value<double>(), bbox[2].Value<double>()),
                new Point3d(bbox[3].Value<double>(), bbox[4].Value<double>(), bbox[5].Value<double>())),
            // Geometry/ProxyMesh are build-time only inputs; a restored graph never re-runs intersections.
            Geometry = null,
            ProxyMesh = null
        };

        return true;
    }

    private static JArray PointArray(Point3d point)
    {
        return new JArray(point.X, point.Y, point.Z);
    }
}
