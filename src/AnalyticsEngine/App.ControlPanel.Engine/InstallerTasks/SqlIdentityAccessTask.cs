using App.ControlPanel.Engine.Entities;
using Azure;
using Azure.ResourceManager.Sql;
using Common.Entities.Installer;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
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
            SqlServerAuthMode configuredMode, ILogger logger, Guid installerObjectId = default(Guid),
            bool hasConfiguredDatabaseUsers = false)
        {
            if (sqlServer == null) throw new ArgumentNullException(nameof(sqlServer));

            var state = await ReadAsync(sqlServer, logger);
            var decision = SqlServerAuthDetection.Decide(state,
                state.HasSqlAdminLogin && !string.IsNullOrWhiteSpace(sqlPassword), configuredMode);
            decision.ServerState = state;
            logger?.LogInformation($"SQL authentication: {decision.Reason}");

            // Said BEFORE anything tries to connect: without it, a server whose administrator was reassigned
            // to a person looks completely healthy for several minutes and then fails with a login error
            // that names no principal at all.
            var lockout = SqlServerAuthDetection.GetInstallerLockoutWarning(state, decision, installerObjectId);
            if (!string.IsNullOrEmpty(lockout)) logger?.LogWarning(lockout);

            var noInteractiveAdmin = SqlServerAuthDetection.GetInteractiveAdminWarning(state, decision, hasConfiguredDatabaseUsers);
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
            // authoritative child resource read below. Correlate the two by object ID (SID) rather than by
            // login: a login is a display name, so it is neither stable nor unique, and two administrators
            // that merely share one are not the same principal. Requiring both SIDs to be present and equal
            // also stops a pair of absent values matching each other.
            var embeddedAdminSid = data.Administrators?.Sid;
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
                var childSid = admin.Value.Data.Sid;
                state.HasEntraAdmin = childSid.HasValue;
                state.EntraAdminLogin = admin.Value.Data.Login;
                state.EntraAdminSid = childSid;

                if (childSid.HasValue && embeddedAdminSid.HasValue && embeddedAdminSid.Value == childSid.Value)
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
        private readonly IEntraPrincipalResolver _resolver;
        private readonly IManagedIdentityApplicationIdSource _identitySource;

        public SqlIdentityAccessTask(ILogger logger, IEntraPrincipalResolver resolver = null,
            IManagedIdentityApplicationIdSource identitySource = null)
        {
            _logger = logger;
            _resolver = resolver;
            _identitySource = identitySource;
        }

        /// <summary>
        /// Creates (or repairs) the contained user for <paramref name="principalName"/> and grants it the
        /// database roles the runtime needs.
        /// </summary>
        /// <param name="owner">The resource the identity belongs to, so the messages name the right one.</param>
        /// <param name="resourceId">The ARM ID of that resource, which is where the application ID is read from.</param>
        /// <remarks>
        /// <para>
        /// Best-effort by design: it returns false rather than throwing, because a failure here does not
        /// invalidate the rest of the install and the operator can grant access by hand. It is also
        /// idempotent, so re-running the installer is safe.
        /// </para>
        /// <para>
        /// <paramref name="principalObjectId"/> is what a resource's <c>identity</c> block reports for a
        /// system-assigned managed identity, but it is NOT what Azure SQL matches the sign-in against: a
        /// service principal - which a managed identity is - is identified by its <b>application (client)
        /// ID</b>. The two are different GUIDs, and SQL Server does not validate either, so using the object
        /// ID produces a user that exists, sits in the right roles, and is refused at every sign-in. So the
        /// application ID is read from Azure Resource Manager, then Microsoft Graph, and only when neither can
        /// supply it do we let SQL Server resolve the name itself with <c>FROM EXTERNAL PROVIDER</c> instead
        /// of writing an identifier we know to be wrong.
        /// </para>
        /// </remarks>
        public async Task<bool> GrantDatabaseAccessAsync(string connectionString, ManagedIdentityOwner owner, string principalName,
            Guid principalObjectId, string resourceId, IEnumerable<string> roles)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            }

            var ownerName = DescribeOwner(owner);
            if (string.IsNullOrWhiteSpace(principalName) || principalObjectId == Guid.Empty)
            {
                _logger.LogWarning(
                    $"Skipping the database permission grant for the {ownerName}: it has no system-assigned managed identity yet. " +
                    "Re-run the installer once the identity exists.");
                return false;
            }

            var roleList = roles == null ? new List<string>() : roles.ToList();

            var applicationId = await ResolveApplicationIdAsync(principalName, principalObjectId, resourceId);

            string sql = BuildGrantScript(principalName, applicationId, roleList);
            if (applicationId == null)
            {
                _logger.LogInformation(
                    $"Could not read the application (client) ID of managed identity '{principalName}' from Azure Resource Manager " +
                    "or Microsoft Graph, so SQL Server will be asked to resolve the name itself. That only works if the SQL Server " +
                    "has a managed identity of its own holding the Microsoft Entra 'Directory Readers' role; otherwise the grant " +
                    "fails with \"Principal '...' could not be resolved\". To avoid needing that, make sure the installer's app " +
                    "registration can read the resource in Azure Resource Manager (any error is reported above), or grant it " +
                    "'Application.Read.All', and re-run.");
            }

            _logger.LogInformation(
                $"Granting the {ownerName} managed identity '{principalName}' access to the database " +
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
                // A known application ID gives the operator a statement that needs no directory lookup and
                // cannot pick the wrong principal when two share a display name.
                var createUser = applicationId == null
                    ? $"CREATE USER [{principalName}] FROM EXTERNAL PROVIDER;"
                    : $"CREATE USER [{principalName}] WITH SID = {SqlContainedUserScript.ToSqlSid(applicationId.Value)}, TYPE = E;";

                _logger.LogError(
                    $"Could not grant the {ownerName} managed identity '{principalName}' access to the database: {ex.Message}. " +
                    $"{DescribeImpact(owner)} Sign in to the database as its Microsoft Entra administrator and run: " +
                    $"{createUser} then add it to {string.Join(", ", roleList)}.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Could not grant the {ownerName} managed identity '{principalName}' access to the database: {ex.Message}");
                return false;
            }

            _logger.LogInformation($"The {ownerName} managed identity '{principalName}' now has database access.");
            return true;
        }

        /// <summary>
        /// Works out the application (client) ID to declare the managed identity's contained user with:
        /// from Azure Resource Manager when possible, otherwise Microsoft Graph. Null when neither can say.
        /// </summary>
        /// <remarks>
        /// ARM goes first because it needs only read access to the resource, which the installer already
        /// has, where Graph needs a tenant-wide <c>Application.Read.All</c> grant it often lacks. An ARM
        /// answer for a different principal than the one being granted is ignored rather than used, because
        /// writing the wrong SID produces a user that can never sign in.
        /// </remarks>
        public async Task<Guid?> ResolveApplicationIdAsync(string principalName, Guid principalObjectId, string resourceId)
        {
            if (_identitySource != null && !string.IsNullOrWhiteSpace(resourceId))
            {
                try
                {
                    var identity = await _identitySource.GetSystemAssignedIdentityAsync(resourceId, CancellationToken.None);
                    if (identity == null)
                    {
                        _logger.LogInformation($"Azure Resource Manager reports no system-assigned managed identity for '{principalName}'.");
                    }
                    else if (identity.PrincipalId != principalObjectId)
                    {
                        _logger.LogWarning(
                            $"Ignoring the application ID Azure Resource Manager returned for '{principalName}': it belongs to " +
                            $"principal '{identity.PrincipalId}', not '{principalObjectId}'. The identity may have been re-created " +
                            "while the installer was running.");
                    }
                    else if (identity.ClientId != Guid.Empty)
                    {
                        return identity.ClientId;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(
                        $"Could not read the application ID of managed identity '{principalName}' from Azure Resource Manager: {ex.Message}");
                }
            }

            if (_resolver != null)
            {
                try
                {
                    var fromGraph = await _resolver.ResolveApplicationIdAsync(principalObjectId, CancellationToken.None);
                    if (fromGraph.HasValue && fromGraph.Value != Guid.Empty) return fromGraph;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation($"Could not resolve the application ID for '{principalName}': {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>The resource named in the grant's messages.</summary>
        public static string DescribeOwner(ManagedIdentityOwner owner)
        {
            return owner == ManagedIdentityOwner.AutomationAccount ? "Automation account" : "App Service";
        }

        /// <summary>What stops working while the identity has no database access.</summary>
        public static string DescribeImpact(ManagedIdentityOwner owner)
        {
            return owner == ManagedIdentityOwner.AutomationAccount
                ? "The Automation account's database maintenance runbooks will not be able to connect until this is fixed."
                : "The web application and web-jobs will not be able to connect with their managed identity until this is fixed.";
        }

        /// <summary>
        /// Chooses how to declare the managed identity's contained user: by its application ID when we
        /// could read one, otherwise by asking SQL Server to resolve the name itself.
        /// </summary>
        /// <remarks>
        /// Split out so the choice is testable without a database. The object ID is deliberately never a
        /// candidate - writing it would create a user that exists, holds the right roles, and is refused at
        /// every sign-in.
        /// </remarks>
        public static string BuildGrantScript(string principalName, Guid? applicationId, IEnumerable<string> roles)
        {
            return applicationId != null
                ? SqlContainedUserScript.CreateUserAndGrantRoles(principalName, applicationId.Value, roles)
                : SqlContainedUserScript.CreateUserFromExternalProviderAndGrantRoles(principalName, roles);
        }
    }

    /// <summary>
    /// The Azure resource a managed identity belongs to, so a database grant's messages name the right one.
    /// </summary>
    public enum ManagedIdentityOwner
    {
        /// <summary>The App Service that hosts the web application and web-jobs.</summary>
        AppService,

        /// <summary>The Automation account whose runbooks maintain the database.</summary>
        AutomationAccount
    }
}
