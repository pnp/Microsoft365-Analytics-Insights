using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;

namespace App.ControlPanel
{
    /// <summary>
    /// All installer app preferences
    /// </summary>
    public class InstallerPreferences : SecureLocalPreferences
    {
        protected override string FileTitle => "proxyconfig.dat";

        public InstallerFtpConfig FtpConfig { get; set; } = new InstallerFtpConfig();
        public TestConfiguration TestsConfig { get; set; } = new TestConfiguration();
    }
}
