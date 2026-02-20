using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializeExtrusionGeometry(Extrusion extrusion, bool includeGeometrySummary, int outlineMaxPoints)
    {
        var geometry = new JObject();
        if (!includeGeometrySummary)
        {
            geometry["bbox"] = SerializeBBox(extrusion.GetBoundingBox(true));
            return geometry;
        }

        try
        {
            var brep = extrusion.ToBrep();
            if (brep != null)
            {
                var summary = BuildBrepGeometrySummary(brep, outlineMaxPoints);
                if (summary["obb"] != null)
                {
                    geometry["obb"] = summary["obb"];
                }
                if (summary["pose"] != null)
                {
                    geometry["pose"] = summary["pose"];
                }
            }
        }
        catch
        {
            // Keep serializer resilient; skip summary on failure.
        }

        return geometry;
    }
}
