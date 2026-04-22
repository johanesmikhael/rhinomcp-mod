using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace RhinoMCPModPlugin.Functions
{
    public partial class RhinoMCPModFunctions
    {
        private static readonly Dictionary<string, string> _layerStates = new();

        public JObject GetSelectedObjects(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };

            var selected = new JArray();
            var objs = doc.Objects.GetSelectedObjects(false, false);
            
            if (objs == null) return new JObject { ["selected"] = selected, ["count"] = 0 };

            foreach (var obj in objs)
            {
                if (obj == null) continue;
                var layer = doc.Layers[obj.Attributes.LayerIndex];
                selected.Add(new JObject
                {
                    ["id"] = obj.Id.ToString(),
                    ["name"] = obj.Name ?? "",
                    ["type"] = obj.ObjectType.ToString(),
                    ["layer"] = layer?.Name ?? "",
                    ["materialIndex"] = obj.Attributes.MaterialIndex
                });
            }

            return new JObject { ["selected"] = selected, ["count"] = selected.Count };
        }

        public JObject SelectObjectsByFilter(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };

            var ids = parameters["ids"] as JArray;
            var names = parameters["names"] as JArray;
            var layer = parameters["layer"]?.ToString();
            var type = parameters["type"]?.ToString();

            int selected = 0;
            var objs = doc.Objects.GetObjectList(ObjectType.AnyObject);

            foreach (var obj in objs)
            {
                bool match = false;
                var layerName = doc.Layers[obj.Attributes.LayerIndex]?.Name;

                if (ids != null && ids.Any(id => id.ToString() == obj.Id.ToString())) match = true;
                else if (names != null && names.Any(n => obj.Name?.Equals(n.ToString(), StringComparison.OrdinalIgnoreCase) == true)) match = true;
                else if (!string.IsNullOrEmpty(layer) && layerName?.Equals(layer, StringComparison.OrdinalIgnoreCase) == true) match = true;
                else if (!string.IsNullOrEmpty(type) && obj.ObjectType.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)) match = true;

                if (match) { obj.Select(true); selected++; }
            }

            doc.Views.Redraw();
            return new JObject { ["message"] = $"Selected {selected} object(s).", ["count"] = selected };
        }

        public JObject DeselectAll(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            doc.Objects.UnselectAll();
            doc.Views.Redraw();
            return new JObject { ["message"] = "All objects deselected." };
        }

        public JObject ZoomToObjects(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var ids = parameters["ids"] as JArray;
            var view = doc.Views.ActiveView;
            if (view == null) return new JObject { ["error"] = "No active view" };

            var selected = new List<RhinoObject>();
            if (ids != null && ids.Count > 0)
            {
                foreach (var idStr in ids)
                {
                    if (Guid.TryParse(idStr.ToString(), out Guid id))
                    {
                        var obj = doc.Objects.FindId(id);
                        if (obj != null) selected.Add(obj);
                    }
                }
            }
            else
            {
                var sel = doc.Objects.GetSelectedObjects(false, false);
                if (sel != null) selected = sel.ToList();
            }

            if (selected.Count == 0) return new JObject { ["error"] = "No objects to zoom to" };

            var bbox = BoundingBox.Empty;
            foreach (var obj in selected) { bbox = BoundingBox.Union(bbox, obj.Geometry.GetBoundingBox(false)); }

            view.ActiveViewport.ZoomBoundingBox(bbox);
            view.Redraw();
            return new JObject { ["message"] = $"Zoomed to {selected.Count} object(s)." };
        }

        public JObject GetViewportInfo(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var viewports = new JArray();
            foreach (var view in doc.Views)
            {
                var vp = view.ActiveViewport;
                viewports.Add(new JObject { ["id"] = vp.Id.ToString(), ["name"] = vp.Name,
                    ["cameraLocation"] = $"{vp.CameraLocation.X:F2},{vp.CameraLocation.Y:F2},{vp.CameraLocation.Z:F2}",
                    ["cameraTarget"] = $"{vp.CameraTarget.X:F2},{vp.CameraTarget.Y:F2},{vp.CameraTarget.Z:F2}" });
            }
            return new JObject { ["viewports"] = viewports, ["count"] = viewports.Count };
        }

        public JObject RenameLayer(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var newName = parameters["new_name"]?.ToString();
            var layerIdStr = parameters["id"]?.ToString();
            int layerIndex = -1;
            
            if (!string.IsNullOrEmpty(layerIdStr) && Guid.TryParse(layerIdStr, out Guid id))
            {
                for (int i = 0; i < doc.Layers.Count; i++)
                {
                    if (doc.Layers[i]?.Id == id) { layerIndex = i; break; }
                }
            }

            if (layerIndex < 0) return new JObject { ["error"] = "Layer not found" };
            var layer = doc.Layers[layerIndex];
            if (layer == null) return new JObject { ["error"] = "Layer not found" };

            layer.Name = newName ?? layer.Name;
            doc.Layers.Modify(layer, layerIndex, true);
            return new JObject { ["message"] = $"Layer renamed to '{layer.Name}'." };
        }

        public JObject MoveObjectsToLayer(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var ids = parameters["ids"] as JArray;
            var layerName = parameters["layer"]?.ToString();

            int layerIndex = -1;
            if (!string.IsNullOrEmpty(layerName))
                layerIndex = doc.Layers.FindByFullPath(layerName, -1);

            if (layerIndex < 0) return new JObject { ["error"] = "Layer not found" };

            int moved = 0;
            if (ids != null)
            {
                foreach (var idStr in ids)
                {
                    if (Guid.TryParse(idStr.ToString(), out Guid objId))
                    {
                        var obj = doc.Objects.FindId(objId);
                        if (obj != null)
                        {
                            var attrs = obj.Attributes.Duplicate();
                            attrs.LayerIndex = layerIndex;
                            doc.Objects.ModifyAttributes(obj, attrs, true);
                            moved++;
                        }
                    }
                }
            }
            doc.Views.Redraw();
            return new JObject { ["message"] = $"Moved {moved} object(s) to layer.", ["count"] = moved };
        }

        public JObject GetLayerStates(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var layers = new JArray();
            foreach (var layer in doc.Layers)
            {
                layers.Add(new JObject { ["index"] = layer.Index, ["name"] = layer.Name,
                    ["visible"] = layer.IsVisible, ["locked"] = layer.IsLocked,
                    ["color"] = $"{layer.Color.R},{layer.Color.G},{layer.Color.B}" });
            }
            return new JObject { ["layers"] = layers, ["count"] = layers.Count };
        }

        public JObject SaveLayerState(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) return new JObject { ["error"] = "name is required" };

            var layerStates = new JArray();
            foreach (var layer in doc.Layers)
                layerStates.Add(new JObject { ["name"] = layer.Name, ["visible"] = layer.IsVisible, ["locked"] = layer.IsLocked });

            _layerStates[name] = layerStates.ToString();
            return new JObject { ["message"] = $"Layer state '{name}' saved.", ["name"] = name };
        }

        public JObject RestoreLayerState(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) return new JObject { ["error"] = "name is required" };

            if (!_layerStates.TryGetValue(name, out var stateStr))
                return new JObject { ["error"] = $"Layer state '{name}' not found." };

            var states = JArray.Parse(stateStr);
            int restored = 0;
            foreach (var state in states)
            {
                var layerName = state["name"]?.ToString();
                var visible = state["visible"]?.ToObject<bool>() ?? true;
                var idx = doc.Layers.FindByFullPath(layerName, -1);
                if (idx >= 0)
                {
                    var layer = doc.Layers[idx];
                    layer.IsVisible = visible;
                    doc.Layers.Modify(layer, idx, true);
                    restored++;
                }
            }
            doc.Views.Redraw();
            return new JObject { ["message"] = $"Restored {restored} layer(s).", ["count"] = restored };
        }

        public JObject GetMaterials(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var materials = new JArray();
            foreach (var mat in doc.Materials)
                materials.Add(new JObject { ["index"] = mat.Index, ["name"] = mat.Name,
                    ["diffuseColor"] = $"{mat.DiffuseColor.R},{mat.DiffuseColor.G},{mat.DiffuseColor.B}" });
            return new JObject { ["materials"] = materials, ["count"] = materials.Count };
        }

        public JObject SetObjectMaterial(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var ids = parameters["ids"] as JArray;
            var materialName = parameters["material_name"]?.ToString();
            var materialIndex = parameters["material_index"]?.ToObject<int>();

            int matIdx = materialIndex ?? -1;
            if (matIdx < 0 && !string.IsNullOrEmpty(materialName))
                matIdx = doc.Materials.Find(materialName, true);
            if (matIdx < 0) return new JObject { ["error"] = "Material not found" };

            int updated = 0;
            if (ids != null)
            {
                foreach (var idStr in ids)
                {
                    if (Guid.TryParse(idStr.ToString(), out Guid objId))
                    {
                        var obj = doc.Objects.FindId(objId);
                        if (obj != null)
                        {
                            var attrs = obj.Attributes.Duplicate();
                            attrs.MaterialIndex = matIdx;
                            attrs.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                            doc.Objects.ModifyAttributes(obj, attrs, true);
                            updated++;
                        }
                    }
                }
            }
            doc.Views.Redraw();
            return new JObject { ["message"] = $"Updated {updated} object(s) material.", ["count"] = updated };
        }

        public JObject CreateMaterial(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var name = parameters["name"]?.ToString() ?? "NewMaterial";
            var r = parameters["r"]?.ToObject<int>() ?? 128;
            var g = parameters["g"]?.ToObject<int>() ?? 128;
            var b = parameters["b"]?.ToObject<int>() ?? 128;

            var mat = doc.Materials.Add();
            if (mat >= 0)
            {
                var material = doc.Materials[mat];
                material.Name = name;
                material.DiffuseColor = Color.FromArgb(r, g, b);
                doc.Materials.Modify(material, mat, true);
            }
            doc.Views.Redraw();
            return new JObject { ["message"] = $"Material '{name}' created.", ["index"] = mat };
        }

        public JObject GetObjectMaterials(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };
            var ids = parameters["ids"] as JArray;
            var objects = new JArray();

            if (ids == null)
            {
                var allObjs = doc.Objects.GetObjectList(ObjectType.AnyObject);
                foreach (var obj in allObjs)
                {
                    var matIndex = obj.Attributes.MaterialIndex;
                    var matName = matIndex >= 0 && matIndex < doc.Materials.Count ? doc.Materials[matIndex]?.Name : "ByLayer";
                    objects.Add(new JObject { ["id"] = obj.Id.ToString(), ["name"] = obj.Name ?? "",
                        ["material_index"] = matIndex, ["material_name"] = matName });
                }
            }
            return new JObject { ["objects"] = objects, ["count"] = objects.Count };
        }
    }
}
