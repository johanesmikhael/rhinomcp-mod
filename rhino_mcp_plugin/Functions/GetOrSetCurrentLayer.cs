using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject GetOrSetCurrentLayer(JObject parameters)
    {
        // parse meta data
        bool hasName = parameters.ContainsKey("name");
        bool hasGuid = parameters.ContainsKey("guid");

        string name = hasName ? castToString(parameters.SelectToken("name")) : null;
        string guid = hasGuid ? castToString(parameters.SelectToken("guid")) : null;

        var doc = RhinoDoc.ActiveDoc;

        Layer layer = null;
        if (hasGuid)
        {
            if (!Guid.TryParse(guid, out Guid parsedGuid))
            {
                throw new Exception($"Invalid layer guid format: {guid}");
            }
            if (parsedGuid == Guid.Empty)
            {
                throw new Exception("Layer guid cannot be 00000000-0000-0000-0000-000000000000");
            }
            layer = doc.Layers.FindId(parsedGuid);
            if (layer == null)
            {
                throw new Exception($"Layer not found for guid: {guid}");
            }
        }
        else if (hasName)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new Exception("Layer name cannot be empty");
            }
            layer = doc.Layers.FindName(name);
            if (layer == null)
            {
                throw new Exception($"Layer not found for name: {name}");
            }
        }

        if (layer != null) doc.Layers.SetCurrentLayerIndex(layer.Index, true);
        else layer = doc.Layers.CurrentLayer;

        // Update views
        doc.Views.Redraw();

        return Serializer.SerializeLayer(layer);
    }
}
