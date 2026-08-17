using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    public static bool TryGetPosePlane(GeometryBase geometry, out Plane plane, out bool isPlanar)
    {
        plane = Plane.WorldXY;
        isPlanar = false;
        if (geometry == null)
        {
            return false;
        }

        switch (geometry)
        {
            case LineCurve line:
                plane = BuildLinePlane(line.Line.From, line.Line.To);
                isPlanar = true;
                return true;
            case PolylineCurve polyline:
                return TryGetPosePlaneForCurve(polyline, polyline.ToArray(), out plane, out isPlanar);
            case Curve curve:
                var points = SampleCurvePoints(curve, 32);
                return TryGetPosePlaneForCurve(curve, points, out plane, out isPlanar);
            case Extrusion extrusion:
                var brepFromExtrusion = extrusion.ToBrep();
                if (brepFromExtrusion != null)
                {
                    plane = BuildBrepWorkingPlane(brepFromExtrusion);
                    isPlanar = true;
                    return true;
                }
                break;
            case Brep brep:
                plane = BuildBrepWorkingPlane(brep);
                isPlanar = true;
                return true;
            case Mesh mesh:
                if (TryBuildMeshWorkingPlane(mesh, out plane))
                {
                    isPlanar = false;
                    return true;
                }
                break;
        }

        BoundingBox bbox = geometry.GetBoundingBox(true);
        plane = Plane.WorldXY;
        plane.Origin = bbox.Center;
        return true;
    }

    private static JArray SerializeVector(Vector3d v)
    {
        return new JArray
        {
            Math.Round(v.X, 2),
            Math.Round(v.Y, 2),
            Math.Round(v.Z, 2)
        };
    }

    private static JArray SerializePoint2(double x, double y)
    {
        return new JArray
        {
            Math.Round(x, 2),
            Math.Round(y, 2)
        };
    }

    private static Plane BuildLinePlane(Point3d start, Point3d end)
    {
        Vector3d xAxis = end - start;
        double length = xAxis.Length;
        if (length <= RhinoMath.ZeroTolerance)
        {
            xAxis = Vector3d.XAxis;
        }
        else
        {
            xAxis.Unitize();
        }

        Vector3d up = Vector3d.ZAxis;
        if (Math.Abs(Vector3d.Multiply(xAxis, up)) > 0.99)
        {
            up = Vector3d.YAxis;
        }

        Vector3d yAxis = Vector3d.CrossProduct(up, xAxis);
        if (yAxis.Length <= RhinoMath.ZeroTolerance)
        {
            up = Vector3d.XAxis;
            yAxis = Vector3d.CrossProduct(up, xAxis);
        }
        yAxis.Unitize();

        Vector3d zAxis = Vector3d.CrossProduct(xAxis, yAxis);
        zAxis.Unitize();

        Point3d mid = start + (end - start) * 0.5;
        return new Plane(mid, xAxis, yAxis);
    }

    private static bool TryGetPosePlaneForCurve(Curve curve, IEnumerable<Point3d> points, out Plane plane, out bool isPlanar)
    {
        plane = Plane.WorldXY;
        isPlanar = false;
        if (curve == null)
        {
            return false;
        }

        double tolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
        isPlanar = curve.TryGetPlane(out plane, tolerance);
        if (!isPlanar)
        {
            BoundingBox bbox = curve.GetBoundingBox(true);
            plane = Plane.WorldXY;
            plane.Origin = bbox.Center;
            return true;
        }

        plane = CenterPlaneOnPoints(plane, points);
        plane = StabilizePlane(plane);
        return true;
    }

    private static Plane CenterPlaneOnPoints(Plane plane, IEnumerable<Point3d> points)
    {
        double minU = double.MaxValue, minV = double.MaxValue;
        double maxU = double.MinValue, maxV = double.MinValue;
        foreach (var pt in points)
        {
            if (!plane.ClosestParameter(pt, out double u, out double v))
            {
                u = pt.X;
                v = pt.Y;
            }
            if (u < minU) minU = u;
            if (v < minV) minV = v;
            if (u > maxU) maxU = u;
            if (v > maxV) maxV = v;
        }

        double centerU = (minU + maxU) * 0.5;
        double centerV = (minV + maxV) * 0.5;
        plane.Origin = plane.PointAt(centerU, centerV, 0.0);
        return plane;
    }

    private static Plane StabilizePlane(Plane plane)
    {
        if (Vector3d.Multiply(plane.ZAxis, Vector3d.ZAxis) < 0.0)
        {
            plane.Flip();
        }
        if (Vector3d.Multiply(plane.XAxis, Vector3d.XAxis) < 0.0)
        {
            plane.XAxis = -plane.XAxis;
            plane.YAxis = -plane.YAxis;
        }
        return plane;
    }

    private static void CanonicalizeVectorDirection(ref Vector3d vector)
    {
        double ax = Math.Abs(vector.X);
        double ay = Math.Abs(vector.Y);
        double az = Math.Abs(vector.Z);

        double dominant = vector.X;
        if (ay > ax && ay >= az)
        {
            dominant = vector.Y;
        }
        else if (az > ax && az > ay)
        {
            dominant = vector.Z;
        }

        if (dominant < 0.0)
        {
            vector = -vector;
        }
    }

    private static Plane CanonicalizeGeometryPlane(Plane plane)
    {
        if (!plane.IsValid)
        {
            return plane;
        }

        Vector3d zAxis = plane.ZAxis;
        if (!zAxis.Unitize())
        {
            return plane;
        }
        CanonicalizeVectorDirection(ref zAxis);

        Vector3d xAxis = plane.XAxis - (Vector3d.Multiply(plane.XAxis, zAxis) * zAxis);
        if (!xAxis.Unitize())
        {
            return plane;
        }
        CanonicalizeVectorDirection(ref xAxis);

        Vector3d yAxis = Vector3d.CrossProduct(zAxis, xAxis);
        if (!yAxis.Unitize())
        {
            return plane;
        }

        return new Plane(plane.Origin, xAxis, yAxis);
    }

    private static bool TryAlignBrepPlaneToGeometry(Brep brep, Plane referencePlane, out Plane alignedPlane)
    {
        alignedPlane = referencePlane;
        if (brep == null || !referencePlane.IsValid)
        {
            return false;
        }

        // A planar face fixes the OBB normal, but Rhino's TryGetPlane returns the
        // underlying surface's UV axes. Those axes may be arbitrarily rotated with
        // respect to the trimmed face boundary. Project the full Brep into that
        // plane and derive the in-plane axes from its minimum-area rectangle.
        var projected = new List<Point2d>();

        foreach (BrepVertex vertex in brep.Vertices)
        {
            Point3d point = vertex.Location;
            if (point.IsValid && referencePlane.ClosestParameter(point, out double u, out double v))
            {
                projected.Add(new Point2d(u, v));
            }
        }

        // Vertices are sufficient for polygonal walls. Edge samples also capture
        // extrema on curved trims, preventing a chord-only rectangle for curved Breps.
        foreach (BrepEdge edge in brep.Edges)
        {
            foreach (Point3d point in SampleCurvePoints(edge, 24))
            {
                if (point.IsValid && referencePlane.ClosestParameter(point, out double u, out double v))
                {
                    projected.Add(new Point2d(u, v));
                }
            }
        }

        if (!TryComputeMinimumAreaRectangleFrame(
                projected,
                out Vector2d xAxis2d,
                out Vector2d yAxis2d,
                out Point2d center2d))
        {
            return false;
        }

        Vector3d xAxis = (referencePlane.XAxis * xAxis2d.X) + (referencePlane.YAxis * xAxis2d.Y);
        Vector3d yAxis = (referencePlane.XAxis * yAxis2d.X) + (referencePlane.YAxis * yAxis2d.Y);
        Point3d origin = referencePlane.PointAt(center2d.X, center2d.Y, 0.0);
        Plane candidate = new Plane(origin, xAxis, yAxis);
        if (!candidate.IsValid)
        {
            return false;
        }

        alignedPlane = CanonicalizeGeometryPlane(candidate);
        return alignedPlane.IsValid;
    }

    private static Plane BuildBrepWorkingPlane(Brep brep)
    {
        if (brep == null)
        {
            return Plane.WorldXY;
        }

        double tolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
        Plane workingPlane = Plane.Unset;
        double bestArea = -1.0;

        foreach (var face in brep.Faces)
        {
            if (!face.IsPlanar(tolerance))
            {
                continue;
            }
            if (!face.TryGetPlane(out Plane facePlane, tolerance))
            {
                continue;
            }
            var amp = AreaMassProperties.Compute(face);
            if (amp == null)
            {
                continue;
            }
            double area = Math.Abs(amp.Area);
            if (area > bestArea)
            {
                bestArea = area;
                workingPlane = facePlane;
            }
        }

        if (!workingPlane.IsValid)
        {
            var amp = AreaMassProperties.Compute(brep);
            if (amp != null)
            {
                workingPlane = Plane.WorldXY;
                workingPlane.Origin = amp.Centroid;
            }
            else
            {
                workingPlane = Plane.WorldXY;
            }
        }

        if (workingPlane.IsValid && TryAlignBrepPlaneToGeometry(brep, workingPlane, out Plane alignedPlane))
        {
            workingPlane = alignedPlane;
        }

        BoundingBox bbox0 = brep.GetBoundingBox(workingPlane);
        if (bbox0.IsValid)
        {
            Point3d c0 = bbox0.Center;
            Point3d centerWorld = workingPlane.PointAt(c0.X, c0.Y, c0.Z);
            workingPlane.Origin = centerWorld;
        }

        workingPlane = CanonicalizeGeometryPlane(workingPlane);
        return workingPlane;
    }

    private static bool TryBuildMeshWorkingPlane(Mesh mesh, out Plane plane)
    {
        plane = Plane.WorldXY;
        if (mesh == null || mesh.Vertices.Count == 0)
        {
            return false;
        }

        BoundingBox bbox = mesh.GetBoundingBox(true);
        if (!bbox.IsValid)
        {
            return false;
        }

        var points2d = new List<Point2d>(mesh.Vertices.Count);
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            Point3f p = mesh.Vertices[i];
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y))
            {
                continue;
            }
            points2d.Add(new Point2d(p.X, p.Y));
        }

        if (points2d.Count < 2)
        {
            plane = Plane.WorldXY;
            plane.Origin = bbox.Center;
            return true;
        }

        Vector2d xAxis2d;
        Vector2d yAxis2d;
        Point2d center2d;
        if (!TryComputeMinimumAreaRectangleFrame(points2d, out xAxis2d, out yAxis2d, out center2d))
        {
            plane = Plane.WorldXY;
            plane.Origin = bbox.Center;
            return true;
        }

        Vector3d xAxis = new Vector3d(xAxis2d.X, xAxis2d.Y, 0.0);
        Vector3d yAxis = new Vector3d(yAxis2d.X, yAxis2d.Y, 0.0);
        Point3d origin = new Point3d(center2d.X, center2d.Y, bbox.Center.Z);
        plane = new Plane(origin, xAxis, yAxis);
        if (!plane.IsValid)
        {
            return false;
        }

        plane = StabilizePlane(plane);
        return true;
    }

    private static bool TryComputeMinimumAreaRectangleFrame(
        List<Point2d> points,
        out Vector2d xAxis,
        out Vector2d yAxis,
        out Point2d center)
    {
        xAxis = new Vector2d(1.0, 0.0);
        yAxis = new Vector2d(0.0, 1.0);
        center = Point2d.Origin;

        if (points == null || points.Count < 2)
        {
            return false;
        }

        int[] hullIndices;
        Curve hullCurve = PolylineCurve.CreateConvexHull2d(points.ToArray(), out hullIndices);
        if (hullCurve == null || hullIndices == null || hullIndices.Length < 2)
        {
            return false;
        }

        var hull = new List<Point2d>(hullIndices.Length);
        for (int i = 0; i < hullIndices.Length; i++)
        {
            int idx = hullIndices[i];
            if (idx < 0 || idx >= points.Count)
            {
                continue;
            }
            hull.Add(points[idx]);
        }
        if (hull.Count > 1 && hull[0].DistanceTo(hull[hull.Count - 1]) <= RhinoMath.ZeroTolerance)
        {
            hull.RemoveAt(hull.Count - 1);
        }

        if (hull.Count < 2)
        {
            return false;
        }

        const double eps = 1e-12;
        bool found = false;
        double bestArea = double.MaxValue;
        double bestWidth = -1.0;
        double bestAngle = double.MaxValue;
        Point2d bestCenter = Point2d.Origin;
        Vector2d bestX = new Vector2d(1.0, 0.0);
        Vector2d bestY = new Vector2d(0.0, 1.0);

        for (int i = 0; i < hull.Count; i++)
        {
            Point2d a = hull[i];
            Point2d b = hull[(i + 1) % hull.Count];
            Vector2d edge = b - a;
            double edgeLen = edge.Length;
            if (edgeLen <= RhinoMath.ZeroTolerance)
            {
                continue;
            }

            Vector2d ux = edge / edgeLen;
            Vector2d uy = new Vector2d(-ux.Y, ux.X);
            Canonicalize2dAxes(ref ux, ref uy);

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            for (int j = 0; j < hull.Count; j++)
            {
                Point2d p = hull[j];
                double u = Dot2d(p, ux);
                double v = Dot2d(p, uy);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            double width = maxU - minU;
            double height = maxV - minV;
            double area = width * height;
            double angle = Math.Abs(Math.Atan2(ux.Y, ux.X));

            double areaTolerance = found
                ? Math.Max(eps, Math.Max(Math.Abs(area), Math.Abs(bestArea)) * 1e-10)
                : eps;
            double widthTolerance = found
                ? Math.Max(eps, Math.Max(Math.Abs(width), Math.Abs(bestWidth)) * 1e-10)
                : eps;

            bool better = false;
            if (!found || area < bestArea - areaTolerance)
            {
                better = true;
            }
            else if (Math.Abs(area - bestArea) <= areaTolerance)
            {
                if (width > bestWidth + widthTolerance)
                {
                    better = true;
                }
                else if (Math.Abs(width - bestWidth) <= widthTolerance && angle < bestAngle - eps)
                {
                    better = true;
                }
            }

            if (!better)
            {
                continue;
            }

            found = true;
            bestArea = area;
            bestWidth = width;
            bestAngle = angle;
            bestX = ux;
            bestY = uy;
            double centerU = 0.5 * (minU + maxU);
            double centerV = 0.5 * (minV + maxV);
            bestCenter = new Point2d(
                centerU * ux.X + centerV * uy.X,
                centerU * ux.Y + centerV * uy.Y);
        }

        if (!found)
        {
            return false;
        }

        xAxis = bestX;
        yAxis = bestY;
        center = bestCenter;
        return true;
    }

    private static void Canonicalize2dAxes(ref Vector2d xAxis, ref Vector2d yAxis)
    {
        const double eps = 1e-12;
        if (xAxis.X < -eps || (Math.Abs(xAxis.X) <= eps && xAxis.Y < 0.0))
        {
            xAxis = -xAxis;
            yAxis = -yAxis;
        }
    }

    private static double Dot2d(Point2d p, Vector2d v)
    {
        return p.X * v.X + p.Y * v.Y;
    }

    private static List<Point3d> SampleCurvePoints(Curve curve, int maxPoints)
    {
        var points = new List<Point3d>();
        if (curve == null || maxPoints < 2)
        {
            return points;
        }

        if (curve.TryGetPolyline(out Polyline polyline))
        {
            var pts = polyline.ToArray();
            int step = Math.Max(1, (int)Math.Ceiling(pts.Length / (double)maxPoints));
            for (int i = 0; i < pts.Length; i += step)
            {
                points.Add(pts[i]);
            }
            if (pts.Length > 0 && points.Count == 0)
            {
                points.Add(pts[0]);
            }
            return points;
        }

        double[] t = curve.DivideByCount(Math.Max(2, maxPoints), true);
        if (t == null)
        {
            return points;
        }

        foreach (var ti in t)
        {
            points.Add(curve.PointAt(ti));
        }
        return points;
    }
}
