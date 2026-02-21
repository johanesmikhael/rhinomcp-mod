using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject ModifyObject(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var obj = getObjectByIdOrName(parameters);
        var geometry = obj.Geometry;
        BoundingBox bbox = geometry.GetBoundingBox(true);
        Point3d center = bbox.Center;
        var poseBefore = GetOrBootstrapPose(obj);
        var xform = Transform.Identity;
        var changedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitUpdated = new JObject();

        // Handle different modifications based on parameters
        bool attributesModified = false;
        bool geometryModified = false;

        // Change name if provided
        if (parameters["new_name"] != null)
        {
            string name = parameters["new_name"].ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                obj.Attributes.Name = name;
                attributesModified = true;
                changedFields.Add("name");
            }
        }

        // Change color if provided
        if (parameters["new_color"] != null)
        {
            int[] color = parameters["new_color"]?.ToObject<int[]>() ?? new[] { 0, 0, 0 };
            obj.Attributes.ObjectColor = Color.FromArgb(color[0], color[1], color[2]);
            obj.Attributes.ColorSource = ObjectColorSource.ColorFromObject;
            attributesModified = true;
            changedFields.Add("color");
        }

        // Change layer if provided (name or guid)
        if (parameters["layer"] != null)
        {
            string layerValue = parameters["layer"]?.ToString();
            if (!string.IsNullOrWhiteSpace(layerValue))
            {
                Layer targetLayer = null;
                if (Guid.TryParse(layerValue, out Guid layerId))
                {
                    targetLayer = doc.Layers.FindId(layerId);
                }
                if (targetLayer == null)
                {
                    targetLayer = doc.Layers.FindName(layerValue);
                }
                if (targetLayer != null)
                {
                    obj.Attributes.LayerIndex = targetLayer.Index;
                    attributesModified = true;
                    changedFields.Add("layer");
                }
            }
        }

        // Change translation if provided
        if (parameters["translation"] != null)
        {
            xform *= applyTranslation(parameters);
            geometryModified = true;
            changedFields.Add("pose");
            changedFields.Add("position");
        }

        // Apply scale if provided
        if (parameters["scale"] != null)
        {
            xform *= applyScale(parameters, geometry);
            geometryModified = true;
            changedFields.Add("scale");
            explicitUpdated["scale"] = parameters["scale"]?.DeepClone();
        }

        // Apply rotation if provided
        if (parameters["rotation"] != null && parameters["rotation_matrix"] == null)
        {
            throw new InvalidOperationException("rotation is deprecated; please provide rotation_matrix instead.");
        }

        if (parameters["rotation_matrix"] != null)
        {
            xform *= applyRotationMatrix(parameters, geometry);
            geometryModified = true;
            changedFields.Add("pose");
            changedFields.Add("position");
        }
        else if (parameters["rotation"] != null)
        {
            xform *= applyRotation(parameters, geometry);
            geometryModified = true;
            changedFields.Add("pose");
            changedFields.Add("position");
        }

        if (attributesModified)
        {
            // Update the object attributes if needed
            doc.Objects.ModifyAttributes(obj, obj.Attributes, true);
        }

        if (geometryModified)
        {
            // Update the object geometry if needed
            doc.Objects.Transform(obj, xform, true);
        }

        // Update views
        doc.Views.Redraw();

        var updatedObject = getObjectByIdOrName(new JObject { ["id"] = obj.Id.ToString() });
        if (geometryModified)
        {
            var poseAfter = ApplyTransformToPose(poseBefore, xform, center);
            WriteStoredPose(updatedObject, poseAfter);
            RefreshStoredObbFromObject(updatedObject);
        }
        else
        {
            GetOrBootstrapPose(updatedObject);
        }
        return BuildMinimalObjectState(updatedObject, changedFields, explicitUpdated);

    }
}
