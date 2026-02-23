using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject RotateObject(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var obj = getObjectByIdOrName(parameters);
        if (parameters["rotation"] != null && parameters["rotation_matrix"] == null)
        {
            throw new InvalidOperationException("rotation is deprecated; please provide rotation_matrix instead.");
        }
        if (parameters["rotation_matrix"] == null)
        {
            throw new InvalidOperationException("Missing rotation_matrix.");
        }
        if (parameters["pivot"] == null)
        {
            throw new InvalidOperationException("Missing pivot.");
        }

        BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;
        var poseBefore = GetOrBootstrapPose(obj);
        var xform = applyRotationMatrixAtPivot(parameters);
        doc.Objects.Transform(obj, xform, true);

        doc.Views.Redraw();
        var updatedObject = getObjectByIdOrName(new JObject { ["id"] = obj.Id.ToString() });
        var poseAfter = ApplyTransformToPose(poseBefore, xform, center);
        WriteStoredPose(updatedObject, poseAfter);
        RefreshStoredObbFromObject(updatedObject);
        return BuildMinimalObjectState(updatedObject, new[] { "pose", "position" });
    }
}
