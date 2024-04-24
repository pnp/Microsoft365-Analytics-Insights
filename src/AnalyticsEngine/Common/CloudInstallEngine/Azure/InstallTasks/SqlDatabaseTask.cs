using Azure;
using Azure.Core;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class SqlDatabaseTask : InstallTaskInAzResourceGroup<SqlDatabaseResource>
    {
        public const string CONFIG_KEY_PERF_TIER = "tier";
        public const string PERF_TIER_BASIC = "Basic";
        public const string PERF_TIER_S2 = "S2";

        public SqlDatabaseTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create SQL Database";

        public override async Task<SqlDatabaseResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<SqlServerResource>(contextArg);

            var databaseName = base._config.GetNameConfigValue();
            var sqlServer = (SqlServerResource)contextArg;

            // Get database
            SqlDatabaseResource db = null;
            foreach (var existingDb in sqlServer.GetSqlDatabases())
            {
                if (existingDb.Data.Name == databaseName)
                {
                    db = existingDb;
                    break;
                }
            }

            if (db == null)
            {
                // Performance tier configured?
                var perfTier = PERF_TIER_BASIC;
                if (_config.ContainsKey(CONFIG_KEY_PERF_TIER))
                    perfTier = _config.GetConfigValue(CONFIG_KEY_PERF_TIER);

                _logger.LogInformation($"Creating new SQL Database '{databaseName}' on server '{sqlServer.Data.FullyQualifiedDomainName}' at service-level '{perfTier}'...");
                var dbInfo = new SqlDatabaseData(AzureLocation)
                {
                    Sku = new SqlSku(perfTier)
                };

                base.EnsureTagsOnNew(dbInfo.Tags);
                var dbCreateResp = await sqlServer.GetSqlDatabases().CreateOrUpdateAsync(WaitUntil.Completed, databaseName, dbInfo);
                db = dbCreateResp.Value;

                Console.WriteLine($"Created SQL Database '{db.Data.Name}' on server '{sqlServer.Data.FullyQualifiedDomainName}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing SQL Database '{db.Data.Name}' on server '{sqlServer.Data.FullyQualifiedDomainName}'.");
                await base.EnsureTagsOnExisting(db.Data.Tags, db.GetTagResource());
            }

            return db;
        }
    }
}
