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
    private const string PoseStorageKey = "rhinomcp.pose.v1";
    private const string ObbStorageKey = "rhinomcp.obb.v1";

    private static JArray BuildIdentityRotation()
    {
        return new JArray
        {
            new JArray { 1.0, 0.0, 0.0 },
            new JArray { 0.0, 1.0, 0.0 },
            new JArray { 0.0, 0.0, 1.0 }
        };
    }

    private static JArray BuildTranslationFromPoint(Point3d point)
    {
        return new JArray
        {
            Math.Round(point.X, 2),
            Math.Round(point.Y, 2),
            Math.Round(point.Z, 2)
        };
    }

    private static JObject BuildDefaultPose(Point3d point)
    {
        return new JObject
        {
            ["world_from_local"] = new JObject
            {
                ["R"] = BuildIdentityRotation(),
                ["t"] = BuildTranslationFromPoint(point)
            }
        };
    }

    private static JObject BuildPoseFromFrame(Vector3d xAxis, Vector3d yAxis, Vector3d zAxis, Point3d origin)
    {
        return new JObject
        {
            ["world_from_local"] = new JObject
            {
                ["R"] = new JArray
                {
                    new JArray { Math.Round(xAxis.X, 6), Math.Round(yAxis.X, 6), Math.Round(zAxis.X, 6) },
                    new JArray { Math.Round(xAxis.Y, 6), Math.Round(yAxis.Y, 6), Math.Round(zAxis.Y, 6) },
                    new JArray { Math.Round(xAxis.Z, 6), Math.Round(yAxis.Z, 6), Math.Round(zAxis.Z, 6) }
                },
                ["t"] = BuildTranslationFromPoint(origin)
            }
        };
    }

    private static bool TryReadPoseFrame(JObject pose, out Vector3d xAxis, out Vector3d yAxis, out Vector3d zAxis, out Point3d origin)
    {
        xAxis = Vector3d.XAxis;
        yAxis = Vector3d.YAxis;
        zAxis = Vector3d.ZAxis;
        origin = Point3d.Origin;

        if (pose?["world_from_local"] is not JObject worldFromLocal)
        {
            return false;
        }
        if (worldFromLocal["R"] is not JArray r || r.Count != 3)
        {
            return false;
        }
        if (r[0] is not JArray r0 || r0.Count != 3 ||
            r[1] is not JArray r1 || r1.Count != 3 ||
            r[2] is not JArray r2 || r2.Count != 3)
        {
            return false;
        }
        if (worldFromLocal["t"] is not JArray t || t.Count != 3)
        {
            return false;
        }

        xAxis = new Vector3d(
            r0[0]?.ToObject<double>() ?? 0.0,
            r1[0]?.ToObject<double>() ?? 0.0,
            r2[0]?.ToObject<double>() ?? 0.0
        );
        yAxis = new Vector3d(
            r0[1]?.ToObject<double>() ?? 0.0,
            r1[1]?.ToObject<double>() ?? 0.0,
            r2[1]?.ToObject<double>() ?? 0.0
        );
        zAxis = new Vector3d(
            r0[2]?.ToObject<double>() ?? 0.0,
            r1[2]?.ToObject<double>() ?? 0.0,
            r2[2]?.ToObject<double>() ?? 0.0
        );
        origin = new Point3d(
            t[0]?.ToObject<double>() ?? 0.0,
            t[1]?.ToObject<double>() ?? 0.0,
            t[2]?.ToObject<double>() ?? 0.0
        );
        return true;
    }

    private static JObject CanonicalizePose(JObject pose, Point3d fallbackOrigin)
    {
        if (!TryReadPoseFrame(pose, out Vector3d xAxis, out Vector3d yAxis, out Vector3d zAxis, out Point3d origin))
        {
            return BuildDefaultPose(fallbackOrigin);
        }

        if (!xAxis.Unitize())
        {
            xAxis = Vector3d.XAxis;
        }

        // Gram-Schmidt y against x; if degenerate, pick a stable fallback.
        yAxis = yAxis - (Vector3d.Multiply(yAxis, xAxis) * xAxis);
        if (!yAxis.Unitize())
        {
            Vector3d up = Math.Abs(Vector3d.Multiply(xAxis, Vector3d.ZAxis)) > 0.99 ? Vector3d.YAxis : Vector3d.ZAxis;
            yAxis = Vector3d.CrossProduct(up, xAxis);
            if (!yAxis.Unitize())
            {
                yAxis = Vector3d.YAxis;
            }
        }

        zAxis = Vector3d.CrossProduct(xAxis, yAxis);
        if (!zAxis.Unitize())
        {
            zAxis = Vector3d.ZAxis;
        }

        // Recompute y to ensure orthonormal right-handed frame.
        yAxis = Vector3d.CrossProduct(zAxis, xAxis);
        if (!yAxis.Unitize())
        {
            yAxis = Vector3d.YAxis;
        }

        return BuildPoseFromFrame(xAxis, yAxis, zAxis, origin);
    }

    private JObject BuildCanonicalPoseFromObject(RhinoObject obj, int outlineMaxPoints = 32)
    {
        var summary = Serializer.RhinoObject(obj, true, outlineMaxPoints);
        var geometry = summary["geometry"] as JObject;
        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;

        if (geometry?["pose"] is JObject poseFromSummary)
        {
            return CanonicalizePose((JObject)poseFromSummary.DeepClone(), center);
        }

        return BuildDefaultPose(center);
    }

    private bool TryReadStoredPose(RhinoObject obj, out JObject pose)
    {
        pose = null;
        string raw = obj?.Attributes?.GetUserString(PoseStorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            pose = JObject.Parse(raw);
            BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
            pose = CanonicalizePose(pose, bbox.Center);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WriteStoredPose(RhinoObject obj, JObject pose)
    {
        if (obj == null || pose == null)
        {
            return;
        }

        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        JObject canonical = CanonicalizePose((JObject)pose.DeepClone(), bbox.Center);
        obj.Attributes.SetUserString(PoseStorageKey, canonical.ToString(Newtonsoft.Json.Formatting.None));
        // Pose changes invalidate pose-dependent OBB/projection cache.
        obj.Attributes.DeleteUserString(ObbStorageKey);
        obj.CommitChanges();
    }

    private JObject GetOrBootstrapPose(RhinoObject obj)
    {
        JObject canonical = BuildCanonicalPoseFromObject(obj);
        if (TryReadStoredPose(obj, out JObject stored))
        {
            if (IsPoseEquivalentForValidity(stored, canonical))
            {
                return stored;
            }

            WriteStoredPose(obj, canonical);
            return canonical;
        }

        WriteStoredPose(obj, canonical);
        return canonical;
    }

    private JObject ApplyTransformToPose(JObject pose, Transform xform, Point3d fallbackOrigin)
    {
        if (!TryReadPoseFrame(pose, out Vector3d xAxis, out Vector3d yAxis, out Vector3d zAxis, out Point3d origin))
        {
            return BuildDefaultPose(fallbackOrigin);
        }

        xAxis.Transform(xform);
        yAxis.Transform(xform);
        zAxis.Transform(xform);
        origin.Transform(xform);

        JObject transformed = BuildPoseFromFrame(xAxis, yAxis, zAxis, origin);
        return CanonicalizePose(transformed, fallbackOrigin);
    }

    private void InjectStoredPoseIntoSummary(RhinoObject obj, JObject summary)
    {
        if (summary?["geometry"] is JObject geometry)
        {
            JObject pose = GetOrBootstrapPose(obj);
            geometry["pose"] = pose;
            ReprojectGeometryLocalsFromPose(geometry, pose);
        }
    }

    private static JObject BuildBboxSnapshot(BoundingBox bbox)
    {
        return new JObject
        {
            ["min"] = new JArray
            {
                Math.Round(bbox.Min.X, 6),
                Math.Round(bbox.Min.Y, 6),
                Math.Round(bbox.Min.Z, 6)
            },
            ["max"] = new JArray
            {
                Math.Round(bbox.Max.X, 6),
                Math.Round(bbox.Max.Y, 6),
                Math.Round(bbox.Max.Z, 6)
            }
        };
    }

    private static bool TryReadVec3(JToken token, out double x, out double y, out double z)
    {
        x = y = z = 0.0;
        if (token is not JArray arr || arr.Count < 3)
        {
            return false;
        }

        x = arr[0]?.ToObject<double>() ?? 0.0;
        y = arr[1]?.ToObject<double>() ?? 0.0;
        z = arr[2]?.ToObject<double>() ?? 0.0;
        return true;
    }

    private static bool IsBboxSnapshotValidForObject(JObject snapshot, RhinoObject obj)
    {
        if (snapshot == null || obj?.Geometry == null)
        {
            return false;
        }

        if (!TryReadVec3(snapshot["min"], out double minX, out double minY, out double minZ) ||
            !TryReadVec3(snapshot["max"], out double maxX, out double maxY, out double maxZ))
        {
            return false;
        }

        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
        {
            return false;
        }

        double tol = Math.Max((RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.01) * 10.0, 1e-4);
        return Math.Abs(minX - bbox.Min.X) <= tol &&
               Math.Abs(minY - bbox.Min.Y) <= tol &&
               Math.Abs(minZ - bbox.Min.Z) <= tol &&
               Math.Abs(maxX - bbox.Max.X) <= tol &&
               Math.Abs(maxY - bbox.Max.Y) <= tol &&
               Math.Abs(maxZ - bbox.Max.Z) <= tol;
    }

    private static JObject BuildStoredObbPayload(RhinoObject obj, JObject geometry)
    {
        if (obj?.Geometry == null || geometry == null)
        {
            return null;
        }

        if (geometry["obb"] is not JObject obb)
        {
            return null;
        }

        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
        {
            return null;
        }

        var payload = new JObject
        {
            ["bbox_world"] = BuildBboxSnapshot(bbox),
            ["obb"] = obb.DeepClone()
        };

        if (geometry["proj_outline_world"] is JObject projOutlineWorld)
        {
            payload["proj_outline_world"] = projOutlineWorld.DeepClone();
        }

        if (geometry["surface_edges_world"] is JObject surfaceEdgesWorld)
        {
            payload["surface_edges_world"] = surfaceEdgesWorld.DeepClone();
        }

        return payload;
    }

    private bool TryReadStoredObb(RhinoObject obj, out JObject payload)
    {
        payload = null;
        string raw = obj?.Attributes?.GetUserString(ObbStorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            if (JObject.Parse(raw) is not JObject parsed)
            {
                return false;
            }

            if (!IsBboxSnapshotValidForObject(parsed["bbox_world"] as JObject, obj))
            {
                return false;
            }

            if (parsed["obb"] is not JObject)
            {
                return false;
            }

            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WriteStoredObb(RhinoObject obj, JObject geometry)
    {
        if (obj == null || geometry == null)
        {
            return;
        }

        JObject payload = BuildStoredObbPayload(obj, geometry);
        if (payload == null)
        {
            return;
        }

        obj.Attributes.SetUserString(ObbStorageKey, payload.ToString(Newtonsoft.Json.Formatting.None));
        obj.CommitChanges();
    }

    private void RefreshStoredObbFromObject(RhinoObject obj, int outlineMaxPoints = 32)
    {
        if (obj?.Geometry == null)
        {
            return;
        }

        var summary = Serializer.RhinoObject(obj, includeGeometrySummary: true, outlineMaxPoints: outlineMaxPoints);
        if (summary["geometry"] is JObject geometry && geometry["obb"] is JObject)
        {
            WriteStoredObb(obj, geometry);
        }
    }

    private void InjectStoredObbIntoSummary(RhinoObject obj, JObject summary)
    {
        if (summary?["geometry"] is not JObject geometry)
        {
            return;
        }

        if (geometry["pose"] is JObject pose &&
            TryReadPoseFrame(pose, out Vector3d xAxis, out Vector3d yAxis, out _, out Point3d origin))
        {
            Plane posePlane = new Plane(origin, xAxis, yAxis);
            BoundingBox obbBox = obj.Geometry.GetBoundingBox(posePlane);
            if (obbBox.IsValid)
            {
                Box obb = new Box(posePlane, obbBox);
                var obbCorners = new JArray();
                foreach (var pt in obb.GetCorners())
                {
                    obbCorners.Add(new JArray
                    {
                        Math.Round(pt.X, 2),
                        Math.Round(pt.Y, 2),
                        Math.Round(pt.Z, 2)
                    });
                }

                geometry["obb"] = new JObject
                {
                    ["extents"] = new JArray
                    {
                        Math.Round(obb.X.Length, 2),
                        Math.Round(obb.Y.Length, 2),
                        Math.Round(obb.Z.Length, 2)
                    },
                    ["world_corners"] = obbCorners
                };
                WriteStoredObb(obj, geometry);
                return;
            }
        }

        if (TryReadStoredObb(obj, out JObject cachedPayload))
        {
            if (cachedPayload["obb"] is JObject cachedObb)
            {
                geometry["obb"] = cachedObb.DeepClone();
            }

            if (cachedPayload["proj_outline_world"] is JObject cachedProjOutline)
            {
                geometry["proj_outline_world"] = cachedProjOutline.DeepClone();
            }

            if (cachedPayload["surface_edges_world"] is JObject cachedSurfaceEdges)
            {
                geometry["surface_edges_world"] = cachedSurfaceEdges.DeepClone();
            }

            return;
        }

        if (geometry["obb"] is JObject)
        {
            WriteStoredObb(obj, geometry);
        }
    }

    private JObject BuildPublicAttributes(RhinoObject obj)
    {
        var attributes = Serializer.RhinoObjectAttributes(obj);
        attributes.Remove(PoseStorageKey);
        attributes.Remove(ObbStorageKey);
        return attributes;
    }

    private static bool TryParsePointToken(JToken token, out Point3d point)
    {
        point = Point3d.Origin;
        if (token is not JArray arr || arr.Count < 3)
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

    private static JArray BuildLocalPoint(Point3d worldPoint, Vector3d xAxis, Vector3d yAxis, Vector3d zAxis, Point3d origin)
    {
        Vector3d d = worldPoint - origin;
        return new JArray
        {
            Math.Round(Vector3d.Multiply(d, xAxis), 2),
            Math.Round(Vector3d.Multiply(d, yAxis), 2),
            Math.Round(Vector3d.Multiply(d, zAxis), 2)
        };
    }

    private static void ReprojectLineLocals(JObject geometry, Vector3d xAxis, Vector3d yAxis, Vector3d zAxis, Point3d origin)
    {
        if (geometry["world_start"] is JToken ws && TryParsePointToken(ws, out Point3d start))
        {
            geometry["local_start"] = BuildLocalPoint(start, xAxis, yAxis, zAxis, origin);
        }
        if (geometry["world_end"] is JToken we && TryParsePointToken(we, out Point3d end))
        {
            geometry["local_end"] = BuildLocalPoint(end, xAxis, yAxis, zAxis, origin);
        }
    }

    private static void ReprojectCurveOrPolylineLocals(JObject geometry, Vector3d xAxis, Vector3d yAxis, Vector3d zAxis, Point3d origin)
    {
        if (geometry["world_points"] is not JArray worldPoints)
        {
            return;
        }

        var localPoints = new JArray();
        foreach (var token in worldPoints)
        {
            if (TryParsePointToken(token, out Point3d world))
            {
                localPoints.Add(BuildLocalPoint(world, xAxis, yAxis, zAxis, origin));
            }
        }

        geometry["local_points"] = localPoints;
    }

    private static void ReprojectBrepLocals(JObject geometry, Vector3d xAxis, Vector3d yAxis, Vector3d zAxis, Point3d origin)
    {
        Plane posePlane = new Plane(origin, xAxis, yAxis);

        if (geometry["surface_edges_world"] is JObject sew &&
            sew["points"] is JArray worldPoints)
        {
            var local2d = new JArray();
            foreach (var token in worldPoints)
            {
                if (TryParsePointToken(token, out Point3d world))
                {
                    var local = BuildLocalPoint(world, xAxis, yAxis, zAxis, origin);
                    local2d.Add(new JArray { local[0], local[1] });
                }
            }

            geometry["surface_edges_local"] = new JObject
            {
                ["points"] = local2d,
                ["closed"] = sew["closed"]?.DeepClone()
            };
        }

        if (geometry["proj_outline_world"] is JObject pow &&
            pow["points"] is JArray outlineWorld)
        {
            var local2d = new JArray();
            var projectedWorld = new JArray();
            foreach (var token in outlineWorld)
            {
                if (TryParsePointToken(token, out Point3d world))
                {
                    Point3d projected = posePlane.ClosestPoint(world);
                    projectedWorld.Add(new JArray
                    {
                        Math.Round(projected.X, 2),
                        Math.Round(projected.Y, 2),
                        Math.Round(projected.Z, 2)
                    });

                    var local = BuildLocalPoint(projected, xAxis, yAxis, zAxis, origin);
                    local2d.Add(new JArray { local[0], local[1] });
                }
            }

            geometry["proj_outline_world"] = new JObject
            {
                ["points"] = projectedWorld,
                ["closed"] = pow["closed"]?.DeepClone()
            };
            geometry["proj_outline_local_xy"] = new JObject
            {
                ["points"] = local2d,
                ["closed"] = pow["closed"]?.DeepClone()
            };
        }
    }

    private static void ReprojectGeometryLocalsFromPose(JObject geometry, JObject pose)
    {
        if (!TryReadPoseFrame(pose, out Vector3d xAxis, out Vector3d yAxis, out Vector3d zAxis, out Point3d origin))
        {
            return;
        }

        ReprojectLineLocals(geometry, xAxis, yAxis, zAxis, origin);
        ReprojectCurveOrPolylineLocals(geometry, xAxis, yAxis, zAxis, origin);
        ReprojectBrepLocals(geometry, xAxis, yAxis, zAxis, origin);
    }

    private static bool IsPoseEquivalentForValidity(JObject storedPose, JObject derivedPose)
    {
        if (!TryReadPoseFrame(storedPose, out Vector3d sx, out Vector3d sy, out Vector3d sz, out Point3d st))
        {
            return false;
        }
        if (!TryReadPoseFrame(derivedPose, out Vector3d dx, out Vector3d dy, out Vector3d dz, out Point3d dt))
        {
            return false;
        }

        double docTol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.01;
        double translationTol = Math.Max(docTol * 10.0, 0.1);
        if (st.DistanceTo(dt) > translationTol)
        {
            return false;
        }

        // Robust against axis sign flips and order permutations from geometric ambiguity.
        Vector3d[] a = { sx, sy, sz };
        Vector3d[] b = { dx, dy, dz };
        int[][] perms = new int[][]
        {
            new [] { 0, 1, 2 },
            new [] { 0, 2, 1 },
            new [] { 1, 0, 2 },
            new [] { 1, 2, 0 },
            new [] { 2, 0, 1 },
            new [] { 2, 1, 0 }
        };

        const double axisCosTol = 0.995;
        foreach (var p in perms)
        {
            if (Math.Abs(Vector3d.Multiply(a[0], b[p[0]])) >= axisCosTol &&
                Math.Abs(Vector3d.Multiply(a[1], b[p[1]])) >= axisCosTol &&
                Math.Abs(Vector3d.Multiply(a[2], b[p[2]])) >= axisCosTol)
            {
                return true;
            }
        }

        return false;
    }

    private static JArray ResolveRotationMatrix(JObject geometry)
    {
        if (geometry?["pose"] is JObject pose &&
            pose["world_from_local"] is JObject worldFromLocal &&
            worldFromLocal["R"] is JArray rotation)
        {
            return (JArray)rotation.DeepClone();
        }

        return BuildIdentityRotation();
    }

    private JArray GetObjectPoseRotationMatrix(RhinoObject obj)
    {
        var pose = GetOrBootstrapPose(obj);
        if (pose["world_from_local"] is JObject worldFromLocal &&
            worldFromLocal["R"] is JArray rotation)
        {
            return (JArray)rotation.DeepClone();
        }
        return BuildIdentityRotation();
    }

    private Point3d GetObjectPoseTranslationPoint(RhinoObject obj)
    {
        var pose = GetOrBootstrapPose(obj);
        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        var t = BuildTranslationFromPoint(bbox.Center);
        if (pose["world_from_local"] is JObject worldFromLocal &&
            worldFromLocal["t"] is JArray translation)
        {
            t = (JArray)translation.DeepClone();
        }
        double x = t[0]?.ToObject<double>() ?? 0.0;
        double y = t[1]?.ToObject<double>() ?? 0.0;
        double z = t[2]?.ToObject<double>() ?? 0.0;
        return new Point3d(x, y, z);
    }

    private static JArray ResolvePosition(JObject geometry, Point3d fallbackCenter)
    {
        if (geometry == null)
        {
            return BuildTranslationFromPoint(fallbackCenter);
        }

        if (geometry["pose"] is JObject pose &&
            pose["world_from_local"] is JObject worldFromLocal &&
            worldFromLocal["t"] is JArray translation)
        {
            return (JArray)translation.DeepClone();
        }

        if (geometry["start"] is JArray lineStart && geometry["end"] is JArray lineEnd)
        {
            double sx = lineStart[0]?.ToObject<double>() ?? 0.0;
            double sy = lineStart[1]?.ToObject<double>() ?? 0.0;
            double sz = lineStart[2]?.ToObject<double>() ?? 0.0;
            double ex = lineEnd[0]?.ToObject<double>() ?? 0.0;
            double ey = lineEnd[1]?.ToObject<double>() ?? 0.0;
            double ez = lineEnd[2]?.ToObject<double>() ?? 0.0;
            return new JArray
            {
                Math.Round((sx + ex) / 2.0, 2),
                Math.Round((sy + ey) / 2.0, 2),
                Math.Round((sz + ez) / 2.0, 2)
            };
        }

        return BuildTranslationFromPoint(fallbackCenter);
    }

    private JObject BuildMinimalObjectState(RhinoObject obj, IEnumerable<string> changedFields, JObject explicitUpdated = null)
    {
        var summary = Serializer.RhinoObject(obj, true, 32);
        InjectStoredPoseIntoSummary(obj, summary);
        var geometry = summary["geometry"] as JObject;
        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;

        var updated = new JObject();
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (changedFields != null)
        {
            foreach (var field in changedFields)
            {
                if (!string.IsNullOrWhiteSpace(field))
                {
                    changed.Add(field);
                }
            }
        }

        if (changed.Contains("pose"))
        {
            if (geometry?["pose"] is JObject pose)
            {
                updated["pose"] = pose.DeepClone();
            }
            else
            {
                updated["pose"] = BuildDefaultPose(center);
            }
        }

        if (changed.Contains("position"))
        {
            updated["position"] = ResolvePosition(geometry, center);
        }

        if (changed.Contains("scale") && explicitUpdated?["scale"] != null)
        {
            updated["scale"] = explicitUpdated["scale"].DeepClone();
        }

        if (changed.Contains("layer") && summary["layer"] != null)
        {
            updated["layer"] = summary["layer"].DeepClone();
        }

        if (changed.Contains("color") && summary["color"] != null)
        {
            updated["color"] = summary["color"].DeepClone();
        }

        if (changed.Contains("name") && summary["name"] != null)
        {
            updated["name"] = summary["name"].DeepClone();
        }

        if (explicitUpdated != null)
        {
            foreach (var property in explicitUpdated.Properties())
            {
                if (property.Value == null)
                {
                    continue;
                }

                if (updated[property.Name] == null)
                {
                    updated[property.Name] = property.Value.DeepClone();
                }
            }
        }

        return new JObject
        {
            ["id"] = summary["id"],
            ["name"] = summary["name"],
            ["updated"] = updated,
            ["changed_fields"] = new JArray(updated.Properties().Select(p => p.Name))
        };
    }

    private double castToDouble(JToken token)
    {
        return token?.ToObject<double>() ?? 0;
    }
    private double[] castToDoubleArray(JToken token)
    {
        return token?.ToObject<double[]>() ?? new double[] { 0, 0, 0 };
    }
    private double[][] castToDoubleArray2D(JToken token)
    {
        List<double[]> result = new List<double[]>();
        foreach (var t in (JArray)token)
        {
            double[] inner = castToDoubleArray(t);
            result.Add(inner);
        }
        return result.ToArray();
    }
    private int castToInt(JToken token)
    {
        return token?.ToObject<int>() ?? 0;
    }
    private int[] castToIntArray(JToken token)
    {
        return token?.ToObject<int[]>() ?? new int[] { 0, 0, 0 };
    }

    private bool[] castToBoolArray(JToken token)
    {
        return token?.ToObject<bool[]>() ?? new bool[] { false, false };
    }

    private List<string> castToStringList(JToken token)
    {
        return token?.ToObject<List<string>>() ?? new List<string>();
    }

    private bool castToBool(JToken token)
    {
        return token?.ToObject<bool>() ?? false;
    }

    private string castToString(JToken token)
    {
        return token?.ToString();
    }

    private Guid castToGuid(JToken token)
    {
        var guid = token?.ToString();
        if (guid == null) return Guid.Empty;
        return new Guid(guid);
    }

    private List<Point3d> castToPoint3dList(JToken token)
    {
        double[][] points = castToDoubleArray2D(token);
        var ptList = new List<Point3d>();
        foreach (var point in points)
        {
            ptList.Add(new Point3d(point[0], point[1], point[2]));
        }
        return ptList;
    }

    private Point3d castToPoint3d(JToken token)
    {
        double[] point = castToDoubleArray(token);
        return new Point3d(point[0], point[1], point[2]);
    }

    private RhinoObject getObjectByIdOrName(JObject parameters)
    {
        string objectId = parameters["id"]?.ToString();
        string objectName = parameters["name"]?.ToString();

        var doc = RhinoDoc.ActiveDoc;
        RhinoObject obj = null;

        if (!string.IsNullOrEmpty(objectId))
            obj = doc.Objects.Find(new Guid(objectId));
        else if (!string.IsNullOrEmpty(objectName))
        {
            // we assume there's only one of the object with the given name
            var objs = doc.Objects.GetObjectList(new ObjectEnumeratorSettings() { NameFilter = objectName }).ToList();
            if (objs == null) throw new InvalidOperationException($"Object with name {objectName} not found.");
            if (objs.Count > 1) throw new InvalidOperationException($"Multiple objects with name {objectName} found.");
            obj = objs[0];
        }

        if (obj == null)
            throw new InvalidOperationException($"Object with ID {objectId} not found");
        return obj;
    }

    private Transform applyRotation(JObject parameters, GeometryBase geometry)
    {
        double[] rotation = parameters["rotation"].ToObject<double[]>();
        var xform = Transform.Identity;

        // Calculate the center for rotation
        BoundingBox bbox = geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;

        // Create rotation transformations (in radians)
        Transform rotX = Transform.Rotation(rotation[0], Vector3d.XAxis, center);
        Transform rotY = Transform.Rotation(rotation[1], Vector3d.YAxis, center);
        Transform rotZ = Transform.Rotation(rotation[2], Vector3d.ZAxis, center);

        // Apply transformations
        xform *= rotX;
        xform *= rotY;
        xform *= rotZ;

        return xform;
    }

    private Transform applyRotationMatrix(JObject parameters, GeometryBase geometry)
    {
        double[][] matrix = castToDoubleArray2D(parameters["rotation_matrix"]);
        if (matrix.Length != 3 || matrix.Any(row => row.Length != 3))
        {
            throw new InvalidOperationException("rotation_matrix must be a 3x3 matrix.");
        }
        bool invertRotationMatrix = castToBool(parameters["invert_rotation_matrix"]);
        if (invertRotationMatrix)
        {
            // For proper rotation matrices, inverse(R) = transpose(R).
            matrix = new double[][]
            {
                new double[] { matrix[0][0], matrix[1][0], matrix[2][0] },
                new double[] { matrix[0][1], matrix[1][1], matrix[2][1] },
                new double[] { matrix[0][2], matrix[1][2], matrix[2][2] },
            };
        }

        Transform rot = Transform.Identity;
        rot.M00 = matrix[0][0];
        rot.M01 = matrix[0][1];
        rot.M02 = matrix[0][2];
        rot.M10 = matrix[1][0];
        rot.M11 = matrix[1][1];
        rot.M12 = matrix[1][2];
        rot.M20 = matrix[2][0];
        rot.M21 = matrix[2][1];
        rot.M22 = matrix[2][2];
        rot.M33 = 1.0;

        BoundingBox bbox = geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;

        Transform toOrigin = Transform.Translation(-center.X, -center.Y, -center.Z);
        Transform back = Transform.Translation(center.X, center.Y, center.Z);

        return back * rot * toOrigin;
    }

    private Transform applyRotationMatrixAtPivot(JObject parameters)
    {
        double[][] matrix = castToDoubleArray2D(parameters["rotation_matrix"]);
        if (matrix.Length != 3 || matrix.Any(row => row.Length != 3))
        {
            throw new InvalidOperationException("rotation_matrix must be a 3x3 matrix.");
        }
        bool invertRotationMatrix = castToBool(parameters["invert_rotation_matrix"]);
        if (invertRotationMatrix)
        {
            // For proper rotation matrices, inverse(R) = transpose(R).
            matrix = new double[][]
            {
                new double[] { matrix[0][0], matrix[1][0], matrix[2][0] },
                new double[] { matrix[0][1], matrix[1][1], matrix[2][1] },
                new double[] { matrix[0][2], matrix[1][2], matrix[2][2] },
            };
        }

        Transform rot = Transform.Identity;
        rot.M00 = matrix[0][0];
        rot.M01 = matrix[0][1];
        rot.M02 = matrix[0][2];
        rot.M10 = matrix[1][0];
        rot.M11 = matrix[1][1];
        rot.M12 = matrix[1][2];
        rot.M20 = matrix[2][0];
        rot.M21 = matrix[2][1];
        rot.M22 = matrix[2][2];
        rot.M33 = 1.0;

        Point3d pivot = castToPoint3d(parameters["pivot"]);
        Transform toOrigin = Transform.Translation(-pivot.X, -pivot.Y, -pivot.Z);
        Transform back = Transform.Translation(pivot.X, pivot.Y, pivot.Z);

        return back * rot * toOrigin;
    }

    private Transform applyTranslation(JObject parameters)
    {
        double[] translation = parameters["translation"].ToObject<double[]>();
        var xform = Transform.Identity;
        Vector3d move = new Vector3d(translation[0], translation[1], translation[2]);
        xform *= Transform.Translation(move);

        return xform;
    }

    private Transform applyScale(JObject parameters, GeometryBase geometry)
    {
        double[] scale = parameters["scale"].ToObject<double[]>();
        var xform = Transform.Identity;

        // Calculate the center for scaling
        BoundingBox bbox = geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;
        Plane plane = Plane.WorldXY;
        plane.Origin = center;

        // Create scale transformation
        Transform scaleTransform = Transform.Scale(plane, scale[0], scale[1], scale[2]);
        xform *= scaleTransform;

        return xform;
    }
}
