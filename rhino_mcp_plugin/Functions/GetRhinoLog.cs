using Newtonsoft.Json.Linq;
using Rhino;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RhinoMCPModPlugin.Functions
{
    public partial class RhinoMCPModFunctions
    {
        public JObject GetRhinoLog(JObject parameters)
        {
            int lines = 20;
            if (parameters["lines"] != null)
            {
                int.TryParse(parameters["lines"].ToString(), out lines);
                lines = Math.Max(1, Math.Min(100, lines));
            }

            var entries = new List<string>();
            
            try
            {
                // Try to find the Rhino command history file
                // Rhino 8 stores it in various locations depending on version
                var possiblePaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "McNeel", "Rhinoceros", "8.0", "settings", "default", "CommandHistory.txt"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "McNeel", "Rhinoceros", "8.0", "CommandHistory.txt"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "McNeel", "Rhinoceros", "8.0", "CommandHistory.txt"),
                };

                string? historyPath = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        historyPath = path;
                        break;
                    }
                }

                if (historyPath != null && File.Exists(historyPath))
                {
                    var historyLines = File.ReadAllLines(historyPath);
                    entries = historyLines.Reverse().Take(lines).Reverse().ToList();
                }
                else
                {
                    entries.Add("Command history file not found.");
                    entries.Add("Searched paths:");
                    foreach (var p in possiblePaths)
                    {
                        entries.Add($"  - {p}");
                    }
                }
            }
            catch (Exception e)
            {
                entries.Add($"Error reading command history: {e.Message}");
            }

            return new JObject
            {
                ["entries"] = new JArray(entries.ToArray()),
                ["count"] = entries.Count
            };
        }
    }
}
