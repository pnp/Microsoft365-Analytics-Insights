using Azure;
using Azure.Core;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class SqlServerTask : InstallTaskInAzResourceGroup<SqlServerResource>
    {
        public const string CONFIG_KEY_USERNAME = "username";
        public const string CONFIG_KEY_PASSWORD = "password";
        private readonly bool _allowPublicAccess;

        public SqlServerTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create SQL Server";

        public override async Task<SqlServerResource> ExecuteTaskReturnResult(object contextArg)
        {
            var serverName = base._config.GetNameConfigValue();
            var adminUsername = base._config.GetConfigValue(CONFIG_KEY_USERNAME);
            var adminPassword = base._config.GetConfigValue(CONFIG_KEY_PASSWORD);
            var desiredAccess = _allowPublicAccess ? ServerNetworkAccessFlag.Enabled : ServerNetworkAccessFlag.Disabled;

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
                _logger.LogInformation($"Creating new SQL Server '{serverName}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")})...");

                var sqlServerData = new SqlServerData(AzureLocation)
                {
                    AdministratorLogin = adminUsername,
                    AdministratorLoginPassword = adminPassword,
                    MinimalTlsVersion = "1.2",
                    PublicNetworkAccess = desiredAccess
                };

                base.EnsureTagsOnNew(sqlServerData.Tags);
                var serverCreateResult = await Container.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, serverName, sqlServerData);
                sqlServer = serverCreateResult.Value;
            }
            else
            {
                var needsUpdate = false;
                var updateData = new SqlServerData(AzureLocation);

                // Ensure minimum TLS version is 1.2
                if (sqlServer.Data.MinimalTlsVersion == null || string.Compare(sqlServer.Data.MinimalTlsVersion, "1.2") < 0)
                {
                    _logger.LogInformation($"Updating SQL Server '{serverName}' to enforce TLS 1.2...");
                    updateData.MinimalTlsVersion = "1.2";
                    needsUpdate = true;
                }

                if (sqlServer.Data.PublicNetworkAccess == null || sqlServer.Data.PublicNetworkAccess.Value != desiredAccess)
                {
                    _logger.LogInformation($"Updating SQL Server '{serverName}' public network access to '{desiredAccess}'...");
                    updateData.PublicNetworkAccess = desiredAccess;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    await Container.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, serverName, updateData);
                }

                _logger.LogInformation($"Found existing SQL Server '{sqlServer.Data.FullyQualifiedDomainName}'.");
                await base.EnsureTagsOnExisting(sqlServer.Data.Tags, sqlServer.GetTagResource());
            }
            return sqlServer;
        }
    }
}
