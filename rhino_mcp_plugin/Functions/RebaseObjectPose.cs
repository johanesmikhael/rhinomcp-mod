using System;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject RebaseObjectPose(JObject parameters)
    {
        var obj = getObjectByIdOrName(parameters);
        string translationMode = (parameters["translation_mode"]?.ToString() ?? "pose_t").ToLowerInvariant();

        Point3d anchor;
        if (translationMode == "bbox_center")
        {
            BoundingBox bbox = obj.Geometry.GetBoundingBox(true);
            anchor = bbox.Center;
        }
        else if (translationMode == "pose_t")
        {
            anchor = GetObjectPoseTranslationPoint(obj);
        }
        else
        {
            throw new InvalidOperationException("translation_mode must be 'pose_t' or 'bbox_center'.");
        }

        var rebasedPose = BuildDefaultPose(anchor);
        WriteStoredPose(obj, rebasedPose);

        var updatedObject = getObjectByIdOrName(new JObject { ["id"] = obj.Id.ToString() });
        return BuildMinimalObjectState(updatedObject, new[] { "pose", "position" });
    }
}
