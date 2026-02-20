using System;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using rhinomcp_mod.Serializers;

namespace RhinoMCPModPlugin.Functions;

public partial class RhinoMCPModFunctions
{
    public JObject CreateObjects(JObject parameters)
        {
            var doc = RhinoDoc.ActiveDoc;
            var results = new JObject();
            
            // Process each object in the parameters
            foreach (var property in parameters.Properties())
            {
                try
                {
                    // Get the object parameters
                    JObject objectParams = (JObject)property.Value;
                    
                    // Create the object using the existing CreateObject method
                    JObject result = CreateObject(objectParams);
                    
                    // Add the result to our results collection
                    results[property.Name] = result;
                }
                catch (Exception ex)
                {
                    // If there's an error creating this object, add the error to the results
                    results[property.Name] = new JObject
                    {
                        ["error"] = ex.Message
                    };
                }
            }
            
            // Update views
            doc.Views.Redraw();
            
            return results;
        }
}