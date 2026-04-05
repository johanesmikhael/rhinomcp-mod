using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPModPlugin.Functions
{
    public partial class RhinoMCPModFunctions
    {
        public JObject RunRhinoCommand(JObject parameters)
        {
            string command = parameters["command"]?.ToString() ?? "";
            
            if (string.IsNullOrEmpty(command))
            {
                return new JObject
                {
                    ["error"] = "Command name is required"
                };
            }

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                return new JObject
                {
                    ["error"] = "No active Rhino document"
                };
            }

            try
            {
                // Ensure command has exactly one leading underscore
                // Remove all leading underscores first
                var trimmed = command.TrimStart('_');
                // Add single underscore for silent mode
                var formattedCommand = $"_{trimmed}";
                
                // Run the command via RhinoApp.RunScript
                var result = RhinoApp.RunScript(formattedCommand, false);
                
                return new JObject
                {
                    ["message"] = result ? $"Command '{command}' executed successfully." : $"Command '{command}' returned false (may need user input or arguments).",
                    ["success"] = result
                };
            }
            catch (System.Exception e)
            {
                return new JObject
                {
                    ["error"] = $"Error running command: {e.Message}"
                };
            }
        }
    }
}
