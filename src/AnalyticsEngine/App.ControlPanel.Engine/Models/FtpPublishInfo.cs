namespace App.ControlPanel.Engine.Models
{
    public class FtpPublishInfo
    {
        public string RootUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    // For autodetecting test resources
    public class AutodetectedSqlAndFtpDetails
    {
        public FtpDetails Ftp { get; set; }
        public SqlDetails Sql { get; set; }
        public class SqlDetails
        {
            public string SqlFqdn { get; set; }
            public string SqlUsername { get; set; }
            public string SqlPassword { get; set; }
        }

        public class FtpDetails
        {
            public string Domain { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }
    }
}
