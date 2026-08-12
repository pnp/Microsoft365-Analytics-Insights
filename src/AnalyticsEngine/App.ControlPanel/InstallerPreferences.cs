using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;
using Newtonsoft.Json;

namespace App.ControlPanel
{
    /// <summary>
    /// All installer app preferences
    /// </summary>
    public class InstallerPreferences : SecureLocalPreferences
    {
        protected override string FileTitle => "proxyconfig.dat";

        // Keep the legacy JSON key so existing encrypted local preferences continue to load.
        [JsonProperty("FtpConfig")]
        public InstallerProxyConfig ProxyConfig { get; set; } = new InstallerProxyConfig();
        public TestConfiguration TestsConfig { get; set; } = new TestConfiguration();
    }
}
