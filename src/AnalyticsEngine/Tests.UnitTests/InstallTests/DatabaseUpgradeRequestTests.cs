using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.SqlClient;
using SolutionTestConfiguration = App.ControlPanel.Engine.Models.TestConfiguration;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Rules behind the installer's "Upgrade Database Schema" form: which authentication an entered
    /// connection string implies, and what the form has to collect before the upgrade can run.
    /// </summary>
    [TestClass]
    public class DatabaseUpgradeRequestTests
    {
        const string SqlLoginConnectionString =
            "data source=contoso-sql.database.windows.net;initial catalog=analytics;persist security info=True;" +
            "user id=sqladmin;password=SyntheticPassword!;MultipleActiveResultSets=True";

        const string EntraConnectionString =
            "data source=contoso-sql.database.windows.net;initial catalog=analytics;persist security info=False;" +
            "MultipleActiveResultSets=True;Encrypt=True";

        const string TenantId = "00000000-0000-0000-0000-000000000001";
        const string ClientId = "00000000-0000-0000-0000-000000000002";
        const string ClientSecret = "SyntheticClientSecret";

        const string LocalDbConnectionString =
            @"data source=(localdb)\MSSQLLocalDB;initial catalog=UnitTestingAnalytics;integrated security=True";

        [TestMethod]
        public void Build_NoConnectionString_IsRejected()
        {
            foreach (var entered in new[] { null, string.Empty, "   " })
            {
                var request = DatabaseUpgradeRequest.Build(entered, TenantId, ClientId, ClientSecret);

                Assert.IsFalse(request.IsValid, $"'{entered ?? "(null)"}' should not be upgradable.");
                Assert.IsNull(request.UpgradeInfo);
                Assert.AreEqual(DatabaseUpgradeRequest.NoConnectionStringError, request.ValidationError);
            }
        }

        [TestMethod]
        public void Build_SqlLoginConnectionString_CarriesNoEntraCredential()
        {
            // Even with the Entra boxes filled in: a connection string with a login never uses a token, so
            // carrying the secret into the upgrade would be pointless.
            var request = DatabaseUpgradeRequest.Build(SqlLoginConnectionString, TenantId, ClientId, ClientSecret);

            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.IsFalse(request.UpgradeInfo.HasEntraCredential);
            Assert.IsNull(request.UpgradeInfo.EntraTenantId);
            Assert.IsNull(request.UpgradeInfo.EntraClientId);
            Assert.IsNull(request.UpgradeInfo.EntraClientSecret);
        }

        [TestMethod]
        public void Build_EntraConnectionStringWithFullCredential_IsAccepted()
        {
            var request = DatabaseUpgradeRequest.Build(EntraConnectionString, TenantId, ClientId, ClientSecret);

            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.IsTrue(request.UpgradeInfo.HasEntraCredential);
            Assert.AreEqual(TenantId, request.UpgradeInfo.EntraTenantId);
            Assert.AreEqual(ClientId, request.UpgradeInfo.EntraClientId);
            Assert.AreEqual(ClientSecret, request.UpgradeInfo.EntraClientSecret);
        }

        [DataTestMethod]
        [DataRow("", ClientId, ClientSecret)]
        [DataRow(TenantId, "", ClientSecret)]
        [DataRow(TenantId, ClientId, "")]
        [DataRow(null, null, null)]
        public void Build_EntraConnectionStringWithIncompleteCredential_IsRejected(string tenantId, string clientId, string clientSecret)
        {
            // Without this the upgrade reaches Entity Framework with no credential and fails with an error
            // about the 'master' database that names neither Entra ID nor the real cause. See issue #117.
            var request = DatabaseUpgradeRequest.Build(EntraConnectionString, tenantId, clientId, clientSecret);

            Assert.IsFalse(request.IsValid);
            Assert.IsNull(request.UpgradeInfo);
            Assert.AreEqual(DatabaseUpgradeRequest.MissingEntraCredentialError, request.ValidationError);
        }

        [TestMethod]
        public void Build_TrimsPastedValues()
        {
            var request = DatabaseUpgradeRequest.Build($"  {EntraConnectionString}\r\n", $" {TenantId} ", $"\t{ClientId}", $"{ClientSecret}\n");

            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.AreEqual(EntraConnectionString, request.UpgradeInfo.ConnectionString);
            Assert.AreEqual(TenantId, request.UpgradeInfo.EntraTenantId);
            Assert.AreEqual(ClientId, request.UpgradeInfo.EntraClientId);
            Assert.AreEqual(ClientSecret, request.UpgradeInfo.EntraClientSecret);
        }

        [TestMethod]
        public void Build_NonAzureConnectionStringWithoutLogin_NeedsNoEntraCredential()
        {
            // Integrated security against LocalDB or an on-premises server: no login in the string, but
            // no token either. Demanding a service principal here would make the form unusable for it.
            var request = DatabaseUpgradeRequest.Build(LocalDbConnectionString, null, null, null);

            Assert.IsFalse(DatabaseUpgradeRequest.NeedsEntraCredential(LocalDbConnectionString));
            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.IsFalse(request.UpgradeInfo.HasEntraCredential);
        }

        [TestMethod]
        public void DescribeAuthentication_MatchesTheCredentialThatWillBeUsed()
        {
            Assert.AreEqual(DatabaseUpgradeRequest.EntraAuthDescription,
                DatabaseUpgradeRequest.DescribeAuthentication(EntraConnectionString));

            Assert.AreEqual(DatabaseUpgradeRequest.AzureSqlLoginAuthDescription,
                DatabaseUpgradeRequest.DescribeAuthentication(SqlLoginConnectionString));
        }

        /// <summary>
        /// The description sits under the connection-string box and is always visible, so every state it
        /// can be in has to be true. Telling the operator of a LocalDB or on-premises server to "remove the
        /// user id and password" to get Microsoft Entra ID is advice that cannot work.
        /// </summary>
        [TestMethod]
        public void DescribeAuthentication_DoesNotGiveAzureAdviceForNonAzureOrEmptyTargets()
        {
            Assert.AreEqual(DatabaseUpgradeRequest.NothingEnteredDescription, DatabaseUpgradeRequest.DescribeAuthentication(null));
            Assert.AreEqual(DatabaseUpgradeRequest.NothingEnteredDescription, DatabaseUpgradeRequest.DescribeAuthentication("   "));

            Assert.AreEqual(DatabaseUpgradeRequest.UnrecognisedDescription,
                DatabaseUpgradeRequest.DescribeAuthentication("this is not a connection string ==== ;;;"));

            Assert.AreEqual(DatabaseUpgradeRequest.NonAzureAuthDescription,
                DatabaseUpgradeRequest.DescribeAuthentication(LocalDbConnectionString));
            Assert.AreEqual(DatabaseUpgradeRequest.NonAzureAuthDescription,
                DatabaseUpgradeRequest.DescribeAuthentication("data source=sql01.contoso.local;initial catalog=analytics;user id=sa;password=SyntheticPassword!"));
        }

        /// <summary>
        /// The damage case. Without 'initial catalog' the upgrade connects to the login's default database
        /// - 'master' on Azure SQL - and Entity Framework would build the entire analytics schema there.
        /// Autodetection produces exactly this connection string when the loaded configuration has no
        /// database name, because the same detection code deliberately returns a server-level string for
        /// the connectivity test.
        /// </summary>
        [TestMethod]
        public void Build_ConnectionStringWithoutADatabase_IsRejected()
        {
            var serverOnly = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                AuthMethod = SqlConnectionAuthMethod.EntraId
            }.GetConnectionString(string.Empty);

            var request = DatabaseUpgradeRequest.Build(serverOnly, TenantId, ClientId, ClientSecret);

            Assert.IsFalse(request.IsValid, "A server-level connection string must never be upgradable - it would target master.");
            Assert.IsNull(request.UpgradeInfo);
            Assert.AreEqual(DatabaseUpgradeRequest.NoDatabaseError, request.ValidationError);

            // ...and the SQL-authentication shape of the same mistake.
            var sqlServerOnly = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                AuthMethod = SqlConnectionAuthMethod.SqlLogin,
                SqlUsername = "sqladmin",
                SqlPassword = "SyntheticPassword!"
            }.ConnectionString;

            Assert.AreEqual(DatabaseUpgradeRequest.NoDatabaseError,
                DatabaseUpgradeRequest.Build(sqlServerOnly, null, null, null).ValidationError);
        }

        [TestMethod]
        public void Build_UnparseableText_IsRejectedRatherThanTreatedAsASqlLogin()
        {
            var request = DatabaseUpgradeRequest.Build("this is not a connection string ==== ;;;", null, null, null);

            Assert.IsFalse(request.IsValid);
            Assert.IsNull(request.UpgradeInfo);
            Assert.AreEqual(DatabaseUpgradeRequest.MalformedConnectionStringError, request.ValidationError);
        }

        /// <summary>
        /// Both of these are evaluated on every keystroke in the form's connection-string box, so neither
        /// may throw. A numeric keyword with a bad value throws <see cref="FormatException"/> or
        /// <see cref="OverflowException"/> - not <see cref="ArgumentException"/> - and the installer has no
        /// global WinForms exception handler, so an escape would put the operator in a crash dialog on
        /// every subsequent character typed. The autodetected connection strings literally carry a
        /// "Connection Timeout=120", which is what makes this a normal edit rather than an exotic one.
        /// </summary>
        [DataTestMethod]
        [DataRow("data source=contoso-sql.database.windows.net;initial catalog=analytics;Connection Timeout=12x")]
        [DataRow("data source=contoso-sql.database.windows.net;initial catalog=analytics;Connection Timeout=12000000000")]
        [DataRow("data source=contoso-sql.database.windows.net;initial catalog=analytics;Connection Timeout=-")]
        [DataRow("=;;;")]
        public void HalfTypedConnectionStrings_AreRejectedWithoutThrowing(string halfTyped)
        {
            // Would throw rather than return if the parse guards were too narrow.
            Assert.IsFalse(DatabaseUpgradeRequest.NeedsEntraCredential(halfTyped));
            Assert.IsFalse(AzureSqlTokenAuth.NeedsAccessToken(halfTyped));

            Assert.AreEqual(DatabaseUpgradeRequest.UnrecognisedDescription, DatabaseUpgradeRequest.DescribeAuthentication(halfTyped));

            var request = DatabaseUpgradeRequest.Build(halfTyped, TenantId, ClientId, ClientSecret);
            Assert.IsFalse(request.IsValid);
            Assert.AreEqual(DatabaseUpgradeRequest.MalformedConnectionStringError, request.ValidationError);
        }

        /// <summary>
        /// A LocalDB database addressed by file is just as explicitly named as one addressed by catalog, so
        /// the guard against upgrading 'master' must not reject it.
        /// </summary>
        [TestMethod]
        public void Build_AttachedDatabaseFile_CountsAsNamingADatabase()
        {
            const string attached = @"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=C:\Contoso\Analytics.mdf;Integrated Security=True";

            var request = DatabaseUpgradeRequest.Build(attached, null, null, null);

            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.AreEqual(attached, request.UpgradeInfo.ConnectionString);
        }

        /// <summary>
        /// "Authentication=Active Directory ..." is already Microsoft Entra ID - SqlClient acquires the
        /// token itself, and attaching one of ours as well is an error. Describing it as a SQL login would
        /// tell the operator to delete the very thing making it work.
        /// </summary>
        [DataTestMethod]
        [DataRow("Active Directory Managed Identity")]
        [DataRow("Active Directory Default")]
        [DataRow("Active Directory Service Principal")]
        public void DescribeAuthentication_RecognisesSqlClientManagedEntraModes(string authenticationMode)
        {
            var connectionString =
                $"data source=contoso-sql.database.windows.net;initial catalog=analytics;Authentication={authenticationMode}";

            Assert.IsFalse(DatabaseUpgradeRequest.NeedsEntraCredential(connectionString),
                "SqlClient signs in for itself here - the installer must not also attach a token.");
            Assert.AreEqual(DatabaseUpgradeRequest.SqlClientEntraAuthDescription,
                DatabaseUpgradeRequest.DescribeAuthentication(connectionString));

            // ...and it is still a runnable upgrade, needing no service principal from us.
            var request = DatabaseUpgradeRequest.Build(connectionString, null, null, null);
            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.IsFalse(request.UpgradeInfo.HasEntraCredential);
        }

        [TestMethod]
        public void DescribeAuthentication_SqlPasswordModeIsStillASqlLogin()
        {
            // Authentication=SqlPassword is NOT Entra, so it must not get the Entra wording.
            var connectionString =
                "data source=contoso-sql.database.windows.net;initial catalog=analytics;Authentication=SqlPassword;" +
                "user id=sqladmin;password=SyntheticPassword!";

            Assert.AreEqual(DatabaseUpgradeRequest.AzureSqlLoginAuthDescription,
                DatabaseUpgradeRequest.DescribeAuthentication(connectionString));
        }

        /// <summary>
        /// The upgrade form has to target the solution's own database, not the server, so the autodetected
        /// details must be able to produce a connection string with the catalog named.
        /// </summary>
        [TestMethod]
        public void AutodetectedSqlDetails_NamesTheDatabaseWhenAskedTo()
        {
            var entra = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                AuthMethod = SqlConnectionAuthMethod.EntraId
            };

            Assert.AreEqual("analytics", new SqlConnectionStringBuilder(entra.GetConnectionString("analytics")).InitialCatalog);
            Assert.AreEqual(string.Empty, new SqlConnectionStringBuilder(entra.ConnectionString).InitialCatalog,
                "The server-level connection string is what a connectivity test uses - it must not name a database.");

            var sqlLogin = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                AuthMethod = SqlConnectionAuthMethod.SqlLogin,
                SqlUsername = "sqladmin",
                SqlPassword = "SyntheticPassword!"
            };

            var withDb = new SqlConnectionStringBuilder(sqlLogin.GetConnectionString("analytics"));
            Assert.AreEqual("analytics", withDb.InitialCatalog);
            Assert.AreEqual("sqladmin", withDb.UserID);
            Assert.AreEqual(string.Empty, new SqlConnectionStringBuilder(sqlLogin.ConnectionString).InitialCatalog);
        }

        [TestMethod]
        public void AutodetectedSqlDetails_BlankDatabaseNameIsTreatedAsNoDatabase()
        {
            var details = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                AuthMethod = SqlConnectionAuthMethod.EntraId
            };

            // SQLServerDatabaseName is empty on a configuration that has not been filled in yet.
            Assert.AreEqual(details.ConnectionString, details.GetConnectionString(string.Empty));
            Assert.AreEqual(details.ConnectionString, details.GetConnectionString("   "));
            Assert.AreEqual(details.ConnectionString, details.GetConnectionString(null));
        }

        /// <summary>
        /// The whole point of the Entra path: what the form builds has to be recognised as token-authenticated
        /// by the upgrader, which makes that decision from the connection string alone.
        /// </summary>
        [TestMethod]
        public void AutodetectedEntraDetails_ProduceATokenAuthenticatedConnectionString()
        {
            var details = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                AuthMethod = SqlConnectionAuthMethod.EntraId
            };

            Assert.IsTrue(DatabaseUpgradeRequest.NeedsEntraCredential(details.GetConnectionString("analytics")));

            var request = DatabaseUpgradeRequest.Build(details.GetConnectionString("analytics"), TenantId, ClientId, ClientSecret);
            Assert.IsTrue(request.IsValid, request.ValidationError);
            Assert.IsTrue(request.UpgradeInfo.HasEntraCredential);
        }

        /// <summary>
        /// A configuration test detects its own SQL target when none is saved, but must never override one
        /// the operator deliberately configured in 'Solution Tests Configuration'.
        /// </summary>
        [TestMethod]
        public void SavedTestTarget_WinsOverAutodetectionOnlyWhenItIsUsable()
        {
            Assert.IsFalse(SolutionInstallVerifier.SavedTestTargetWins((SolutionTestConfiguration)null),
                "Never configured: the test should detect a target rather than give up.");
            Assert.IsFalse(SolutionInstallVerifier.SavedTestTargetWins(new SolutionTestConfiguration()),
                "Form opened but nothing entered: still nothing to test against.");
            Assert.IsFalse(SolutionInstallVerifier.SavedTestTargetWins(new SolutionTestConfiguration { SQLConnectionString = string.Empty }));

            Assert.IsTrue(SolutionInstallVerifier.SavedTestTargetWins(new SolutionTestConfiguration { SQLConnectionString = SqlLoginConnectionString }),
                "A deliberately saved target must not be silently replaced by autodetection.");
        }
    }
}
