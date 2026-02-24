using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers
{
    public static partial class Serializer
    {
        private const string PoseStorageKey = "rhinomcp.pose.v1";

        public static RhinoDoc doc = RhinoDoc.ActiveDoc;

        public static JObject SerializeColor(Color color)
        {
            return new JObject()
            {
                ["r"] = color.R,
                ["g"] = color.G,
                ["b"] = color.B
            };
        }

        public static JArray SerializePoint(Point3d pt)
        {
            return new JArray
            {
                Math.Round(pt.X, 2),
                Math.Round(pt.Y, 2),
                Math.Round(pt.Z, 2)
            };
        }

        public static JArray SerializePoints(IEnumerable<Point3d> pts)
        {
            return new JArray
            {
                pts.Select(p => SerializePoint(p))
            };
        }

        public static JObject SerializeCurve(Curve crv)
        {
            return new JObject
            {
                ["type"] = "Curve",
                ["geometry"] = new JObject
                {
                    ["points"] = SerializePoints(crv.ControlPolygon().ToArray()),
                    ["degree"] = crv.Degree.ToString()
                }
            };
        }

        private static JArray SerializeCurvePoints(Curve crv, int maxPoints)
        {
            if (crv == null || maxPoints < 2)
            {
                return new JArray();
            }

            if (crv.TryGetPolyline(out Polyline polyline))
            {
                var pts = polyline.ToArray();
                var result = new JArray();
                int step = Math.Max(1, (int)Math.Ceiling(pts.Length / (double)maxPoints));
                for (int i = 0; i < pts.Length; i += step)
                {
                    result.Add(SerializePoint(pts[i]));
                }
                if (pts.Length > 0 && (int)result.Count == 0)
                {
                    result.Add(SerializePoint(pts[0]));
                }
                return result;
            }

            double[] t = crv.DivideByCount(Math.Max(2, maxPoints), true);
            if (t == null)
            {
                return new JArray();
            }

            var sampled = new JArray();
            foreach (var ti in t)
            {
                sampled.Add(SerializePoint(crv.PointAt(ti)));
            }
            return sampled;
        }

        public static JArray SerializeBBox(BoundingBox bbox)
        {
            return new JArray
            {
                new JArray { bbox.Min.X, bbox.Min.Y, bbox.Min.Z },
                new JArray { bbox.Max.X, bbox.Max.Y, bbox.Max.Z }
            };
        }

        public static JObject SerializeLayer(Layer layer)
        {
            return new JObject
            {
                ["id"] = layer.Id.ToString(),
                ["name"] = layer.Name,
                ["color"] = SerializeColor(layer.Color),
                ["parent"] = layer.ParentLayerId.ToString()
            };
        }

        public static JObject RhinoObjectAttributes(RhinoObject obj)
        {
            var attributes = obj.Attributes.GetUserStrings();
            var attributesDict = new JObject();
            foreach (string key in attributes.AllKeys)
            {
                attributesDict[key] = attributes[key];
            }
            return attributesDict;
        }

        public static JObject RhinoObject(RhinoObject obj, bool includeGeometrySummary = false, int outlineMaxPoints = 0)
        {
            Plane? preferredWorkingPlane = null;
            bool hasStoredWorkingPlane = TryReadStoredPosePlane(obj, out Plane storedPlane);
            if (hasStoredWorkingPlane)
            {
                preferredWorkingPlane = storedPlane;
            }
            bool hasPoseUserString = !string.IsNullOrWhiteSpace(obj?.Attributes?.GetUserString(PoseStorageKey));
            string workingPlaneSource = hasStoredWorkingPlane ? "stored_pose" : "geometry_fallback";

            var objInfo = new JObject
            {
                ["id"] = obj.Id.ToString(),
                ["name"] = obj.Name ?? "(unnamed)",
                ["type"] = obj.ObjectType.ToString(),
                ["layer"] = doc.Layers[obj.Attributes.LayerIndex].Name,
                ["material"] = obj.Attributes.MaterialIndex.ToString(),
                ["color"] = SerializeColor(obj.Attributes.ObjectColor)
            };

            // add boundingbox
            // BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
            // objInfo["bounding_box"] = SerializeBBox(bbox);

            // Add geometry data
            if (obj.Geometry is Rhino.Geometry.Point point)
            {
                objInfo["type"] = "POINT";
                objInfo["geometry"] = SerializePoint(point.Location);
            }
            else if (obj.Geometry is Rhino.Geometry.LineCurve line)
            {
                objInfo["type"] = "LINE";
                objInfo["geometry"] = SerializeLineGeometry(line.Line.From, line.Line.To, includeGeometrySummary);
            }
            else if (obj.Geometry is Rhino.Geometry.PolylineCurve polyline)
            {
                objInfo["type"] = "POLYLINE";
                objInfo["geometry"] = SerializePolylineGeometry(polyline, includeGeometrySummary);
            }
            else if (obj.Geometry is Rhino.Geometry.Curve curve)
            {
                objInfo["type"] = "CURVE";
                objInfo["geometry"] = SerializeCurveGeometry(curve, includeGeometrySummary, outlineMaxPoints);
            }
            else if (obj.Geometry is Rhino.Geometry.Extrusion extrusion)
            {
                objInfo["type"] = "EXTRUSION";
                if (includeGeometrySummary)
                {
                    RhinoApp.WriteLine(
                        $"[outline-debug] object={obj.Id} type=EXTRUSION plane_source={workingPlaneSource} pose_user_string={hasPoseUserString}"
                    );
                }
                objInfo["geometry"] = SerializeExtrusionGeometry(extrusion, includeGeometrySummary, outlineMaxPoints, preferredWorkingPlane);
            }
            else if (obj.Geometry is Rhino.Geometry.Brep brep)
            {
                string brepType;
                if (includeGeometrySummary)
                {
                    RhinoApp.WriteLine(
                        $"[outline-debug] object={obj.Id} type=BREP plane_source={workingPlaneSource} pose_user_string={hasPoseUserString}"
                    );
                }
                objInfo["geometry"] = SerializeBrepGeometry(brep, includeGeometrySummary, outlineMaxPoints, out brepType, preferredWorkingPlane);
                objInfo["type"] = brepType;
            }

            return objInfo;
        }

        private static bool TryReadStoredPosePlane(RhinoObject obj, out Plane plane)
        {
            plane = Plane.WorldXY;
            const string poseStorageKey = PoseStorageKey;
            string raw = obj?.Attributes?.GetUserString(poseStorageKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            try
            {
                if (JObject.Parse(raw)?["world_from_local"] is not JObject worldFromLocal)
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

                var xAxis = new Vector3d(
                    r0[0]?.ToObject<double>() ?? 0.0,
                    r1[0]?.ToObject<double>() ?? 0.0,
                    r2[0]?.ToObject<double>() ?? 0.0
                );
                var yAxis = new Vector3d(
                    r0[1]?.ToObject<double>() ?? 0.0,
                    r1[1]?.ToObject<double>() ?? 0.0,
                    r2[1]?.ToObject<double>() ?? 0.0
                );
                var zAxis = new Vector3d(
                    r0[2]?.ToObject<double>() ?? 0.0,
                    r1[2]?.ToObject<double>() ?? 0.0,
                    r2[2]?.ToObject<double>() ?? 0.0
                );
                var origin = new Point3d(
                    t[0]?.ToObject<double>() ?? 0.0,
                    t[1]?.ToObject<double>() ?? 0.0,
                    t[2]?.ToObject<double>() ?? 0.0
                );

                if (!xAxis.Unitize() || !yAxis.Unitize() || !zAxis.Unitize())
                {
                    return false;
                }

                // Preserve stored pose orientation: keep z axis direction, then orthonormalize x/y around it.
                xAxis = xAxis - (Vector3d.Multiply(xAxis, zAxis) * zAxis);
                if (!xAxis.Unitize())
                {
                    return false;
                }

                yAxis = Vector3d.CrossProduct(zAxis, xAxis);
                if (!yAxis.Unitize())
                {
                    return false;
                }

                // Recompute x to remove accumulated numeric drift.
                xAxis = Vector3d.CrossProduct(yAxis, zAxis);
                if (!xAxis.Unitize())
                {
                    return false;
                }

                plane = new Plane(origin, xAxis, yAxis);
                return plane.IsValid;
            }
            catch
            {
                return false;
            }
        }
    }
}
