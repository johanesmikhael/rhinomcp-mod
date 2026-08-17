using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
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
        private const double DefaultPerspectiveLensMm = 50.0;
        private static readonly object CaptureViewSync = new object();

        public JObject CaptureView(JObject parameters)
        {
            // CaptureView temporarily operates on Rhino's active viewport. Serialize
            // calls so concurrent MCP requests cannot interleave projection changes.
            lock (CaptureViewSync)
            {
                return CaptureViewCore(parameters);
            }
        }

        private JObject CaptureViewCore(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new JObject { ["error"] = "No active document" };

            var view = doc.Views.ActiveView;
            if (view == null) return new JObject { ["error"] = "No active view" };

            var displayModeName = parameters["display_mode"]?.ToString() ?? "Shaded";
            var displayMode = DisplayModeDescription.FindByName(displayModeName);
            if (displayMode == null)
            {
                return new JObject { ["error"] = $"Display mode '{displayModeName}' not found" };
            }

            if (!TryResolveCaptureSize(parameters, out int width, out int height, out string sizeError))
            {
                return new JObject { ["error"] = sizeError };
            }

            var targets = ResolveCaptureTargets(doc, parameters, out string targetMode, out string targetError);
            if (targetError != null) return new JObject { ["error"] = targetError };

            var bbox = BuildTargetsBoundingBox(targets);
            if (targets.Count > 0 && !bbox.IsValid)
            {
                return new JObject { ["error"] = "Target objects do not have valid bounds" };
            }

            var viewport = view.ActiveViewport;
            var requestedView = (parameters["view"]?.ToString() ?? "perspective").ToLowerInvariant();
            bool hasCameraLocation = TryReadPoint3d(parameters["camera_location"], out Point3d cameraLocation, out string cameraLocationError);
            bool hasCameraTarget = TryReadPoint3d(parameters["camera_target"], out Point3d cameraTarget, out string cameraTargetError);
            bool hasExplicitCamera = hasCameraLocation && hasCameraTarget;
            bool preserveView = parameters["preserve_view"]?.ToObject<bool>() ?? true;

            if (cameraLocationError != null) return new JObject { ["error"] = $"camera_location {cameraLocationError}" };
            if (cameraTargetError != null) return new JObject { ["error"] = $"camera_target {cameraTargetError}" };
            if (hasCameraLocation != hasCameraTarget) return new JObject { ["error"] = "camera_location and camera_target must be provided together" };
            if (!hasExplicitCamera && !IsSupportedViewName(requestedView))
            {
                return new JObject { ["error"] = $"Unsupported view '{requestedView}'" };
            }
            if (!TryReadLensMm(parameters["lens_mm"], out double? requestedLensMm, out string lensError))
            {
                return new JObject { ["error"] = lensError };
            }

            Vector3d? requestedCameraUp = null;
            if (parameters.TryGetValue("camera_up", out JToken upToken) && upToken != null)
            {
                if (!TryReadVector3d(upToken, out Vector3d cameraUp, out string upError))
                {
                    return new JObject { ["error"] = $"camera_up {upError}" };
                }
                requestedCameraUp = cameraUp;
            }

            DisplayModeDescription originalDisplayMode = viewport.DisplayMode;
            bool pushedProjection = false;
            if (preserveView)
            {
                viewport.PushViewProjection();
                pushedProjection = true;
            }

            try
            {
                if (hasExplicitCamera)
                {
                    double lens = ResolvePerspectiveLens(viewport, requestedLensMm, preserveCurrentPerspective: true);
                    viewport.ChangeToPerspectiveProjection(true, lens);
                    viewport.Camera35mmLensLength = lens;
                    viewport.SetCameraLocations(cameraTarget, cameraLocation);
                }
                else if (bbox.IsValid)
                {
                    if (!ApplyPresetView(viewport, requestedView, bbox, requestedLensMm))
                    {
                        return new JObject { ["error"] = $"Unsupported view '{requestedView}'" };
                    }
                }
                else if (requestedLensMm.HasValue && viewport.IsPerspectiveProjection)
                {
                    viewport.Camera35mmLensLength = requestedLensMm.Value;
                }

                if (requestedCameraUp.HasValue)
                {
                    viewport.CameraUp = requestedCameraUp.Value;
                }

                if (bbox.IsValid && (parameters["fit"]?.ToObject<bool>() ?? true))
                {
                    double padding = Math.Max(parameters["padding"]?.ToObject<double>() ?? 1.15, 1.0);
                    var fitBox = bbox;
                    double inflate = Math.Max(fitBox.Diagonal.Length * (padding - 1.0) * 0.5, doc.ModelAbsoluteTolerance * 10.0);
                    fitBox.Inflate(inflate);
                    viewport.ZoomBoundingBox(fitBox);
                }

                viewport.DisplayMode = displayMode;
                doc.Views.Redraw();

                var size = new Size(width, height);
                bool drawGrid = parameters["draw_grid"]?.ToObject<bool>() ?? false;
                bool drawAxes = parameters["draw_axes"]?.ToObject<bool>() ?? false;
                using var bitmap = view.CaptureToBitmap(size, drawGrid, drawAxes, drawAxes);
                if (bitmap == null) return new JObject { ["error"] = "View capture failed" };

                string pngBase64;
                using (var stream = new MemoryStream())
                {
#pragma warning disable CA1416
                    bitmap.Save(stream, ImageFormat.Png);
#pragma warning restore CA1416
                    pngBase64 = Convert.ToBase64String(stream.ToArray());
                }

                var metadata = new JObject
                {
                    ["view"] = requestedView,
                    ["target_mode"] = targetMode,
                    ["display_mode"] = displayMode.EnglishName ?? displayModeName,
                    ["width"] = width,
                    ["height"] = height,
                    ["camera_location"] = SerializePoint(viewport.CameraLocation),
                    ["camera_target"] = SerializePoint(viewport.CameraTarget),
                    ["camera_up"] = SerializeVector(viewport.CameraUp),
                    ["lens_mm"] = viewport.IsPerspectiveProjection ? viewport.Camera35mmLensLength : null,
                    ["projection"] = GetProjectionName(viewport),
                    ["preserve_view"] = preserveView,
                    ["object_count"] = targets.Count,
                    ["objects"] = new JArray(targets.Select(o => new JObject
                    {
                        ["id"] = o.Id.ToString(),
                        ["name"] = o.Name ?? "",
                        ["type"] = o.ObjectType.ToString()
                    }))
                };

                if (bbox.IsValid) metadata["bbox"] = SerializeBoundingBox(bbox);

                return new JObject
                {
                    ["png_base64"] = pngBase64,
                    ["metadata"] = metadata
                };
            }
            finally
            {
                if (pushedProjection)
                {
                    viewport.PopViewProjection();
                    viewport.DisplayMode = originalDisplayMode;
                    doc.Views.Redraw();
                }
            }
        }

        private static bool TryResolveCaptureSize(JObject parameters, out int width, out int height, out string error)
        {
            const int min = 256;
            const int max = 1920;
            const int maxPixels = 2073600;

            string resolution = parameters["resolution"]?.ToString()?.ToLowerInvariant() ?? "medium";
            (width, height) = resolution switch
            {
                "low" => (640, 480),
                "medium" => (960, 720),
                "high" => (1280, 900),
                _ => (0, 0)
            };

            if (width == 0)
            {
                error = $"Unsupported resolution '{resolution}'";
                return false;
            }

            if (parameters["width"] != null) width = parameters["width"].ToObject<int>();
            if (parameters["height"] != null) height = parameters["height"].ToObject<int>();

            width = Math.Clamp(width, min, max);
            height = Math.Clamp(height, min, max);

            if (width * height > maxPixels)
            {
                double scale = Math.Sqrt((double)maxPixels / (width * height));
                width = Math.Max(min, (int)Math.Floor(width * scale));
                height = Math.Max(min, (int)Math.Floor(height * scale));
            }

            error = null;
            return true;
        }

        private static List<RhinoObject> ResolveCaptureTargets(RhinoDoc doc, JObject parameters, out string mode, out string error)
        {
            error = null;
            var targets = new List<RhinoObject>();
            var ids = parameters["ids"] as JArray;

            if (ids != null && ids.Count > 0)
            {
                mode = "ids";
                foreach (var idToken in ids)
                {
                    if (!Guid.TryParse(idToken?.ToString(), out Guid id))
                    {
                        error = $"Invalid object id '{idToken}'";
                        return targets;
                    }
                    var obj = doc.Objects.FindId(id);
                    if (obj == null)
                    {
                        error = $"Object '{id}' not found";
                        return targets;
                    }
                    targets.Add(obj);
                }
                return targets;
            }

            if (parameters["selected"]?.ToObject<bool>() ?? false)
            {
                mode = "selected";
                targets = doc.Objects.GetSelectedObjects(false, false)?.ToList() ?? new List<RhinoObject>();
                if (targets.Count == 0) error = "No selected objects to capture";
                return targets;
            }

            if (parameters["all_visible"]?.ToObject<bool>() ?? false)
            {
                mode = "all_visible";
                targets = doc.Objects.GetObjectList(ObjectType.AnyObject)
                    .Where(o => o != null && !o.IsDeleted && !o.IsHidden)
                    .ToList();
                if (targets.Count == 0) error = "No visible objects to capture";
                return targets;
            }

            mode = "viewport";
            return targets;
        }

        private static BoundingBox BuildTargetsBoundingBox(IEnumerable<RhinoObject> targets)
        {
            var bbox = BoundingBox.Empty;
            foreach (var obj in targets)
            {
                var objBbox = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                if (objBbox.IsValid) bbox = BoundingBox.Union(bbox, objBbox);
            }
            return bbox;
        }

        private static bool TryReadLensMm(JToken token, out double? lensMm, out string error)
        {
            lensMm = null;
            error = null;
            if (token == null)
            {
                return true;
            }

            double value;
            try
            {
                value = token.ToObject<double>();
            }
            catch
            {
                error = "lens_mm must be a positive finite number";
                return false;
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                error = "lens_mm must be a positive finite number";
                return false;
            }

            lensMm = value;
            return true;
        }

        private static double ResolvePerspectiveLens(
            RhinoViewport viewport,
            double? requestedLensMm,
            bool preserveCurrentPerspective)
        {
            if (requestedLensMm.HasValue)
            {
                return requestedLensMm.Value;
            }

            if (preserveCurrentPerspective && viewport.IsPerspectiveProjection)
            {
                double current = viewport.Camera35mmLensLength;
                if (!double.IsNaN(current) && !double.IsInfinity(current) && current > 0.0)
                {
                    return current;
                }
            }

            // RhinoCommon explicitly recommends 50 mm when converting a parallel
            // projection to perspective. A parallel viewport's lens property is not
            // a safe perspective value and can be sub-millimetre.
            return DefaultPerspectiveLensMm;
        }

        private static string GetProjectionName(RhinoViewport viewport)
        {
            if (viewport.IsTwoPointPerspectiveProjection)
            {
                return "two_point_perspective";
            }
            if (viewport.IsPerspectiveProjection)
            {
                return "perspective";
            }
            return "parallel";
        }

        private static bool ApplyPresetView(RhinoViewport viewport, string viewName, BoundingBox bbox, double? lensMm)
        {
            var center = bbox.Center;
            double distance = Math.Max(bbox.Diagonal.Length * 2.0, 1.0);

            Vector3d direction;
            Vector3d up;
            bool perspective = false;

            switch (viewName)
            {
                case "perspective":
                    perspective = true;
                    direction = new Vector3d(1, -1, 0.65);
                    up = Vector3d.ZAxis;
                    break;
                case "isometric":
                    perspective = true;
                    direction = new Vector3d(1, -1, 1);
                    up = Vector3d.ZAxis;
                    break;
                case "top":
                    direction = Vector3d.ZAxis;
                    up = Vector3d.YAxis;
                    break;
                case "front":
                    direction = -Vector3d.YAxis;
                    up = Vector3d.ZAxis;
                    break;
                case "right":
                    direction = Vector3d.XAxis;
                    up = Vector3d.ZAxis;
                    break;
                default:
                    return false;
            }

            direction.Unitize();
            if (perspective)
            {
                double resolvedLens = ResolvePerspectiveLens(
                    viewport,
                    lensMm,
                    preserveCurrentPerspective: false);
                viewport.ChangeToPerspectiveProjection(true, resolvedLens);
                viewport.Camera35mmLensLength = resolvedLens;
            }
            else
            {
                viewport.ChangeToParallelProjection(true);
            }

            viewport.SetCameraLocations(center, center + direction * distance);
            viewport.CameraUp = up;
            return true;
        }

        private static bool IsSupportedViewName(string viewName)
        {
            return viewName == "perspective" || viewName == "isometric" || viewName == "top" || viewName == "front" || viewName == "right";
        }

        private static bool TryReadPoint3d(JToken token, out Point3d point, out string error)
        {
            point = Point3d.Unset;
            error = null;
            if (token == null) return false;
            if (token is not JArray arr || arr.Count != 3)
            {
                error = "must be [x, y, z]";
                return false;
            }
            point = new Point3d(arr[0].ToObject<double>(), arr[1].ToObject<double>(), arr[2].ToObject<double>());
            return true;
        }

        private static bool TryReadVector3d(JToken token, out Vector3d vector, out string error)
        {
            vector = Vector3d.Unset;
            error = null;
            if (token is not JArray arr || arr.Count != 3)
            {
                error = "must be [x, y, z]";
                return false;
            }
            vector = new Vector3d(arr[0].ToObject<double>(), arr[1].ToObject<double>(), arr[2].ToObject<double>());
            if (!vector.Unitize())
            {
                error = "must be non-zero";
                return false;
            }
            return true;
        }

        private static JArray SerializePoint(Point3d point)
        {
            return new JArray(Math.Round(point.X, 6), Math.Round(point.Y, 6), Math.Round(point.Z, 6));
        }

        private static JArray SerializeVector(Vector3d vector)
        {
            return new JArray(Math.Round(vector.X, 6), Math.Round(vector.Y, 6), Math.Round(vector.Z, 6));
        }

        private static JObject SerializeBoundingBox(BoundingBox bbox)
        {
            return new JObject
            {
                ["min"] = SerializePoint(bbox.Min),
                ["max"] = SerializePoint(bbox.Max)
            };
        }
    }
}
