using App.ControlPanel.Engine.Entities;
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
    /// Turns a configured Microsoft Entra principal into the object ID SQL Server needs as a SID.
    /// </summary>
    /// <remarks>
    /// An interface so the grant logic can be unit tested without Microsoft Graph, and so a tenant that
    /// supplies object IDs directly never needs a directory-read permission at all.
    /// </remarks>
    public interface IEntraPrincipalResolver
    {
        /// <summary>
        /// Resolves the principal's object ID, or null when it cannot be found. Implementations report
        /// why rather than throwing, so one unresolvable entry does not abandon the rest.
        /// </summary>
        Task<Guid?> ResolveObjectIdAsync(SqlDatabaseUser user, CancellationToken cancellationToken);

        /// <summary>
        /// Resolves a service principal's application (client) ID from its object ID, or null when it
        /// cannot be read.
        /// </summary>
        /// <remarks>
        /// Needed because Azure SQL identifies a service principal - including a managed identity - by its
        /// application ID, while ARM only reports the object ID of a system-assigned identity. The two are
        /// different GUIDs, and using the wrong one creates a database user that can never sign in.
        /// </remarks>
        Task<Guid?> ResolveApplicationIdAsync(Guid servicePrincipalObjectId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Grants the Microsoft Entra users and groups named in the installer config access to the analytics
    /// database, as contained database users.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how people get to query the data on an Entra-authenticated server. The obvious-looking
    /// alternative - making yourself the server's Microsoft Entra administrator in the Azure portal - is a
    /// trap: Azure permits exactly ONE administrator per server and it is the installer's own service
    /// principal, so "Set admin" removes the identity that applies schema upgrades and the next upgrade
    /// fails with <c>Login failed for user '&lt;token-identified principal&gt;'</c>.
    /// </para>
    /// <para>
    /// The installer performs the grants because on an Entra-only server it is the only thing that can
    /// authenticate as the administrator; a person cannot sign in to run the <c>CREATE USER</c> themselves.
    /// </para>
    /// <para>
    /// Best-effort and idempotent, like the managed-identity grant it sits next to: a failure is reported
    /// but never aborts the install, because losing an operator's convenience access is not a reason to
    /// leave a deployment half-upgraded. See issue #117.
    /// </para>
    /// </remarks>
    public class SqlDatabaseUserGrantTask
    {
        /// <summary>
        /// Roles granted to a configured database user. <c>db_owner</c> because these are the deployment's
        /// own administrators: as well as reading data they are expected to be able to run the shipped
        /// stored procedures and maintain the database by hand.
        /// </summary>
        public static readonly IReadOnlyList<string> DatabaseUserRoles = new[] { "db_owner" };

        private readonly ILogger _logger;
        private readonly IEntraPrincipalResolver _resolver;

        public SqlDatabaseUserGrantTask(ILogger logger, IEntraPrincipalResolver resolver)
        {
            _logger = logger;
            _resolver = resolver;
        }

        /// <summary>
        /// Works out the Entra object ID for each configured principal, dropping the ones that cannot be
        /// used and saying why.
        /// </summary>
        /// <remarks>
        /// Separate from the grant so the decision logic - what is skipped, and what the operator is told
        /// about it - is unit testable without a database. A bad or unresolvable entry only ever costs
        /// itself: the rest of the list is still granted, because one mistyped name is not a reason to
        /// leave everybody else locked out.
        /// </remarks>
        public async Task<List<KeyValuePair<SqlDatabaseUser, Guid>>> ResolveUsersAsync(IList<SqlDatabaseUser> users,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var resolved = new List<KeyValuePair<SqlDatabaseUser, Guid>>();
            if (users == null || users.Count == 0) return resolved;

            foreach (var user in users)
            {
                if (user == null) continue;

                var validationError = user.GetValidationError();
                if (validationError != null)
                {
                    _logger.LogWarning($"Skipping a configured database user: {validationError}");
                    continue;
                }

                Guid objectId;
                if (!user.TryGetObjectId(out objectId))
                {
                    Guid? looked;
                    try
                    {
                        looked = _resolver == null
                            ? null
                            : await _resolver.ResolveObjectIdAsync(user, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Could not look up database user '{user.Login}' in Microsoft Entra ID: {ex.Message}");
                        looked = null;
                    }

                    if (looked == null || looked.Value == Guid.Empty)
                    {
                        _logger.LogWarning(
                            $"Skipping database user '{user.Login}': its Microsoft Entra object ID could not be resolved. " +
                            "Either grant the installer's app registration permission to read the directory, or paste the " +
                            "principal's Object ID into the installer next to the name - it is on the principal's overview " +
                            "blade in the Azure portal, and with it no directory lookup is needed.");
                        continue;
                    }

                    objectId = looked.Value;
                }

                resolved.Add(new KeyValuePair<SqlDatabaseUser, Guid>(user, objectId));
            }

            return resolved;
        }

        /// <summary>
        /// Creates or repairs a contained database user for each configured principal. Returns how many
        /// were granted successfully.
        /// </summary>
        public async Task<int> GrantConfiguredUsersAsync(string connectionString, IList<SqlDatabaseUser> users,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return 0;

            var resolved = await ResolveUsersAsync(users, cancellationToken).ConfigureAwait(false);
            if (resolved.Count == 0) return 0;

            _logger.LogInformation(
                $"Granting {resolved.Count} configured database user(s) access to the database " +
                $"({string.Join(", ", DatabaseUserRoles)}): {string.Join(", ", resolved.Select(r => r.Key.Login))}...");

            var granted = 0;
            try
            {
                using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var entry in resolved)
                    {
                        var sql = SqlContainedUserScript.CreateUserAndGrantRoles(
                            entry.Key.Login, entry.Value, DatabaseUserRoles, entry.Key.IsGroup);

                        try
                        {
                            using (var cmd = connection.CreateCommand())
                            {
                                cmd.CommandText = sql;
                                cmd.CommandTimeout = 120;
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                            }
                            granted++;
                        }
                        catch (SqlException ex)
                        {
                            // One bad entry must not cost the others their access.
                            _logger.LogWarning($"Could not grant database access to '{entry.Key.Login}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"Could not grant the configured database users access: {ex.Message}. The people listed in the installer " +
                    "will not be able to query the database until this is fixed.");
                return granted;
            }

            if (granted > 0)
            {
                _logger.LogInformation(
                    $"{granted} database user(s) can now sign in to the database with their own Microsoft Entra account. " +
                    "The SQL Server's Microsoft Entra administrator was not changed.");
            }

            return granted;
        }
    }

    /// <summary>
    /// Resolves Microsoft Entra principals with Microsoft Graph, trying each app registration the
    /// installer knows about until one has enough directory permission.
    /// </summary>
    /// <remarks>
    /// Two credentials are tried because neither is guaranteed to work. The installer's own app
    /// registration is an Azure Resource Manager identity and often has no Graph directory permission at
    /// all; the runtime account does read user metadata (<c>User.Read.All</c>) and so usually can resolve a
    /// UPN. Falling back rather than picking one keeps this working on tenants that granted either.
    /// </remarks>
    public class GraphEntraPrincipalResolver : IEntraPrincipalResolver
    {
        private readonly ILogger _logger;
        private readonly List<AppRegistrationCredentials> _candidates;

        public GraphEntraPrincipalResolver(ILogger logger, params AppRegistrationCredentials[] candidateAccounts)
        {
            _logger = logger;
            _candidates = (candidateAccounts ?? new AppRegistrationCredentials[0])
                .Where(IsUsable)
                .ToList();
        }

        static bool IsUsable(AppRegistrationCredentials account)
        {
            return account != null
                && !string.IsNullOrWhiteSpace(account.DirectoryId)
                && !string.IsNullOrWhiteSpace(account.ClientId)
                && !string.IsNullOrWhiteSpace(account.Secret);
        }

        public async Task<Guid?> ResolveObjectIdAsync(SqlDatabaseUser user, CancellationToken cancellationToken)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.Login)) return null;

            if (_candidates.Count == 0)
            {
                _logger?.LogWarning(
                    $"No app registration with a client secret is configured, so '{user.Login}' cannot be looked up in " +
                    "Microsoft Entra ID. Supply the principal's Object ID in the installer instead.");
                return null;
            }

            Exception lastFailure = null;

            foreach (var account in _candidates)
            {
                try
                {
                    var credential = new Azure.Identity.ClientSecretCredential(account.DirectoryId, account.ClientId, account.Secret);
                    var graph = new Microsoft.Graph.GraphServiceClient(credential);

                    var objectId = user.IsGroup
                        ? await ResolveGroupAsync(graph, user.Login, cancellationToken).ConfigureAwait(false)
                        : await ResolveUserAsync(graph, user.Login, cancellationToken).ConfigureAwait(false);

                    if (objectId != null) return objectId;
                }
                catch (Exception ex)
                {
                    // Typically a missing directory-read grant on this particular app registration. Keep the
                    // reason for the final message, but try the other one before giving up.
                    lastFailure = ex;
                }
            }

            if (lastFailure != null)
            {
                _logger?.LogWarning($"Microsoft Graph could not resolve '{user.Login}': {lastFailure.Message}");
            }

            return null;
        }

        static async Task<Guid?> ResolveUserAsync(Microsoft.Graph.GraphServiceClient graph, string login, CancellationToken cancellationToken)
        {
            // A UPN is a valid key for the users collection, so this needs no filter and no ConsistencyLevel.
            var found = await graph.Users[login.Trim()]
                .GetAsync(rc => rc.QueryParameters.Select = new[] { "id", "userPrincipalName" }, cancellationToken)
                .ConfigureAwait(false);

            return ParseId(found?.Id);
        }

        static async Task<Guid?> ResolveGroupAsync(Microsoft.Graph.GraphServiceClient graph, string login, CancellationToken cancellationToken)
        {
            var escaped = login.Trim().Replace("'", "''");

            var found = await graph.Groups
                .GetAsync(rc =>
                {
                    rc.QueryParameters.Filter = $"displayName eq '{escaped}'";
                    rc.QueryParameters.Select = new[] { "id", "displayName" };
                }, cancellationToken)
                .ConfigureAwait(false);

            var matches = found?.Value;
            if (matches == null || matches.Count == 0) return null;

            if (matches.Count > 1)
            {
                // Display names are not unique, so guessing could grant the wrong group access to the data.
                throw new InvalidOperationException(
                    $"more than one group is named '{login}'. Supply the group's Object ID in the installer instead.");
            }

            return ParseId(matches[0].Id);
        }

        static Guid? ParseId(string id)
        {
            Guid parsed;
            if (!string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out parsed) && parsed != Guid.Empty) return parsed;
            return null;
        }

        /// <summary>
        /// Reads a service principal's application (client) ID from its object ID.
        /// </summary>
        /// <remarks>
        /// Needs <c>Application.Read.All</c> or <c>Directory.Read.All</c> on one of the candidate app
        /// registrations. Returns null rather than throwing when neither has it, so the caller can fall
        /// back to letting SQL Server resolve the name itself.
        /// </remarks>
        public async Task<Guid?> ResolveApplicationIdAsync(Guid servicePrincipalObjectId, CancellationToken cancellationToken)
        {
            if (servicePrincipalObjectId == Guid.Empty) return null;
            if (_candidates.Count == 0) return null;

            Exception lastFailure = null;

            foreach (var account in _candidates)
            {
                try
                {
                    var credential = new Azure.Identity.ClientSecretCredential(account.DirectoryId, account.ClientId, account.Secret);
                    var graph = new Microsoft.Graph.GraphServiceClient(credential);

                    var sp = await graph.ServicePrincipals[servicePrincipalObjectId.ToString()]
                        .GetAsync(rc => rc.QueryParameters.Select = new[] { "id", "appId", "displayName" }, cancellationToken)
                        .ConfigureAwait(false);

                    var appId = ParseId(sp?.AppId);
                    if (appId != null) return appId;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                }
            }

            if (lastFailure != null)
            {
                _logger?.LogInformation(
                    $"Could not read the managed identity's application ID from Microsoft Graph: {lastFailure.Message}");
            }

            return null;
        }
    }
}
