using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject RotateObjects(JObject parameters)
    {
        bool all = parameters.ContainsKey("all");
        JArray objectParameters = (JArray)parameters["objects"];

        var doc = RhinoDoc.ActiveDoc;
        var objects = doc.Objects.ToList();

        if (objectParameters == null || objectParameters.Count == 0)
        {
            throw new InvalidOperationException("No objects provided to rotate.");
        }

        if (all && objectParameters.Count == 1)
        {
            JObject firstModification = (JObject)objectParameters.FirstOrDefault()!;

            foreach (var obj in objects)
            {
                JObject newModification = new JObject(firstModification) { ["id"] = obj.Id.ToString() };
                objectParameters.Add(newModification);
            }
        }

        int rotated = 0;
        var updates = new JArray();
        foreach (JObject parameter in objectParameters)
        {
            if (parameter.ContainsKey("id") || parameter.ContainsKey("name"))
            {
                updates.Add(RotateObject(parameter));
                rotated++;
            }
        }

        doc.Views.Redraw();
        return new JObject
        {
            ["rotated"] = rotated,
            ["updates"] = updates
        };
    }
}
