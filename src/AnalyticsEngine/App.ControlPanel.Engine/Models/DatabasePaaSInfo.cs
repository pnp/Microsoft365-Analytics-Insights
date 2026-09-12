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

                    // Explain the real problem here. Otherwise GetConnectionString throws a bare
                    // ArgumentException about a parameter name, which surfaces as "FATAL: Unexpected error
                    // of type 'ArgumentException'" and tells the operator nothing. See issue #117.
                    EnsureSqlLoginUsable(this.Server.Data.FullyQualifiedDomainName, this.Server.Data.AdministratorLogin, Config.SQLServerAdminPassword);

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
        /// Throws with an actionable message when there is no way to authenticate to the server: no
        /// Microsoft Entra administrator to get a token as, and no SQL administrator login configured.
        /// </summary>
        /// <remarks>
        /// Exists as its own method purely so the message is unit testable - the ARM resource types the
        /// <see cref="ConnectionString"/> getter reads cannot be constructed in a test. See issue #117.
        /// </remarks>
        public static void EnsureSqlLoginUsable(string server, string username, string password)
        {
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password)) return;

            throw new InvalidOperationException(
                $"There is no way to authenticate to SQL Server '{server}'. It has no Microsoft Entra administrator, so " +
                "Microsoft Entra ID authentication is not possible, and no SQL administrator username/password is " +
                "configured. Either set the SQL administrator credentials in the installer, or assign a Microsoft Entra " +
                "administrator to the server in the Azure portal (SQL Server > Settings > Microsoft Entra ID) and re-run " +
                "the installer.");
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
        /// the token itself, which the in-box .NET Framework Microsoft.Data.SqlClient provider EF6 requires
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
