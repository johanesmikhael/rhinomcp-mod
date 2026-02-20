using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject CreateObject(JObject parameters)
    {
        // parse meta data
        string type = castToString(parameters.SelectToken("type"));
        string name = castToString(parameters.SelectToken("name"));
        bool customColor = parameters.ContainsKey("color");
        int[] color = castToIntArray(parameters.SelectToken("color"));
        JObject geoParams = (JObject)parameters.SelectToken("params");

        var doc = RhinoDoc.ActiveDoc;
        Guid objectId = Guid.Empty;

        // Create a box centered at the specified point
        switch (type)
        {
            case "POINT":
                double x = castToDouble(geoParams.SelectToken("x"));
                double y = castToDouble(geoParams.SelectToken("y"));
                double z = castToDouble(geoParams.SelectToken("z"));
                objectId = doc.Objects.AddPoint(x, y, z);
                break;
            case "LINE":
                double[] start = castToDoubleArray(geoParams.SelectToken("start"));
                double[] end = castToDoubleArray(geoParams.SelectToken("end"));
                var ptStart = new Point3d(start[0], start[1], start[2]);
                var ptEnd = new Point3d(end[0], end[1], end[2]);
                objectId = doc.Objects.AddLine(ptStart, ptEnd);
                break;
            case "POLYLINE":
                List<Point3d> ptList = castToPoint3dList(geoParams.SelectToken("points"));
                objectId = doc.Objects.AddPolyline(ptList);
                break;
            case "CIRCLE":
                Point3d circleCenter = castToPoint3d(geoParams.SelectToken("center"));
                double circleRadius = castToDouble(geoParams.SelectToken("radius"));
                var circle = new Circle(circleCenter, circleRadius);
                objectId = doc.Objects.AddCircle(circle);
                break;
            case "ARC":
                Point3d arcCenter = castToPoint3d(geoParams.SelectToken("center"));
                double arcRadius = castToDouble(geoParams.SelectToken("radius"));
                double arcAngle = castToDouble(geoParams.SelectToken("angle"));
                var arc = new Arc(new Plane(arcCenter, Vector3d.ZAxis), arcRadius, arcAngle * Math.PI / 180);
                objectId = doc.Objects.AddArc(arc);
                break;
            case "ELLIPSE":
                Point3d ellipseCenter = castToPoint3d(geoParams.SelectToken("center"));
                double ellipseRadiusX = castToDouble(geoParams.SelectToken("radius_x"));
                double ellipseRadiusY = castToDouble(geoParams.SelectToken("radius_y"));
                var ellipse = new Ellipse(new Plane(ellipseCenter, Vector3d.ZAxis), ellipseRadiusX, ellipseRadiusY);
                objectId = doc.Objects.AddEllipse(ellipse);
                break;
            case "CURVE":
                List<Point3d> controlPoints = castToPoint3dList(geoParams.SelectToken("points"));
                int degree = castToInt(geoParams.SelectToken("degree"));
                var curve = Curve.CreateControlPointCurve(controlPoints, degree) ?? throw new InvalidOperationException("unable to create control point curve from given points");
                objectId = doc.Objects.AddCurve(curve);
                break;
            case "BOX":
                // parse size
                double width = castToDouble(geoParams.SelectToken("width"));
                double length = castToDouble(geoParams.SelectToken("length"));
                double height = castToDouble(geoParams.SelectToken("height"));
                double xSize = width, ySize = length, zSize = height;
                Box box = new Box(
                    Plane.WorldXY,
                    new Interval(-xSize / 2, xSize / 2),
                    new Interval(-ySize / 2, ySize / 2),
                    new Interval(-zSize / 2, zSize / 2)
                );
                objectId = doc.Objects.AddBox(box);
                break;

            case "SPHERE":
                // parse radius
                double radius = castToDouble(geoParams.SelectToken("radius"));
                // Create sphere at origin with specified radius
                Sphere sphere = new Sphere(Point3d.Origin, radius);
                // Convert sphere to BREP for adding to document
                objectId = doc.Objects.AddBrep(sphere.ToBrep());
                break;
            case "CONE":
                double coneRadius = castToDouble(geoParams.SelectToken("radius"));
                double coneHeight = castToDouble(geoParams.SelectToken("height"));
                bool coneCap = castToBool(geoParams.SelectToken("cap"));
                string coneAxis = (castToString(geoParams.SelectToken("axis")) ?? "z").ToLowerInvariant();
                Vector3d coneAxisVec = coneAxis == "x" ? Vector3d.XAxis : coneAxis == "y" ? Vector3d.YAxis : Vector3d.ZAxis;
                Point3d coneBaseOrigin = coneAxis == "x"
                    ? new Point3d(-coneHeight / 2.0, 0, 0)
                    : coneAxis == "y"
                        ? new Point3d(0, -coneHeight / 2.0, 0)
                        : new Point3d(0, 0, -coneHeight / 2.0);
                // Center the cone at the origin (base at -height/2 along axis, apex at +height/2 along axis).
                var coneBasePlane = new Plane(coneBaseOrigin, coneAxisVec);
                Cone cone = new Cone(coneBasePlane, coneHeight, coneRadius);
                Brep brep = Brep.CreateFromCone(cone, coneCap);
                objectId = doc.Objects.AddBrep(brep);
                break;
            case "CYLINDER":
                double cylinderRadius = castToDouble(geoParams.SelectToken("radius"));
                double cylinderHeight = castToDouble(geoParams.SelectToken("height"));
                bool cylinderCap = castToBool(geoParams.SelectToken("cap"));
                string cylinderAxis = (castToString(geoParams.SelectToken("axis")) ?? "z").ToLowerInvariant();
                Vector3d cylinderAxisVec = cylinderAxis == "x" ? Vector3d.XAxis : cylinderAxis == "y" ? Vector3d.YAxis : Vector3d.ZAxis;
                Point3d cylinderBaseOrigin = cylinderAxis == "x"
                    ? new Point3d(-cylinderHeight / 2.0, 0, 0)
                    : cylinderAxis == "y"
                        ? new Point3d(0, -cylinderHeight / 2.0, 0)
                        : new Point3d(0, 0, -cylinderHeight / 2.0);
                // Center the cylinder at the origin (base at -height/2 along axis, top at +height/2 along axis).
                var basePlane = new Plane(cylinderBaseOrigin, cylinderAxisVec);
                Circle cylinderCircle = new Circle(basePlane, cylinderRadius);
                Cylinder cylinder = new Cylinder(cylinderCircle, cylinderHeight);
                objectId = doc.Objects.AddBrep(cylinder.ToBrep(cylinderCap, cylinderCap));
                break;
            case "SURFACE":
                int[] surfaceCount = castToIntArray(geoParams.SelectToken("count"));
                if (surfaceCount.Length < 2)
                {
                    throw new InvalidOperationException("SURFACE.params.count must have 2 values: [u_count, v_count].");
                }

                int uCount = surfaceCount[0];
                int vCount = surfaceCount[1];
                if (uCount < 2 || vCount < 2)
                {
                    throw new InvalidOperationException("SURFACE requires count values >= 2 in both directions.");
                }

                List<Point3d> surfacePoints = castToPoint3dList(geoParams.SelectToken("points"));
                if (surfacePoints.Count != uCount * vCount)
                {
                    throw new InvalidOperationException($"SURFACE point count mismatch: expected {uCount * vCount} points from count [{uCount}, {vCount}], got {surfacePoints.Count}.");
                }

                int[] surfaceDegreeRaw = geoParams.SelectToken("degree") != null
                    ? castToIntArray(geoParams.SelectToken("degree"))
                    : new int[] { 3, 3 };
                int uDegreeRaw = surfaceDegreeRaw.Length > 0 ? surfaceDegreeRaw[0] : 3;
                int vDegreeRaw = surfaceDegreeRaw.Length > 1 ? surfaceDegreeRaw[1] : 3;

                // Rhino requires 1 <= degree <= (point_count - 1) for each direction.
                int uDegree = Math.Max(1, Math.Min(uDegreeRaw, uCount - 1));
                int vDegree = Math.Max(1, Math.Min(vDegreeRaw, vCount - 1));

                bool[] surfaceClosedRaw = geoParams.SelectToken("closed") != null
                    ? castToBoolArray(geoParams.SelectToken("closed"))
                    : new bool[] { false, false };
                bool uClosed = surfaceClosedRaw.Length > 0 && surfaceClosedRaw[0];
                bool vClosed = surfaceClosedRaw.Length > 1 && surfaceClosedRaw[1];

                var surf = NurbsSurface.CreateThroughPoints(surfacePoints, uCount, vCount, uDegree, vDegree, uClosed, vClosed);
                if (surf == null)
                {
                    throw new InvalidOperationException("Unable to create SURFACE from provided points/count/degree. Check that points form a valid rectangular grid.");
                }
                objectId = doc.Objects.AddSurface(surf);
                break;
            default:
                throw new InvalidOperationException("Invalid object type");
        }

        if (objectId == Guid.Empty)
            throw new InvalidOperationException("Failed to create object");

        var rhinoObject = doc.Objects.Find(objectId);
        if (rhinoObject != null)
        {
            if (!string.IsNullOrEmpty(name)) rhinoObject.Attributes.Name = name;
            if (customColor)
            {
                rhinoObject.Attributes.ColorSource = ObjectColorSource.ColorFromObject;
                rhinoObject.Attributes.ObjectColor = Color.FromArgb(color[0], color[1], color[2]);
            }
            doc.Objects.ModifyAttributes(rhinoObject, rhinoObject.Attributes, true);
            GetOrBootstrapPose(rhinoObject);
        }

        // Update views
        doc.Views.Redraw();

        // apply modification
        parameters["id"] = objectId;
        return ModifyObject(parameters);
    }
}
