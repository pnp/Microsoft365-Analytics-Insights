namespace App.ControlPanel.Engine.Models
{
    public class KuduPublishInfo
    {
        public string RootUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class AutodetectedSqlDetails
    {
        public SqlDetails Sql { get; set; }

        public class SqlDetails
        {
            public string SqlFqdn { get; set; }
            public string SqlUsername { get; set; }
            public string SqlPassword { get; set; }
            public Entities.SqlConnectionAuthMethod AuthMethod { get; set; }

            public string ConnectionString => AuthMethod == Entities.SqlConnectionAuthMethod.EntraId
                ? Entities.DatabasePaaSInfo.GetEntraIdConnectionString(SqlFqdn, null)
                : Entities.DatabasePaaSInfo.GetConnectionString(SqlFqdn, null, SqlUsername, SqlPassword);
        }
    }
}
