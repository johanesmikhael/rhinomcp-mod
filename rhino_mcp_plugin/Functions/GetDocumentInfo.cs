using Newtonsoft.Json.Linq;
using Rhino;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    private static JToken SafeLayerProperty(System.Func<JToken> getter, JToken fallback = null)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback ?? JValue.CreateNull();
        }
    }

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

            try
            {
                objectData.Add(Serializer.RhinoObject(docObject));
            }
            catch (System.Exception ex)
            {
                RhinoApp.WriteLine($"Skipping object in get_document_info ({docObject?.Id}): {ex}");
            }
            count++;
        }

        var layerData = new JArray();

        count = 0;
        foreach (var docLayer in doc.Layers)
        {
            if (count >= LIMIT) break;

            try
            {
                layerData.Add(new JObject
                {
                    ["id"] = SafeLayerProperty(() => docLayer.Id.ToString(), "(unknown)"),
                    ["name"] = SafeLayerProperty(() => docLayer.Name, "(unnamed)"),
                    ["color"] = SafeLayerProperty(() => docLayer.Color.ToString(), "(unknown)"),
                    ["visible"] = SafeLayerProperty(() => docLayer.IsVisible, false),
                    ["locked"] = SafeLayerProperty(() => docLayer.IsLocked, false)
                });
            }
            catch (System.Exception ex)
            {
                RhinoApp.WriteLine($"Skipping layer in get_document_info ({docLayer?.Id}): {ex.Message}");
            }
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
