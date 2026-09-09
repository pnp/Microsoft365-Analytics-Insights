using Azure.Core;
using Azure.Identity;
using System;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Threading;

namespace DataUtils.Sql
{
    /// <summary>
    /// Entra ID (Azure AD) authentication for Azure SQL.
    /// <para>
    /// Azure SQL servers can be created with SQL authentication disabled ("Microsoft Entra-only
    /// authentication"). When that is the case there is no login/password to put in the connection
    /// string; instead the caller must acquire an Entra access token for the SQL resource and hand
    /// it to <see cref="SqlConnection.AccessToken"/> before opening the connection.
    /// </para>
    /// <para>
    /// Everything here is opt-in and inferred from the connection string, so existing deployments -
    /// which still carry <c>user id</c> / <c>password</c> - keep using SQL authentication untouched.
    /// This mirrors how Redis, Service Bus, Storage and Azure AI Language already prefer RBAC in this
    /// solution while falling back to key/secret auth. See issue #117.
    /// </para>
    /// </summary>
    public static class AzureSqlTokenAuth
    {
        /// <summary>
        /// Scope for an Azure SQL Database data-plane token. Sovereign clouds use a different audience,
        /// but the rest of the installer is Azure Public only, so this matches.
        /// </summary>
        public const string SqlTokenScope = "https://database.windows.net/.default";

        /// <summary>Host suffix that identifies an Azure SQL Database server (Azure Public cloud).</summary>
        public const string AzureSqlHostSuffix = ".database.windows.net";

        static TokenCredential _credential;

        /// <summary>
        /// Registers the credential used to acquire SQL access tokens. Called by the installer (its own
        /// service principal), by the downloaded control-panel app during a schema upgrade, and by the
        /// runtime when the App Service has no managed identity available.
        /// </summary>
        public static void SetCredential(TokenCredential credential)
        {
            Interlocked.Exchange(ref _credential, credential);
        }

        /// <summary>
        /// Registers a credential only if one has not already been set. Lets the runtime supply its
        /// service-principal fallback without overwriting a more specific credential.
        /// </summary>
        public static void SetCredentialIfNotSet(TokenCredential credential)
        {
            Interlocked.CompareExchange(ref _credential, credential, null);
        }

        /// <summary>Clears the registered credential. Test hook.</summary>
        public static void ResetCredential()
        {
            Interlocked.Exchange(ref _credential, null);
        }

        /// <summary>
        /// The credential to acquire SQL tokens with. Prefers the App Service / VM system-assigned
        /// managed identity when the host exposes one, because that is the identity the installer
        /// grants database access to. Otherwise falls back to whatever was registered via
        /// <see cref="SetCredential"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT <see cref="DefaultAzureCredential"/>: it probes a long chain of sources
        /// (Visual Studio, Azure CLI, Azure PowerShell, developer tooling) which is slow, unpredictable
        /// on a server, and can silently authenticate as the wrong identity. The rest of this solution
        /// has the same rule for Redis/Storage/Service Bus RBAC.
        /// </remarks>
        public static TokenCredential Credential
        {
            get
            {
                var explicitlySet = Volatile.Read(ref _credential);
                if (explicitlySet != null) return explicitlySet;

                if (HasHostManagedIdentity())
                {
                    var managedIdentity = new ManagedIdentityCredential();
                    Interlocked.CompareExchange(ref _credential, managedIdentity, null);
                    return Volatile.Read(ref _credential);
                }

                return null;
            }
        }

        /// <summary>
        /// Whether the process is running somewhere that exposes a managed identity endpoint. App Service
        /// and Azure Functions set IDENTITY_ENDPOINT/IDENTITY_HEADER (older stamps set MSI_ENDPOINT).
        /// </summary>
        /// <remarks>
        /// Checked explicitly rather than letting <see cref="ManagedIdentityCredential"/> probe IMDS,
        /// which blocks for seconds on a developer machine before failing.
        /// </remarks>
        public static bool HasHostManagedIdentity()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
        }

        // "Authentication=Active Directory ..." makes SqlClient acquire the token itself, in which case
        // setting AccessToken as well is an error ("Cannot set the AccessToken property if 'Authentication'
        // has been specified in the connection string").
        static readonly Regex _authenticationKeyword =
            new Regex(@"(^|;)\s*authentication\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Whether this connection string describes an Azure SQL database that has no credentials of its
        /// own, and therefore needs an Entra access token.
        /// </summary>
        /// <remarks>
        /// Pure function - no I/O, no token acquisition - so the decision is unit testable. A connection
        /// string that still carries <c>user id</c> is left alone: that is an existing SQL-authentication
        /// install, and quietly switching it to Entra would break it.
        /// </remarks>
        public static bool NeedsAccessToken(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return false;

            // SqlClient rejects AccessToken alongside its own Authentication modes, and those modes do
            // not need our help anyway.
            if (_authenticationKeyword.IsMatch(connectionString)) return false;

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException)
            {
                // Not parseable as a SQL connection string - not ours to reason about.
                return false;
            }

            // Explicit credentials of any kind mean "leave this connection alone".
            if (!string.IsNullOrWhiteSpace(builder.UserID)) return false;
            if (builder.IntegratedSecurity) return false;

            return IsAzureSqlDataSource(builder.DataSource);
        }

        /// <summary>
        /// Whether a connection string's data source points at Azure SQL Database. Handles the
        /// <c>tcp:</c> protocol prefix and an explicit <c>,1433</c> port that Azure's own portal-copied
        /// connection strings include.
        /// </summary>
        public static bool IsAzureSqlDataSource(string dataSource)
        {
            if (string.IsNullOrWhiteSpace(dataSource)) return false;

            var host = dataSource.Trim();

            var protocolSeparator = host.IndexOf(':');
            if (protocolSeparator >= 0) host = host.Substring(protocolSeparator + 1);

            var portSeparator = host.IndexOf(',');
            if (portSeparator >= 0) host = host.Substring(0, portSeparator);

            return host.Trim().EndsWith(AzureSqlHostSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a <see cref="SqlConnection"/> for the given connection string, attaching an Entra
        /// access token when the connection string has no credentials of its own. Drop-in replacement
        /// for <c>new SqlConnection(connectionString)</c>.
        /// </summary>
        public static SqlConnection CreateConnection(string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                ApplyAccessTokenIfNeeded(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            return connection;
        }

        /// <summary>
        /// Sets <see cref="SqlConnection.AccessToken"/> when the connection needs one. No-op for
        /// SQL-authentication and non-Azure connections, so it is safe to call on every connection.
        /// </summary>
        /// <remarks>
        /// Deliberately refreshes the token even when one is already present. A pooled/reused
        /// <see cref="SqlConnection"/> keeps its <c>AccessToken</c> across close/reopen, so a long-lived
        /// context (the importer holds contexts for hours) would otherwise reopen with an expired token.
        /// Re-acquiring is cheap: Azure.Identity credentials cache the token in memory and only call the
        /// identity provider again when it is close to expiry.
        /// </remarks>
        public static void ApplyAccessTokenIfNeeded(SqlConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (!NeedsAccessToken(connection.ConnectionString)) return;

            connection.AccessToken = GetAccessToken();
        }

        /// <summary>
        /// Acquires an Azure SQL access token from the registered credential. The credential (not the
        /// token) is cached, so long-running work such as an EF migration keeps getting fresh tokens
        /// rather than expiring mid-upgrade.
        /// </summary>
        public static string GetAccessToken()
        {
            var credential = Credential;
            if (credential == null)
            {
                throw new InvalidOperationException(
                    "This Azure SQL connection string has no user id/password, so it needs Microsoft Entra ID authentication, " +
                    "but no credential is available. On Azure this means the App Service has no system-assigned managed identity " +
                    "enabled; elsewhere it means no credential was registered via AzureSqlTokenAuth.SetCredential(). " +
                    "Re-run the installer to repair the identity and its database permissions.");
            }

            try
            {
                return credential.GetToken(new TokenRequestContext(new[] { SqlTokenScope }), CancellationToken.None).Token;
            }
            catch (AuthenticationFailedException ex)
            {
                throw new InvalidOperationException(
                    "Could not acquire a Microsoft Entra ID access token for Azure SQL. The identity this process runs as " +
                    $"could not authenticate: {ex.Message}", ex);
            }
        }
    }
}
