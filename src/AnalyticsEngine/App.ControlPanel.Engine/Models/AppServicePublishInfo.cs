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

            /// <summary>
            /// Server-level connection string, with no database selected.
            /// </summary>
            /// <remarks>
            /// Right for a connectivity test, which only has to prove the login and the firewall work.
            /// Anything that reads or writes the solution's schema - the database schema upgrade, for
            /// example - needs <see cref="GetConnectionString(string)"/> with the database name instead.
            /// </remarks>
            public string ConnectionString => GetConnectionString(null);

            /// <summary>
            /// Connection string for a named database on this server, honouring the detected
            /// authentication method.
            /// </summary>
            /// <param name="databaseName">Database to select, or null/empty for a server-level connection.</param>
            public string GetConnectionString(string databaseName)
            {
                var db = string.IsNullOrWhiteSpace(databaseName) ? null : databaseName.Trim();

                return AuthMethod == Entities.SqlConnectionAuthMethod.EntraId
                    ? Entities.DatabasePaaSInfo.GetEntraIdConnectionString(SqlFqdn, db)
                    : Entities.DatabasePaaSInfo.GetConnectionString(SqlFqdn, db, SqlUsername, SqlPassword);
            }
        }
    }
}
