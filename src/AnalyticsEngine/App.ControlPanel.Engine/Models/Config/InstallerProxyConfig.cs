using Newtonsoft.Json;

namespace App.ControlPanel.Engine.Entities
{
    public class InstallerProxyConfig
    {
        // Legacy JSON names preserve compatibility with existing proxyconfig.dat files.
        [JsonProperty("UseFtpProxy")]
        public bool UseProxy { get; set; }

        [JsonProperty("ProxyHost")]
        public string Host { get; set; }

        [JsonProperty("ProxyPort")]
        public int Port { get; set; }

        [JsonProperty("IntegratedAuth")]
        public bool IntegratedAuth { get; set; }

        [JsonProperty("ProxyUsername")]
        public string Username { get; set; }

        [JsonProperty("ProxyPassword")]
        public string Password { get; set; }

        [JsonIgnore]
        public bool IsValid => !UseProxy ||
            (!string.IsNullOrEmpty(Host) && Port > 0 &&
             (IntegratedAuth || (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))));

        [JsonIgnore]
        public static InstallerProxyConfig Default => new InstallerProxyConfig();
    }
}
