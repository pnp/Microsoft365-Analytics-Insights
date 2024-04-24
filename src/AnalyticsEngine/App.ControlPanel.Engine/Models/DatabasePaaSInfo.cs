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

        public string ConnectionString
        {
            get
            {
                if (Database != null && Server != null && Config != null)
                {
                    var sqlConnectionString = GetConnectionString(this.Server.Data.FullyQualifiedDomainName, Database.Data.Name, this.Server.Data.AdministratorLogin, Config.SQLServerAdminPassword);

                    return sqlConnectionString;
                }
                else
                {
                    throw new InvalidOperationException("Server/Database/Config properties not set");
                }
            }
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
