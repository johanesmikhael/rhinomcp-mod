using Newtonsoft.Json.Linq;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject ResetObjectPose(JObject parameters)
    {
        var obj = getObjectByIdOrName(parameters);

        bool resetRotation = parameters["reset_rotation"]?.ToObject<bool>() ?? true;
        bool resetTranslation = parameters["reset_translation"]?.ToObject<bool>() ?? true;
        Point3d target = parameters["target_translation"] != null
            ? castToPoint3d(parameters["target_translation"])
            : Point3d.Origin;

        var modifyParams = new JObject
        {
            ["id"] = obj.Id.ToString()
        };

        if (resetRotation)
        {
            var rotationMatrix = GetObjectPoseRotationMatrix(obj);
            modifyParams["rotation_matrix"] = rotationMatrix;
            modifyParams["invert_rotation_matrix"] = true;
        }

        if (resetTranslation)
        {
            Point3d current = GetObjectPoseTranslationPoint(obj);
            var delta = new JArray
            {
                target.X - current.X,
                target.Y - current.Y,
                target.Z - current.Z
            };
            modifyParams["translation"] = delta;
        }

        return ModifyObject(modifyParams);
    }
}
