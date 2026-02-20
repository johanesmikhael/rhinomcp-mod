using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject ResetObjectsPose(JObject parameters)
    {
        bool all = parameters.ContainsKey("all");
        JArray objectParameters = (JArray)parameters["objects"];

        var doc = RhinoDoc.ActiveDoc;
        var objects = doc.Objects.ToList();

        if ((objectParameters == null || objectParameters.Count == 0) && !all)
        {
            throw new InvalidOperationException("No objects provided to reset.");
        }

        if (all)
        {
            if (objectParameters == null)
            {
                objectParameters = new JArray();
            }

            if (objectParameters.Count == 1)
            {
                JObject firstTemplate = (JObject)objectParameters.FirstOrDefault()!;
                foreach (var obj in objects)
                {
                    JObject newItem = new JObject(firstTemplate) { ["id"] = obj.Id.ToString() };
                    objectParameters.Add(newItem);
                }
            }
            else if (objectParameters.Count == 0)
            {
                foreach (var obj in objects)
                {
                    objectParameters.Add(new JObject { ["id"] = obj.Id.ToString() });
                }
            }
        }

        int reset = 0;
        var updates = new JArray();
        foreach (JObject parameter in objectParameters)
        {
            if (parameter.ContainsKey("id") || parameter.ContainsKey("name"))
            {
                updates.Add(ResetObjectPose(parameter));
                reset++;
            }
        }

        doc.Views.Redraw();
        return new JObject
        {
            ["reset"] = reset,
            ["updates"] = updates
        };
    }
}
