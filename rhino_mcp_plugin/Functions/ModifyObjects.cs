using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject ModifyObjects(JObject parameters)
    {
        bool all = parameters.ContainsKey("all");
        JArray objectParameters = (JArray)parameters["objects"];
        
        var doc = RhinoDoc.ActiveDoc;
        var objects = doc.Objects.ToList();
        
        if (all && objectParameters.Count == 1)
        {
            // Get the first modification parameters (excluding the "all" property)
            JObject firstModification = (JObject)objectParameters.FirstOrDefault()!;
            
            // Create new parameters object with all object IDs
            foreach (var obj in objects)
            {
                // Create a new copy of the modification parameters for each object
                JObject newModification = new JObject(firstModification) { ["id"] = obj.Id.ToString() };
                objectParameters.Add(newModification);
            }
        }

        var i = 0;
        var updates = new JArray();
        foreach (JObject parameter in objectParameters)
        {
            if (parameter.ContainsKey("id") || parameter.ContainsKey("name"))
            {
                updates.Add(ModifyObject(parameter));
                i++;
            }
        }
        doc.Views.Redraw();
        return new JObject
        {
            ["modified"] = i,
            ["updates"] = updates
        };
    }
}
