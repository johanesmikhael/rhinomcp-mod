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
        var geometryDetail = ReadGeometryDetail(parameters);
        var includeWorld = ReadIncludeWorld(parameters);
        var doc = RhinoDoc.ActiveDoc;
        var selectedObjs = doc.Objects.GetSelectedObjects(false, false);

        var result = new JArray();
        foreach (var obj in selectedObjs)
        {
            var data = BuildGeometryDetailObjectInfo(obj, geometryDetail, includeWorld, outlineMaxPoints);
            if (includeAttributes)
            {
                data["attributes"] = BuildPublicAttributes(obj);
            }
            result.Add(data);
        }

        return new JObject
        {
            ["geometry_detail"] = geometryDetail,
            ["include_world"] = includeWorld,
            ["selected_objects"] = result
        };
    }
}
