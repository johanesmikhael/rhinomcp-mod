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

        BoundingBox bbox0 = brep.GetBoundingBox(workingPlane);
        if (bbox0.IsValid)
        {
            Point3d c0 = bbox0.Center;
            Point3d centerWorld = workingPlane.PointAt(c0.X, c0.Y, c0.Z);
            workingPlane.Origin = centerWorld;
        }

        workingPlane = StabilizePlane(workingPlane);
        return workingPlane;
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
