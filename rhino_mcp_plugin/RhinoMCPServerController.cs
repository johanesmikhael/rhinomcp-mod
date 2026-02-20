using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rhino;

namespace RhinoMCPModPlugin
{
    class RhinoMCPModServerController
    {
        private static RhinoMCPModServer server;

        public static void StartServer()
        {
            if (server == null)
            {
                server = new RhinoMCPModServer();
            }

            server.Start();
            RhinoApp.WriteLine("Server started.");
        }

        public static void StopServer()
        {
            if (server != null)
            {
                server.Stop();
                server = null;
                RhinoApp.WriteLine("Server stopped.");
            }
        }

        public static bool IsServerRunning()
        {
            return server != null;
        }
    }
}
