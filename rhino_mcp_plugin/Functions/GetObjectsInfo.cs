using System;
using Newtonsoft.Json.Linq;
using Rhino;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetObjectsInfo(JObject parameters)
    {
        var includeAttributes = parameters["include_attributes"]?.ToObject<bool>() ?? false;
        var outlineMaxPoints = parameters["outline_max_points"]?.ToObject<int>() ?? 0;
        var geometryDetail = ReadGeometryDetail(parameters);
        var includeWorld = ReadIncludeWorld(parameters);
        var objectParameters = parameters["objects"] as JArray;

        if (objectParameters == null || objectParameters.Count == 0)
        {
            throw new InvalidOperationException("objects must be a non-empty list.");
        }

        var result = new JArray();
        foreach (var token in objectParameters)
        {
            if (token is not JObject selector)
            {
                result.Add(new JObject
                {
                    ["error"] = "Each objects entry must be a dictionary."
                });
                continue;
            }

            try
            {
                var obj = getObjectByIdOrName(selector);
                var data = BuildGeometryDetailObjectInfo(obj, geometryDetail, includeWorld, outlineMaxPoints);
                if (includeAttributes)
                {
                    data["attributes"] = BuildPublicAttributes(obj);
                }
                result.Add(data);
            }
            catch (Exception ex)
            {
                result.Add(new JObject
                {
                    ["selector"] = selector.DeepClone(),
                    ["error"] = ex.Message
                });
            }
        }

        return new JObject
        {
            ["geometry_detail"] = geometryDetail,
            ["include_world"] = includeWorld,
            ["objects"] = result
        };
    }
}
