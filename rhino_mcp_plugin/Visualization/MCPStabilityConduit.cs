using System;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;
using RhinoMCPModPlugin.Functions;

namespace RhinoMCPModPlugin;

internal sealed class MCPStabilityConduit : DisplayConduit
{
    private readonly DisplayMaterial _meshMaterial = new(Color.FromArgb(90, 120, 255, 120), 0.65);
    private const int MaxObjects = 240;

    protected override void DrawForeground(DrawEventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var checkedCount = 0;
        var drawn = 0;

        foreach (var obj in doc.Objects)
        {
            if (checkedCount >= MaxObjects)
                break;
            if (obj == null || obj.IsDeleted || !obj.Visible)
                continue;

            checkedCount++;
            if (TryReadFullMesh(obj, out var mesh))
            {
                e.Display.DrawMeshShaded(mesh, _meshMaterial);
                drawn++;
            }
        }
    }

    private static bool TryReadFullMesh(RhinoObject obj, out Mesh mesh)
    {
        mesh = null;
        try
        {
            string raw = obj?.Attributes?.GetUserString(RhinoMCPModFunctions.AfterEvaluationKey);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            if (JObject.Parse(raw) is not JObject payload)
                return false;
            if (payload["full_mesh"] is not JObject fm)
                return false;
            if (fm["vertices"] is not JArray verts || fm["faces"] is not JArray faces)
                return false;

            var m = new Mesh();
            foreach (JToken v in verts)
            {
                if (v is JArray a && a.Count >= 3)
                {
                    var x = a[0]?.ToObject<double>() ?? 0.0;
                    var y = a[1]?.ToObject<double>() ?? 0.0;
                    var z = a[2]?.ToObject<double>() ?? 0.0;
                    m.Vertices.Add(x, y, z);
                }
            }

            foreach (JToken f in faces)
            {
                if (f is JArray fi)
                {
                    if (fi.Count == 3)
                    {
                        int a = fi[0]?.ToObject<int>() ?? 0;
                        int b = fi[1]?.ToObject<int>() ?? 0;
                        int c = fi[2]?.ToObject<int>() ?? 0;
                        m.Faces.AddFace(a, b, c);
                    }
                    else if (fi.Count == 4)
                    {
                        int a = fi[0]?.ToObject<int>() ?? 0;
                        int b = fi[1]?.ToObject<int>() ?? 0;
                        int c = fi[2]?.ToObject<int>() ?? 0;
                        int d = fi[3]?.ToObject<int>() ?? 0;
                        m.Faces.AddFace(a, b, c, d);
                    }
                }
            }

            if (m.Vertices.Count == 0 || m.Faces.Count == 0)
                return false;

            m.Normals.ComputeNormals();
            m.Compact();
            mesh = m;
            return true;
        }
        catch
        {
            return false;
        }
    }

}

internal static class MCPStabilityController
{
    private static readonly MCPStabilityConduit Conduit = new();
    private static bool _enabled;

    public static bool IsEnabled => _enabled;

    public static void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            RhinoApp.WriteLine($"MCP Stability already {(enabled ? "ON" : "OFF")}.");
            return;
        }

        _enabled = enabled;
        Conduit.Enabled = enabled;
        RhinoDoc.ActiveDoc?.Views.Redraw();
        RhinoApp.WriteLine($"MCP Stability Display {(enabled ? "enabled" : "disabled")}.");
    }

    public static void Toggle()
    {
        SetEnabled(!_enabled);
    }
}
