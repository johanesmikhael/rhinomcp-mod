using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject CopyObjects(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var objects = parameters["objects"] as JArray;
        if (objects == null || objects.Count == 0)
        {
            throw new InvalidOperationException("No objects provided to copy.");
        }

        var copiedIds = new List<Guid>();
        foreach (var entry in objects)
        {
            if (entry is not JObject objParams)
                continue;

            JObject result = CopyObject(objParams);
            Guid id = castToGuid(result["id"]);
            if (id != Guid.Empty)
            {
                copiedIds.Add(id);
            }
        }

        doc.Views.Redraw();
        return new JObject
        {
            ["copied"] = copiedIds.Count
        };
    }
}
