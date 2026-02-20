using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetObjectInfo(JObject parameters)
    {
        var obj = getObjectByIdOrName(parameters);

        var outlineMaxPoints = parameters["outline_max_points"]?.ToObject<int>() ?? 0;
        var data = Serializer.RhinoObject(obj, includeGeometrySummary: true, outlineMaxPoints: outlineMaxPoints);
        InjectStoredPoseIntoSummary(obj, data);
        return data;
    }
}
