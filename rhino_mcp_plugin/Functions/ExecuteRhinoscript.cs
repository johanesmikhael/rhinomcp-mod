using System;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;
using Rhino.Runtime;
using System.Text;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject ExecuteRhinoscript(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        string code = parameters["code"]?.ToString();
        if (string.IsNullOrEmpty(code))
        {
            throw new Exception("Code is required");
        }

        // register undo
        var undoRecordSerialNumber = doc.BeginUndoRecord("ExecuteRhinoScript");

        JObject result = new JObject();

        try
        {
            var output = new StringBuilder();
            // Create a new Python script instance
            PythonScript pythonScript = PythonScript.Create();

            pythonScript.Output += (message) =>
            {
                output.Append(message);
            };

            // Setup the script context with the current document
            if (doc != null)
                pythonScript.SetupScriptContext(doc);

            // Execute the Python code
            pythonScript.ExecuteScript(code);


            result["success"] = true;
            result["result"] = $"Script successfully executed! Print output: {output}";
        }
        catch (Exception ex)
        {

            result["success"] = false;
            result["message"] = $"Error executing rhinoscript: {ex}";
        }
        finally
        {
            // undo
            doc.EndUndoRecord(undoRecordSerialNumber);
        }

        // if the script failed, undo the changes
        if (!result["success"].ToObject<bool>())
        {
            doc.Undo();
        }

        return result;
    }
}