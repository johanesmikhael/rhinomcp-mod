using System;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPModPlugin;

internal sealed class MCPOBBConduit : DisplayConduit
{
    private readonly Color _boxColor = Color.FromArgb(220, 255, 210, 0);
    private readonly Color _outlineColor = Color.FromArgb(240, 0, 220, 255);
    private const string PoseStorageKey = "rhinomcp.pose.v1";
    private const string ObbStorageKey = "rhinomcp.obb.v1";
    private const int MaxObjects = 240;

    protected override void DrawForeground(DrawEventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            return;
        }

        var checkedCount = 0;
        var eligible = 0;
        var drawn = 0;

        foreach (var obj in doc.Objects)
        {
            if (checkedCount >= MaxObjects)
            {
                break;
            }

            if (!IsVisibleObject(obj))
            {
                continue;
            }

            checkedCount++;

            if (!TryGetCachedGeometryWithPose(
                    obj,
                    out var corners,
                    out var outlinePoints,
                    out var outlineClosed,
                    out var origin,
                    out var xAxis,
                    out var yAxis,
                    out var zAxis))
            {
                continue;
            }

            eligible++;
            DrawBoxEdges(e.Display, corners, _boxColor, 2);
            DrawProjectedOutline(e.Display, outlinePoints, outlineClosed, _outlineColor, 3);
            DrawPoseAxes(e.Display, corners, origin, xAxis, yAxis, zAxis);
            drawn++;
        }

        e.Display.Draw2dText(
            $"MCP OBB ON | cached+pose: {drawn}/{eligible}" + (checkedCount >= MaxObjects ? $" (max {MaxObjects})" : string.Empty),
            Color.White,
            new Point2d(20, 60),
            false,
            14);
    }

    private static bool IsVisibleObject(RhinoObject obj)
    {
        if (obj == null || obj.IsDeleted || !obj.Visible || obj.Geometry == null)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetCachedGeometryWithPose(
        RhinoObject obj,
        out Point3d[] corners,
        out Point3d[] outlinePoints,
        out bool outlineClosed,
        out Point3d origin,
        out Vector3d xAxis,
        out Vector3d yAxis,
        out Vector3d zAxis)
    {
        corners = null;
        outlinePoints = null;
        outlineClosed = false;
        origin = Point3d.Unset;
        xAxis = Vector3d.Unset;
        yAxis = Vector3d.Unset;
        zAxis = Vector3d.Unset;

        if (!TryReadCachedPoseFrame(obj, out origin, out xAxis, out yAxis, out zAxis))
        {
            return false;
        }

        return TryReadCachedObbAndOutline(obj, out corners, out outlinePoints, out outlineClosed);
    }

    private static bool TryReadCachedPoseFrame(
        RhinoObject obj,
        out Point3d origin,
        out Vector3d xAxis,
        out Vector3d yAxis,
        out Vector3d zAxis)
    {
        origin = Point3d.Unset;
        xAxis = Vector3d.Unset;
        yAxis = Vector3d.Unset;
        zAxis = Vector3d.Unset;

        string raw = obj?.Attributes?.GetUserString(PoseStorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            if (JObject.Parse(raw) is not JObject pose)
            {
                return false;
            }

            if (pose["world_from_local"] is not JObject worldFromLocal)
            {
                return false;
            }

            if (worldFromLocal["R"] is not JArray r || r.Count != 3 ||
                worldFromLocal["t"] is not JArray t || t.Count != 3)
            {
                return false;
            }

            if (r[0] is not JArray r0 || r0.Count != 3 ||
                r[1] is not JArray r1 || r1.Count != 3 ||
                r[2] is not JArray r2 || r2.Count != 3)
            {
                return false;
            }

            xAxis = new Vector3d(
                r0[0]?.ToObject<double>() ?? 0.0,
                r1[0]?.ToObject<double>() ?? 0.0,
                r2[0]?.ToObject<double>() ?? 0.0);
            yAxis = new Vector3d(
                r0[1]?.ToObject<double>() ?? 0.0,
                r1[1]?.ToObject<double>() ?? 0.0,
                r2[1]?.ToObject<double>() ?? 0.0);
            zAxis = new Vector3d(
                r0[2]?.ToObject<double>() ?? 0.0,
                r1[2]?.ToObject<double>() ?? 0.0,
                r2[2]?.ToObject<double>() ?? 0.0);
            origin = new Point3d(
                t[0]?.ToObject<double>() ?? 0.0,
                t[1]?.ToObject<double>() ?? 0.0,
                t[2]?.ToObject<double>() ?? 0.0);

            return origin.IsValid && xAxis.IsValid && yAxis.IsValid && zAxis.IsValid &&
                   xAxis.Unitize() && yAxis.Unitize() && zAxis.Unitize();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadCachedObbAndOutline(
        RhinoObject obj,
        out Point3d[] corners,
        out Point3d[] outlinePoints,
        out bool outlineClosed)
    {
        corners = null;
        outlinePoints = null;
        outlineClosed = false;

        string raw = obj?.Attributes?.GetUserString(ObbStorageKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            if (JObject.Parse(raw) is not JObject payload)
            {
                return false;
            }

            JObject obb = payload["obb"] as JObject ?? payload;
            if (obb["world_corners"] is not JArray worldCorners || worldCorners.Count != 8)
            {
                return false;
            }

            var parsedCorners = new Point3d[8];
            for (var i = 0; i < 8; i++)
            {
                if (!TryParsePoint(worldCorners[i], out var point))
                {
                    return false;
                }

                parsedCorners[i] = point;
            }

            if (!TryReadOutline(payload, out outlinePoints, out outlineClosed))
            {
                return false;
            }

            corners = parsedCorners;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadOutline(JObject payload, out Point3d[] points, out bool closed)
    {
        points = null;
        closed = false;

        JObject outline = payload["proj_outline_world"] as JObject ?? payload["surface_edges_world"] as JObject;
        if (outline? ["points"] is not JArray arr || arr.Count < 2)
        {
            return false;
        }

        var parsed = new Point3d[arr.Count];
        for (var i = 0; i < arr.Count; i++)
        {
            if (!TryParsePoint(arr[i], out var p))
            {
                return false;
            }

            parsed[i] = p;
        }

        points = parsed;
        closed = outline["closed"]?.ToObject<bool>() ?? false;
        return true;
    }

    private static bool TryParsePoint(JToken token, out Point3d point)
    {
        point = Point3d.Unset;
        if (token is not JArray array || array.Count < 3)
        {
            return false;
        }

        point = new Point3d(
            array[0]?.ToObject<double>() ?? 0.0,
            array[1]?.ToObject<double>() ?? 0.0,
            array[2]?.ToObject<double>() ?? 0.0);
        return point.IsValid;
    }

    private static void DrawBoxEdges(DisplayPipeline display, Point3d[] corners, Color color, int thickness)
    {
        if (corners == null || corners.Length != 8)
        {
            return;
        }

        DrawEdge(display, corners, 0, 1, color, thickness);
        DrawEdge(display, corners, 1, 2, color, thickness);
        DrawEdge(display, corners, 2, 3, color, thickness);
        DrawEdge(display, corners, 3, 0, color, thickness);

        DrawEdge(display, corners, 4, 5, color, thickness);
        DrawEdge(display, corners, 5, 6, color, thickness);
        DrawEdge(display, corners, 6, 7, color, thickness);
        DrawEdge(display, corners, 7, 4, color, thickness);

        DrawEdge(display, corners, 0, 4, color, thickness);
        DrawEdge(display, corners, 1, 5, color, thickness);
        DrawEdge(display, corners, 2, 6, color, thickness);
        DrawEdge(display, corners, 3, 7, color, thickness);
    }

    private static void DrawProjectedOutline(DisplayPipeline display, Point3d[] points, bool closed, Color color, int thickness)
    {
        if (points == null || points.Length < 2)
        {
            return;
        }

        for (var i = 0; i < points.Length - 1; i++)
        {
            display.DrawLine(points[i], points[i + 1], color, thickness);
        }

        if (closed)
        {
            display.DrawLine(points[points.Length - 1], points[0], color, thickness);
        }
    }

    private static void DrawPoseAxes(
        DisplayPipeline display,
        Point3d[] corners,
        Point3d origin,
        Vector3d xAxis,
        Vector3d yAxis,
        Vector3d zAxis)
    {
        if (corners == null || corners.Length != 8 || !origin.IsValid)
        {
            return;
        }

        var min = corners[0];
        var max = corners[0];
        for (var i = 1; i < corners.Length; i++)
        {
            min.X = Math.Min(min.X, corners[i].X);
            min.Y = Math.Min(min.Y, corners[i].Y);
            min.Z = Math.Min(min.Z, corners[i].Z);
            max.X = Math.Max(max.X, corners[i].X);
            max.Y = Math.Max(max.Y, corners[i].Y);
            max.Z = Math.Max(max.Z, corners[i].Z);
        }

        var diag = min.DistanceTo(max);
        var axisLength = Math.Max(diag * 0.10, 0.1);

        display.DrawLine(origin, origin + (xAxis * axisLength), Color.FromArgb(240, 255, 80, 80), 3);
        display.DrawLine(origin, origin + (yAxis * axisLength), Color.FromArgb(240, 80, 255, 120), 3);
        display.DrawLine(origin, origin + (zAxis * axisLength), Color.FromArgb(240, 100, 170, 255), 3);
    }

    private static void DrawEdge(DisplayPipeline display, Point3d[] corners, int a, int b, Color color, int thickness)
    {
        display.DrawLine(corners[a], corners[b], color, thickness);
    }
}

internal static class MCPOBBController
{
    private static readonly MCPOBBConduit Conduit = new();
    private static bool _enabled;

    public static bool IsEnabled => _enabled;

    public static void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            RhinoApp.WriteLine($"MCP OBB already {(enabled ? "ON" : "OFF")}.");
            return;
        }

        _enabled = enabled;
        Conduit.Enabled = enabled;
        RhinoDoc.ActiveDoc?.Views.Redraw();
        RhinoApp.WriteLine($"MCP OBB {(enabled ? "enabled" : "disabled")}.");
    }

    public static void Toggle()
    {
        SetEnabled(!_enabled);
    }
}
