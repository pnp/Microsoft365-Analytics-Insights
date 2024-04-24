using System.Collections.Generic;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// For reporting a config & install messages
    /// </summary>
    public class InstallStatus : Base64Serialisable<InstallStatus>
    {
        public string ConfigurationJSon { get; set; }
        public List<InstallLogEventArgs> Events { get; set; }

        public string ConnectionString { get; set; }

        public string SetupUserName { get; set; }

    }
}
