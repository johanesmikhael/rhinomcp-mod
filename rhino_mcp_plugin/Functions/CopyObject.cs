using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject CopyObject(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var obj = getObjectByIdOrName(parameters);
        var sourcePose = GetOrBootstrapPose(obj);
        BoundingBox sourceBbox = obj.Geometry.GetBoundingBox(true);
        Point3d sourceCenter = sourceBbox.Center;
        var geometry = obj.Geometry?.Duplicate();
        if (geometry == null)
        {
            throw new InvalidOperationException("Unable to duplicate object geometry.");
        }

        Transform xform = Transform.Identity;
        if (parameters["translation"] != null)
        {
            xform = applyTranslation(parameters);
            geometry.Transform(xform);
        }

        var attrs = obj.Attributes.Duplicate();
        Guid newId = doc.Objects.Add(geometry, attrs);
        if (newId == Guid.Empty)
        {
            throw new InvalidOperationException("Failed to add copied object to document.");
        }

        var copiedObject = getObjectByIdOrName(new JObject { ["id"] = newId.ToString() });
        var copiedPose = ApplyTransformToPose(sourcePose, xform, sourceCenter);
        WriteStoredPose(copiedObject, copiedPose);

        doc.Views.Redraw();
        var data = Serializer.RhinoObject(copiedObject, includeGeometrySummary: true, outlineMaxPoints: 32);
        InjectStoredPoseIntoSummary(copiedObject, data);
        return data;
    }
}
