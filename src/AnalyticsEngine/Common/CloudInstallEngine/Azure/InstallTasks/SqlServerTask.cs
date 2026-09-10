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
    public class SqlServerTask : InstallTaskInAzResourceGroup<SqlServerResource>
    {
        public const string CONFIG_KEY_USERNAME = "username";
        public const string CONFIG_KEY_PASSWORD = "password";

        /// <summary>Display name / UPN of the Microsoft Entra administrator to assign on a NEW server.</summary>
        public const string CONFIG_KEY_ENTRA_ADMIN_LOGIN = "entraAdminLogin";

        /// <summary>Entra object (principal) ID of the administrator. Azure requires the ID, not the name.</summary>
        public const string CONFIG_KEY_ENTRA_ADMIN_OBJECT_ID = "entraAdminObjectId";

        /// <summary>Directory (tenant) ID the administrator principal lives in.</summary>
        public const string CONFIG_KEY_ENTRA_ADMIN_TENANT_ID = "entraAdminTenantId";

        /// <summary>"User", "Group" or "Application".</summary>
        public const string CONFIG_KEY_ENTRA_ADMIN_PRINCIPAL_TYPE = "entraAdminPrincipalType";

        /// <summary>"true" to create the server with SQL authentication disabled (Entra-only).</summary>
        public const string CONFIG_KEY_ENTRA_ONLY_AUTH = "entraOnlyAuth";

        private readonly bool _allowPublicAccess;

        public SqlServerTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create SQL Server";

        /// <summary>
        /// Whether the server already existed when the task ran. Callers use this to honour the rule that an
        /// existing server's authentication configuration is never changed - see issue #117.
        /// </summary>
        public bool ServerAlreadyExisted { get; private set; }

        /// <summary>
        /// Whether the task created the server with Microsoft Entra-only authentication (SQL authentication
        /// disabled). Always false for a pre-existing server.
        /// </summary>
        public bool CreatedWithEntraOnlyAuth { get; private set; }

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
                ServerAlreadyExisted = false;

                var entraAdmin = BuildEntraAdministrator();
                var entraOnly = entraAdmin != null && entraAdmin.IsAzureADOnlyAuthenticationEnabled == true;

                _logger.LogInformation($"Creating new SQL Server '{serverName}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")})...");

                var sqlServerData = new SqlServerData(AzureLocation)
                {
                    MinimalTlsVersion = "1.2",
                    PublicNetworkAccess = desiredAccess
                };

                if (entraAdmin != null)
                {
                    sqlServerData.Administrators = entraAdmin;
                    _logger.LogInformation(
                        $"SQL Server '{serverName}' will use Microsoft Entra ID authentication with administrator '{entraAdmin.Login}'" +
                        (entraOnly
                            ? " and SQL Server authentication DISABLED. No SQL administrator password is stored anywhere."
                            : ", alongside SQL Server authentication."));
                }

                // Azure rejects a SQL admin login without a password, and rejects both when the server is
                // created Entra-only. Only set them when SQL authentication will actually be available.
                if (!entraOnly)
                {
                    sqlServerData.AdministratorLogin = adminUsername;
                    sqlServerData.AdministratorLoginPassword = adminPassword;
                }

                CreatedWithEntraOnlyAuth = entraOnly;

                base.EnsureTagsOnNew(sqlServerData.Tags);
                var serverCreateResult = await Container.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, serverName, sqlServerData);
                sqlServer = serverCreateResult.Value;
            }
            else
            {
                ServerAlreadyExisted = true;
                CreatedWithEntraOnlyAuth = false;

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

                // Deliberately NOT touching Administrators / Entra-only authentication here. An existing
                // server may well be a long-standing SQL-authentication deployment, and silently flipping
                // its authentication would lock out the customer's own tooling, Power BI refreshes and any
                // other consumer of the database. See issue #117.
                if (needsUpdate)
                {
                    await Container.GetSqlServers().CreateOrUpdateAsync(WaitUntil.Completed, serverName, updateData);
                }

                _logger.LogInformation($"Found existing SQL Server '{sqlServer.Data.FullyQualifiedDomainName}'.");
                await base.EnsureTagsOnExisting(sqlServer.Data.Tags, sqlServer.GetTagResource());
            }
            return sqlServer;
        }

        /// <summary>
        /// Builds the Microsoft Entra administrator to stamp onto a newly created server, or null when this
        /// install is not using Entra authentication (the default, and what every existing deployment does).
        /// </summary>
        ServerExternalAdministrator BuildEntraAdministrator()
        {
            var objectIdRaw = base._config.GetOptionalConfigValue(CONFIG_KEY_ENTRA_ADMIN_OBJECT_ID);
            var tenantIdRaw = base._config.GetOptionalConfigValue(CONFIG_KEY_ENTRA_ADMIN_TENANT_ID);
            var login = base._config.GetOptionalConfigValue(CONFIG_KEY_ENTRA_ADMIN_LOGIN);

            if (string.IsNullOrWhiteSpace(objectIdRaw)) return null;

            Guid objectId;
            if (!Guid.TryParse(objectIdRaw, out objectId) || objectId == Guid.Empty)
            {
                _logger.LogWarning(
                    $"Ignoring the configured Microsoft Entra SQL administrator: '{objectIdRaw}' is not a valid object ID. " +
                    "The SQL Server will be created with SQL Server authentication only.");
                return null;
            }

            Guid tenantId;
            Guid.TryParse(tenantIdRaw, out tenantId);

            var admin = new ServerExternalAdministrator
            {
                AdministratorType = SqlAdministratorType.ActiveDirectory,
                Login = string.IsNullOrWhiteSpace(login) ? objectId.ToString() : login,
                Sid = objectId,
                PrincipalType = ParsePrincipalType(base._config.GetOptionalConfigValue(CONFIG_KEY_ENTRA_ADMIN_PRINCIPAL_TYPE)),
                IsAzureADOnlyAuthenticationEnabled =
                    string.Equals(base._config.GetOptionalConfigValue(CONFIG_KEY_ENTRA_ONLY_AUTH), "true", StringComparison.OrdinalIgnoreCase),
            };

            if (tenantId != Guid.Empty) admin.TenantId = tenantId;

            return admin;
        }

        static SqlServerPrincipalType ParsePrincipalType(string value)
        {
            if (string.Equals(value, "Group", StringComparison.OrdinalIgnoreCase)) return SqlServerPrincipalType.Group;
            if (string.Equals(value, "Application", StringComparison.OrdinalIgnoreCase)) return SqlServerPrincipalType.Application;
            return SqlServerPrincipalType.User;
        }
    }
}
