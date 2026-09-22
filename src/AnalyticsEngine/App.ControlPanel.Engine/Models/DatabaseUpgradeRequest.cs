using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Turns what an operator entered on the "Upgrade Database Schema" form into an upgrade request, or
    /// explains why it cannot work.
    /// </summary>
    /// <remarks>
    /// Lives in the engine rather than in the form so the authentication rules - which decide whether the
    /// upgrade signs in with the connection string's own credentials or with a Microsoft Entra ID service
    /// principal - are unit testable without a window. See issue #117 for why the Entra path exists at all:
    /// an Azure SQL server with SQL authentication disabled has no login to put in a connection string, so
    /// the credential has to be supplied separately.
    /// </remarks>
    public class DatabaseUpgradeRequest
    {
        /// <summary>Shown when the operator has not entered a connection string at all.</summary>
        public const string NoConnectionStringError =
            "Enter the connection string of the database to upgrade, or use 'Autodetect from installer configuration'.";

        /// <summary>Shown when the text entered is not a SQL Server connection string.</summary>
        public const string MalformedConnectionStringError =
            "That is not a valid SQL Server connection string, so there is nothing to upgrade. It should look like " +
            "'data source=<server>;initial catalog=<database>;...'.";

        /// <summary>Shown when the connection string names a server but no database.</summary>
        /// <remarks>
        /// This is a guard against real damage, not a formality. Without 'initial catalog' SQL Server
        /// connects to the login's default database - <c>master</c> for an Azure SQL administrator - and the
        /// upgrade would create the whole analytics schema there instead of failing. Autodetection can
        /// produce exactly such a connection string whenever the loaded configuration has no database name,
        /// because a server-level connection string is the correct result for the connectivity test that
        /// shares the same detection code.
        /// </remarks>
        public const string NoDatabaseError =
            "This connection string does not name a database ('initial catalog'), so the upgrade would run against the " +
            "server's default database - normally 'master' - and create the solution's schema in the wrong place. " +
            "Add 'initial catalog=<your analytics database>;' to it, or set the database name in the installer " +
            "configuration and autodetect again.";

        /// <summary>
        /// Shown when the connection string can only be used with Microsoft Entra ID but no service
        /// principal was given. Without this the upgrade would reach Entity Framework with no credential
        /// and fail with an error about the 'master' database that names neither Entra ID nor the cause.
        /// </summary>
        public const string MissingEntraCredentialError =
            "This connection string has no SQL login, so the upgrade has to authenticate with Microsoft Entra ID. " +
            "Enter the directory (tenant) ID, client ID and client secret of a service principal that is a database user " +
            "with schema-upgrade permissions - normally the installer's own app registration.";

        /// <summary>How the form describes an empty connection-string box.</summary>
        public const string NothingEnteredDescription =
            "Enter the connection string of the database to upgrade, or use 'Autodetect from installer configuration' " +
            "to read it from the configuration open in the installer.";

        /// <summary>How the form describes text that is not a connection string.</summary>
        public const string UnrecognisedDescription =
            "This is not a valid SQL Server connection string, so the authentication method can't be determined.";

        /// <summary>How an Entra-authenticated upgrade is described on the form.</summary>
        public const string EntraAuthDescription =
            "This connection string names an Azure SQL server and carries no SQL login, so the upgrade will authenticate " +
            "with Microsoft Entra ID as the service principal below.";

        /// <summary>How an Azure SQL upgrade that uses a login in the connection string is described.</summary>
        public const string AzureSqlLoginAuthDescription =
            "This connection string carries a SQL login, so the upgrade will authenticate with it. To use Microsoft " +
            "Entra ID instead - required when the server has SQL authentication disabled - remove the user id and password.";

        /// <summary>
        /// How a connection string that asks SqlClient to acquire its own Entra token is described.
        /// </summary>
        /// <remarks>
        /// These already work: SqlClient signs in itself, so the installer neither needs nor may attach a
        /// token of its own (setting both is an error). The distinction matters because the wording for a
        /// plain SQL login would tell the operator to delete credentials that are doing nothing wrong.
        /// </remarks>
        public const string SqlClientEntraAuthDescription =
            "This connection string selects a Microsoft Entra ID authentication mode of its own " +
            "('Authentication=Active Directory ...'), so SQL Server's client will sign in with it directly. " +
            "No service principal is needed here.";

        /// <summary>
        /// How a non-Azure target is described. Microsoft Entra ID is not an option for these at all, so
        /// telling the operator to remove the login - as the Azure SQL wording does - would be actively wrong.
        /// </summary>
        public const string NonAzureAuthDescription =
            "This connection string targets a SQL Server that is not Azure SQL Database, so the upgrade will use whatever " +
            "credentials it specifies. Microsoft Entra ID authentication does not apply here.";

        private DatabaseUpgradeRequest(DatabaseUpgradeInfo upgradeInfo, string validationError)
        {
            UpgradeInfo = upgradeInfo;
            ValidationError = validationError;
        }

        /// <summary>The upgrade to run, or null when <see cref="ValidationError"/> is set.</summary>
        public DatabaseUpgradeInfo UpgradeInfo { get; }

        /// <summary>Why the upgrade cannot be run, or null when it can.</summary>
        public string ValidationError { get; }

        public bool IsValid => ValidationError == null;

        /// <summary>
        /// Whether this connection string can only be used with Microsoft Entra ID, i.e. it names an Azure
        /// SQL server and carries no login. Exactly the test the upgrade itself applies, so the form can
        /// never offer one authentication method and then use the other.
        /// </summary>
        public static bool NeedsEntraCredential(string connectionString) => AzureSqlTokenAuth.NeedsAccessToken(connectionString);

        /// <summary>Plain-English description of how the given connection string is going to authenticate.</summary>
        public static string DescribeAuthentication(string connectionString)
        {
            var target = Clean(connectionString);
            if (target.Length == 0) return NothingEnteredDescription;

            var builder = TryParse(target);
            if (builder == null) return UnrecognisedDescription;

            // Checked before everything else: an "Authentication=Active Directory ..." connection string is
            // already Entra-authenticated, just by SqlClient rather than by us, and telling its operator to
            // remove the login would break a working configuration.
            if (UsesSqlClientManagedEntra(builder)) return SqlClientEntraAuthDescription;

            if (!AzureSqlTokenAuth.IsAzureSqlDataSource(builder.DataSource)) return NonAzureAuthDescription;

            return NeedsEntraCredential(target) ? EntraAuthDescription : AzureSqlLoginAuthDescription;
        }

        /// <summary>
        /// Whether the connection string asks SqlClient itself to acquire an Entra token (any of the
        /// <c>Authentication=Active Directory ...</c> modes).
        /// </summary>
        /// <remarks>
        /// Matched on the enum member name rather than a list of values so a newer SqlClient's additional
        /// Active Directory mode is classified correctly without a code change. <c>SqlPassword</c> and
        /// <c>NotSpecified</c> deliberately do not match - neither is Entra.
        /// </remarks>
        static bool UsesSqlClientManagedEntra(SqlConnectionStringBuilder builder) =>
            builder.Authentication.ToString().StartsWith("ActiveDirectory", StringComparison.Ordinal);

        public static DatabaseUpgradeRequest Build(string connectionString, string entraTenantId, string entraClientId, string entraClientSecret)
        {
            var target = Clean(connectionString);
            if (target.Length == 0)
            {
                return new DatabaseUpgradeRequest(null, NoConnectionStringError);
            }

            var builder = TryParse(target);
            if (builder == null)
            {
                return new DatabaseUpgradeRequest(null, MalformedConnectionStringError);
            }

            // AttachDBFilename names the target just as explicitly as a catalog does - it is how a LocalDB
            // development database is addressed - so it counts.
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog) && string.IsNullOrWhiteSpace(builder.AttachDBFilename))
            {
                return new DatabaseUpgradeRequest(null, NoDatabaseError);
            }

            var upgradeInfo = new DatabaseUpgradeInfo { ConnectionString = target };

            if (!NeedsEntraCredential(target))
            {
                // Deliberately left empty even when the operator filled the boxes in: the connection string
                // carries a login, so a token would never be used and storing the secret would be pointless.
                return new DatabaseUpgradeRequest(upgradeInfo, null);
            }

            upgradeInfo.EntraTenantId = Clean(entraTenantId);
            upgradeInfo.EntraClientId = Clean(entraClientId);
            upgradeInfo.EntraClientSecret = Clean(entraClientSecret);

            return upgradeInfo.HasEntraCredential
                ? new DatabaseUpgradeRequest(upgradeInfo, null)
                : new DatabaseUpgradeRequest(null, MissingEntraCredentialError);
        }

        static SqlConnectionStringBuilder TryParse(string connectionString)
        {
            try
            {
                return new SqlConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (FormatException)
            {
                // A numeric keyword with a non-numeric value, e.g. "Connection Timeout=12x". Reachable
                // because this runs on every keystroke in the connection-string box, so it must not escape
                // onto the UI thread.
                return null;
            }
            catch (OverflowException)
            {
                // Same, for a numeric keyword whose value does not fit, e.g. "Connection Timeout=12000000000".
                return null;
            }
        }

        /// <summary>
        /// Trimmed because every one of these values is pasted, and a trailing newline off the clipboard
        /// would otherwise surface as an unexplained login failure. None of them - a directory ID, a client
        /// ID or an Entra client secret - can legitimately begin or end with whitespace.
        /// </summary>
        static string Clean(string value) => (value ?? string.Empty).Trim();
    }
}
