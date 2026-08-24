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
        var extents = new JArray();
        var unmeasured = new JArray();
        var exact = new JArray();
        var exactUnmeasured = new JArray();
        foreach (var edge in graph.Edges)
        {
            edges.Add(new JArray(
                edge.A,
                edge.B,
                RoundPoint(edge.ContactPoint)));

            // The bearing region behind the contact, reported separately so the compact edge
            // form is unchanged for anything already reading it. A contact found by
            // intersection or proximity has no region and does not appear here, which is why
            // this carries its own a/b rather than being positional.
            if (edge.Extent.IsValid)
            {
                extents.Add(new JObject
                {
                    ["a"] = edge.A,
                    ["b"] = edge.B,
                    ["centre"] = RoundPoint(edge.Extent.Frame.Origin),
                    ["u"] = RoundVector(edge.Extent.Frame.XAxis),
                    ["v"] = RoundVector(edge.Extent.Frame.YAxis),
                    ["normal"] = RoundVector(edge.Extent.Frame.ZAxis),
                    // Full lengths, because a bearing is quoted as its size rather than as
                    // half of it.
                    ["length_u"] = Math.Round(edge.Extent.HalfU * 2.0, 2),
                    ["length_v"] = Math.Round(edge.Extent.HalfV * 2.0, 2),
                    ["area"] = Math.Round(edge.Extent.Area, 2),
                    ["samples"] = edge.Extent.Samples
                });
            }
            if (edge.Exact.IsValid)
            {
                // The same joint measured by region intersection rather than by sampling.
                // Reported beside the sampled extent, not instead of it: until the two have
                // been compared across the suite, replacing one with the other would be a
                // change nobody had measured.
                exact.Add(new JObject
                {
                    ["a"] = edge.A,
                    ["b"] = edge.B,
                    ["centre"] = RoundPoint(edge.Exact.Frame.Origin),
                    ["u"] = RoundVector(edge.Exact.Frame.XAxis),
                    ["v"] = RoundVector(edge.Exact.Frame.YAxis),
                    ["normal"] = RoundVector(edge.Exact.Frame.ZAxis),
                    ["length_u"] = Math.Round(edge.Exact.HalfU * 2.0, 3),
                    ["length_v"] = Math.Round(edge.Exact.HalfV * 2.0, 3),
                    // Rectangle area and polygon area differ exactly when the bearing is not
                    // rectangular, so the pair of them says whether the rectangle is a fair
                    // description of it.
                    ["area"] = Math.Round(edge.Exact.RectangleArea, 3),
                    ["polygon_area"] = Math.Round(edge.Exact.PolygonArea, 3),
                    // Deliberate overlap and a wall driven through a slab look identical to
                    // the geometry. The depth is what tells them apart.
                    ["penetration_depth"] = Math.Round(edge.Exact.PenetrationDepth, 3),
                    ["offset"] = Math.Round(edge.Exact.Offset, 3),
                    ["pairs"] = edge.Exact.Pairs,
                    ["pieces"] = edge.Exact.Pieces,
                    ["regions_a"] = edge.Exact.RegionsA,
                    ["regions_b"] = edge.Exact.RegionsB,
                    // A line is a real contact with a real length, not a rectangle that came
                    // out thin. Naming it says the joint carries no moment about that line
                    // because it physically cannot, rather than because a fit collapsed.
                    ["kind"] = edge.Exact.IsLine
                        ? "line"
                        : edge.Exact.IsBuried ? "buried" : "planar",
                    // Kept beside a buried area: the line those faces would touch along if the
                    // overlap were removed, which is the weaker reading of the same joint.
                    ["line_length"] = Math.Round(edge.Exact.LineLength, 3),
                    ["skew_deg"] = Math.Round(edge.Exact.SkewDegrees, 2),
                    // What the other candidate rule would have given. The normal here comes
                    // from the face being pressed into; the bisector of the two face normals
                    // is the alternative, and this is the angle between them.
                    ["bisector_deg"] = Math.Round(edge.Exact.BisectorDegrees, 2)
                });
            }

            else
            {
                // Why the exact measurement did not land, in the same shape as the sampled
                // one's diagnostics. Zero regions on a side means nothing flat was found on
                // it; regions on both sides with no pairs means every combination failed the
                // parallel or offset test; pairs with no result means the intersection did.
                exactUnmeasured.Add(new JObject
                {
                    ["a"] = edge.A,
                    ["b"] = edge.B,
                    ["pairs"] = edge.Exact.Pairs,
                    ["regions_a"] = edge.Exact.RegionsA,
                    ["regions_b"] = edge.Exact.RegionsB
                });
            }

            if (!edge.Extent.IsValid)
            {
                // Why it was not measured, rather than only that it was not. A contact with
                // no samples was never sampled; one with samples but no faces from a body is
                // a grid that missed; one with faces on both sides is a pair the parallel
                // test rejected.
                unmeasured.Add(new JObject
                {
                    ["a"] = edge.A,
                    ["b"] = edge.B,
                    ["samples"] = edge.Extent.Samples,
                    ["faces_a"] = edge.Extent.FacesA,
                    ["faces_b"] = edge.Extent.FacesB
                });
            }
        }

        var result = new JObject
        {
            ["n"] = nodes,
            ["e"] = edges,
            ["contact_extent"] = extents,
            ["contact_extent_measured"] = extents.Count,
            ["contact_extent_unmeasured"] = unmeasured,
            ["contact_extent_exact"] = exact,
            ["contact_extent_exact_measured"] = exact.Count,
            ["contact_extent_exact_unmeasured"] = exactUnmeasured,
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

    private static JArray RoundVector(Rhino.Geometry.Vector3d vector)
    {
        // Six places: these are unit vectors, and rounding a direction to the two places a
        // position gets would tilt a metre-long bearing by millimetres.
        return new JArray(
            Math.Round(vector.X, 6),
            Math.Round(vector.Y, 6),
            Math.Round(vector.Z, 6));
    }

    private static JArray RoundPoint(Rhino.Geometry.Point3d point)
    {
        return new JArray(
            Math.Round(point.X, 2),
            Math.Round(point.Y, 2),
            Math.Round(point.Z, 2));
    }
}
