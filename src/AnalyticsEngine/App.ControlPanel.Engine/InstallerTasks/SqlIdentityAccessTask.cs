using App.ControlPanel.Engine.Entities;
using Azure;
using Azure.ResourceManager.Sql;
using Common.Entities.Installer;
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
        public static async Task<SqlAuthDecision> DetectAsync(SqlServerResource sqlServer, string sqlPassword,
            SqlServerAuthMode configuredMode, ILogger logger)
        {
            if (sqlServer == null) throw new ArgumentNullException(nameof(sqlServer));

            var state = await ReadAsync(sqlServer, logger);
            var decision = SqlServerAuthDetection.Decide(state,
                state.HasSqlAdminLogin && !string.IsNullOrWhiteSpace(sqlPassword), configuredMode);
            logger?.LogInformation($"SQL authentication: {decision.Reason}");

            var noInteractiveAdmin = SqlServerAuthDetection.GetInteractiveAdminWarning(state, decision);
            if (!string.IsNullOrEmpty(noInteractiveAdmin)) logger?.LogWarning(noInteractiveAdmin);

            return decision;
        }

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
            };

            // The principal type is only carried on the server payload's embedded administrator, not on the
            // authoritative child resource read below. Keep it, but discard it if the two disagree about who
            // the administrator is - that means the embedded copy is stale and its type cannot be trusted.
            var embeddedAdminLogin = data.Administrators?.Login;
            var embeddedPrincipalType = data.Administrators?.PrincipalType?.ToString();

            // Always read the authoritative child, even when the embedded administrator says true:
            // that value can disagree after authentication is changed through the portal/CLI.
            try
            {
                var onlyAuth = await sqlServer.GetSqlServerAzureADOnlyAuthentications().GetAsync("Default");
                state.EntraOnlyAuthEnabled = onlyAuth.Value.Data.IsAzureADOnlyAuthenticationEnabled
                    ?? throw new InvalidOperationException("Azure did not return the SQL Server's Microsoft Entra-only authentication setting.");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                logger?.LogInformation("No Microsoft Entra-only authentication resource exists for this SQL Server; SQL logins are not disabled.");
            }
            catch (RequestFailedException ex)
            {
                logger?.LogError($"Could not read the SQL Server's Microsoft Entra-only authentication setting (HTTP {ex.Status}). " +
                    "Verify the installer can read Microsoft.Sql/servers/azureADOnlyAuthentications. Authentication will not be guessed.");
                throw;
            }

            // A separately removed/replaced administrator must not be inferred from the embedded payload.
            try
            {
                var admin = await sqlServer.GetSqlServerAzureADAdministrators().GetAsync("ActiveDirectory");
                state.HasEntraAdmin = admin.Value.Data.Sid.HasValue;
                state.EntraAdminLogin = admin.Value.Data.Login;

                if (state.HasEntraAdmin
                    && string.Equals(state.EntraAdminLogin, embeddedAdminLogin, StringComparison.OrdinalIgnoreCase))
                {
                    state.EntraAdminPrincipalType = embeddedPrincipalType;
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                logger?.LogInformation("No Microsoft Entra administrator is configured for this SQL Server.");
            }
            catch (RequestFailedException ex)
            {
                logger?.LogError($"Could not read the SQL Server's Microsoft Entra administrator (HTTP {ex.Status}). " +
                    "Verify the installer can read Microsoft.Sql/servers/administrators. Authentication will not be guessed.");
                throw;
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
