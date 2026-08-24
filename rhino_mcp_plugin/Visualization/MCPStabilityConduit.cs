using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Where the bodies ended up, in a colour that classifies nothing.
    /// </summary>
    /// <remarks>
    /// This was the same green the graph overlay uses for a contact joint, drawn as a
    /// translucent shell over the very bearings it collided with - two different statements in
    /// one colour, on top of each other. The settled shape is not a kind of joint and has no
    /// place in that vocabulary, so it is grey: visible against a white viewport and a dark
    /// one, and impossible to mistake for contact green, pin blue or welded amber.
    /// </remarks>
    private readonly DisplayMaterial _meshMaterial =
        new(Color.FromArgb(110, 125, 130, 140), 0.55);
    private const int MaxObjects = 240;

    /// <summary>
    /// Push and pull, at the joints, along the force itself.
    /// </summary>
    /// <remarks>
    /// Both bodies at a joint carry equal and opposite forces, so the pair reads without a
    /// key. A bearing in compression pushes each body *away* from the interface - the podium
    /// down, the wall up - so the arrows point apart; a joint in tension pulls them together.
    /// Colour says the same thing again, for a joint seen end-on where the pair overlaps.
    ///
    /// Length goes as the square root of the force against the largest in the model. Linear
    /// scaling with a floor was tried first and is unreadable: a 3.3 N corner contact drew
    /// nearly as long as a 118 kN reaction, because the floor is what a force four orders of
    /// magnitude down gets. The square root keeps a tenth of the load visible while leaving a
    /// thousandth of it obviously nothing.
    /// </remarks>
    private static readonly Color PushColour = Color.FromArgb(255, 30, 90, 200);
    private static readonly Color PullColour = Color.FromArgb(255, 205, 45, 40);

    private const double ArrowShare = 0.10;
    private const double ArrowFloor = 0.02;

    protected override void DrawForeground(DrawEventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var checkedCount = 0;
        var forces = new List<(Point3d At, Vector3d Force, double Tension)>();
        var strongest = 0.0;
        var bounds = BoundingBox.Empty;

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
                bounds.Union(mesh.GetBoundingBox(true));
            }

            foreach (var force in ReadForces(obj))
            {
                forces.Add(force);
                strongest = Math.Max(strongest, force.Force.Length);
            }
        }

        if (forces.Count == 0 || !(strongest > 0.0) || !bounds.IsValid)
        {
            return;
        }

        // Scaled against the model's own size and its own largest force, so the same drawing
        // works for a stair in millimetres and a bridge in metres without a knob.
        var full = bounds.Diagonal.Length * ArrowShare;
        foreach (var (at, vector, tension) in forces)
        {
            var length = vector.Length;
            if (!(length > 0.0))
            {
                continue;
            }

            var direction = vector / length;
            var share = Math.Sqrt(length / strongest);
            var drawnLength = full * (ArrowFloor + (1.0 - ArrowFloor) * share);
            var line = new Line(at, at + direction * drawnLength);
            if (!line.IsValid)
            {
                continue;
            }

            e.Display.DrawArrow(line, tension > 0.0 ? PullColour : PushColour);
        }
    }

    /// <summary>
    /// The forces stored on one object by the last evaluation, if it carries any.
    /// </summary>
    private static IEnumerable<(Point3d At, Vector3d Force, double Tension)> ReadForces(
        RhinoObject obj)
    {
        JArray stored = null;
        try
        {
            var raw = obj?.Attributes?.GetUserString(RhinoMCPModFunctions.AfterEvaluationKey);
            if (!string.IsNullOrWhiteSpace(raw) && JObject.Parse(raw) is JObject payload)
            {
                stored = payload["forces"] as JArray;
            }
        }
        catch
        {
            stored = null;
        }

        if (stored == null)
        {
            yield break;
        }

        foreach (var token in stored)
        {
            if (token is not JObject record ||
                record["at"] is not JArray at || at.Count < 3 ||
                record["f"] is not JArray f || f.Count < 3)
            {
                continue;
            }

            yield return (
                new Point3d(
                    at[0].ToObject<double>(), at[1].ToObject<double>(), at[2].ToObject<double>()),
                new Vector3d(
                    f[0].ToObject<double>(), f[1].ToObject<double>(), f[2].ToObject<double>()),
                record["tension_n"]?.ToObject<double>() ?? 0.0);
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
