namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Config used to run install tests
    /// </summary>
    public class TestConfiguration
    {
        public TestConfiguration() { }

        public string FtpHostname { get; set; }
        public string FtpUsername { get; set; }
        public string FtpPassword { get; set; }
        public string SQLConnectionString { get; set; }

        public bool IsValid => !string.IsNullOrEmpty(FtpHostname) && !string.IsNullOrEmpty(FtpUsername) && !string.IsNullOrEmpty(FtpPassword) && !string.IsNullOrEmpty(SQLConnectionString);
    }
}
