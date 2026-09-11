using Common.Entities.Installer;
using System;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.Entities
{
    /// <summary>
    /// How the installer should authenticate to an Azure SQL server for this run.
    /// </summary>
    public enum SqlConnectionAuthMethod
    {
        /// <summary>SQL Server authentication - an admin login and password in the connection string.</summary>
        SqlLogin,

        /// <summary>Microsoft Entra ID - an access token, no stored secret.</summary>
        EntraId,
    }

    /// <summary>
    /// What we know about an Azure SQL server's authentication configuration, as read back from Azure.
    /// </summary>
    public class SqlServerAuthState
    {
        /// <summary>
        /// Server has "Microsoft Entra-only authentication" turned on, so SQL logins are rejected
        /// outright. Read from the server's <c>azureADOnlyAuthentications/Default</c> child resource.
        /// </summary>
        public bool EntraOnlyAuthEnabled { get; set; }

        /// <summary>Server has a Microsoft Entra administrator assigned.</summary>
        public bool HasEntraAdmin { get; set; }

        /// <summary>Server has a SQL authentication administrator login.</summary>
        public bool HasSqlAdminLogin { get; set; }

        /// <summary>Login name of the Entra administrator, for logging. Never a secret.</summary>
        public string EntraAdminLogin { get; set; }

        /// <summary>
        /// Object (principal) ID of the Entra administrator, read from the authoritative
        /// <c>administrators/ActiveDirectory</c> child resource. Null when the server has no Entra
        /// administrator.
        /// </summary>
        /// <remarks>
        /// This is what makes "can the installer actually sign in?" answerable before trying: the installer
        /// knows its own service principal's object ID, so it can compare the two. A login name cannot be
        /// used for that - it is a display name, so it is neither stable nor unique.
        /// </remarks>
        public Guid? EntraAdminSid { get; set; }

        /// <summary>
        /// Principal type of the Entra administrator - "User", "Group" or "Application". An
        /// <c>Application</c> administrator is a service principal, which no person can sign in as.
        /// Null when the server has no Entra administrator or Azure did not report the type.
        /// </summary>
        public string EntraAdminPrincipalType { get; set; }
    }

    /// <summary>
    /// Chooses between SQL authentication and Microsoft Entra ID for a given Azure SQL server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backwards compatibility is the priority. A server the installer did not create is never
    /// reconfigured; instead we look at what it actually supports and pick the method that works. An
    /// existing SQL-authentication deployment therefore keeps behaving exactly as it did before this
    /// change, and only a server that has SQL authentication disabled - whether because a new install
    /// asked for that, or because the customer turned it on themselves - is driven over Entra ID.
    /// </para>
    /// <para>
    /// Pure rules with no Azure calls, so the decision is unit testable. See issue #117.
    /// </para>
    /// </remarks>
    public static class SqlServerAuthDetection
    {
        /// <summary>
        /// Decides how to authenticate, and explains why in a line safe to write to the install log.
        /// </summary>
        /// <param name="serverState">
        /// What Azure reports about the server, or null when the server could not be inspected (in which
        /// case we fall back to the legacy behaviour rather than guess).
        /// </param>
        /// <param name="haveSqlCredentials">Whether a SQL admin username AND password are configured.</param>
        /// <param name="configuredMode">What the operator asked for in the installer config.</param>
        public static SqlAuthDecision Decide(SqlServerAuthState serverState, bool haveSqlCredentials, SqlServerAuthMode configuredMode)
        {
            if (serverState == null)
            {
                return new SqlAuthDecision(
                    haveSqlCredentials ? SqlConnectionAuthMethod.SqlLogin : SqlConnectionAuthMethod.EntraId,
                    "The SQL server could not be inspected, so the configured credentials decide the authentication method.");
            }

            // Nothing else matters: the server rejects SQL logins, so Entra ID is the only option.
            if (serverState.EntraOnlyAuthEnabled)
            {
                return new SqlAuthDecision(SqlConnectionAuthMethod.EntraId,
                    "SQL Server has Microsoft Entra-only authentication enabled, so the installer will authenticate with Microsoft Entra ID. " +
                    "The SQL administrator username/password are ignored.");
            }

            // The operator asked for Entra and the server can actually do it.
            if (configuredMode == SqlServerAuthMode.EntraId && serverState.HasEntraAdmin)
            {
                return new SqlAuthDecision(SqlConnectionAuthMethod.EntraId,
                    "Microsoft Entra ID authentication is selected and the SQL Server has a Microsoft Entra administrator, " +
                    "so the installer will authenticate with Microsoft Entra ID.");
            }

            // An existing SQL-authentication deployment. Leave it exactly as it is.
            if (haveSqlCredentials)
            {
                var note = configuredMode == SqlServerAuthMode.EntraId && !serverState.HasEntraAdmin
                    ? "Microsoft Entra ID authentication is selected, but this SQL Server has no Microsoft Entra administrator assigned - " +
                      "existing servers are deliberately never reconfigured. Falling back to the configured SQL administrator login."
                    : "Using the configured SQL administrator login (SQL Server authentication).";

                return new SqlAuthDecision(SqlConnectionAuthMethod.SqlLogin, note);
            }

            if (serverState.HasEntraAdmin)
            {
                return new SqlAuthDecision(SqlConnectionAuthMethod.EntraId,
                    "No SQL administrator password is configured and the SQL Server has a Microsoft Entra administrator, " +
                    "so the installer will authenticate with Microsoft Entra ID.");
            }

            return new SqlAuthDecision(SqlConnectionAuthMethod.SqlLogin,
                "No SQL administrator password is configured and the SQL Server has no Microsoft Entra administrator. " +
                "There is no usable way to authenticate to this server - set a SQL administrator password, or assign a " +
                "Microsoft Entra administrator to the server in the Azure portal.");
        }

        /// <summary>
        /// Whether the installer should provision this server with Microsoft Entra ID authentication.
        /// Only ever true for a server the installer is about to create: an existing server's
        /// authentication configuration is left alone, per the backwards-compatibility rule.
        /// </summary>
        public static bool ShouldProvisionWithEntraAuth(bool serverAlreadyExists, SqlServerAuthMode configuredMode)
        {
            return !serverAlreadyExists && configuredMode == SqlServerAuthMode.EntraId;
        }

        /// <summary>
        /// Warns when Microsoft Entra ID authentication is in use but no <em>person</em> can sign in to the
        /// database, so an operator who tries to browse the data by hand is refused.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the normal outcome of a default install, not a failure. Azure allows exactly one Entra
        /// administrator per SQL server, and the installer makes itself that administrator because it is the
        /// identity that has to sign in to apply the schema upgrade. The solution therefore works perfectly -
        /// but the administrator is a service principal, and nobody can authenticate interactively as one.
        /// </para>
        /// <para>
        /// The symptom is unhelpfully worded: the Azure portal's Query Editor reports
        /// <c>"You don't have access to this database"</c> and says the account "was authenticated
        /// successfully, but it doesn't have permission to access this database", which reads like a missing
        /// database role rather than a missing server administrator.
        /// </para>
        /// <para>Returns null when there is nothing to warn about. Pure, so it is unit testable.</para>
        /// <para>
        /// An administrator whose principal type Azure did not report is assumed to be a principal that can
        /// sign in, consistent with the "authentication will not be guessed" rule this class follows: the
        /// case the warning exists for - a server the installer created and made itself the administrator of -
        /// always reports <c>Application</c>, because the installer is what set it.
        /// </para>
        /// </remarks>
        /// <param name="serverState">
        /// What Azure reports about the server, or null when it could not be inspected - in which case we
        /// say nothing rather than guess.
        /// </param>
        /// <param name="decision">The authentication method chosen for this run.</param>
        /// <param name="hasConfiguredDatabaseUsers">
        /// Whether the installer config already lists people to grant database access to. When it does, the
        /// install is about to fix this by itself, so the warning says so instead of asking for action.
        /// </param>
        public static string GetInteractiveAdminWarning(SqlServerAuthState serverState, SqlAuthDecision decision,
            bool hasConfiguredDatabaseUsers = false)
        {
            if (decision == null || !decision.UsesEntraId) return null;
            if (serverState == null) return null;

            // A human or a group containing humans can sign in, so there is nothing to say.
            if (serverState.HasEntraAdmin
                && !string.Equals(serverState.EntraAdminPrincipalType, "Application", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var opening = serverState.HasEntraAdmin
                ? "This SQL Server's only Microsoft Entra administrator is an application" +
                  $"{FormatAdminLogin(serverState.EntraAdminLogin)}, which nobody can sign in as. On a new install this is " +
                  "normally the installer's own service principal, which must keep access so it can apply future schema upgrades."
                : "This SQL Server has no Microsoft Entra administrator assigned.";

            // Only a server with SQL authentication actually disabled leaves an operator with no way in at
            // all. On a mixed-auth server the SQL administrator login still works, so say so rather than
            // overstate the problem.
            var consequence = serverState.EntraOnlyAuthEnabled
                ? " Because SQL authentication is disabled on this server, no person can sign in to the database, so " +
                  "querying it by hand - for example with the Azure portal's Query Editor, SSMS or Azure Data Studio - " +
                  "will be refused with \"You don't have access to this database\". That message reports a successful " +
                  "sign-in and blames database permissions, which is misleading: the real cause is that you are not an " +
                  "administrator of the server."
                : " Signing in with Microsoft Entra ID will be refused with \"You don't have access to this database\" - a " +
                  "misleading message, because the real cause is that you are not a Microsoft Entra administrator of the " +
                  "server rather than anything to do with database permissions." +
                  (serverState.HasSqlAdminLogin
                      ? " SQL authentication is still enabled on this server, so its SQL administrator login remains an option."
                      : " SQL authentication is still enabled on this server, but Azure reports no SQL administrator login, " +
                        "so there is no alternative way in either.");

            // Deliberately does NOT tell anyone to reassign the server's Entra administrator. Azure permits
            // only one, so "Set admin" to a named user silently evicts the installer's service principal and
            // breaks every future schema upgrade - which is exactly what happened to the deployment this
            // guidance was rewritten for. Data access for people is a contained-user grant, which leaves the
            // administrator alone, and the installer performs it because it is the only identity that can
            // sign in as that administrator.
            var remedy = hasConfiguredDatabaseUsers
                ? " The database users configured in this installer will be granted access during this run, so the people " +
                  "listed there will be able to query the database with their own Microsoft Entra account once it finishes."
                : $" To give people access, add them to '{DatabaseUsersConfigUiName}' in the installer and re-run it: the " +
                  "installer signs in as the administrator and creates a contained database user for each one. " +
                  "Do NOT reassign the server's Microsoft Entra administrator to do this. Azure permits only ONE " +
                  "administrator per server, so replacing this service principal - for example via SQL Server > Settings > " +
                  "Microsoft Entra ID > Set admin - removes the identity that applies schema upgrades, and your next upgrade " +
                  "will fail with \"Login failed for user '<token-identified principal>'\".";

            return opening +
                " This does not affect the solution's own database access: the App Service and Automation account " +
                "authenticate as themselves." + consequence + remedy;
        }

        /// <summary>
        /// Name of the installer setting that lists the people to grant database access to. Kept in one
        /// place so the warning text and the UI cannot drift apart.
        /// </summary>
        public const string DatabaseUsersConfigUiName = "Azure Config > SQL database users";

        /// <summary>
        /// Warns, before anything tries to connect, that this run will authenticate to SQL as the installer's
        /// service principal but that principal is not the server's Microsoft Entra administrator.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately a warning rather than a hard stop, because a mismatch is not proof of lockout: the
        /// administrator may be a security <em>group</em> that contains the installer principal, and the
        /// principal may already have a contained user in the database from an earlier run. Only an actual
        /// login failure proves it, which is what <see cref="GetInstallerLockoutRemedy"/> is for.
        /// </para>
        /// <para>
        /// Worth saying up front all the same: without it the operator gets several minutes of apparently
        /// healthy install followed by a bare <c>Login failed for user '&lt;token-identified principal&gt;'</c>,
        /// which names no principal and suggests nothing actionable.
        /// </para>
        /// </remarks>
        /// <param name="installerObjectId">
        /// Object ID of the installer's own service principal, or <see cref="Guid.Empty"/> when it could not
        /// be resolved - in which case nothing is said rather than guessed.
        /// </param>
        public static string GetInstallerLockoutWarning(SqlServerAuthState serverState, SqlAuthDecision decision, Guid installerObjectId)
        {
            if (decision == null || !decision.UsesEntraId) return null;
            if (serverState == null) return null;
            if (installerObjectId == Guid.Empty) return null;

            // We are the administrator, so there is nothing to worry about.
            if (serverState.EntraAdminSid.HasValue && serverState.EntraAdminSid.Value == installerObjectId) return null;

            if (!serverState.HasEntraAdmin)
            {
                return "This run will authenticate to SQL Server with Microsoft Entra ID, but the server has no Microsoft Entra " +
                       "administrator assigned at all. Unless the installer's service principal already has a contained user in " +
                       "the database, it will not be able to sign in and the schema upgrade cannot run.";
            }

            var isGroup = string.Equals(serverState.EntraAdminPrincipalType, "Group", StringComparison.OrdinalIgnoreCase);

            return "This run will authenticate to SQL Server with Microsoft Entra ID as the installer's service principal " +
                   $"(object ID '{installerObjectId}'), which is NOT this server's Microsoft Entra administrator" +
                   $"{FormatAdminLogin(serverState.EntraAdminLogin)}. " +
                   (isGroup
                       ? "The administrator is a security group, so this is fine as long as the installer's service principal is " +
                         "a member of it - which cannot be verified from here."
                       : "That normally means the administrator was reassigned to a named user after the install, which Azure " +
                         "treats as replacing the previous one because it permits only ONE administrator per server. Unless the " +
                         "installer's service principal already has a contained user in the database, the schema upgrade will " +
                         "fail with \"Login failed for user '<token-identified principal>'\".");
        }

        /// <summary>
        /// The actionable explanation for a login failure that has already happened, once SQL has actually
        /// rejected the installer's service principal.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="GetInstallerLockoutWarning"/> because by this point the ambiguity is
        /// gone - the principal demonstrably cannot sign in - so this states the cause outright and names
        /// both routes back: let the installer repair it with an administrator sign-in, or restore the
        /// administrator assignment it expects.
        /// </remarks>
        public static string GetInstallerLockoutRemedy(SqlServerAuthState serverState, Guid installerObjectId)
        {
            var adminDescription = serverState != null && serverState.HasEntraAdmin
                ? $"This server's Microsoft Entra administrator is{FormatAdminLogin(serverState.EntraAdminLogin)}"
                : "This server has no Microsoft Entra administrator assigned";

            var identity = installerObjectId == Guid.Empty
                ? "the installer's service principal"
                : $"the installer's service principal (object ID '{installerObjectId}')";

            return $"SQL Server rejected {identity}, so it has no access to this database. {adminDescription}, which is a " +
                   "different principal. Azure permits only ONE Microsoft Entra administrator per SQL server, so assigning a " +
                   "named user as administrator removes the installer's principal and with it the ability to apply schema " +
                   "upgrades. Fix it either by letting the installer repair its own access - sign in when prompted as a " +
                   "Microsoft Entra administrator of this server and it will create the contained database user it needs - or " +
                   "by making the installer's service principal the server's Microsoft Entra administrator again. To give " +
                   $"people access to query the data, use '{DatabaseUsersConfigUiName}' rather than reassigning the " +
                   "administrator.";
        }

        static string FormatAdminLogin(string login)
        {
            return string.IsNullOrWhiteSpace(login) ? string.Empty : $", '{login}'";
        }
    }

    /// <summary>The chosen authentication method plus a human-readable reason for the install log.</summary>
    public class SqlAuthDecision
    {
        public SqlAuthDecision(SqlConnectionAuthMethod method, string reason)
        {
            this.Method = method;
            this.Reason = reason;
        }

        public SqlConnectionAuthMethod Method { get; }
        public string Reason { get; }

        /// <summary>
        /// What Azure reported about the server this decision was made from, or null when it could not be
        /// inspected. Carried along so a later failure can be explained in terms of the actual server
        /// configuration - which administrator is assigned - instead of a bare login error.
        /// </summary>
        public SqlServerAuthState ServerState { get; set; }

        public bool UsesEntraId => Method == SqlConnectionAuthMethod.EntraId;
    }

    /// <summary>
    /// Builds the T-SQL that gives an Azure managed identity / service principal access to the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An Entra principal gets database access through a "contained user" created in the database itself -
    /// there is no ARM role that grants data-plane access to Azure SQL, which is why this is T-SQL rather
    /// than another <c>RoleAssignmentTask</c>.
    /// </para>
    /// <para>
    /// The user is created <c>WITH SID = ..., TYPE = E</c> rather than <c>FROM EXTERNAL PROVIDER</c>. Both
    /// produce the same user, but <c>FROM EXTERNAL PROVIDER</c> makes the SQL server call Microsoft Graph
    /// to resolve the name, which fails with "Principal '...' could not be found" unless the server's own
    /// identity has been granted the Directory Readers role - a tenant-wide, Global-Administrator-only
    /// grant we cannot assume an operator has done. Supplying the object ID as the SID needs no directory
    /// lookup at all.
    /// </para>
    /// <para>Pure string generation, so the emitted script is unit testable. See issue #117.</para>
    /// </remarks>
    public static class SqlContainedUserScript
    {
        /// <summary>
        /// Roles granted to the App Service identity.
        /// <c>db_ddladmin</c> is required, not belt-and-braces: the importers create and drop real
        /// <c>dbo</c> staging tables at runtime (see <c>InsertBatch</c>), and a DEBUG build can run EF
        /// migrations on start-up.
        /// </summary>
        public static readonly IReadOnlyList<string> AppServiceRoles =
            new[] { "db_datareader", "db_datawriter", "db_ddladmin" };

        /// <summary>
        /// Emits an idempotent script that creates the contained user for <paramref name="principalName"/>
        /// and adds it to <paramref name="roles"/>.
        /// </summary>
        /// <param name="principalName">
        /// The database user name. For a system-assigned managed identity this is the Azure resource name
        /// (e.g. the App Service name), which is also the identity's display name in Entra ID.
        /// </param>
        /// <param name="principalSid">
        /// The value Azure SQL matches the sign-in against, and it is NOT the same kind of ID for every
        /// principal:
        /// <list type="bullet">
        /// <item><description>a <b>user</b> or <b>group</b> - its Entra <b>object ID</b>;</description></item>
        /// <item><description>a <b>service principal</b>, which includes an app registration and any
        /// managed identity - its <b>application (client) ID</b>.</description></item>
        /// </list>
        /// Getting this wrong fails silently: SQL Server does not validate the value against Entra ID, so
        /// <c>CREATE USER</c> succeeds and only the subsequent sign-in is rejected, with
        /// <c>Login failed for user '&lt;token-identified principal&gt;'</c> - an error that names no
        /// principal and looks nothing like a bad SID. See the remarks on
        /// <see cref="CreateUserAndGrantRoles"/>'s caller and the CREATE USER documentation.
        /// </param>
        /// <param name="roles">Database roles to add the user to.</param>
        public static string CreateUserAndGrantRoles(string principalName, Guid principalSid, IEnumerable<string> roles)
        {
            if (string.IsNullOrWhiteSpace(principalName))
            {
                throw new ArgumentException($"'{nameof(principalName)}' cannot be null or empty.", nameof(principalName));
            }
            if (principalSid == Guid.Empty)
            {
                throw new ArgumentException($"'{nameof(principalSid)}' cannot be an empty GUID.", nameof(principalSid));
            }
            if (roles == null) throw new ArgumentNullException(nameof(roles));

            var quotedName = QuoteIdentifier(principalName);
            var literalName = QuoteLiteral(principalName);
            var sid = ToSqlSid(principalSid);

            var sql = new System.Text.StringBuilder();
            sql.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'{literalName}')");
            sql.AppendLine("BEGIN");
            sql.AppendLine($"    CREATE USER {quotedName} WITH SID = {sid}, TYPE = E;");
            sql.AppendLine("END");

            foreach (var role in roles)
            {
                if (string.IsNullOrWhiteSpace(role)) continue;

                var quotedRole = QuoteIdentifier(role);
                var literalRole = QuoteLiteral(role);
                sql.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_role_members rm");
                sql.AppendLine($"    INNER JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id");
                sql.AppendLine($"    INNER JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id");
                sql.AppendLine($"    WHERE r.[name] = N'{literalRole}' AND m.[name] = N'{literalName}')");
                sql.AppendLine("BEGIN");
                sql.AppendLine($"    ALTER ROLE {quotedRole} ADD MEMBER {quotedName};");
                sql.AppendLine("END");
            }

            // db_datareader/db_datawriter cover tables but not EXECUTE, and the solution ships stored
            // procedures (sp_CreateDimTables and the Power BI refresh procs).
            sql.AppendLine($"GRANT EXECUTE TO {quotedName};");

            return sql.ToString();
        }

        /// <summary>
        /// Converts an Entra object ID to the <c>VARBINARY(16)</c> literal SQL Server expects as a SID for
        /// an external (type E) principal. This is the GUID's little-endian byte layout - i.e.
        /// <see cref="Guid.ToByteArray"/> - not the textual GUID.
        /// </summary>
        public static string ToSqlSid(Guid principalObjectId)
        {
            var bytes = principalObjectId.ToByteArray();
            var hex = new System.Text.StringBuilder("0x", 2 + (bytes.Length * 2));
            foreach (var b in bytes)
            {
                hex.Append(b.ToString("X2"));
            }
            return hex.ToString();
        }

        static string QuoteIdentifier(string name)
        {
            return "[" + name.Replace("]", "]]") + "]";
        }

        static string QuoteLiteral(string value)
        {
            return value.Replace("'", "''");
        }
    }
}
