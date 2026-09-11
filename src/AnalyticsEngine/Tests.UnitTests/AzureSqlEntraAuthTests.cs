using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Core;
using Common.Entities.Installer;
using Common.Entities.Sql;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.SqlClient;
using System.Threading;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers Microsoft Entra ID authentication for Azure SQL (issue #117).
    /// </summary>
    /// <remarks>
    /// The rules under test are the ones that decide whether an existing customer deployment keeps using
    /// its SQL login or is switched to token authentication, so most of these assertions exist to prove
    /// the backwards-compatible answer, not the new one.
    /// </remarks>
    [TestClass]
    public class AzureSqlEntraAuthTests
    {
        // Synthetic throughout - no real server, tenant or identity.
        const string AzureSqlWithLogin = "data source=contoso-sql.database.windows.net;initial catalog=analytics;persist security info=True;user id=sqladmin;password=Fake123!;MultipleActiveResultSets=True";
        const string AzureSqlNoLogin = "data source=contoso-sql.database.windows.net;initial catalog=analytics;persist security info=False;MultipleActiveResultSets=True;Encrypt=True";
        // A deliberately non-palindromic GUID: the SID conversion is endian-sensitive, and a symmetrical
        // value would let a byte-order bug pass.
        static readonly Guid AppServiceIdentity = new Guid("0a1b2c3d-4e5f-6789-abcd-ef0123456789");

        [TestCleanup]
        public void Cleanup()
        {
            // The credential is process-wide ambient state; leaking one would silently change what other
            // tests exercise.
            AzureSqlTokenAuth.ResetCredential();
        }

        #region Connection-string detection

        [TestMethod]
        public void NeedsAccessToken_AzureSqlWithNoLogin_IsTrue()
        {
            Assert.IsTrue(AzureSqlTokenAuth.NeedsAccessToken(AzureSqlNoLogin));
        }

        /// <summary>
        /// The single most important assertion here: an existing SQL-authentication deployment must never
        /// be switched to token authentication behind the operator's back.
        /// </summary>
        [TestMethod]
        public void NeedsAccessToken_AzureSqlWithLogin_IsFalse()
        {
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken(AzureSqlWithLogin));
        }

        [TestMethod]
        public void NeedsAccessToken_NonAzureServer_IsFalse()
        {
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken(@"data source=(localdb)\MSSQLLocalDB;initial catalog=UnitTestingAnalytics;integrated security=True"));
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken("data source=sql01.contoso.local;initial catalog=analytics"));
        }

        [TestMethod]
        public void NeedsAccessToken_IntegratedSecurity_IsFalse()
        {
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken("data source=contoso-sql.database.windows.net;integrated security=True"));
        }

        /// <summary>
        /// SqlClient throws if AccessToken is set alongside its own Authentication keyword, so those
        /// connection strings must be left entirely alone.
        /// </summary>
        [TestMethod]
        public void NeedsAccessToken_SqlClientAuthenticationKeyword_IsFalse()
        {
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken(
                "data source=contoso-sql.database.windows.net;initial catalog=analytics;Authentication=Active Directory Managed Identity"));
        }

        [TestMethod]
        public void NeedsAccessToken_ProtocolPrefixAndPort_IsTrue()
        {
            Assert.IsTrue(AzureSqlTokenAuth.NeedsAccessToken("data source=tcp:contoso-sql.database.windows.net,1433;initial catalog=analytics"));
        }

        [TestMethod]
        public void NeedsAccessToken_NullEmptyOrUnparseable_IsFalse()
        {
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken(null));
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken(string.Empty));
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken("this is not a connection string ==== ;;;"));
        }

        [TestMethod]
        public void IsAzureSqlDataSource_HandlesCasingAndDecoration()
        {
            Assert.IsTrue(AzureSqlTokenAuth.IsAzureSqlDataSource("CONTOSO-SQL.DATABASE.WINDOWS.NET"));
            Assert.IsTrue(AzureSqlTokenAuth.IsAzureSqlDataSource("tcp:contoso-sql.database.windows.net,1433"));
            Assert.IsFalse(AzureSqlTokenAuth.IsAzureSqlDataSource("contoso-sql.database.windows.net.evil.example"));
            Assert.IsFalse(AzureSqlTokenAuth.IsAzureSqlDataSource(null));
        }

        #endregion

        #region Token application

        [TestMethod]
        public void ApplyAccessToken_TokenlessAzureConnection_SetsToken()
        {
            AzureSqlTokenAuth.SetCredential(new StubTokenCredential("stub-token"));

            using (var connection = AzureSqlTokenAuth.CreateConnection(AzureSqlNoLogin))
            {
                Assert.AreEqual("stub-token", connection.AccessToken);
            }
        }

        [TestMethod]
        public void ApplyAccessToken_ConnectionWithLogin_LeavesTokenUnset()
        {
            AzureSqlTokenAuth.SetCredential(new StubTokenCredential("stub-token"));

            using (var connection = AzureSqlTokenAuth.CreateConnection(AzureSqlWithLogin))
            {
                Assert.IsTrue(string.IsNullOrEmpty(connection.AccessToken));
            }
        }

        /// <summary>
        /// A reused connection keeps its AccessToken across close/reopen, so the token must be replaced on
        /// every open or a long-running importer would eventually reopen with an expired one.
        /// </summary>
        [TestMethod]
        public void ApplyAccessToken_CalledTwice_RefreshesTheToken()
        {
            var credential = new StubTokenCredential("token-1");
            AzureSqlTokenAuth.SetCredential(credential);

            using (var connection = AzureSqlTokenAuth.CreateConnection(AzureSqlNoLogin))
            {
                Assert.AreEqual("token-1", connection.AccessToken);

                credential.NextToken = "token-2";
                AzureSqlTokenAuth.ApplyAccessTokenIfNeeded(connection);

                Assert.AreEqual("token-2", connection.AccessToken);
            }
        }

        [TestMethod]
        public void ApplyAccessToken_NoCredential_ThrowsExplanatoryError()
        {
            AzureSqlTokenAuth.ResetCredential();

            try
            {
                AzureSqlTokenAuth.CreateConnection(AzureSqlNoLogin);
                Assert.Fail("Expected an InvalidOperationException - there is no credential to get a token with.");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "Microsoft Entra ID authentication");
            }
        }

        /// <summary>
        /// The EF6 interceptor is what tokenises the connections the migration pipeline opens for itself.
        /// </summary>
        [TestMethod]
        public void EfInterceptor_OnlyTokenisesConnectionsThatNeedIt()
        {
            AzureSqlTokenAuth.SetCredential(new StubTokenCredential("stub-token"));
            var interceptor = new AzureSqlAccessTokenInterceptor();

            using (var tokenless = new SqlConnection(AzureSqlNoLogin))
            using (var withLogin = new SqlConnection(AzureSqlWithLogin))
            {
                interceptor.Opening(tokenless, null);
                interceptor.Opening(withLogin, null);

                Assert.AreEqual("stub-token", tokenless.AccessToken);
                Assert.IsTrue(string.IsNullOrEmpty(withLogin.AccessToken));
            }
        }

        #endregion

        #region Connection string the installer writes

        /// <summary>
        /// Crosses the boundary that matters: the connection string the installer writes to the App Service
        /// must be one the runtime recognises as needing a token. If these two ever drift, the web app
        /// silently fails to authenticate.
        /// </summary>
        [TestMethod]
        public void EntraIdConnectionString_IsRecognisedByTheRuntimeAsNeedingAToken()
        {
            var connectionString = DatabasePaaSInfo.GetEntraIdConnectionString("contoso-sql.database.windows.net", "analytics");

            Assert.IsTrue(AzureSqlTokenAuth.NeedsAccessToken(connectionString));

            var builder = new SqlConnectionStringBuilder(connectionString);
            Assert.AreEqual(string.Empty, builder.UserID);
            Assert.AreEqual(string.Empty, builder.Password);
            Assert.AreEqual("analytics", builder.InitialCatalog);
        }

        [TestMethod]
        public void EntraIdConnectionString_WithoutDatabase_StillOmitsCredentials()
        {
            var connectionString = DatabasePaaSInfo.GetEntraIdConnectionString("contoso-sql.database.windows.net", null);

            Assert.IsTrue(AzureSqlTokenAuth.NeedsAccessToken(connectionString));
            Assert.AreEqual(string.Empty, new SqlConnectionStringBuilder(connectionString).InitialCatalog);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void EntraIdConnectionString_NoServer_Throws()
        {
            DatabasePaaSInfo.GetEntraIdConnectionString(string.Empty, "analytics");
        }

        /// <summary>
        /// The SQL-login path must fail with something an operator can act on. Without this the missing
        /// password surfaced as a bare ArgumentException naming a parameter, which the installer reports as
        /// "FATAL: Unexpected error of type 'ArgumentException'".
        /// </summary>
        [TestMethod]
        public void EnsureSqlLoginUsable_MissingCredentials_ExplainsBothWaysToFixIt()
        {
            foreach (var missing in new[] { new[] { "sqladmin", "" }, new[] { "", "Fake123!" }, new[] { " ", " " } })
            {
                try
                {
                    DatabasePaaSInfo.EnsureSqlLoginUsable("contoso-sql.database.windows.net", missing[0], missing[1]);
                    Assert.Fail("Expected an InvalidOperationException when there is no usable way to authenticate.");
                }
                catch (InvalidOperationException ex)
                {
                    StringAssert.Contains(ex.Message, "contoso-sql.database.windows.net");
                    StringAssert.Contains(ex.Message, "SQL administrator credentials");
                    StringAssert.Contains(ex.Message, "Microsoft Entra");
                }
            }
        }

        [TestMethod]
        public void EnsureSqlLoginUsable_WithCredentials_DoesNotThrow()
        {
            DatabasePaaSInfo.EnsureSqlLoginUsable("contoso-sql.database.windows.net", "sqladmin", "Fake123!");
        }

        #endregion

        #region Per-server auth detection

        /// <summary>
        /// A server with Entra-only authentication rejects SQL logins outright, so configured credentials
        /// cannot win here however they are set.
        /// </summary>
        [TestMethod]
        public void Decide_EntraOnlyServer_UsesEntraEvenWithSqlCredentials()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = true, HasEntraAdmin = true, HasSqlAdminLogin = true };

            Assert.AreEqual(SqlConnectionAuthMethod.EntraId, SqlServerAuthDetection.Decide(state, true, SqlServerAuthMode.SqlLogin).Method);
            Assert.AreEqual(SqlConnectionAuthMethod.EntraId, SqlServerAuthDetection.Decide(state, false, SqlServerAuthMode.EntraId).Method);
        }

        /// <summary>The existing-deployment case: nothing about it changes.</summary>
        [TestMethod]
        public void Decide_ExistingSqlAuthServer_KeepsSqlLogin()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = false, HasEntraAdmin = false, HasSqlAdminLogin = true };

            var decision = SqlServerAuthDetection.Decide(state, true, SqlServerAuthMode.SqlLogin);

            Assert.AreEqual(SqlConnectionAuthMethod.SqlLogin, decision.Method);
            Assert.IsFalse(decision.UsesEntraId);
        }

        /// <summary>
        /// Selecting Entra ID must not break an install pointed at an existing SQL-authentication server:
        /// that server is never reconfigured, so it has no Entra administrator to sign in as.
        /// </summary>
        [TestMethod]
        public void Decide_EntraSelectedButExistingServerHasNoEntraAdmin_FallsBackToSqlLogin()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = false, HasEntraAdmin = false, HasSqlAdminLogin = true };

            var decision = SqlServerAuthDetection.Decide(state, true, SqlServerAuthMode.EntraId);

            Assert.AreEqual(SqlConnectionAuthMethod.SqlLogin, decision.Method);
            StringAssert.Contains(decision.Reason, "never reconfigured");
        }

        [TestMethod]
        public void Decide_EntraSelectedAndServerHasEntraAdmin_UsesEntra()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = false, HasEntraAdmin = true, HasSqlAdminLogin = true };

            Assert.AreEqual(SqlConnectionAuthMethod.EntraId, SqlServerAuthDetection.Decide(state, true, SqlServerAuthMode.EntraId).Method);
        }

        [TestMethod]
        public void Decide_NoSqlCredentialsButEntraAdminPresent_UsesEntra()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = false, HasEntraAdmin = true, HasSqlAdminLogin = true };

            Assert.AreEqual(SqlConnectionAuthMethod.EntraId, SqlServerAuthDetection.Decide(state, false, SqlServerAuthMode.SqlLogin).Method);
        }

        [TestMethod]
        public void Decide_NothingUsable_ReportsHowToFixIt()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = false, HasEntraAdmin = false, HasSqlAdminLogin = true };

            var decision = SqlServerAuthDetection.Decide(state, false, SqlServerAuthMode.SqlLogin);

            Assert.AreEqual(SqlConnectionAuthMethod.SqlLogin, decision.Method);
            StringAssert.Contains(decision.Reason, "no usable way to authenticate");
        }

        [TestMethod]
        public void Decide_ServerCouldNotBeInspected_FollowsTheConfiguredCredentials()
        {
            Assert.AreEqual(SqlConnectionAuthMethod.SqlLogin, SqlServerAuthDetection.Decide(null, true, SqlServerAuthMode.SqlLogin).Method);
            Assert.AreEqual(SqlConnectionAuthMethod.EntraId, SqlServerAuthDetection.Decide(null, false, SqlServerAuthMode.EntraId).Method);
        }

        /// <summary>
        /// "New SQL: Entra ID enabled. Existing SQL: leave as is." An existing server must never be
        /// provisioned with Entra-only authentication, whatever the operator selected.
        /// </summary>
        [TestMethod]
        public void ShouldProvisionWithEntraAuth_OnlyForNewServers()
        {
            Assert.IsTrue(SqlServerAuthDetection.ShouldProvisionWithEntraAuth(serverAlreadyExists: false, configuredMode: SqlServerAuthMode.EntraId));
            Assert.IsFalse(SqlServerAuthDetection.ShouldProvisionWithEntraAuth(serverAlreadyExists: true, configuredMode: SqlServerAuthMode.EntraId));
            Assert.IsFalse(SqlServerAuthDetection.ShouldProvisionWithEntraAuth(serverAlreadyExists: false, configuredMode: SqlServerAuthMode.SqlLogin));
        }

        #endregion

        #region No interactive administrator warning

        static SqlAuthDecision EntraDecision => new SqlAuthDecision(SqlConnectionAuthMethod.EntraId, "test");
        static SqlAuthDecision SqlLoginDecision => new SqlAuthDecision(SqlConnectionAuthMethod.SqlLogin, "test");

        /// <summary>
        /// The default install: the installer makes its own service principal the Entra administrator, so the
        /// solution works but no person can sign in to query the database by hand.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_ApplicationAdministrator_Warns()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminPrincipalType = "Application",
                EntraAdminLogin = "00000000-0000-0000-0000-000000000000",
            };

            var warning = SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "Set admin");
            StringAssert.Contains(warning, "You don't have access to this database");
            StringAssert.Contains(warning, "Because SQL authentication is disabled");
        }

        /// <summary>
        /// A mixed-authentication server is reachable: Decide picks Entra whenever it is configured and the
        /// server has an Entra administrator, even though SQL logins still work. The warning must not claim
        /// SQL authentication is disabled there, because it is not.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_MixedAuthServer_DoesNotClaimSqlAuthIsDisabled()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = false,
                HasSqlAdminLogin = true,
                HasEntraAdmin = true,
                EntraAdminPrincipalType = "Application",
                EntraAdminLogin = "some-other-app",
            };

            var warning = SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "SQL authentication is still enabled");
            StringAssert.Contains(warning, "remains an option");
            Assert.IsFalse(warning.Contains("Because SQL authentication is disabled"),
                "A mixed-authentication server still accepts the SQL administrator login.");
        }

        /// <summary>
        /// Decide picks Entra whenever it is configured and the server has an Entra administrator, without
        /// checking for a SQL administrator login. So a mixed-auth server with no SQL login is reachable, and
        /// the warning must not offer a login that Azure says does not exist.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_MixedAuthWithNoSqlLogin_DoesNotOfferOne()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = false,
                HasSqlAdminLogin = false,
                HasEntraAdmin = true,
                EntraAdminPrincipalType = "Application",
            };

            var warning = SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "no SQL administrator login");
            Assert.IsFalse(warning.Contains("remains an option"),
                "There is no SQL administrator login to fall back on.");
        }

        /// <summary>
        /// The administrator is only known to be an application, not known to be OUR application - an
        /// operator can configure a different one, and an existing server may already have its own.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_DoesNotAssertWhichApplicationIsAdministrator()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminPrincipalType = "Application",
                EntraAdminLogin = "some-unrelated-automation-app",
            };

            var warning = SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision);

            StringAssert.Contains(warning, "some-unrelated-automation-app");
            Assert.IsFalse(warning.Contains("is the installer's own service principal"),
                "The warning must not assert an identity it has not verified.");
        }

        [TestMethod]
        public void InteractiveAdminWarning_NoAdministratorAtAll_Warns()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = true, HasEntraAdmin = false };

            var warning = SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "no Microsoft Entra administrator");
        }

        /// <summary>
        /// A user or a group administrator can sign in, so there is nothing to warn about. A group is the
        /// shape we actually recommend, so it must not warn either.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_UserOrGroupAdministrator_IsSilent()
        {
            foreach (var principalType in new[] { "User", "Group", "group" })
            {
                var state = new SqlServerAuthState
                {
                    EntraOnlyAuthEnabled = true,
                    HasEntraAdmin = true,
                    EntraAdminPrincipalType = principalType,
                    EntraAdminLogin = "dba-team@contoso.com",
                };

                Assert.IsNull(SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision),
                    $"A '{principalType}' administrator can sign in, so no warning should be produced.");
            }
        }

        /// <summary>
        /// On the SQL-login path the operator already has a username and password, so none of this applies.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_SqlLoginPath_IsSilent()
        {
            var state = new SqlServerAuthState { HasEntraAdmin = false, HasSqlAdminLogin = true };

            Assert.IsNull(SqlServerAuthDetection.GetInteractiveAdminWarning(state, SqlLoginDecision));
        }

        /// <summary>Never guess: an uninspectable server produces no claim either way.</summary>
        [TestMethod]
        public void InteractiveAdminWarning_UnknownServerState_IsSilent()
        {
            Assert.IsNull(SqlServerAuthDetection.GetInteractiveAdminWarning(null, EntraDecision));
            Assert.IsNull(SqlServerAuthDetection.GetInteractiveAdminWarning(new SqlServerAuthState(), null));
        }

        /// <summary>
        /// Azure does not always report a principal type. Consistent with the "authentication will not be
        /// guessed" rule elsewhere in this reader, an administrator of unknown type is assumed to be a real
        /// principal that can sign in, so we stay silent rather than tell an operator who already has a
        /// working administrator to go and set one. The case this warning exists for - a server the
        /// installer itself created - always reports the type, because the installer sets it.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_UnknownPrincipalType_IsSilent()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminPrincipalType = null,
                EntraAdminLogin = "someone@contoso.com",
            };

            Assert.IsNull(SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision));
        }

        #endregion

        #region Contained-user T-SQL

        /// <summary>
        /// SQL Server wants the object ID's raw little-endian GUID bytes, not the textual GUID. Getting the
        /// byte order wrong produces a user that exists but can never authenticate.
        /// </summary>
        [TestMethod]
        public void ToSqlSid_IsTheGuidsLittleEndianByteLayout()
        {
            var expected = "0x" + BitConverter.ToString(AppServiceIdentity.ToByteArray()).Replace("-", string.Empty);

            Assert.AreEqual(expected, SqlContainedUserScript.ToSqlSid(AppServiceIdentity));
        }

        [TestMethod]
        public void CreateUserAndGrantRoles_CreatesExternalUserAndAddsRoles()
        {
            var sql = SqlContainedUserScript.CreateUserAndGrantRoles(
                "contoso-analytics-web", AppServiceIdentity, SqlContainedUserScript.AppServiceRoles);

            StringAssert.Contains(sql, $"CREATE USER [contoso-analytics-web] WITH SID = {SqlContainedUserScript.ToSqlSid(AppServiceIdentity)}, TYPE = E;");
            StringAssert.Contains(sql, "ALTER ROLE [db_datareader] ADD MEMBER [contoso-analytics-web];");
            StringAssert.Contains(sql, "ALTER ROLE [db_datawriter] ADD MEMBER [contoso-analytics-web];");

            // The importers create and drop real dbo staging tables, so DDL rights are functional, not
            // optional - see InsertBatch.
            StringAssert.Contains(sql, "ALTER ROLE [db_ddladmin] ADD MEMBER [contoso-analytics-web];");

            StringAssert.Contains(sql, "GRANT EXECUTE TO [contoso-analytics-web];");

            // No Microsoft Graph lookup, so the SQL server does not need the Directory Readers role.
            Assert.IsFalse(sql.Contains("FROM EXTERNAL PROVIDER"));
        }

        /// <summary>Re-running the installer must not fail on an already-granted identity.</summary>
        [TestMethod]
        public void CreateUserAndGrantRoles_IsIdempotent()
        {
            var sql = SqlContainedUserScript.CreateUserAndGrantRoles("contoso-analytics-web", AppServiceIdentity, new[] { "db_datareader" });

            StringAssert.Contains(sql, "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'contoso-analytics-web')");
            StringAssert.Contains(sql, "sys.database_role_members");
        }

        [TestMethod]
        public void CreateUserAndGrantRoles_EscapesIdentifiers()
        {
            var sql = SqlContainedUserScript.CreateUserAndGrantRoles("we][rd'name", AppServiceIdentity, new string[0]);

            // T-SQL bracket quoting only doubles the closing bracket; the single quote only matters in the
            // N'...' literal used by the existence check.
            StringAssert.Contains(sql, "CREATE USER [we]][rd'name]");
            StringAssert.Contains(sql, "N'we][rd''name'");
        }

        [TestMethod]
        public void ToSqlSid_KnownGuid_MatchesSqlServersExpectedByteOrder()
        {
            // 0a1b2c3d-4e5f-6789-abcd-ef0123456789: first three groups are byte-reversed, the last two are not.
            Assert.AreEqual("0x3D2C1B0A5F4E8967ABCDEF0123456789", SqlContainedUserScript.ToSqlSid(AppServiceIdentity));
        }

        [TestMethod]
        public void CreateUserAndGrantRoles_SkipsBlankRoles()
        {
            var sql = SqlContainedUserScript.CreateUserAndGrantRoles("contoso-analytics-web", AppServiceIdentity, new[] { "db_datareader", string.Empty, null });

            Assert.AreEqual(1, CountOccurrences(sql, "ALTER ROLE"));
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void CreateUserAndGrantRoles_EmptyPrincipalName_Throws()
        {
            SqlContainedUserScript.CreateUserAndGrantRoles(" ", AppServiceIdentity, new string[0]);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void CreateUserAndGrantRoles_EmptyObjectId_Throws()
        {
            SqlContainedUserScript.CreateUserAndGrantRoles("contoso-analytics-web", Guid.Empty, new string[0]);
        }

        #endregion

        #region Schema-upgrade credential hand-off

        /// <summary>
        /// The schema upgrade runs in a separately downloaded control-panel process, so it needs its own
        /// credential. All three parts must be present or it cannot authenticate.
        /// </summary>
        [TestMethod]
        public void DatabaseUpgradeInfo_HasEntraCredential_RequiresAllThreeParts()
        {
            Assert.IsFalse(new DatabaseUpgradeInfo().HasEntraCredential);
            Assert.IsFalse(new DatabaseUpgradeInfo { EntraTenantId = "tenant", EntraClientId = "client" }.HasEntraCredential);
            Assert.IsTrue(new DatabaseUpgradeInfo { EntraTenantId = "tenant", EntraClientId = "client", EntraClientSecret = "secret" }.HasEntraCredential);
        }

        #endregion

        #region Config schema back-compatibility

        /// <summary>
        /// A config file written before this change has no SqlAuthMode property, and must load as SQL
        /// authentication - the behaviour it was saved with.
        /// </summary>
        [TestMethod]
        public void SqlAuthMode_DefaultsToSqlLoginForOlderConfigs()
        {
            var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<BaseSolutionInstallConfig>(
                "{ \"SQLServerName\": \"contoso-sql\", \"SQLServerAdminUsername\": \"sqladmin\" }");

            Assert.AreEqual(SqlServerAuthMode.SqlLogin, loaded.SqlAuthMode);
        }

        [TestMethod]
        public void SqlAuthMode_SerialisesByName()
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new BaseSolutionInstallConfig { SqlAuthMode = SqlServerAuthMode.EntraId });

            StringAssert.Contains(json, "\"SqlAuthMode\":\"EntraId\"");
        }

        #endregion

        static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>Hands out a fixed token so the tests never touch Entra ID.</summary>
        private class StubTokenCredential : TokenCredential
        {
            public StubTokenCredential(string token)
            {
                NextToken = token;
            }

            public string NextToken { get; set; }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                Assert.AreEqual(AzureSqlTokenAuth.SqlTokenScope, requestContext.Scopes[0]);
                return new AccessToken(NextToken, DateTimeOffset.UtcNow.AddHours(1));
            }

            public override System.Threading.Tasks.ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new System.Threading.Tasks.ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
            }
        }
    }
}
