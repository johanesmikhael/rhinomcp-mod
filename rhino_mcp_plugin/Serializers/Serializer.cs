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
                objInfo["geometry"] = SerializeExtrusionGeometry(extrusion, includeGeometrySummary, outlineMaxPoints);
            }
            else if (obj.Geometry is Rhino.Geometry.Brep brep)
            {
                string brepType;
                objInfo["geometry"] = SerializeBrepGeometry(brep, includeGeometrySummary, outlineMaxPoints, out brepType);
                objInfo["type"] = brepType;
            }

            return objInfo;
        }
    }
}
