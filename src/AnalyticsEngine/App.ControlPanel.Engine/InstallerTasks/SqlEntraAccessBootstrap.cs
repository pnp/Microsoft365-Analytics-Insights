using App.ControlPanel.Engine.Entities;
using Azure.Core;
using Azure.Identity;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Repairs the installer's own access to an Azure SQL database by signing a human Microsoft Entra
    /// administrator in interactively and creating the contained database user the installer needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists to break a genuine chicken-and-egg. On a server with Microsoft Entra-only authentication the
    /// installer signs in as its own service principal. If that principal is not the server's Entra
    /// administrator - typically because somebody assigned a named user as administrator afterwards, which
    /// Azure treats as <em>replacing</em> the previous one because it permits exactly one - then it cannot
    /// sign in, and the only identity that could grant it access is an administrator nobody is currently
    /// authenticated as. There is no SQL password to fall back on, because the server rejects SQL logins
    /// outright.
    /// </para>
    /// <para>
    /// The way out is the person sitting in front of the installer: they can sign in interactively as the
    /// administrator, which the installer cannot. So we ask them to, use their token for exactly one
    /// statement batch - creating the contained user - and then go back to the service principal for the
    /// rest of the install. Deliberately NOT done by reassigning the server's administrator over ARM: that
    /// would mutate a live customer server and could strand the assignment if the installer died halfway.
    /// </para>
    /// <para>
    /// Never runs unattended. A headless process has nobody to sign in, so prompting there would hang a
    /// scripted install until it timed out, which is worse than the clear failure it replaces.
    /// </para>
    /// <para>See issue #117.</para>
    /// </remarks>
    public static class SqlEntraAccessBootstrap
    {
        /// <summary>
        /// Roles the installer's own principal needs. It applies Entity Framework migrations, so it
        /// creates, alters and drops tables and stored procedures - <c>db_owner</c> is what actually
        /// covers that, and it is the access the principal had when it was the server administrator.
        /// </summary>
        public static readonly IReadOnlyList<string> InstallerRoles = new[] { "db_owner" };

        /// <summary>
        /// How long to wait for the operator to complete the sign-in before giving up. Long enough to find
        /// a password and satisfy MFA, short enough that an install left running against an empty desk
        /// still finishes with a real error instead of hanging.
        /// </summary>
        public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Whether this process can ask a human to sign in. False for the headless child processes the
        /// installer launches (<c>--initdb</c> / <c>--registerconfig</c>) and for any scripted run.
        /// </summary>
        public static bool CanPromptForSignIn()
        {
            try
            {
                return Environment.UserInteractive;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Signs an administrator in and grants the installer's service principal a contained database
        /// user. Returns whether the database now has that user.
        /// </summary>
        /// <remarks>
        /// Best-effort and non-throwing: every failure is reported and returns false, so the caller falls
        /// back to its normal "could not connect" diagnosis rather than replacing one confusing error with
        /// another.
        /// </remarks>
        /// <param name="connectionString">Connection string for the target database. Must be a token-auth one.</param>
        /// <param name="tenantId">Directory the administrator lives in - the installer's own tenant.</param>
        /// <param name="installerObjectId">Object ID of the installer's service principal.</param>
        /// <param name="installerLogin">Name to give the contained user; the installer's client ID or app name.</param>
        public static async Task<bool> TryRepairInstallerAccessAsync(string connectionString, string tenantId,
            Guid installerObjectId, string installerLogin, ILogger logger, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return false;

            if (installerObjectId == Guid.Empty)
            {
                logger?.LogWarning(
                    "Cannot repair the installer's database access: its service principal object ID could not be resolved.");
                return false;
            }

            if (!AzureSqlTokenAuth.NeedsAccessToken(connectionString))
            {
                // A SQL-authentication connection string has a login of its own; there is nothing here to repair.
                return false;
            }

            if (!CanPromptForSignIn())
            {
                logger?.LogError(
                    "The installer's service principal has no access to this database, and this process cannot prompt anyone to " +
                    "sign in to repair it. Re-run the installer interactively, or make the installer's service principal the " +
                    "SQL Server's Microsoft Entra administrator again.");
                return false;
            }

            logger?.LogWarning(
                "The installer's service principal cannot sign in to the database, so it will ask you to sign in as a Microsoft " +
                "Entra administrator of this SQL Server and repair its own access. A browser window will open. Sign in with an " +
                "account that is the server's Microsoft Entra administrator - the same one shown in the authentication line " +
                "above. Nothing is stored: the sign-in is used once, to create the database user the installer needs.");

            AccessToken adminToken;
            try
            {
                adminToken = await AcquireAdminTokenAsync(tenantId, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger?.LogError(
                    $"The administrator sign-in was cancelled or did not complete within {SignInTimeout.TotalMinutes:0} minutes, " +
                    "so the installer could not repair its database access.");
                return false;
            }
            catch (AuthenticationFailedException ex)
            {
                logger?.LogError($"The administrator sign-in failed, so the installer could not repair its database access: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogError($"Could not sign in to repair the installer's database access: {ex.Message}");
                return false;
            }

            var script = SqlContainedUserScript.CreateUserAndGrantRoles(installerLogin, installerObjectId, InstallerRoles);

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.AccessToken = adminToken.Token;
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = script;
                        cmd.CommandTimeout = 120;
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (SqlException ex)
            {
                // 18456 here means the person who signed in is not an administrator of this server either,
                // which is a different problem from the one we set out to fix - so name it rather than
                // letting it read as "the repair does not work".
                if (ex.Number == SqlLoginFailedErrorNumber)
                {
                    logger?.LogError(
                        "The account you signed in with was rejected by SQL Server, so it is not this server's Microsoft Entra " +
                        "administrator and cannot grant database access. Check SQL Server > Settings > Microsoft Entra ID in the " +
                        "Azure portal to see which account is the administrator, and re-run the installer signing in as that one.");
                }
                else
                {
                    logger?.LogError($"Could not create the installer's database user: {ex.Message}");
                }
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogError($"Could not create the installer's database user: {ex.Message}");
                return false;
            }

            logger?.LogInformation(
                $"Created contained database user '{installerLogin}' ({string.Join(", ", InstallerRoles)}) for the installer's " +
                "service principal. The installer can now sign in to apply the schema upgrade, and future runs will not need " +
                "this sign-in. The SQL Server's Microsoft Entra administrator was NOT changed.");

            return true;
        }

        /// <summary>SQL Server's "Login failed for user" error.</summary>
        public const int SqlLoginFailedErrorNumber = 18456;

        /// <summary>
        /// Gets a SQL access token for a human administrator, preferring the system browser and falling
        /// back to a device code when no browser can be opened (Server Core, a locked-down jump box, or a
        /// remote session with no default browser registered).
        /// </summary>
        static async Task<AccessToken> AcquireAdminTokenAsync(string tenantId, ILogger logger, CancellationToken cancellationToken)
        {
            var request = new TokenRequestContext(new[] { AzureSqlTokenAuth.SqlTokenScope });

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(SignInTimeout);

                try
                {
                    // TenantId is pinned to the installer's own directory: the administrator of a SQL server
                    // in this subscription lives there, and leaving it unset would let the browser's existing
                    // session sign in against some other tenant the operator happens to be signed in to.
                    var browser = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
                    {
                        TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                    });

                    return await browser.GetTokenAsync(request, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(
                        $"Could not sign in with a browser ({ex.Message}). Falling back to device-code sign-in.");
                }

                var deviceCode = new DeviceCodeCredential(new DeviceCodeCredentialOptions
                {
                    TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                    DeviceCodeCallback = (info, ct) =>
                    {
                        logger?.LogWarning($"To repair the installer's database access: {info.Message}");
                        return Task.CompletedTask;
                    },
                });

                return await deviceCode.GetTokenAsync(request, timeout.Token).ConfigureAwait(false);
            }
        }
    }
}
