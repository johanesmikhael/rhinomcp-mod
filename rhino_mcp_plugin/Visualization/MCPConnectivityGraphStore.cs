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
    // v2 adds candidate/limit/truncation stats. v1 payloads are rejected rather than
    // loaded, so a restored graph can never under-report truncation.
    private const int SchemaVersion = 2;

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

                edges.Add(new Edge
                {
                    A = a,
                    B = b,
                    ContactPoint = new Point3d(
                        edgeArray[2].Value<double>(),
                        edgeArray[3].Value<double>(),
                        edgeArray[4].Value<double>())
                });
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
            var stabilityText = rhinoObject?.Attributes?.GetUserString(
                Functions.RhinoMCPModFunctions.StabilityKey);
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
            edges.Add(new JArray(edge.A, edge.B, contact.X, contact.Y, contact.Z));
        }

        return new JObject
        {
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
