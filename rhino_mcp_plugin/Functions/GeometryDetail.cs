using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
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
            return BuildOrtho3ObjectInfo(obj, includeWorld, outlineMaxPoints);
        }

        return BuildObbPoseObjectInfo(obj, includeWorld, outlineMaxPoints);
    }

    private JObject BuildObbPoseObjectInfo(RhinoObject obj, bool includeWorld, int outlineMaxPoints)
    {
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

    // ortho3: up to three informative orthographic outline views (top/front/right) in the
    // pose local frame. Edge-on (degenerate) and near-duplicate views are dropped.
    private JObject BuildOrtho3ObjectInfo(RhinoObject obj, bool includeWorld, int outlineMaxPoints)
    {
        GeometryBase g = obj?.Geometry;
        Brep brep = g as Brep;
        if (brep == null && g is Extrusion extrusion)
        {
            brep = extrusion.ToBrep();
        }
        Mesh mesh = g as Mesh;

        // ortho3 only meaningful for solids/meshes; everything else falls back to obb_pose.
        if (brep == null && mesh == null)
        {
            var fallback = BuildObbPoseObjectInfo(obj, includeWorld, outlineMaxPoints);
            if (fallback["geometry"] is JObject fg)
            {
                fg["ortho3_note"] = "ortho3 applies to solids/meshes only; returned obb_pose";
            }
            return fallback;
        }

        // 16 is too thin for a complex mesh/brep silhouette across three views;
        // default to a richer per-view budget when the caller did not set one.
        if (outlineMaxPoints <= 0)
        {
            outlineMaxPoints = 40;
        }

        var data = BuildDetailedObjectBaseInfo(obj);
        var geometry = new JObject();

        // Resolve canonical pose plane (same source obb_pose uses).
        JObject pose = GetOrBootstrapPose(obj);
        if (!TryReadPoseFrame(pose, out Vector3d xAxis, out Vector3d yAxis, out Vector3d zAxis, out Point3d origin))
        {
            return BuildObbPoseObjectInfo(obj, includeWorld, outlineMaxPoints);
        }

        Plane posePlane = new Plane(origin, xAxis, yAxis);
        double tolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;

        // OBB extents (bbox in pose frame) drive both the payload and the per-view area gate.
        BoundingBox obbBox = g.GetBoundingBox(posePlane);
        double eX = obbBox.IsValid ? obbBox.Max.X - obbBox.Min.X : 0.0;
        double eY = obbBox.IsValid ? obbBox.Max.Y - obbBox.Min.Y : 0.0;
        double eZ = obbBox.IsValid ? obbBox.Max.Z - obbBox.Min.Z : 0.0;

        geometry["obb"] = new JObject
        {
            ["extents"] = new JArray
            {
                Math.Round(eX, 2),
                Math.Round(eY, 2),
                Math.Round(eZ, 2)
            }
        };
        geometry["pose"] = pose;
        geometry["views_frame"] =
            "local pose; top=[X,Y] front=[X,Z] right=[Y,Z]; shared origin; silhouette (direction-agnostic)";

        // Three ortho planes derived from the pose frame (outer-loop silhouettes).
        var candidates = new List<Ortho3Candidate>
        {
            BuildOrtho3Candidate("top", new Plane(origin, xAxis, yAxis), eX * eY, brep, mesh, tolerance, outlineMaxPoints),
            BuildOrtho3Candidate("front", new Plane(origin, xAxis, zAxis), eX * eZ, brep, mesh, tolerance, outlineMaxPoints),
            BuildOrtho3Candidate("right", new Plane(origin, yAxis, zAxis), eY * eZ, brep, mesh, tolerance, outlineMaxPoints)
        };

        var views = new JArray();
        var dropped = new JObject();
        var kept = new List<Ortho3Candidate>();

        foreach (Ortho3Candidate c in candidates)
        {
            if (c == null)
            {
                continue;
            }

            // Degenerate gate: no closed outline, edge-on (tiny area ratio), or near-line aspect.
            if (!c.Valid || c.AreaRatio < 0.015 || c.Aspect < 0.06)
            {
                continue;
            }

            // Dedup: skip if signature matches an already-kept view (symmetric object).
            Ortho3Candidate match = kept.FirstOrDefault(k => IsRedundant(c, k));
            if (match != null)
            {
                dropped[c.Axis] = match.Axis;
                continue;
            }

            kept.Add(c);
            views.Add(BuildOrtho3ViewJson(c, includeWorld));
        }

        // Always return >=1 view: if the gate dropped everything, keep the largest-area candidate.
        if (kept.Count == 0)
        {
            Ortho3Candidate best = candidates
                .Where(c => c != null && c.Valid)
                .OrderByDescending(c => c.Area)
                .FirstOrDefault();
            if (best != null)
            {
                kept.Add(best);
                views.Add(BuildOrtho3ViewJson(best, includeWorld));
                dropped.Remove(best.Axis);
            }
        }

        geometry["views"] = views;
        if (dropped.Count > 0)
        {
            geometry["views_dropped"] = dropped;
        }

        data["geometry"] = geometry;
        return data;
    }

    private sealed class Ortho3Candidate
    {
        public string Axis;
        public bool Valid;
        public double Area;
        public double AreaRatio;
        public double Aspect;
        public double PerimNorm;
        public int PointCount;
        public JArray Local;
        public JArray World;
    }

    private Ortho3Candidate BuildOrtho3Candidate(
        string axis, Plane plane, double bboxPlaneArea,
        Brep brep, Mesh mesh, double tolerance, int outlineMaxPoints)
    {
        var candidate = new Ortho3Candidate { Axis = axis, Valid = false };

        JObject projected;
        try
        {
            projected = brep != null
                ? Serializer.ProjectBrepOutlineOntoPlane(brep, plane, tolerance, outlineMaxPoints)
                : Serializer.BuildProjectedMeshOutline(mesh, plane, tolerance, outlineMaxPoints);
        }
        catch
        {
            return candidate;
        }

        if (projected?["local"] is not JArray local || local.Count < 4)
        {
            return candidate;
        }

        bool closed = projected["closed"]?.ToObject<bool>() ?? false;
        if (!closed)
        {
            return candidate;
        }

        double area = projected["area"]?.ToObject<double>() ?? 0.0;

        // 2D outline bbox for aspect + perimeter for the dedup signature.
        double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
        var pts = new List<double[]>();
        foreach (JToken t in local)
        {
            if (t is JArray p && p.Count >= 2)
            {
                double u = p[0].ToObject<double>();
                double v = p[1].ToObject<double>();
                pts.Add(new[] { u, v });
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
        }
        if (pts.Count < 4)
        {
            return candidate;
        }

        double w = maxU - minU;
        double h = maxV - minV;
        double aspect = Math.Max(w, h) > 1e-9 ? Math.Min(w, h) / Math.Max(w, h) : 0.0;

        double perimeter = 0.0;
        for (int i = 1; i < pts.Count; i++)
        {
            double du = pts[i][0] - pts[i - 1][0];
            double dv = pts[i][1] - pts[i - 1][1];
            perimeter += Math.Sqrt(du * du + dv * dv);
        }

        candidate.Valid = true;
        candidate.Area = area;
        candidate.AreaRatio = bboxPlaneArea > 1e-9 ? area / bboxPlaneArea : 0.0;
        candidate.Aspect = aspect;
        candidate.PerimNorm = area > 1e-9 ? perimeter / Math.Sqrt(area) : 0.0;
        candidate.PointCount = pts.Count;
        candidate.Local = local;
        candidate.World = projected["world"] as JArray;
        return candidate;
    }

    private static bool IsRedundant(Ortho3Candidate a, Ortho3Candidate b)
    {
        const double rel = 0.05;
        return CloseRel(a.AreaRatio, b.AreaRatio, rel)
            && CloseRel(a.Aspect, b.Aspect, rel)
            && CloseRel(a.PerimNorm, b.PerimNorm, rel)
            && a.PointCount == b.PointCount;
    }

    private static bool CloseRel(double x, double y, double rel)
    {
        double denom = Math.Max(Math.Abs(x), Math.Abs(y));
        if (denom < 1e-9)
        {
            return true;
        }
        return Math.Abs(x - y) / denom <= rel;
    }

    private static JObject BuildOrtho3ViewJson(Ortho3Candidate c, bool includeWorld)
    {
        var view = new JObject
        {
            ["axis"] = c.Axis,
            ["points"] = c.Local
        };
        if (includeWorld && c.World != null)
        {
            view["points_world"] = c.World;
        }
        return view;
    }
}
