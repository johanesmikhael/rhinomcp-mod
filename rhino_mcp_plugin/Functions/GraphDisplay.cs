using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPModPlugin;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    /// <summary>
    /// Turn the connectivity-graph overlay on or off, and say what it should show.
    /// </summary>
    /// <remarks>
    /// The overlay existed only behind <c>mcpmodgraph</c>, which asks for a scope at the
    /// command line and therefore blocks forever when it is driven over the socket - the
    /// handler never returns and Rhino needs a restart. So an agent could build a model,
    /// resolve its joints and never see the picture that would show whether the joints it
    /// resolved are the ones it meant.
    ///
    /// This is that command's state, set directly and returned, with no prompting: what is
    /// visible and what it is scoped to. It is a display switch and nothing more - it
    /// computes no graph of its own and changes no geometry, so the picture it turns on is
    /// the same one the command turns on.
    /// </remarks>
    public JObject GraphDisplay(JObject parameters)
    {
        try
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                throw new InvalidOperationException("No active Rhino document.");
            }

            // Everything is optional: called with nothing at all this reports the current
            // state, which is what a caller wants before deciding to change it.
            if (parameters?["enabled"]?.Type == JTokenType.Boolean)
            {
                MCPConnectivityGraphController.SetEnabled(parameters["enabled"].Value<bool>());
            }

            if (TryReadGraphScope(doc, parameters, out var scope))
            {
                MCPConnectivityGraphController.PinnedScope = scope;
                MCPConnectivityGraphController.MarkDirty();
            }

            doc.Views.Redraw();

            var pinned = MCPConnectivityGraphController.PinnedScope;
            return new JObject
            {
                ["success"] = true,
                ["enabled"] = MCPConnectivityGraphController.IsEnabled,
                ["scope"] = pinned == null || pinned.IsWholeDocument
                    ? "whole document"
                    : pinned.Key,
                ["scope_object_count"] = pinned?.Ids?.Count ?? 0
            };
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = ex.Message
            };
        }
    }

    /// <summary>
    /// The scope arguments, if any were given.
    /// </summary>
    /// <remarks>
    /// Returns false when the call said nothing about scope, which is different from saying
    /// "the whole document": the first leaves a pinned scope alone, the second replaces it.
    /// <c>scope: "all"</c> is how a caller asks for the second.
    /// </remarks>
    private static bool TryReadGraphScope(RhinoDoc doc, JObject parameters, out GraphScope scope)
    {
        scope = null;
        if (parameters == null)
        {
            return false;
        }

        if (string.Equals(parameters["scope"]?.ToString(), "all", StringComparison.OrdinalIgnoreCase))
        {
            scope = GraphScope.All;
            return true;
        }

        var ids = new HashSet<Guid>();
        if (parameters["ids"] is JArray idTokens)
        {
            foreach (var token in idTokens)
            {
                if (Guid.TryParse(token?.ToString(), out var guid))
                {
                    ids.Add(guid);
                }
            }
        }

        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parameters["layer"] is JArray layerTokens)
        {
            foreach (var token in layerTokens.Select(t => t?.ToString())
                .Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                layers.Add(token);
            }
        }
        else if (!string.IsNullOrWhiteSpace(parameters["layer"]?.ToString()))
        {
            layers.Add(parameters["layer"].ToString());
        }

        var selectedOnly = parameters["selected"]?.Type == JTokenType.Boolean &&
            parameters["selected"].Value<bool>();

        if (ids.Count == 0 && layers.Count == 0 && !selectedOnly)
        {
            return false;
        }

        scope = new GraphScope
        {
            Ids = ids.Count > 0 ? ids : null,
            Layers = layers.Count > 0 ? layers : null,
            SelectedOnly = selectedOnly
        };
        return true;
    }
}
