using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    private const int DefaultDocumentInfoLimit = 100;
    private const int MaxDocumentInfoLimit = 1000;
    private const int DefaultDocumentInfoFullLimit = 300;
    private const int DefaultDocumentInfoGeometryPointCap = 64;

    private static JToken SafeLayerProperty(System.Func<JToken> getter, JToken fallback = null)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback ?? JValue.CreateNull();
        }
    }

    private static int ReadBoundedInt(JObject parameters, string name, int fallback, int min, int max)
    {
        int value = parameters?[name]?.ToObject<int>() ?? fallback;
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }

    private static string ReadDocumentInfoDetail(JObject parameters)
    {
        string detail = parameters?["detail"]?.ToString()?.Trim().ToLowerInvariant() ?? "inventory";
        if (detail == "inventory" || detail == "summary" || detail == "full")
        {
            return detail;
        }
        throw new ArgumentException("detail must be one of: inventory, summary, full.");
    }

    private static string ReadDocumentInfoBboxMode(JObject parameters)
    {
        string mode = parameters?["bbox_mode"]?.ToString()?.Trim().ToLowerInvariant() ?? "intersects";
        if (mode == "intersects" || mode == "contains_center" || mode == "contained")
        {
            return mode;
        }
        throw new ArgumentException("bbox_mode must be one of: intersects, contains_center, contained.");
    }

    private static bool TryReadDocumentInfoPoint3d(JToken token, out Point3d point)
    {
        point = Point3d.Unset;
        if (token is not JArray arr || arr.Count != 3)
        {
            return false;
        }

        point = new Point3d(
            arr[0]?.ToObject<double>() ?? 0.0,
            arr[1]?.ToObject<double>() ?? 0.0,
            arr[2]?.ToObject<double>() ?? 0.0
        );
        return true;
    }

    private static bool TryReadDocumentInfoBbox(JObject parameters, out BoundingBox bbox)
    {
        bbox = BoundingBox.Unset;
        if (parameters?["bbox"] == null || parameters["bbox"].Type == JTokenType.Null)
        {
            return false;
        }

        if (parameters["bbox"] is not JArray arr || arr.Count != 2 ||
            !TryReadDocumentInfoPoint3d(arr[0], out Point3d first) ||
            !TryReadDocumentInfoPoint3d(arr[1], out Point3d second))
        {
            throw new ArgumentException("bbox must be [[min_x,min_y,min_z],[max_x,max_y,max_z]].");
        }

        bbox = new BoundingBox(
            new Point3d(
                Math.Min(first.X, second.X),
                Math.Min(first.Y, second.Y),
                Math.Min(first.Z, second.Z)
            ),
            new Point3d(
                Math.Max(first.X, second.X),
                Math.Max(first.Y, second.Y),
                Math.Max(first.Z, second.Z)
            )
        );

        if (!bbox.IsValid)
        {
            throw new ArgumentException("bbox must define a valid world axis-aligned bounding box.");
        }

        return true;
    }

    private static bool ContainsPoint(BoundingBox container, Point3d point)
    {
        return point.X >= container.Min.X && point.X <= container.Max.X &&
               point.Y >= container.Min.Y && point.Y <= container.Max.Y &&
               point.Z >= container.Min.Z && point.Z <= container.Max.Z;
    }

    private static bool ContainsBbox(BoundingBox container, BoundingBox candidate)
    {
        return ContainsPoint(container, candidate.Min) && ContainsPoint(container, candidate.Max);
    }

    private static bool BboxesIntersect(BoundingBox a, BoundingBox b)
    {
        return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
               a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
               a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
    }

    private static bool BboxMatches(BoundingBox query, BoundingBox candidate, string mode)
    {
        return mode switch
        {
            "contained" => ContainsBbox(query, candidate),
            "contains_center" => ContainsPoint(query, candidate.Center),
            _ => BboxesIntersect(query, candidate)
        };
    }

    private static JObject BuildDocumentObjectInventory(RhinoObject obj, string detail, bool includeBbox)
    {
        var objectInfo = new JObject
        {
            ["id"] = obj.Id.ToString(),
            ["name"] = obj.Name ?? "(unnamed)",
            ["type"] = obj.ObjectType.ToString(),
            ["layer"] = SafeGetLayerName(obj)
        };

        GeometryBase geometry = obj.Geometry;
        if (includeBbox && geometry != null)
        {
            BoundingBox bbox = geometry.GetBoundingBox(true);
            if (bbox.IsValid)
            {
                objectInfo["bbox"] = Serializer.SerializeBBox(bbox);
                objectInfo["bbox_frame"] = "world_aabb";
            }
        }

        if (detail != "summary" || geometry == null)
        {
            return objectInfo;
        }

        objectInfo["material"] = obj.Attributes.MaterialIndex.ToString();
        objectInfo["color"] = Serializer.SerializeColor(obj.Attributes.ObjectColor);

        double tolerance = DocumentUnits.AbsoluteTolerance();
        var descriptor = new JObject();

        switch (geometry)
        {
            case Point:
                descriptor["kind"] = "point";
                break;
            case LineCurve line:
                descriptor["kind"] = "line";
                descriptor["point_count"] = 2;
                descriptor["length"] = Math.Round(line.Line.Length, 2);
                break;
            case PolylineCurve polyline:
                descriptor["kind"] = "polyline";
                descriptor["point_count"] = polyline.ToArray().Length;
                descriptor["closed"] = polyline.IsClosed;
                descriptor["planar"] = polyline.IsPlanar(tolerance);
                break;
            case Curve curve:
                descriptor["kind"] = "curve";
                descriptor["degree"] = curve.Degree;
                descriptor["control_point_count"] = curve.ControlPolygon().Count();
                descriptor["closed"] = curve.IsClosed;
                descriptor["planar"] = curve.IsPlanar(tolerance);
                break;
            case Extrusion extrusion:
                descriptor["kind"] = "extrusion";
                descriptor["closed"] = extrusion.IsSolid;
                break;
            case Brep brep:
                descriptor["kind"] = brep.Faces.Count == 1 ? "surface" : "brep";
                descriptor["face_count"] = brep.Faces.Count;
                descriptor["edge_count"] = brep.Edges.Count;
                descriptor["solid"] = brep.IsSolid;
                break;
            case Mesh mesh:
                descriptor["kind"] = "mesh";
                descriptor["vertex_count"] = mesh.Vertices.Count;
                descriptor["face_count"] = mesh.Faces.Count;
                descriptor["closed"] = mesh.IsClosed;
                break;
            default:
                descriptor["kind"] = geometry.ObjectType.ToString();
                break;
        }

        objectInfo["geometry_summary"] = descriptor;
        return objectInfo;
    }

    private static string SafeGetLayerName(RhinoObject obj)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            int layerIndex = obj.Attributes.LayerIndex;
            if (doc != null && layerIndex >= 0)
            {
                var layer = doc.Layers[layerIndex];
                if (layer != null)
                {
                    return layer.FullPath ?? layer.Name ?? "(unknown)";
                }
            }
        }
        catch
        {
        }

        return "(unknown)";
    }

    public JObject GetDocumentInfo(JObject parameters)
    {
        string detail = ReadDocumentInfoDetail(parameters);
        int defaultLimit = detail == "full" ? DefaultDocumentInfoFullLimit : DefaultDocumentInfoLimit;
        int limit = ReadBoundedInt(parameters, "limit", defaultLimit, 1, MaxDocumentInfoLimit);
        int offset = ReadBoundedInt(parameters, "offset", 0, 0, int.MaxValue);
        bool includeBbox = parameters?["include_bbox"]?.ToObject<bool>() ?? true;
        bool hasSpatialFilter = TryReadDocumentInfoBbox(parameters, out BoundingBox queryBbox);
        string bboxMode = hasSpatialFilter ? ReadDocumentInfoBboxMode(parameters) : "intersects";
        int maxGeometryPoints = ReadBoundedInt(
            parameters,
            "max_geometry_points",
            DefaultDocumentInfoGeometryPointCap,
            2,
            1000
        );

        RhinoApp.WriteLine($"Getting document info detail={detail} limit={limit} offset={offset}...");

        var doc = RhinoDoc.ActiveDoc;

        var metaData = new JObject
        {
            ["name"] = doc.Name,
            ["date_created"] = doc.DateCreated,
            ["date_modified"] = doc.DateLastEdited,
            ["tolerance"] = doc.ModelAbsoluteTolerance,
            ["angle_tolerance"] = doc.ModelAngleToleranceDegrees,
            ["path"] = doc.Path,
            ["units"] = doc.ModelUnitSystem.ToString(),
        };

        var objectData = new JArray();

        var objects = doc.Objects
            .Where(docObject => docObject != null)
            .OrderBy(docObject => docObject.Id)
            .ToList();

        int skippedObjectErrors = 0;
        var matchedObjects = new List<RhinoObject>();
        if (hasSpatialFilter)
        {
            foreach (var docObject in objects)
            {
                try
                {
                    BoundingBox objectBbox = docObject.Geometry.GetBoundingBox(true);
                    if (objectBbox.IsValid && BboxMatches(queryBbox, objectBbox, bboxMode))
                    {
                        matchedObjects.Add(docObject);
                    }
                }
                catch (System.Exception ex)
                {
                    RhinoApp.WriteLine($"Skipping object in get_document_info spatial filter ({docObject?.Id}): {ex.Message}");
                    skippedObjectErrors++;
                }
            }
        }
        else
        {
            matchedObjects = objects;
        }

        foreach (var docObject in matchedObjects.Skip(offset).Take(limit))
        {
            try
            {
                objectData.Add(
                    detail == "full"
                        ? Serializer.RhinoObject(docObject, includeGeometrySummary: false, outlineMaxPoints: maxGeometryPoints)
                        : BuildDocumentObjectInventory(docObject, detail, includeBbox)
                );
            }
            catch (System.Exception ex)
            {
                RhinoApp.WriteLine($"Skipping object in get_document_info ({docObject?.Id}): {ex}");
                skippedObjectErrors++;
            }
        }

        var layerData = new JArray();

        int skippedLayerErrors = 0;
        foreach (var docLayer in doc.Layers.Take(limit))
        {
            try
            {
                layerData.Add(new JObject
                {
                    ["id"] = SafeLayerProperty(() => docLayer.Id.ToString(), "(unknown)"),
                    ["name"] = SafeLayerProperty(() => docLayer.Name, "(unnamed)"),
                    ["color"] = SafeLayerProperty(() => docLayer.Color.ToString(), "(unknown)"),
                    ["visible"] = SafeLayerProperty(() => docLayer.IsVisible, false),
                    ["locked"] = SafeLayerProperty(() => docLayer.IsLocked, false)
                });
            }
            catch (System.Exception ex)
            {
                RhinoApp.WriteLine($"Skipping layer in get_document_info ({docLayer?.Id}): {ex.Message}");
                skippedLayerErrors++;
            }
        }

        var result = new JObject
        {
            ["meta_data"] = metaData,
            ["detail"] = detail,
            ["object_count"] = objects.Count,
            ["objects_returned"] = objectData.Count,
            ["objects_offset"] = offset,
            ["objects_limit"] = limit,
            ["objects_truncated"] = (long)offset + limit < matchedObjects.Count,
            ["objects_skipped_errors"] = skippedObjectErrors,
            ["objects"] = objectData,
            ["layer_count"] = doc.Layers.Count,
            ["layers_returned"] = layerData.Count,
            ["layers_limit"] = limit,
            ["layers_truncated"] = doc.Layers.Count > limit,
            ["layers_skipped_errors"] = skippedLayerErrors,
            ["layers"] = layerData
        };

        if (hasSpatialFilter)
        {
            result["spatial_filter"] = new JObject
            {
                ["bbox"] = Serializer.SerializeBBox(queryBbox),
                ["bbox_frame"] = "world_aabb",
                ["bbox_mode"] = bboxMode,
                ["matched_objects"] = matchedObjects.Count
            };
        }

        RhinoApp.WriteLine($"Document info collected: {objectData.Count} objects");
        return result;
    }
}
