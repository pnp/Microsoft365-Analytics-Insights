namespace App.ControlPanel.Engine.Entities
{
    public class InstallerFtpConfig
    {
        public bool UsePassive { get; set; } = false;
        public bool UseFTPS { get; set; } = true;

        public bool UseFtpProxy { get; set; } = false;
        public string ProxyHost { get; set; } = null;
        public int ProxyPort { get; set; } = 0;

        public bool IntegratedAuth { get; set; }

        public string ProxyUsername { get; set; } = null;
        public string ProxyPassword { get; set; } = null;

        [Newtonsoft.Json.JsonIgnore]
        public bool IsValid => UseFtpProxy ? ((!string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0) && (!IntegratedAuth ? (!string.IsNullOrEmpty(ProxyUsername) && !string.IsNullOrEmpty(ProxyUsername)) : true)) : true;

        [Newtonsoft.Json.JsonIgnore]
        public static InstallerFtpConfig Default => new InstallerFtpConfig();
    }
}
