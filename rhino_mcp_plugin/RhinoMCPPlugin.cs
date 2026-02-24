using System;
using Rhino;

namespace RhinoMCPModPlugin
{
    ///<summary>
    /// <para>Every RhinoCommon .rhp assembly must have one and only one PlugIn-derived
    /// class. DO NOT create instances of this class yourself. It is the
    /// responsibility of Rhino to create an instance of this class.</para>
    /// <para>To complete plug-in information, please also see all PlugInDescription
    /// attributes in AssemblyInfo.cs (you might need to click "Project" ->
    /// "Show All Files" to see it in the "Solution Explorer" window).</para>
    ///</summary>
    public class RhinoMCPModPlugin : Rhino.PlugIns.PlugIn
    {
        public RhinoMCPModPlugin()
        {
            Instance = this;
        }
        
        ///<summary>Gets the only instance of the RhinoMCPModPlugin plug-in.</summary>
        public static RhinoMCPModPlugin Instance { get; private set; }
        public override Rhino.PlugIns.PlugInLoadTime LoadTime => Rhino.PlugIns.PlugInLoadTime.AtStartup;

        protected override Rhino.PlugIns.LoadReturnCode OnLoad(ref string errorMessage)
        {
            RhinoMCPModServerController.StartServer();
            return Rhino.PlugIns.LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            RhinoMCPModServerController.StopServer();
            base.OnShutdown();
        }
    }
}
