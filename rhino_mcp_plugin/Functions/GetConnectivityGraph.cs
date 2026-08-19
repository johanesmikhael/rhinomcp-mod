using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetConnectivityGraph(JObject parameters)
    {
        var doc = Rhino.RhinoDoc.ActiveDoc
            ?? throw new System.InvalidOperationException("No active Rhino document.");

        var scope = ReadGraphScope(parameters);
        var graph = MCPConnectivityGraphController.GetOrComputeGraph(doc, persist: true, scope: scope);

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

        var result = new JObject
        {
            ["n"] = nodes,
            ["e"] = edges,
            ["node_count"] = graph.Nodes.Count,
            ["edge_count"] = graph.Edges.Count,
            ["candidate_count"] = graph.CandidateCount,
            ["examined_count"] = graph.ExaminedCount,
            ["node_limit"] = graph.NodeLimit,
            ["truncated"] = graph.Truncated,
            ["tolerance"] = graph.Tolerance,
            ["scope"] = DescribeScope(scope),
            ["source"] = MCPConnectivityGraphController.LastSource switch
            {
                GraphCacheSource.DocumentText => "document_text_cache",
                GraphCacheSource.MemoryCache => "memory_cache",
                GraphCacheSource.Computed => "computed",
                _ => "none"
            }
        };

        if (graph.Truncated)
        {
            result["truncation_warning"] =
                $"Examined {graph.ExaminedCount} of {graph.CandidateCount} candidate objects " +
                $"(node_limit {graph.NodeLimit}); {graph.Nodes.Count} remain after component " +
                $"filtering. The {graph.CandidateCount - graph.ExaminedCount} objects beyond the " +
                "limit were never tested for contact, so a missing edge does not mean the parts " +
                "are disconnected.";
        }

        return result;
    }

    private static GraphScope ReadGraphScope(JObject parameters)
    {
        if (parameters == null)
        {
            return GraphScope.All;
        }

        HashSet<Guid> ids = null;
        if (parameters["ids"] is JArray idArray && idArray.Count > 0)
        {
            ids = new HashSet<Guid>();
            foreach (var token in idArray)
            {
                var raw = token?.ToString();
                if (!Guid.TryParse(raw, out var id))
                {
                    throw new ArgumentException($"ids contains a value that is not a GUID: {raw}");
                }

                ids.Add(id);
            }
        }

        HashSet<string> layers = null;
        var layerToken = parameters["layer"];
        if (layerToken is JArray layerArray && layerArray.Count > 0)
        {
            layers = new HashSet<string>(
                layerArray.Select(t => t?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)),
                StringComparer.OrdinalIgnoreCase);
        }
        else if (layerToken != null && layerToken.Type != JTokenType.Null)
        {
            var name = layerToken.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                layers = new HashSet<string>(new[] { name }, StringComparer.OrdinalIgnoreCase);
            }
        }

        BoundingBox? bbox = null;
        if (parameters["bbox"] != null && parameters["bbox"].Type != JTokenType.Null)
        {
            if (parameters["bbox"] is not JArray box || box.Count != 2 ||
                !TryReadPoint(box[0], out var first) || !TryReadPoint(box[1], out var second))
            {
                throw new ArgumentException("bbox must be [[min_x,min_y,min_z],[max_x,max_y,max_z]].");
            }

            bbox = new BoundingBox(
                new Point3d(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Min(first.Z, second.Z)),
                new Point3d(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y), Math.Max(first.Z, second.Z)));
        }

        var bboxMode = parameters["bbox_mode"]?.ToString()?.Trim().ToLowerInvariant() ?? "intersects";
        if (bboxMode != "intersects" && bboxMode != "contains_center" && bboxMode != "contained")
        {
            throw new ArgumentException("bbox_mode must be one of: intersects, contains_center, contained.");
        }

        var selectedOnly = parameters["selected"]?.ToObject<bool>() ?? false;

        return new GraphScope
        {
            Ids = ids,
            Layers = layers,
            Bbox = bbox,
            BboxMode = bboxMode,
            SelectedOnly = selectedOnly
        };
    }

    private static bool TryReadPoint(JToken token, out Point3d point)
    {
        point = Point3d.Unset;
        if (token is not JArray arr || arr.Count != 3)
        {
            return false;
        }

        point = new Point3d(
            arr[0]?.ToObject<double>() ?? 0.0,
            arr[1]?.ToObject<double>() ?? 0.0,
            arr[2]?.ToObject<double>() ?? 0.0);
        return true;
    }

    private static JObject DescribeScope(GraphScope scope)
    {
        var description = new JObject
        {
            ["whole_document"] = scope.IsWholeDocument
        };

        if (scope.Ids != null)
        {
            description["ids"] = scope.Ids.Count;
        }

        if (scope.Layers != null)
        {
            description["layer"] = new JArray(scope.Layers.ToArray());
        }

        if (scope.Bbox.HasValue)
        {
            var box = scope.Bbox.Value;
            description["bbox"] = new JArray(
                new JArray(box.Min.X, box.Min.Y, box.Min.Z),
                new JArray(box.Max.X, box.Max.Y, box.Max.Z));
            description["bbox_mode"] = scope.BboxMode;
        }

        if (scope.SelectedOnly)
        {
            description["selected"] = true;
        }

        return description;
    }

    private static JArray RoundPoint(Rhino.Geometry.Point3d point)
    {
        return new JArray(
            Math.Round(point.X, 2),
            Math.Round(point.Y, 2),
            Math.Round(point.Z, 2));
    }
}
