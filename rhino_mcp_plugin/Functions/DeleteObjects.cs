using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject DeleteObjects(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        bool confirm = parameters.ContainsKey("confirm") && castToBool(parameters["confirm"]);

        if (!confirm)
        {
            throw new InvalidOperationException("Delete blocked: confirm=true is required.");
        }

        var ids = parameters["ids"]?.ToObject<List<string>>() ?? new List<string>();
        var names = parameters["names"]?.ToObject<List<string>>() ?? new List<string>();

        if (ids.Count == 0 && names.Count == 0)
        {
            throw new InvalidOperationException("No ids or names provided.");
        }

        var objectsToDelete = new List<RhinoObject>();

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var guid = new Guid(id);
            var obj = doc.Objects.Find(guid);
            if (obj == null) throw new InvalidOperationException($"Object with ID {id} not found");
            objectsToDelete.Add(obj);
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var objs = doc.Objects.GetObjectList(new ObjectEnumeratorSettings() { NameFilter = name }).ToList();
            if (objs == null || objs.Count == 0) throw new InvalidOperationException($"Object with name {name} not found.");
            if (objs.Count > 1) throw new InvalidOperationException($"Multiple objects with name {name} found.");
            objectsToDelete.Add(objs[0]);
        }

        // Deduplicate by id
        var uniqueObjects = objectsToDelete
            .GroupBy(o => o.Id)
            .Select(g => g.First())
            .ToList();

        foreach (var obj in uniqueObjects)
        {
            bool success = doc.Objects.Delete(obj.Id, true);
            if (!success)
            {
                throw new InvalidOperationException($"Failed to delete object with ID {obj.Id}");
            }
        }

        doc.Views.Redraw();

        return new JObject
        {
            ["count"] = uniqueObjects.Count
        };
    }
}
