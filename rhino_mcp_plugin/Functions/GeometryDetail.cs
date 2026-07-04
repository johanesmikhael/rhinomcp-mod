using System;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    private static string ReadGeometryDetail(JObject parameters)
    {
        string detail = parameters?["geometry_detail"]?.ToString()?.Trim().ToLowerInvariant() ?? "obb_pose";
        if (detail == "bbox" || detail == "obb_pose" || detail == "ortho3")
        {
            return detail;
        }

        throw new ArgumentException("geometry_detail must be one of: bbox, obb_pose, ortho3.");
    }

    private static bool ReadIncludeWorld(JObject parameters)
    {
        return parameters?["include_world"]?.ToObject<bool>() ?? false;
    }

    private static string ResolveDetailedObjectType(RhinoObject obj)
    {
        if (obj?.Geometry is Point)
        {
            return "POINT";
        }
        if (obj?.Geometry is LineCurve)
        {
            return "LINE";
        }
        if (obj?.Geometry is PolylineCurve)
        {
            return "POLYLINE";
        }
        if (obj?.Geometry is Curve)
        {
            return "CURVE";
        }
        if (obj?.Geometry is Extrusion)
        {
            return "EXTRUSION";
        }
        if (obj?.Geometry is Brep brep)
        {
            return brep.Faces.Count == 1 ? "SURFACE" : "BREP";
        }
        if (obj?.Geometry is Rhino.Geometry.Mesh)
        {
            return "MESH";
        }

        return obj?.ObjectType.ToString() ?? "UNKNOWN";
    }

    private JObject BuildDetailedObjectBaseInfo(RhinoObject obj)
    {
        return new JObject
        {
            ["id"] = obj?.Id.ToString() ?? "(unknown)",
            ["name"] = obj?.Name ?? "(unnamed)",
            ["type"] = ResolveDetailedObjectType(obj),
            ["layer"] = SafeGetLayerName(obj),
            ["material"] = obj?.Attributes?.MaterialIndex.ToString() ?? "-1",
            ["color"] = obj?.Attributes != null
                ? Serializer.SerializeColor(obj.Attributes.ObjectColor)
                : Serializer.SerializeColor(System.Drawing.Color.Black)
        };
    }

    private JObject BuildBboxDetailObjectInfo(RhinoObject obj)
    {
        var data = BuildDetailedObjectBaseInfo(obj);
        var geometry = new JObject();

        if (obj?.Geometry != null)
        {
            BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
            if (bbox.IsValid)
            {
                geometry["bbox"] = Serializer.SerializeBBox(bbox);
                geometry["bbox_frame"] = "world_aabb";
            }
        }

        data["geometry"] = geometry;
        return data;
    }

    private static void RemoveWorldDuplicates(JObject geometry)
    {
        if (geometry == null)
        {
            return;
        }

        geometry.Remove("world_start");
        geometry.Remove("world_end");
        geometry.Remove("world_points");

        if (geometry["obb"] is JObject obb)
        {
            obb.Remove("world_corners");
        }
    }

    private static void RemoveOutlineGeometry(JObject geometry)
    {
        if (geometry == null)
        {
            return;
        }

        geometry.Remove("proj_outline_local_xy");
        geometry.Remove("proj_outline_world");
        geometry.Remove("surface_edges_local");
        geometry.Remove("surface_edges_world");
    }

    private JObject BuildGeometryDetailObjectInfo(
        RhinoObject obj,
        string geometryDetail,
        bool includeWorld,
        int outlineMaxPoints
    )
    {
        if (geometryDetail == "bbox")
        {
            return BuildBboxDetailObjectInfo(obj);
        }

        if (geometryDetail == "ortho3")
        {
            throw new NotImplementedException("geometry_detail='ortho3' is not implemented yet.");
        }

        var data = Serializer.RhinoObject(
            obj,
            includeGeometrySummary: true,
            outlineMaxPoints: outlineMaxPoints,
            includeWorld: includeWorld,
            includeOutlines: false
        );
        InjectStoredPoseIntoSummary(obj, data);
        InjectStoredObbIntoSummary(obj, data);

        if (data["geometry"] is JObject geometry)
        {
            RemoveOutlineGeometry(geometry);
            if (!includeWorld)
            {
                RemoveWorldDuplicates(geometry);
            }
        }

        return data;
    }
}
