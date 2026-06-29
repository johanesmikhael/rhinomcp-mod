using System;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject RebaseObjectPose(JObject parameters)
    {
        var obj = getObjectByIdOrName(parameters);
        string zDirection = castToString(parameters["z_direction"]);
        string xDirection = castToString(parameters["x_direction"]);
        var currentPose = GetOrBootstrapPose(obj);

        Point3d anchor = obj.Geometry.GetBoundingBox(true).Center;
        if (TryReadPoseFrame(currentPose, out Vector3d poseX, out Vector3d poseY, out _, out Point3d poseOrigin))
        {
            Plane posePlane = new Plane(poseOrigin, poseX, poseY);
            BoundingBox obbBox = obj.Geometry.GetBoundingBox(posePlane);
            if (obbBox.IsValid)
            {
                anchor = new Box(posePlane, obbBox).Center;
            }
        }

        bool hasDirectionOverride = !string.IsNullOrWhiteSpace(zDirection) || !string.IsNullOrWhiteSpace(xDirection);
        var rebasedPose = hasDirectionOverride
            ? BuildPoseByClosestAxisSwap(
                currentPose,
                anchor,
                string.IsNullOrWhiteSpace(zDirection) ? null : zDirection,
                string.IsNullOrWhiteSpace(xDirection) ? null : xDirection
            )
            : BuildPoseByClosestAxisSwap(currentPose, anchor, null, null);
        bool hadCachedObb = TryReadStoredObb(obj, out _);
        WriteStoredPose(obj, rebasedPose, invalidateObbCache: false);

        var updatedObject = getObjectByIdOrName(new JObject { ["id"] = obj.Id.ToString() });
        if (!hadCachedObb)
        {
            // Bootstrap cache when it does not exist yet.
            RefreshStoredObbFromObject(updatedObject);
        }
        return BuildMinimalObjectState(updatedObject, new[] { "pose", "position" });
    }
}
