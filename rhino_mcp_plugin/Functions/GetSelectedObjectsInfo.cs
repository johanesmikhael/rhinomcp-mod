using Newtonsoft.Json.Linq;
using Rhino;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetSelectedObjectsInfo(JObject parameters)
    {
        var includeAttributes = parameters["include_attributes"]?.ToObject<bool>() ?? false;
        var outlineMaxPoints = parameters["outline_max_points"]?.ToObject<int>() ?? 0;
        var doc = RhinoDoc.ActiveDoc;
        var selectedObjs = doc.Objects.GetSelectedObjects(false, false);

        var result = new JArray();
        foreach (var obj in selectedObjs)
        {
            var data = Serializer.RhinoObject(obj, includeGeometrySummary: true, outlineMaxPoints: outlineMaxPoints);
            InjectStoredPoseIntoSummary(obj, data);
            if (includeAttributes)
            {
                data["attributes"] = BuildPublicAttributes(obj);
            }
            result.Add(data);
        }

        return new JObject
        {
            ["selected_objects"] = result
        };
    }
}
