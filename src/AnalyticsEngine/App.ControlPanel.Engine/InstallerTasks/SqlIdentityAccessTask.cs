using App.ControlPanel.Engine.Entities;
using Azure.ResourceManager.Sql;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Reads an Azure SQL server's authentication configuration back from Azure Resource Manager, so the
    /// installer can decide whether to talk to it with a SQL login or a Microsoft Entra ID token rather
    /// than assuming. See issue #117.
    /// </summary>
    public static class SqlServerAuthReader
    {
        /// <summary>
        /// Inspects the server. Returns null when there is no server to inspect.
        /// </summary>
        public static async Task<SqlServerAuthState> ReadAsync(SqlServerResource sqlServer, ILogger logger)
        {
            if (sqlServer == null) return null;

            // The cached ARM payload may predate this run's create/update, so re-read it.
            var server = await sqlServer.GetAsync();
            var data = server.Value.Data;

            var state = new SqlServerAuthState
            {
                HasSqlAdminLogin = !string.IsNullOrWhiteSpace(data.AdministratorLogin),
                HasEntraAdmin = data.Administrators != null && data.Administrators.Sid.HasValue,
                EntraAdminLogin = data.Administrators?.Login,
                EntraOnlyAuthEnabled = data.Administrators?.IsAzureADOnlyAuthenticationEnabled == true,
            };

            // The server payload only reports Entra-only auth when the administrator was set through the
            // server resource. The authoritative source is the azureADOnlyAuthentications child resource,
            // which is also where a customer's own portal/CLI change shows up.
            if (!state.EntraOnlyAuthEnabled)
            {
                try
                {
                    foreach (var onlyAuth in sqlServer.GetSqlServerAzureADOnlyAuthentications())
                    {
                        if (onlyAuth.Data != null && onlyAuth.Data.IsAzureADOnlyAuthenticationEnabled == true)
                        {
                            state.EntraOnlyAuthEnabled = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A server that has never had it set returns 404/NotFound here. Not an error.
                    logger?.LogInformation($"Could not read Microsoft Entra-only authentication state for SQL Server '{data.Name}': {ex.Message}");
                }
            }

            // Likewise, an administrator assigned separately (portal, CLI, an older install) lives in the
            // administrators child collection rather than on the server payload.
            if (!state.HasEntraAdmin)
            {
                try
                {
                    var admin = sqlServer.GetSqlServerAzureADAdministrators().FirstOrDefault();
                    if (admin != null && admin.Data != null)
                    {
                        state.HasEntraAdmin = true;
                        state.EntraAdminLogin = admin.Data.Login;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogInformation($"Could not read the Microsoft Entra administrator for SQL Server '{data.Name}': {ex.Message}");
                }
            }

            logger?.LogInformation(
                $"SQL Server '{data.Name}' authentication: Microsoft Entra-only = {state.EntraOnlyAuthEnabled}, " +
                $"Microsoft Entra administrator = {(state.HasEntraAdmin ? (string.IsNullOrWhiteSpace(state.EntraAdminLogin) ? "yes" : state.EntraAdminLogin) : "none")}, " +
                $"SQL administrator login = {(state.HasSqlAdminLogin ? "present" : "none")}.");

            return state;
        }
    }

    /// <summary>
    /// Grants an Azure managed identity access to the analytics database by creating a contained user for
    /// it. This is the database half of "the App Service needs its own RBAC assignment" - Azure SQL has no
    /// ARM role that grants data-plane access, so it has to be done in T-SQL. See issue #117.
    /// </summary>
    public class SqlIdentityAccessTask
    {
        private readonly ILogger _logger;

        public SqlIdentityAccessTask(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Creates (or repairs) the contained user for <paramref name="principalName"/> and grants it the
        /// database roles the runtime needs.
        /// </summary>
        /// <remarks>
        /// Best-effort by design: it returns false rather than throwing, because a failure here does not
        /// invalidate the rest of the install and the operator can grant access by hand. It is also
        /// idempotent, so re-running the installer is safe.
        /// </remarks>
        public async Task<bool> GrantDatabaseAccessAsync(string connectionString, string principalName, Guid principalObjectId, IEnumerable<string> roles)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }

            if (string.IsNullOrWhiteSpace(principalName) || principalObjectId == Guid.Empty)
            {
                _logger.LogWarning(
                    "Skipping the database permission grant for the App Service: it has no system-assigned managed identity yet. " +
                    "Re-run the installer once the identity exists.");
                return false;
            }

            var roleList = roles == null ? new List<string>() : roles.ToList();
            var sql = SqlContainedUserScript.CreateUserAndGrantRoles(principalName, principalObjectId, roleList);

            _logger.LogInformation(
                $"Granting the App Service managed identity '{principalName}' access to the database " +
                $"({string.Join(", ", roleList)})...");

            try
            {
                using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
                {
                    await connection.OpenAsync();
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        cmd.CommandTimeout = 120;
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (SqlException ex)
            {
                _logger.LogError(
                    $"Could not grant the App Service managed identity '{principalName}' access to the database: {ex.Message}. " +
                    "The web application and web-jobs will not be able to connect with their managed identity until this is " +
                    "fixed. Sign in to the database as its Microsoft Entra administrator and run: " +
                    $"CREATE USER [{principalName}] FROM EXTERNAL PROVIDER; then add it to {string.Join(", ", roleList)}.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Could not grant the App Service managed identity access to the database: {ex.Message}");
                return false;
            }

            _logger.LogInformation($"App Service managed identity '{principalName}' now has database access.");
            return true;
        }
    }
}
