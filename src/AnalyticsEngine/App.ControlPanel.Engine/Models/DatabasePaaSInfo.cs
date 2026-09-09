using Azure.ResourceManager.Sql;
using System;

namespace App.ControlPanel.Engine.Entities
{
    public class DatabasePaaSInfo
    {
        public DatabasePaaSInfo(SqlServerResource server, SqlDatabaseResource database, SolutionInstallConfig config)
        {
            this.Database = database;
            this.Server = server;
            this.Config = config;
        }

        public SqlDatabaseResource Database { get; set; }
        public SqlServerResource Server { get; set; }
        public SolutionInstallConfig Config { get; set; }

        /// <summary>
        /// How to authenticate to this server. Set from what Azure reports about the server itself
        /// (see <see cref="SqlServerAuthDetection"/>) rather than assumed, so an existing SQL
        /// authentication deployment keeps its login and only a server with SQL authentication disabled
        /// gets a token-based connection string. Defaults to the legacy behaviour. See issue #117.
        /// </summary>
        public SqlConnectionAuthMethod AuthMethod { get; set; } = SqlConnectionAuthMethod.SqlLogin;

        public string ConnectionString
        {
            get
            {
                if (Database != null && Server != null && Config != null)
                {
                    if (AuthMethod == SqlConnectionAuthMethod.EntraId)
                    {
                        return GetEntraIdConnectionString(this.Server.Data.FullyQualifiedDomainName, Database.Data.Name);
                    }

                    var sqlConnectionString = GetConnectionString(this.Server.Data.FullyQualifiedDomainName, Database.Data.Name, this.Server.Data.AdministratorLogin, Config.SQLServerAdminPassword);

                    return sqlConnectionString;
                }
                else
                {
                    throw new InvalidOperationException("Server/Database/Config properties not set");
                }
            }
        }

        /// <summary>
        /// Connection string for a server that authenticates with Microsoft Entra ID: deliberately carries
        /// no <c>user id</c> and no password.
        /// </summary>
        /// <remarks>
        /// The absence of credentials is what signals Entra authentication - both to
        /// <c>DataUtils.Sql.AzureSqlTokenAuth</c>, which attaches an access token at connection-open time,
        /// and to anything reading the App Service configuration. Deliberately NOT
        /// <c>Authentication=Active Directory Managed Identity</c>: that keyword makes SqlClient acquire
        /// the token itself, which the in-box .NET Framework System.Data.SqlClient provider EF6 requires
        /// does not support for managed identity.
        /// </remarks>
        public static string GetEntraIdConnectionString(string server, string db)
        {
            if (string.IsNullOrEmpty(server))
            {
                throw new ArgumentException($"'{nameof(server)}' cannot be null or empty.", nameof(server));
            }

            var catalog = string.IsNullOrEmpty(db) ? string.Empty : $"initial catalog={db};";

            return $"data source={server};{catalog}" +
                "persist security info=False;" +
                "MultipleActiveResultSets=True;Encrypt=True;Connection Timeout=120";
        }

        public static string GetConnectionString(string server, string db, string username, string password)
        {
            if (string.IsNullOrEmpty(server))
            {
                throw new ArgumentException($"'{nameof(server)}' cannot be null or empty.", nameof(server));
            }


            if (string.IsNullOrEmpty(username))
            {
                throw new ArgumentException($"'{nameof(username)}' cannot be null or empty.", nameof(username));
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException($"'{nameof(password)}' cannot be null or empty.", nameof(password));
            }

            if (string.IsNullOrEmpty(db))
            {
                return $"data source={server};" +
                    $"persist security info=True;user id={username};password={password};" +
                    $"MultipleActiveResultSets=True;Connection Timeout=120";
            }
            else
            {
                return $"data source={server};initial catalog={db};" +
                    $"persist security info=True;user id={username};password={password};" +
                    $"MultipleActiveResultSets=True;Connection Timeout=120";
            }

        }
    }
}
