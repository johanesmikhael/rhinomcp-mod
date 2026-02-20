using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializePolylineGeometry(PolylineCurve polyline, bool includeGeometrySummary)
    {
        var geometry = new JObject();
        var points = polyline.ToArray();
        if (!includeGeometrySummary)
        {
            geometry["points"] = SerializePoints(points);
            return geometry;
        }

        bool isPlanar = false;
        Plane plane = Plane.WorldXY;
        TryGetPosePlane(polyline, out plane, out isPlanar);
        var localPoints = new JArray();
        var worldPoints = SerializePoints(points);

        if (isPlanar)
        {
            foreach (var pt in points)
            {
                if (!plane.ClosestParameter(pt, out double u, out double v))
                {
                    u = pt.X;
                    v = pt.Y;
                }
                localPoints.Add(new JArray
                {
                    Math.Round(u, 2),
                    Math.Round(v, 2),
                    0.0
                });
            }

            geometry["pose"] = new JObject
            {
                ["world_from_local"] = new JObject
                {
                    ["R"] = new JArray
                    {
                        new JArray { Math.Round(plane.XAxis.X, 6), Math.Round(plane.YAxis.X, 6), Math.Round(plane.ZAxis.X, 6) },
                        new JArray { Math.Round(plane.XAxis.Y, 6), Math.Round(plane.YAxis.Y, 6), Math.Round(plane.ZAxis.Y, 6) },
                        new JArray { Math.Round(plane.XAxis.Z, 6), Math.Round(plane.YAxis.Z, 6), Math.Round(plane.ZAxis.Z, 6) }
                    },
                    ["t"] = new JArray
                    {
                        Math.Round(plane.Origin.X, 2),
                        Math.Round(plane.Origin.Y, 2),
                        Math.Round(plane.Origin.Z, 2)
                    }
                }
            };
        }
        else
        {
            foreach (var pt in points)
            {
                localPoints.Add(new JArray
                {
                    Math.Round(pt.X - plane.Origin.X, 2),
                    Math.Round(pt.Y - plane.Origin.Y, 2),
                    Math.Round(pt.Z - plane.Origin.Z, 2)
                });
            }

            geometry["pose"] = new JObject
            {
                ["world_from_local"] = new JObject
                {
                    ["R"] = new JArray
                    {
                        new JArray { 1.0, 0.0, 0.0 },
                        new JArray { 0.0, 1.0, 0.0 },
                        new JArray { 0.0, 0.0, 1.0 }
                    },
                    ["t"] = new JArray
                    {
                        Math.Round(plane.Origin.X, 2),
                        Math.Round(plane.Origin.Y, 2),
                        Math.Round(plane.Origin.Z, 2)
                    }
                }
            };
        }

        geometry["planar"] = isPlanar;
        geometry["world_points"] = worldPoints;
        geometry["local_points"] = localPoints;
        return geometry;
    }
}
