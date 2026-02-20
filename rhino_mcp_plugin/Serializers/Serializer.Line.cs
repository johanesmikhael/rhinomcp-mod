using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializeLineGeometry(Point3d start, Point3d end, bool includeGeometrySummary)
    {
        var geometry = new JObject();
        if (!includeGeometrySummary)
        {
            geometry["start"] = SerializePoint(start);
            geometry["end"] = SerializePoint(end);
            return geometry;
        }

        double length = start.DistanceTo(end);
        geometry["world_start"] = SerializePoint(start);
        geometry["world_end"] = SerializePoint(end);
        geometry["local_start"] = new JArray
        {
            Math.Round(-length / 2.0, 2),
            0.0,
            0.0
        };
        geometry["local_end"] = new JArray
        {
            Math.Round(length / 2.0, 2),
            0.0,
            0.0
        };
        geometry["pose"] = SerializePoseFromLine(start, end);
        return geometry;
    }

    private static JObject SerializePoseFromLine(Point3d start, Point3d end)
    {
        Plane plane = BuildLinePlane(start, end);

        return new JObject
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
}
