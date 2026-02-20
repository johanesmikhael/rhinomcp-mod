using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializeCurveGeometry(Curve curve, bool includeGeometrySummary, int maxPoints)
    {
        if (curve == null)
        {
            return new JObject();
        }

        if (!includeGeometrySummary)
        {
            var crv = SerializeCurve(curve);
            return (JObject)crv["geometry"];
        }

        if (maxPoints <= 0)
        {
            maxPoints = 32;
        }

        bool isPlanar = false;
        Plane plane = Plane.WorldXY;
        TryGetPosePlane(curve, out plane, out isPlanar);
        var worldPoints = SerializeCurvePoints(curve, maxPoints);
        var localPoints = new JArray();

        if (isPlanar)
        {
            foreach (JToken token in worldPoints)
            {
                double x = token[0]?.ToObject<double>() ?? 0.0;
                double y = token[1]?.ToObject<double>() ?? 0.0;
                double z = token[2]?.ToObject<double>() ?? 0.0;
                var pt = new Point3d(x, y, z);

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

            return new JObject
            {
                ["planar"] = true,
                ["world_points"] = worldPoints,
                ["local_points"] = localPoints,
                ["pose"] = new JObject
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
                }
            };
        }

        foreach (JToken token in worldPoints)
        {
            double x = token[0]?.ToObject<double>() ?? 0.0;
            double y = token[1]?.ToObject<double>() ?? 0.0;
            double z = token[2]?.ToObject<double>() ?? 0.0;
            localPoints.Add(new JArray
            {
                Math.Round(x - plane.Origin.X, 2),
                Math.Round(y - plane.Origin.Y, 2),
                Math.Round(z - plane.Origin.Z, 2)
            });
        }

        return new JObject
        {
            ["planar"] = false,
            ["world_points"] = worldPoints,
            ["local_points"] = localPoints,
            ["pose"] = new JObject
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
            }
        };
    }
}
