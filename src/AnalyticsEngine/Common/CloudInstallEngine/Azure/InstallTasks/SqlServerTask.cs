using Azure;
using Azure.Core;
using Azure.ResourceManager.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class SqlServerTask : InstallTaskInAzResourceGroup<SqlServerResource>
    {
        public const string CONFIG_KEY_USERNAME = "username";
        public const string CONFIG_KEY_PASSWORD = "password";
        public SqlServerTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create SQL Server";

        public override async Task<SqlServerResource> ExecuteTaskReturnResult(object contextArg)
        {
            var serverName = base._config.GetNameConfigValue();
            var adminUsername = base._config.GetConfigValue(CONFIG_KEY_USERNAME);
            var adminPassword = base._config.GetConfigValue(CONFIG_KEY_PASSWORD);

            SqlServerResource sqlServer = null;
            foreach (var server in Container.GetSqlServers())
            {
                if (server.Data.Name == serverName)
                {
                    sqlServer = server;
                    break;
                }
            }
            if (sqlServer == null)
            {
                _logger.LogInformation($"Creating new SQL Server '{serverName}'...");

                var sqlServerData = new SqlServerData(AzureLocation)
                {
                    AdministratorLogin = adminUsername,
                    AdministratorLoginPassword = adminPassword
                };

                base.EnsureTagsOnNew(sqlServerData.Tags);
                var serverCreateResult = await Container.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, serverName, sqlServerData);
                sqlServer = serverCreateResult.Value;
            }
            else
            {
                _logger.LogInformation($"Found existing SQL Server '{sqlServer.Data.FullyQualifiedDomainName}'.");
                await base.EnsureTagsOnExisting(sqlServer.Data.Tags, sqlServer.GetTagResource());
            }
            return sqlServer;
        }
    }
}
