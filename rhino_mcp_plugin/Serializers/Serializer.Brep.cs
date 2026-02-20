using System;
// Brep summary helpers.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializeBrepGeometry(Brep brep, bool includeGeometrySummary, int outlineMaxPoints, out string type)
    {
        type = brep.Faces.Count == 1 ? "SURFACE" : "BREP";
        var geometry = new JObject();
        if (!includeGeometrySummary)
        {
            var bbox = brep.GetBoundingBox(true);
            geometry["bbox"] = SerializeBBox(bbox);
            return geometry;
        }

        try
        {
            geometry = BuildBrepGeometrySummary(brep, outlineMaxPoints);
        }
        catch
        {
            // Keep serializer resilient; skip summary on failure.
        }

        return geometry;
    }

    private static double PointLineDistanceSquared(Point2d p, Point2d a, Point2d b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12)
        {
            double px = p.X - a.X;
            double py = p.Y - a.Y;
            return px * px + py * py;
        }

        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        double projX = a.X + t * dx;
        double projY = a.Y + t * dy;
        double vx = p.X - projX;
        double vy = p.Y - projY;
        return vx * vx + vy * vy;
    }

    private static double PointLineDistanceSquared(Point3d p, Point3d a, Point3d b)
    {
        Vector3d ab = b - a;
        double denom = ab.X * ab.X + ab.Y * ab.Y + ab.Z * ab.Z;
        if (denom < 1e-12)
        {
            return p.DistanceToSquared(a);
        }

        Vector3d ap = p - a;
        double t = (ap.X * ab.X + ap.Y * ab.Y + ap.Z * ab.Z) / denom;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        Point3d proj = a + t * ab;
        return p.DistanceToSquared(proj);
    }

    private static void RdpRecursive(List<Point2d> points, int first, int last, double tolSq, bool[] keep)
    {
        if (last <= first + 1)
        {
            return;
        }

        double maxDist = -1.0;
        int index = -1;
        Point2d a = points[first];
        Point2d b = points[last];

        for (int i = first + 1; i < last; i++)
        {
            double dist = PointLineDistanceSquared(points[i], a, b);
            if (dist > maxDist)
            {
                maxDist = dist;
                index = i;
            }
        }

        if (maxDist > tolSq && index != -1)
        {
            keep[index] = true;
            RdpRecursive(points, first, index, tolSq, keep);
            RdpRecursive(points, index, last, tolSq, keep);
        }
    }

    private static void RdpRecursive(List<Point3d> points, int first, int last, double tolSq, bool[] keep)
    {
        if (last <= first + 1)
        {
            return;
        }

        double maxDist = -1.0;
        int index = -1;
        Point3d a = points[first];
        Point3d b = points[last];

        for (int i = first + 1; i < last; i++)
        {
            double dist = PointLineDistanceSquared(points[i], a, b);
            if (dist > maxDist)
            {
                maxDist = dist;
                index = i;
            }
        }

        if (maxDist > tolSq && index != -1)
        {
            keep[index] = true;
            RdpRecursive(points, first, index, tolSq, keep);
            RdpRecursive(points, index, last, tolSq, keep);
        }
    }

    private static List<Point2d> SimplifyPolyline(List<Point2d> points, double tolerance, int maxPoints)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        bool[] keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        double tolSq = tolerance * tolerance;
        RdpRecursive(points, 0, points.Count - 1, tolSq, keep);

        var simplified = new List<Point2d>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                simplified.Add(points[i]);
            }
        }

        if (maxPoints > 1 && simplified.Count > maxPoints)
        {
            int step = (int)Math.Ceiling((simplified.Count - 1) / (double)(maxPoints - 1));
            var sampled = new List<Point2d>();
            for (int i = 0; i < simplified.Count; i += step)
            {
                sampled.Add(simplified[i]);
            }
            if (!sampled.Last().Equals(simplified.Last()))
            {
                sampled.Add(simplified.Last());
            }
            return sampled;
        }

        return simplified;
    }

    private static List<Point3d> SimplifyPolyline3d(List<Point3d> points, double tolerance, int maxPoints)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        bool[] keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        double tolSq = tolerance * tolerance;
        RdpRecursive(points, 0, points.Count - 1, tolSq, keep);

        var simplified = new List<Point3d>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                simplified.Add(points[i]);
            }
        }

        if (maxPoints > 1 && simplified.Count > maxPoints)
        {
            int step = (int)Math.Ceiling((simplified.Count - 1) / (double)(maxPoints - 1));
            var sampled = new List<Point3d>();
            for (int i = 0; i < simplified.Count; i += step)
            {
                sampled.Add(simplified[i]);
            }
            if (!sampled.Last().Equals(simplified.Last()))
            {
                sampled.Add(simplified.Last());
            }
            return sampled;
        }

        return simplified;
    }

    private static Curve ChooseOutlineCurve(IEnumerable<Curve> curves, double tolerance)
    {
        Curve best = null;
        bool bestClosed = false;
        double bestScore = -1.0;

        foreach (var curve in curves)
        {
            bool closed = curve.IsClosed;
            double score = curve.GetLength();

            if (closed && curve.IsPlanar(tolerance))
            {
                var amp = AreaMassProperties.Compute(curve);
                if (amp != null)
                {
                    score = Math.Abs(amp.Area);
                }
            }

            if (best == null ||
                (closed && !bestClosed) ||
                (closed == bestClosed && score > bestScore))
            {
                best = curve;
                bestClosed = closed;
                bestScore = score;
            }
        }

        return best;
    }

    private static Curve TryGetLoopCurve(BrepLoop loop, double tolerance)
    {
        var edgeCurves = new List<Curve>();
        foreach (var trim in loop.Trims)
        {
            var edge = trim.Edge;
            if (edge == null)
            {
                continue;
            }

            var curve = edge.DuplicateCurve();
            if (curve != null)
            {
                edgeCurves.Add(curve);
            }
        }

        if (edgeCurves.Count == 0)
        {
            return null;
        }

        Curve[] joined = Curve.JoinCurves(edgeCurves, tolerance);
        if (joined == null || joined.Length == 0)
        {
            return null;
        }

        return ChooseOutlineCurve(joined, tolerance);
    }

    private static JObject BuildBrepGeometrySummary(Brep brep, int outlineMaxPoints = 16)
    {
        if (brep == null)
        {
            throw new ArgumentNullException(nameof(brep));
        }

        if (outlineMaxPoints <= 0)
        {
            outlineMaxPoints = 16;
        }

        double tolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
        bool isSingleFace = brep.Faces.Count == 1;

        Plane workingPlane = BuildBrepWorkingPlane(brep);

        JArray localPoints = null;
        JArray worldPoints = null;
        bool isClosed = false;

        if (isSingleFace)
        {
            BrepFace face = brep.Faces[0];
            BrepLoop outerLoop = face.Loops.FirstOrDefault(loop => loop.LoopType == BrepLoopType.Outer);
            Curve edgeCurve = outerLoop != null ? TryGetLoopCurve(outerLoop, tolerance) : null;

            if (edgeCurve == null)
            {
                Curve bestCurve = null;
                double bestScore = -1.0;
                foreach (var loop in face.Loops)
                {
                    Curve loopCurve = TryGetLoopCurve(loop, tolerance);
                    if (loopCurve == null)
                    {
                        continue;
                    }

                    double score = loopCurve.GetLength();
                    if (loopCurve.IsClosed && loopCurve.IsPlanar(tolerance))
                    {
                        var amp = AreaMassProperties.Compute(loopCurve);
                        if (amp != null)
                        {
                            score = Math.Abs(amp.Area);
                        }
                    }

                    if (bestCurve == null || score > bestScore)
                    {
                        bestCurve = loopCurve;
                        bestScore = score;
                    }
                }

                edgeCurve = bestCurve;
            }

            if (edgeCurve == null)
            {
                throw new InvalidOperationException("Unable to extract surface edge curve.");
            }

            Polyline edgePolyline;
            if (!edgeCurve.TryGetPolyline(out edgePolyline))
            {
                PolylineCurve polylineCurve = edgeCurve.ToPolyline(
                    tolerance,
                    RhinoMath.ToRadians(2.0),
                    0.0,
                    0.0
                );
                if (!polylineCurve.TryGetPolyline(out edgePolyline))
                {
                    throw new InvalidOperationException("Unable to convert surface edges to polyline.");
                }
            }

            isClosed = edgeCurve.IsClosed;
            var worldPolylinePoints = new List<Point3d>();
            foreach (var pt in edgePolyline)
            {
                worldPolylinePoints.Add(pt);
            }

            if (isClosed && worldPolylinePoints.Count > 1 &&
                worldPolylinePoints[0].DistanceTo(worldPolylinePoints[worldPolylinePoints.Count - 1]) <= tolerance)
            {
                worldPolylinePoints.RemoveAt(worldPolylinePoints.Count - 1);
            }

            List<Point3d> simplifiedWorld = SimplifyPolyline3d(worldPolylinePoints, tolerance, outlineMaxPoints);
            if (isClosed && simplifiedWorld.Count > 0 &&
                simplifiedWorld[0].DistanceTo(simplifiedWorld[simplifiedWorld.Count - 1]) > tolerance)
            {
                simplifiedWorld.Add(simplifiedWorld[0]);
            }

            localPoints = new JArray();
            worldPoints = new JArray();
            foreach (var pt in simplifiedWorld)
            {
                if (!workingPlane.ClosestParameter(pt, out double u, out double v))
                {
                    u = pt.X;
                    v = pt.Y;
                }

                localPoints.Add(SerializePoint2(u, v));
                worldPoints.Add(Serializer.SerializePoint(pt));
            }
        }
        else
        {
            BoundingBox bbox3d = brep.GetBoundingBox(true);
            double diag = bbox3d.Diagonal.Length;
            if (diag <= RhinoMath.ZeroTolerance)
            {
                diag = 1.0;
            }

            Vector3d camDir = -workingPlane.ZAxis;
            Point3d camLoc = workingPlane.Origin - camDir * (diag * 2.0);
            Point3d target = workingPlane.Origin;

            var viewport = new RhinoViewport();
            viewport.ChangeToParallelProjection(true);
            viewport.SetCameraLocations(target, camLoc);
            viewport.CameraUp = workingPlane.YAxis;

            var hldParams = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = tolerance,
                Flatten = true
            };
            hldParams.SetViewport(viewport);
            hldParams.AddGeometry(brep, tag: null, occluding_sections: true);

            HiddenLineDrawing hld = HiddenLineDrawing.Compute(hldParams, false);
            if (hld == null)
            {
                throw new InvalidOperationException("Hidden line drawing failed.");
            }

            var segments = new List<Curve>();
            foreach (var segment in hld.Segments)
            {
                if (segment.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Visible &&
                    segment.CurveGeometry != null)
                {
                    segments.Add(segment.CurveGeometry.DuplicateCurve());
                }
            }

            if (segments.Count == 0)
            {
                throw new InvalidOperationException("No visible outline segments found.");
            }

            Curve[] joined = Curve.JoinCurves(segments, tolerance);
            var closed = joined.Where(c => c != null && c.IsClosed).ToList();

            if (closed.Count == 0)
            {
                double looseTol = tolerance * 10.0;
                Curve[] joinedLoose = Curve.JoinCurves(segments, looseTol);
                closed = joinedLoose.Where(c => c != null && c.IsClosed).ToList();
            }

            Curve outlineCurve = null;

            if (closed.Count == 0)
            {
                var pts2d = new List<Point2d>();
                foreach (var c in segments)
                {
                    if (c == null)
                    {
                        continue;
                    }

                    Point3d p0 = c.PointAtStart;
                    Point3d p1 = c.PointAtEnd;
                    Point3d pm = c.PointAtNormalizedLength(0.5);

                    pts2d.Add(new Point2d(p0.X, p0.Y));
                    pts2d.Add(new Point2d(p1.X, p1.Y));
                    pts2d.Add(new Point2d(pm.X, pm.Y));
                }

                if (pts2d.Count >= 3)
                {
                    int[] hullIndices;
                    var hull = PolylineCurve.CreateConvexHull2d(
                        pts2d.ToArray(),
                        out hullIndices
                    );

                    if (hull != null && hull.IsClosed)
                    {
                        outlineCurve = hull;
                    }
                }
            }

            if (outlineCurve == null && closed.Count == 0)
            {
                BoundingBox bbox = brep.GetBoundingBox(workingPlane);
                if (!bbox.IsValid)
                {
                    throw new InvalidOperationException("Failed to compute bbox in canonical plane.");
                }

                var rect = new Rectangle3d(
                    Plane.WorldXY,
                    new Interval(bbox.Min.X, bbox.Max.X),
                    new Interval(bbox.Min.Y, bbox.Max.Y)
                );

                outlineCurve = rect.ToNurbsCurve();
            }

            if (outlineCurve == null && closed.Count > 0)
            {
                outlineCurve = closed
                    .Select(c => new { Curve = c, Props = AreaMassProperties.Compute(c) })
                    .Where(x => x.Props != null)
                    .OrderByDescending(x => Math.Abs(x.Props.Area))
                    .Select(x => x.Curve)
                    .FirstOrDefault();
            }

            if (outlineCurve == null)
            {
                throw new InvalidOperationException("Unable to select outline curve.");
            }

            Polyline outlinePolyline;
            if (!outlineCurve.TryGetPolyline(out outlinePolyline))
            {
                PolylineCurve polylineCurve = outlineCurve.ToPolyline(
                    tolerance,
                    RhinoMath.ToRadians(2.0),
                    0.0,
                    0.0
                );
                if (!polylineCurve.TryGetPolyline(out outlinePolyline))
                {
                    throw new InvalidOperationException("Unable to convert outline to polyline.");
                }
            }

            isClosed = outlineCurve.IsClosed;
            var planePoints = new List<Point2d>();
            foreach (var pt in outlinePolyline)
            {
                planePoints.Add(new Point2d(pt.X, pt.Y));
            }

            if (isClosed && planePoints.Count > 1 &&
                planePoints[0].DistanceTo(planePoints[planePoints.Count - 1]) <= tolerance)
            {
                planePoints.RemoveAt(planePoints.Count - 1);
            }

            List<Point2d> simplified = SimplifyPolyline(planePoints, tolerance, outlineMaxPoints);
            if (isClosed && simplified.Count > 0 &&
                simplified[0].DistanceTo(simplified[simplified.Count - 1]) > tolerance)
            {
                simplified.Add(simplified[0]);
            }

            localPoints = new JArray();
            worldPoints = new JArray();
            foreach (var pt in simplified)
            {
                localPoints.Add(SerializePoint2(pt.X, pt.Y));
                worldPoints.Add(Serializer.SerializePoint(workingPlane.PointAt(pt.X, pt.Y, 0.0)));
            }
        }

        BoundingBox obbBox = brep.GetBoundingBox(workingPlane);
        if (!obbBox.IsValid)
        {
            throw new InvalidOperationException("Failed to compute bbox in canonical plane.");
        }
        Box obb = new Box(workingPlane, obbBox);
        var obbCorners = new JArray();
        foreach (var pt in obb.GetCorners())
        {
            obbCorners.Add(Serializer.SerializePoint(pt));
        }

        var shape = new JObject
        {
            ["obb"] = new JObject
            {
                ["extents"] = new JArray
                {
                    Math.Round(obb.X.Length, 2),
                    Math.Round(obb.Y.Length, 2),
                    Math.Round(obb.Z.Length, 2)
                },
                ["world_corners"] = obbCorners
            }
        };

        if (isSingleFace)
        {
            shape["surface_edges_local"] = new JObject
            {
                ["points"] = localPoints,
                ["closed"] = isClosed
            };
            shape["surface_edges_world"] = new JObject
            {
                ["points"] = worldPoints,
                ["closed"] = isClosed
            };
        }
        else
        {
            shape["proj_outline_local_xy"] = new JObject
            {
                ["points"] = localPoints,
                ["closed"] = isClosed
            };
            shape["proj_outline_world"] = new JObject
            {
                ["points"] = worldPoints,
                ["closed"] = isClosed
            };
        }

        return new JObject
        {
            ["obb"] = shape["obb"],
            ["surface_edges_local"] = shape["surface_edges_local"],
            ["surface_edges_world"] = shape["surface_edges_world"],
            ["proj_outline_local_xy"] = shape["proj_outline_local_xy"],
            ["proj_outline_world"] = shape["proj_outline_world"],
            ["pose"] = new JObject
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
            }
        };
    }
}
