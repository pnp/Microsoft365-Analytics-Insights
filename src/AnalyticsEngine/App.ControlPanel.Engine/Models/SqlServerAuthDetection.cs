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
        /// <param name="principalObjectId">The Entra object (principal) ID of the identity.</param>
        /// <param name="roles">Database roles to add the user to.</param>
        public static string CreateUserAndGrantRoles(string principalName, Guid principalObjectId, IEnumerable<string> roles)
        {
            if (string.IsNullOrWhiteSpace(principalName))
            {
                throw new ArgumentException($"'{nameof(principalName)}' cannot be null or empty.", nameof(principalName));
            }
            if (principalObjectId == Guid.Empty)
            {
                throw new ArgumentException($"'{nameof(principalObjectId)}' cannot be an empty GUID.", nameof(principalObjectId));
            }
            if (roles == null) throw new ArgumentNullException(nameof(roles));

            var quotedName = QuoteIdentifier(principalName);
            var literalName = QuoteLiteral(principalName);
            var sid = ToSqlSid(principalObjectId);

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
