using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.PlugIns;
using System;
using System.Collections.Generic;

namespace RhinoMCPModPlugin.Functions
{
    public partial class RhinoMCPModFunctions
    {
        public JObject ListPlugins(JObject parameters)
        {
            var plugins = new JArray();
            
            // Get all installed plugin IDs
            var pluginIds = PlugIn.GetInstalledPlugIns();
            
            foreach (var kvp in pluginIds)
            {
                var pluginId = kvp.Key;
                var pluginName = kvp.Value;
                
                plugins.Add(new JObject
                {
                    ["name"] = pluginName,
                    ["id"] = pluginId.ToString(),
                    ["status"] = "Installed"
                });
            }

            return new JObject
            {
                ["plugins"] = plugins,
                ["count"] = plugins.Count
            };
        }
    }
}
