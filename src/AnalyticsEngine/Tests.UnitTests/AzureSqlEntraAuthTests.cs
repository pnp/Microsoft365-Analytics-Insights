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
        /// <remarks>
        /// The remedy must NOT be "assign yourself as the Entra administrator". Azure permits exactly one,
        /// so doing that evicts the installer's principal and breaks the next schema upgrade - which is
        /// precisely the failure this guidance was rewritten to stop causing. "Set admin" may still appear,
        /// but only as the thing not to do.
        /// </remarks>
        [TestMethod]
        public void InteractiveAdminWarning_ApplicationAdministrator_WarnsAndDoesNotRecommendReassigningTheAdmin()
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
            StringAssert.Contains(warning, "You don't have access to this database");
            StringAssert.Contains(warning, "Because SQL authentication is disabled");

            // The supported route: contained database users, configured in the installer.
            StringAssert.Contains(warning, SqlServerAuthDetection.DatabaseUsersConfigUiName);
            StringAssert.Contains(warning, "contained database user");

            // And an explicit prohibition on the thing that breaks the next upgrade.
            StringAssert.Contains(warning, "Do NOT reassign");
            StringAssert.Contains(warning, "only ONE");
            Assert.IsFalse(warning.Contains("so prefer a group"),
                "Recommending a group administrator was the old guidance; the fix is a contained user, not a different administrator.");
        }

        /// <summary>
        /// When people are already configured the install is about to grant them itself, so the warning must
        /// report that rather than ask an operator to go and do something.
        /// </summary>
        [TestMethod]
        public void InteractiveAdminWarning_WithConfiguredDatabaseUsers_SaysTheInstallWillGrantThem()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminPrincipalType = "Application",
            };

            var warning = SqlServerAuthDetection.GetInteractiveAdminWarning(state, EntraDecision, hasConfiguredDatabaseUsers: true);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "will be granted access during this run");
            Assert.IsFalse(warning.Contains("Do NOT reassign"),
                "Nothing is being asked of the operator, so the prohibition is noise here.");
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

        #region Installer lockout diagnosis

        static readonly Guid InstallerSp = new Guid("11111111-1111-1111-1111-111111111111");
        static readonly Guid SomebodyElse = new Guid("22222222-2222-2222-2222-222222222222");

        /// <summary>
        /// The healthy shape: the installer IS the administrator, so it can sign in and there is nothing to
        /// say. Matching on the object ID rather than the login is the point - a login is a display name.
        /// </summary>
        [TestMethod]
        public void InstallerLockoutWarning_InstallerIsTheAdministrator_IsSilent()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminSid = InstallerSp,
                EntraAdminPrincipalType = "Application",
                EntraAdminLogin = "the-installer-app",
            };

            Assert.IsNull(SqlServerAuthDetection.GetInstallerLockoutWarning(state, EntraDecision, InstallerSp));
        }

        /// <summary>
        /// The failure this whole change exists for: somebody assigned a named user as the server's Entra
        /// administrator, which Azure treats as replacing the installer's service principal, and the next
        /// upgrade fails with a login error naming no principal at all.
        /// </summary>
        [TestMethod]
        public void InstallerLockoutWarning_AdministratorReassignedToAPerson_Warns()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminSid = SomebodyElse,
                EntraAdminPrincipalType = "User",
                EntraAdminLogin = "dba@contoso.com",
            };

            var warning = SqlServerAuthDetection.GetInstallerLockoutWarning(state, EntraDecision, InstallerSp);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "dba@contoso.com");
            StringAssert.Contains(warning, InstallerSp.ToString());
            StringAssert.Contains(warning, "only ONE administrator per server");
            StringAssert.Contains(warning, "<token-identified principal>");
        }

        /// <summary>
        /// A group administrator may well contain the installer principal, which cannot be checked from ARM.
        /// The warning must say so rather than predict a failure that will probably not happen.
        /// </summary>
        [TestMethod]
        public void InstallerLockoutWarning_GroupAdministrator_IsHedged()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminSid = SomebodyElse,
                EntraAdminPrincipalType = "Group",
                EntraAdminLogin = "sql-admins",
            };

            var warning = SqlServerAuthDetection.GetInstallerLockoutWarning(state, EntraDecision, InstallerSp);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "security group");
            StringAssert.Contains(warning, "member of it");
            Assert.IsFalse(warning.Contains("will fail with"),
                "A group administrator may contain the installer principal, so failure must not be asserted.");
        }

        [TestMethod]
        public void InstallerLockoutWarning_NoAdministratorAtAll_Warns()
        {
            var state = new SqlServerAuthState { EntraOnlyAuthEnabled = true, HasEntraAdmin = false };

            var warning = SqlServerAuthDetection.GetInstallerLockoutWarning(state, EntraDecision, InstallerSp);

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "no Microsoft Entra administrator assigned at all");
        }

        /// <summary>
        /// Never guess. Without the installer's own object ID the comparison is meaningless, and on the SQL
        /// login path there is no token principal involved at all.
        /// </summary>
        [TestMethod]
        public void InstallerLockoutWarning_UnknowableOrIrrelevant_IsSilent()
        {
            var mismatched = new SqlServerAuthState
            {
                HasEntraAdmin = true,
                EntraAdminSid = SomebodyElse,
                EntraAdminPrincipalType = "User",
            };

            Assert.IsNull(SqlServerAuthDetection.GetInstallerLockoutWarning(mismatched, EntraDecision, Guid.Empty),
                "Without the installer's object ID there is nothing to compare.");
            Assert.IsNull(SqlServerAuthDetection.GetInstallerLockoutWarning(mismatched, SqlLoginDecision, InstallerSp),
                "A SQL login does not authenticate as the installer's service principal.");
            Assert.IsNull(SqlServerAuthDetection.GetInstallerLockoutWarning(null, EntraDecision, InstallerSp),
                "An uninspectable server produces no claim either way.");
        }

        /// <summary>
        /// After SQL has actually rejected the principal the ambiguity is gone, so the remedy states the
        /// cause outright and offers both routes back - without ever recommending the admin reassignment
        /// that caused it.
        /// </summary>
        [TestMethod]
        public void InstallerLockoutRemedy_NamesBothRoutesBack()
        {
            var state = new SqlServerAuthState
            {
                EntraOnlyAuthEnabled = true,
                HasEntraAdmin = true,
                EntraAdminSid = SomebodyElse,
                EntraAdminPrincipalType = "User",
                EntraAdminLogin = "dba@contoso.com",
            };

            var remedy = SqlServerAuthDetection.GetInstallerLockoutRemedy(state, InstallerSp);

            StringAssert.Contains(remedy, "dba@contoso.com");
            StringAssert.Contains(remedy, InstallerSp.ToString());
            StringAssert.Contains(remedy, "sign in when prompted");
            StringAssert.Contains(remedy, "administrator again");
            StringAssert.Contains(remedy, SqlServerAuthDetection.DatabaseUsersConfigUiName);
        }

        /// <summary>Still useful when the server could not be inspected or the object ID is unknown.</summary>
        [TestMethod]
        public void InstallerLockoutRemedy_WithoutServerState_StillExplainsItself()
        {
            var remedy = SqlServerAuthDetection.GetInstallerLockoutRemedy(null, Guid.Empty);

            Assert.IsFalse(string.IsNullOrWhiteSpace(remedy));
            StringAssert.Contains(remedy, "no Microsoft Entra administrator assigned");
            Assert.IsFalse(remedy.Contains("()"), "An unresolved object ID must not leave empty brackets in the text.");
        }

        #endregion

        #region Configured database users

        [TestMethod]
        public void SqlDatabaseUser_ValidatesLoginAndObjectId()
        {
            Assert.IsNull(new SqlDatabaseUser { Login = "dba@contoso.com" }.GetValidationError(),
                "A login alone is enough - the object ID is resolved from it.");

            Assert.IsNull(new SqlDatabaseUser
            {
                Login = "dba@contoso.com",
                ObjectId = "22222222-2222-2222-2222-222222222222",
            }.GetValidationError());

            StringAssert.Contains(new SqlDatabaseUser { Login = "dba@contoso.com", ObjectId = "not-a-guid" }.GetValidationError(),
                "not a valid Microsoft Entra object ID");

            StringAssert.Contains(new SqlDatabaseUser { Login = " " }.GetValidationError(),
                "needs a login");

            // An all-zero GUID is what an unpopulated field serialises to, and it is not a real principal.
            StringAssert.Contains(new SqlDatabaseUser
            {
                Login = "dba@contoso.com",
                ObjectId = "00000000-0000-0000-0000-000000000000",
            }.GetValidationError(), "not a valid Microsoft Entra object ID");
        }

        [TestMethod]
        public void SqlDatabaseUser_ParsesObjectIdAndPrincipalType()
        {
            Guid parsed;

            Assert.IsTrue(new SqlDatabaseUser { ObjectId = " 22222222-2222-2222-2222-222222222222 " }.TryGetObjectId(out parsed));
            Assert.AreEqual(SomebodyElse, parsed);

            Assert.IsFalse(new SqlDatabaseUser().TryGetObjectId(out parsed));
            Assert.AreEqual(Guid.Empty, parsed);

            Assert.IsTrue(new SqlDatabaseUser { PrincipalType = "group" }.IsGroup, "Principal type is compared case-insensitively.");
            Assert.IsFalse(new SqlDatabaseUser { PrincipalType = "User" }.IsGroup);
            Assert.IsFalse(new SqlDatabaseUser { PrincipalType = null }.IsGroup);
        }

        /// <summary>
        /// The list is persisted in the saved installer config, so it has to survive a JSON round trip - and
        /// a config written before the property existed must still load, granting nobody.
        /// </summary>
        [TestMethod]
        public void SqlDatabaseUsers_RoundTripThroughConfigJson()
        {
            var config = new BaseSolutionInstallConfig();
            config.SQLEntraDatabaseUsers.Add(new SqlDatabaseUser
            {
                Login = "Καλημέρα κόσμε",
                ObjectId = "22222222-2222-2222-2222-222222222222",
                PrincipalType = SqlDatabaseUser.PrincipalTypeGroup,
            });

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
            var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<BaseSolutionInstallConfig>(json);

            Assert.AreEqual(1, loaded.SQLEntraDatabaseUsers.Count);
            Assert.AreEqual("Καλημέρα κόσμε", loaded.SQLEntraDatabaseUsers[0].Login,
                "A group display name is customer text and can be non-Latin.");
            Assert.IsTrue(loaded.SQLEntraDatabaseUsers[0].IsGroup);

            var legacy = Newtonsoft.Json.JsonConvert.DeserializeObject<BaseSolutionInstallConfig>("{}");
            Assert.IsNotNull(legacy.SQLEntraDatabaseUsers);
            Assert.AreEqual(0, legacy.SQLEntraDatabaseUsers.Count,
                "A config written before this property existed must load and grant nobody.");
        }

        /// <summary>
        /// Adding a persisted property to the saved config schema requires the schema version to move, or
        /// config compatibility cannot be reasoned about across upgrades.
        /// </summary>
        [TestMethod]
        public void ConfigSchemaVersion_WasBumpedForTheDatabaseUsersList()
        {
            Assert.IsTrue(new BaseSolutionInstallConfig().ConfigSchemaVersion >= new Version(2, 6, 0),
                "SQLEntraDatabaseUsers is a new persisted property, so CONFIG_VERSION must be at least 2.6.0.");
        }

        class StubPrincipalResolver : App.ControlPanel.Engine.InstallerTasks.IEntraPrincipalResolver
        {
            private readonly System.Collections.Generic.Dictionary<string, Guid?> _answers;

            public StubPrincipalResolver(System.Collections.Generic.Dictionary<string, Guid?> answers)
            {
                _answers = answers;
            }

            public int Calls { get; private set; }

            public System.Threading.Tasks.Task<Guid?> ResolveObjectIdAsync(SqlDatabaseUser user, CancellationToken cancellationToken)
            {
                Calls++;

                Guid? answer;
                if (!_answers.TryGetValue(user.Login ?? string.Empty, out answer))
                {
                    throw new InvalidOperationException("directory read denied");
                }

                return System.Threading.Tasks.Task.FromResult(answer);
            }
        }

        /// <summary>
        /// An explicit object ID must be used as-is. That is the escape hatch for a tenant whose app
        /// registrations have no directory-read permission, so it has to work without Graph being consulted
        /// at all.
        /// </summary>
        [TestMethod]
        public async System.Threading.Tasks.Task ResolveUsers_ExplicitObjectId_SkipsTheDirectoryLookup()
        {
            var resolver = new StubPrincipalResolver(new System.Collections.Generic.Dictionary<string, Guid?>());
            var task = new App.ControlPanel.Engine.InstallerTasks.SqlDatabaseUserGrantTask(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, resolver);

            var resolved = await task.ResolveUsersAsync(new[]
            {
                new SqlDatabaseUser { Login = "dba@contoso.com", ObjectId = "22222222-2222-2222-2222-222222222222" },
            });

            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(SomebodyElse, resolved[0].Value);
            Assert.AreEqual(0, resolver.Calls, "A supplied object ID must not trigger a Graph call.");
        }

        /// <summary>
        /// One unusable entry - malformed, unresolvable, or a directory read the app registration is not
        /// permitted to make - must not cost everybody else their access.
        /// </summary>
        [TestMethod]
        public async System.Threading.Tasks.Task ResolveUsers_BadEntriesAreSkippedWithoutLosingTheGoodOnes()
        {
            var resolver = new StubPrincipalResolver(new System.Collections.Generic.Dictionary<string, Guid?>
            {
                { "found@contoso.com", SomebodyElse },
                { "missing@contoso.com", null },
            });

            var task = new App.ControlPanel.Engine.InstallerTasks.SqlDatabaseUserGrantTask(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, resolver);

            var resolved = await task.ResolveUsersAsync(new[]
            {
                null,
                new SqlDatabaseUser { Login = "   " },                                             // no login at all
                new SqlDatabaseUser { Login = "bad@contoso.com", ObjectId = "not-a-guid" },        // malformed object ID
                new SqlDatabaseUser { Login = "missing@contoso.com" },                             // not in the directory
                new SqlDatabaseUser { Login = "denied@contoso.com" },                              // lookup throws
                new SqlDatabaseUser { Login = "found@contoso.com" },                               // fine
            });

            Assert.AreEqual(1, resolved.Count, "Only the resolvable entry should survive.");
            Assert.AreEqual("found@contoso.com", resolved[0].Key.Login);
            Assert.AreEqual(SomebodyElse, resolved[0].Value);
        }

        /// <summary>Nothing configured is the default, and must be a silent no-op rather than an error.</summary>
        [TestMethod]
        public async System.Threading.Tasks.Task ResolveUsers_EmptyList_IsANoOp()
        {
            var task = new App.ControlPanel.Engine.InstallerTasks.SqlDatabaseUserGrantTask(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, null);

            Assert.AreEqual(0, (await task.ResolveUsersAsync(null)).Count);
            Assert.AreEqual(0, (await task.ResolveUsersAsync(new SqlDatabaseUser[0])).Count);
        }

        /// <summary>
        /// Humans are granted db_owner: they are the deployment's own administrators, and read-only access
        /// would not let them run the shipped reporting procedures.
        /// </summary>
        [TestMethod]
        public void DatabaseUserRoles_AreDbOwner()
        {
            CollectionAssert.AreEqual(new[] { "db_owner" },
                System.Linq.Enumerable.ToArray(App.ControlPanel.Engine.InstallerTasks.SqlDatabaseUserGrantTask.DatabaseUserRoles));
        }

        /// <summary>
        /// Azure SQL matches a <em>service principal</em> on its application (client) ID, not its object
        /// ID - unlike a user or group, which it matches on the object ID.
        /// </summary>
        /// <remarks>
        /// Regression test for a failure that looked like a success. SQL Server does not validate the SID
        /// against Entra ID, so creating the installer's contained user with its object ID reported
        /// "Created contained database user ..." and the very next connection was still rejected with
        /// <c>Login failed for user '&lt;token-identified principal&gt;'</c> - an error naming no principal
        /// and reading like a firewall or password problem.
        /// </remarks>
        [TestMethod]
        public void InstallerContainedUser_UsesTheClientIdAsTheSid_NotTheObjectId()
        {
            var clientId = new Guid("33333333-3333-3333-3333-333333333333");
            var objectId = new Guid("44444444-4444-4444-4444-444444444444");

            var script = App.ControlPanel.Engine.InstallerTasks.SqlEntraAccessBootstrap.BuildInstallerUserScript(clientId);

            StringAssert.Contains(script, SqlContainedUserScript.ToSqlSid(clientId),
                "Azure SQL identifies a service principal by its application (client) ID.");
            Assert.IsFalse(script.Contains(SqlContainedUserScript.ToSqlSid(objectId)),
                "The object ID must never be used as the SID for a service principal - it fails silently at sign-in.");
            StringAssert.Contains(script, "TYPE = E");
            StringAssert.Contains(script, "db_owner");
        }

        /// <summary>
        /// A configured human or group is the other half of the rule: those ARE matched on the object ID,
        /// so the resolver must keep passing it straight through.
        /// </summary>
        [TestMethod]
        public async System.Threading.Tasks.Task ConfiguredUsers_KeepUsingTheObjectIdAsTheSid()
        {
            var task = new App.ControlPanel.Engine.InstallerTasks.SqlDatabaseUserGrantTask(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, null);

            var resolved = await task.ResolveUsersAsync(new[]
            {
                new SqlDatabaseUser { Login = "dba@contoso.com", ObjectId = SomebodyElse.ToString() },
            });

            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(SomebodyElse, resolved[0].Value,
                "A user or group is identified by its Entra object ID, unlike a service principal.");
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
