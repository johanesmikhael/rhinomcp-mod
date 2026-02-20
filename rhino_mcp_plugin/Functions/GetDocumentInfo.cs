using Newtonsoft.Json.Linq;
using Rhino;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetDocumentInfo(JObject parameters)
    {
        const int LIMIT = 300;
                
        RhinoApp.WriteLine("Getting document info...");

        var doc = RhinoDoc.ActiveDoc;

        var metaData = new JObject
        {
            ["name"] = doc.Name,
            ["date_created"] = doc.DateCreated,
            ["date_modified"] = doc.DateLastEdited,
            ["tolerance"] = doc.ModelAbsoluteTolerance,
            ["angle_tolerance"] = doc.ModelAngleToleranceDegrees,
            ["path"] = doc.Path,
            ["units"] = doc.ModelUnitSystem.ToString(),
        };

        var objectData = new JArray();

        // Collect minimal object information (limit to first 10 objects)
        int count = 0;
        foreach (var docObject in doc.Objects)
        {
            if (count >= LIMIT) break;
            
            objectData.Add(Serializer.RhinoObject(docObject));
            count++;
        }

        var layerData = new JArray();

        count = 0;
        foreach (var docLayer in doc.Layers)
        {
            if (count >= LIMIT) break;
            layerData.Add(new JObject
            {
                ["id"] = docLayer.Id.ToString(),
                ["name"] = docLayer.Name,
                ["color"] = docLayer.Color.ToString(),
                ["visible"] = docLayer.IsVisible,
                ["locked"] = docLayer.IsLocked
            });
            count++;
        }


        var result = new JObject
        {
            ["meta_data"] = metaData,
            ["object_count"] = doc.Objects.Count,
            ["objects"] = objectData,
            ["layer_count"] = doc.Layers.Count,
            ["layers"] = layerData
        };

        RhinoApp.WriteLine($"Document info collected: {count} objects");
        return result;
    }
}