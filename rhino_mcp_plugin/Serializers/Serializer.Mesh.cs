using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializeMeshGeometry(
        Mesh mesh,
        bool includeGeometrySummary,
        int outlineMaxPoints,
        Plane? workingPlaneOverride = null,
        bool includeWorld = true,
        bool includeOutlines = true
    )
    {
        var geometry = new JObject();
        if (mesh == null)
        {
            return geometry;
        }

        if (!includeGeometrySummary)
        {
            geometry["bbox"] = SerializeBBox(mesh.GetBoundingBox(true));
            return geometry;
        }

        if (outlineMaxPoints <= 0)
        {
            outlineMaxPoints = 16;
        }

        try
        {
            double tolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            Plane workingPlane = workingPlaneOverride ?? Plane.WorldXY;
            if (workingPlaneOverride == null && !TryGetPosePlane(mesh, out workingPlane, out _))
            {
                BoundingBox fallbackBbox = mesh.GetBoundingBox(true);
                workingPlane = Plane.WorldXY;
                if (fallbackBbox.IsValid)
                {
                    workingPlane.Origin = fallbackBbox.Center;
                }
            }

            BoundingBox obbBox = mesh.GetBoundingBox(workingPlane);
            if (!obbBox.IsValid)
            {
                obbBox = mesh.GetBoundingBox(true);
                if (!obbBox.IsValid)
                {
                    throw new InvalidOperationException("Failed to compute mesh bounding box.");
                }

                if (workingPlaneOverride == null)
                {
                    workingPlane = Plane.WorldXY;
                    workingPlane.Origin = obbBox.Center;
                }
            }

            Box obb = new Box(workingPlane, obbBox);
            var obbCorners = new JArray();
            foreach (Point3d pt in obb.GetCorners())
            {
                obbCorners.Add(SerializePoint(pt));
            }

            geometry["obb"] = new JObject
            {
                ["extents"] = new JArray
                {
                    Math.Round(obb.X.Length, 2),
                    Math.Round(obb.Y.Length, 2),
                    Math.Round(obb.Z.Length, 2)
                }
            };
            if (includeWorld)
            {
                ((JObject)geometry["obb"])["world_corners"] = obbCorners;
            }

            if (includeOutlines)
            {
                var projected = BuildProjectedMeshOutline(mesh, workingPlane, tolerance, outlineMaxPoints);
                if (projected["local"] is JArray local && projected["world"] is JArray world)
                {
                    bool closed = projected["closed"]?.ToObject<bool>() ?? true;
                    geometry["proj_outline_local_xy"] = new JObject
                    {
                        ["points"] = local,
                        ["closed"] = closed
                    };
                    if (includeWorld)
                    {
                        geometry["proj_outline_world"] = new JObject
                        {
                            ["points"] = world,
                            ["closed"] = closed
                        };
                    }
                }
            }

            geometry["pose"] = new JObject
            {
                ["world_from_local"] = new JObject
                {
                    ["R"] = new JArray
                    {
                        new JArray
                        {
                            Math.Round(workingPlane.XAxis.X, 6),
                            Math.Round(workingPlane.YAxis.X, 6),
                            Math.Round(workingPlane.ZAxis.X, 6)
                        },
                        new JArray
                        {
                            Math.Round(workingPlane.XAxis.Y, 6),
                            Math.Round(workingPlane.YAxis.Y, 6),
                            Math.Round(workingPlane.ZAxis.Y, 6)
                        },
                        new JArray
                        {
                            Math.Round(workingPlane.XAxis.Z, 6),
                            Math.Round(workingPlane.YAxis.Z, 6),
                            Math.Round(workingPlane.ZAxis.Z, 6)
                        }
                    },
                    ["t"] = SerializePoint(workingPlane.Origin)
                }
            };
        }
        catch
        {
            // Keep serializer resilient; skip summary on failure.
        }

        if (geometry.Count == 0)
        {
            geometry["bbox"] = SerializeBBox(mesh.GetBoundingBox(true));
        }

        return geometry;
    }

    internal static JObject BuildProjectedMeshOutline(Mesh mesh, Plane workingPlane, double tolerance, int outlineMaxPoints)
    {
        if (outlineMaxPoints <= 0)
        {
            outlineMaxPoints = 16;
        }

        // Preferred path: true silhouette via Mesh.GetOutlines (keeps concavity, holes-as-outer).
        // Falls back to the convex-hull footprint below when it returns nothing usable.
        JObject silhouette = TryBuildMeshSilhouetteOutline(mesh, workingPlane, tolerance, outlineMaxPoints);
        if (silhouette != null)
        {
            return silhouette;
        }

        var points2d = new List<Point2d>();
        var vertices = mesh.Vertices;
        for (int i = 0; i < vertices.Count; i++)
        {
            Point3f p = vertices[i];
            Point3d pt = new Point3d(p.X, p.Y, p.Z);
            if (!workingPlane.ClosestParameter(pt, out double u, out double v))
            {
                u = pt.X;
                v = pt.Y;
            }
            points2d.Add(new Point2d(u, v));
        }

        if (points2d.Count < 3)
        {
            BoundingBox bbox = mesh.GetBoundingBox(workingPlane);
            var localRect = new JArray
            {
                SerializePoint2(bbox.Min.X, bbox.Min.Y),
                SerializePoint2(bbox.Max.X, bbox.Min.Y),
                SerializePoint2(bbox.Max.X, bbox.Max.Y),
                SerializePoint2(bbox.Min.X, bbox.Max.Y),
                SerializePoint2(bbox.Min.X, bbox.Min.Y)
            };
            var worldRect = new JArray
            {
                SerializePoint(workingPlane.PointAt(bbox.Min.X, bbox.Min.Y, 0.0)),
                SerializePoint(workingPlane.PointAt(bbox.Max.X, bbox.Min.Y, 0.0)),
                SerializePoint(workingPlane.PointAt(bbox.Max.X, bbox.Max.Y, 0.0)),
                SerializePoint(workingPlane.PointAt(bbox.Min.X, bbox.Max.Y, 0.0)),
                SerializePoint(workingPlane.PointAt(bbox.Min.X, bbox.Min.Y, 0.0))
            };
            double rectArea = Math.Abs((bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y));
            return new JObject
            {
                ["local"] = localRect,
                ["world"] = worldRect,
                ["closed"] = true,
                ["area"] = rectArea
            };
        }

        int[] hullIndices;
        Curve hullCurve = PolylineCurve.CreateConvexHull2d(points2d.ToArray(), out hullIndices);
        if (hullCurve == null)
        {
            return new JObject();
        }

        if (!hullCurve.TryGetPolyline(out Polyline hullPolyline))
        {
            PolylineCurve polylineCurve = hullCurve.ToPolyline(
                tolerance,
                RhinoMath.ToRadians(2.0),
                0.0,
                0.0
            );
            if (polylineCurve == null || !polylineCurve.TryGetPolyline(out hullPolyline))
            {
                return new JObject();
            }
        }

        var hull2d = new List<Point2d>();
        foreach (Point3d pt in hullPolyline)
        {
            hull2d.Add(new Point2d(pt.X, pt.Y));
        }

        bool closed = hullCurve.IsClosed;
        if (closed && hull2d.Count > 1 &&
            hull2d[0].DistanceTo(hull2d[hull2d.Count - 1]) <= tolerance)
        {
            hull2d.RemoveAt(hull2d.Count - 1);
        }

        List<Point2d> simplified = SimplifyPolyline(hull2d, tolerance, outlineMaxPoints);
        if (closed && simplified.Count > 0 &&
            simplified[0].DistanceTo(simplified[simplified.Count - 1]) > tolerance)
        {
            simplified.Add(simplified[0]);
        }

        var local = new JArray();
        var world = new JArray();
        foreach (Point2d pt in simplified)
        {
            local.Add(SerializePoint2(pt.X, pt.Y));
            world.Add(SerializePoint(workingPlane.PointAt(pt.X, pt.Y, 0.0)));
        }

        double hullArea = GetCurveAreaOrBboxArea(hullCurve, tolerance);
        return new JObject
        {
            ["local"] = local,
            ["world"] = world,
            ["closed"] = closed,
            ["area"] = hullArea
        };
    }

    // True silhouette outline via Mesh.GetOutlines(plane). Picks the largest-area loop,
    // simplifies it, and returns { local, world, closed, area } in the plane's UV frame.
    // Returns null when GetOutlines yields nothing usable so the caller falls back to hull.
    private static JObject TryBuildMeshSilhouetteOutline(Mesh mesh, Plane workingPlane, double tolerance, int outlineMaxPoints)
    {
        Polyline[] outlines;
        try
        {
            outlines = mesh.GetOutlines(workingPlane);
        }
        catch
        {
            return null;
        }

        if (outlines == null || outlines.Length == 0)
        {
            return null;
        }

        // Choose the largest-area loop (outer silhouette). Inner loops/holes dropped in v1.
        List<Point2d> best2d = null;
        double bestArea = -1.0;
        foreach (Polyline pl in outlines)
        {
            if (pl == null || pl.Count < 3)
            {
                continue;
            }

            var pts = new List<Point2d>();
            foreach (Point3d p in pl)
            {
                if (!workingPlane.ClosestParameter(p, out double u, out double v))
                {
                    u = p.X;
                    v = p.Y;
                }
                pts.Add(new Point2d(u, v));
            }

            double area = Math.Abs(PolygonSignedArea(pts));
            if (area > bestArea)
            {
                bestArea = area;
                best2d = pts;
            }
        }

        if (best2d == null || best2d.Count < 3)
        {
            return null;
        }

        if (best2d.Count > 1 && best2d[0].DistanceTo(best2d[best2d.Count - 1]) <= tolerance)
        {
            best2d.RemoveAt(best2d.Count - 1);
        }

        List<Point2d> simplified = SimplifyPolyline(best2d, tolerance, outlineMaxPoints);
        if (simplified.Count < 3)
        {
            return null;
        }
        if (simplified[0].DistanceTo(simplified[simplified.Count - 1]) > tolerance)
        {
            simplified.Add(simplified[0]);
        }

        var local = new JArray();
        var world = new JArray();
        foreach (Point2d pt in simplified)
        {
            local.Add(SerializePoint2(pt.X, pt.Y));
            world.Add(SerializePoint(workingPlane.PointAt(pt.X, pt.Y, 0.0)));
        }

        return new JObject
        {
            ["local"] = local,
            ["world"] = world,
            ["closed"] = true,
            ["area"] = bestArea
        };
    }

    private static double PolygonSignedArea(List<Point2d> pts)
    {
        if (pts == null || pts.Count < 3)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int i = 0; i < pts.Count; i++)
        {
            Point2d a = pts[i];
            Point2d b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum * 0.5;
    }
}
