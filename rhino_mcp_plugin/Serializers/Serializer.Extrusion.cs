using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace rhinomcp_mod.Serializers;

public static partial class Serializer
{
    private static JObject SerializeExtrusionGeometry(Extrusion extrusion, bool includeGeometrySummary, int outlineMaxPoints, Plane? workingPlaneOverride = null)
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
                var summary = BuildBrepGeometrySummary(brep, outlineMaxPoints, workingPlaneOverride);
                if (summary["obb"] != null)
                {
                    geometry["obb"] = summary["obb"];
                }
                if (summary["pose"] != null)
                {
                    geometry["pose"] = summary["pose"];
                }
                if (summary["proj_outline_world"] != null)
                {
                    geometry["proj_outline_world"] = summary["proj_outline_world"];
                }
                if (summary["proj_outline_local_xy"] != null)
                {
                    geometry["proj_outline_local_xy"] = summary["proj_outline_local_xy"];
                }
                if (summary["surface_edges_world"] != null)
                {
                    geometry["surface_edges_world"] = summary["surface_edges_world"];
                }
                if (summary["surface_edges_local"] != null)
                {
                    geometry["surface_edges_local"] = summary["surface_edges_local"];
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
