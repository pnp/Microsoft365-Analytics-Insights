using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Models;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.Sql;
using Common.Entities.Installer;
using Common.Entities.Sql;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class SqlAuthenticationDetectionTests
    {
        [TestCleanup]
        public void Cleanup() => AzureSqlTokenAuth.ResetCredential();

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task Reader_ChildSettingOverridesEmbeddedSettingInBothDirections(bool embedded, bool actual)
        {
            using (var arm = new SqlArmFixture { EmbeddedEntraOnly = embedded, EntraOnly = actual })
            {
                var decision = await SqlServerAuthReader.DetectAsync(arm.Server, "SyntheticPassword!", SqlServerAuthMode.SqlLogin, null);

                Assert.AreEqual(actual, decision.UsesEntraId);
                Assert.AreEqual(1, arm.AuthenticationReads);
            }
        }

        [TestMethod]
        public async Task Reader_LegacyServerWithoutAuthChildren_UsesSqlEvenWhenEntraSelected()
        {
            using (var arm = new SqlArmFixture { AuthenticationStatus = HttpStatusCode.NotFound })
            {
                var state = await SqlServerAuthReader.ReadAsync(arm.Server, null);
                Assert.IsFalse(state.EntraOnlyAuthEnabled);
                Assert.IsFalse(state.HasEntraAdmin);
                Assert.IsTrue(state.HasSqlAdminLogin);

                var decision = await SqlServerAuthReader.DetectAsync(arm.Server, "SyntheticPassword!", SqlServerAuthMode.EntraId, null);
                Assert.AreEqual(SqlConnectionAuthMethod.SqlLogin, decision.Method);
            }
        }

        [TestMethod]
        public async Task Reader_SeparateEntraAdmin_EnablesExplicitEntraSelection()
        {
            using (var arm = new SqlArmFixture { AdministratorStatus = HttpStatusCode.OK })
            {
                var decision = await SqlServerAuthReader.DetectAsync(arm.Server, null, SqlServerAuthMode.EntraId, null);
                Assert.IsTrue(decision.UsesEntraId);
            }
        }

        [TestMethod]
        public async Task Reader_RemovedEntraAdmin_DoesNotSelectEntraFromEmbeddedAdministrator()
        {
            using (var arm = new SqlArmFixture { EmbeddedEntraOnly = true, EntraOnly = false })
            {
                var decision = await SqlServerAuthReader.DetectAsync(arm.Server, "SyntheticPassword!", SqlServerAuthMode.EntraId, null);
                Assert.AreEqual(SqlConnectionAuthMethod.SqlLogin, decision.Method);
            }
        }

        [DataTestMethod]
        [DataRow(403, false)]
        [DataRow(500, false)]
        [DataRow(403, true)]
        [DataRow(500, true)]
        public async Task Reader_FailedMetadataRead_DoesNotGuessAuthentication(int status, bool administrator)
        {
            using (var arm = new SqlArmFixture())
            {
                if (administrator) arm.AdministratorStatus = (HttpStatusCode)status;
                else arm.AuthenticationStatus = (HttpStatusCode)status;

                var error = await Assert.ThrowsExceptionAsync<RequestFailedException>(() =>
                    SqlServerAuthReader.DetectAsync(arm.Server, "SyntheticPassword!", SqlServerAuthMode.SqlLogin, null));
                Assert.AreEqual(status, error.Status);
            }
        }

        [TestMethod]
        public async Task Reader_MissingBoolean_IsNotTreatedAsSqlAuthentication()
        {
            using (var arm = new SqlArmFixture { EntraOnly = null })
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    SqlServerAuthReader.DetectAsync(arm.Server, "SyntheticPassword!", SqlServerAuthMode.SqlLogin, null));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DetectedAuthentication_SurvivesHeadlessPayloadAndEfConnection(bool entraOnly)
        {
            using (var arm = new SqlArmFixture { EntraOnly = entraOnly })
            {
                var config = new SolutionInstallConfig
                {
                    SQLServerAdminPassword = "SyntheticPassword!",
                    InstallerAccount = new AppRegistrationCredentials("synthetic-client", "synthetic-secret", Guid.Empty.ToString())
                };
                var decision = await SqlServerAuthReader.DetectAsync(arm.Server, config.SQLServerAdminPassword, SqlServerAuthMode.SqlLogin, null);
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var dbInfo = new DatabasePaaSInfo(server, database, config) { AuthMethod = decision.Method };
                var tasks = new SqlInstallerTasks(config, null, dbInfo, null, "Synthetic operator", null, null);
                var payload = tasks.CreateDatabaseUpgradeInfo(new List<string> { "https://contoso.sharepoint.com" });
                var received = DatabaseUpgradeInfo.GetFromBase64String(payload.ToBase64());

                Assert.AreEqual(dbInfo.ConnectionString, received.ConnectionString);
                CollectionAssert.AreEqual(payload.OrgURLs, received.OrgURLs);
                Assert.AreEqual(entraOnly, received.HasEntraCredential);
                var builder = new SqlConnectionStringBuilder(received.ConnectionString);
                Assert.AreEqual(entraOnly ? "" : "sqladmin", builder.UserID);
                Assert.AreEqual(entraOnly ? "" : config.SQLServerAdminPassword, builder.Password);
                if (entraOnly)
                    Assert.AreEqual(config.InstallerAccount.Secret, received.EntraClientSecret);
                else
                {
                    Assert.IsNull(received.EntraTenantId);
                    Assert.IsNull(received.EntraClientId);
                    Assert.IsNull(received.EntraClientSecret);
                }

                var credential = new FixedTokenCredential();
                AzureSqlTokenAuth.SetCredential(credential);
                using (var connection = AzureSqlTokenAuth.CreateConnection(received.ConnectionString))
                {
                    new AzureSqlAccessTokenInterceptor().Opening(connection, null);
                    Assert.AreEqual(entraOnly, !string.IsNullOrEmpty(connection.AccessToken));
                }
                Assert.AreEqual(entraOnly ? 2 : 0, credential.TokenRequests);
            }
        }

        [DataTestMethod]
        [DataRow(SqlConnectionAuthMethod.SqlLogin)]
        [DataRow(SqlConnectionAuthMethod.EntraId)]
        public void TestTarget_UsesDetectedAuthenticationNotPresenceOfOldPassword(SqlConnectionAuthMethod method)
        {
            var details = new AutodetectedSqlDetails.SqlDetails
            {
                SqlFqdn = "contoso-sql.database.windows.net",
                SqlUsername = "sqladmin",
                SqlPassword = "SyntheticPassword!",
                AuthMethod = method
            };
            var connection = new SqlConnectionStringBuilder(details.ConnectionString);
            Assert.AreEqual(method == SqlConnectionAuthMethod.EntraId, AzureSqlTokenAuth.NeedsAccessToken(details.ConnectionString));
            Assert.AreEqual(method == SqlConnectionAuthMethod.SqlLogin ? "sqladmin" : "", connection.UserID);
        }

        [TestMethod]
        public void Autodetection_DoesNotRequireSqlCredentialsBeforeReadingServer()
        {
            var config = new SolutionInstallConfig
            {
                InstallerAccount = new AppRegistrationCredentials("synthetic-client", "synthetic-secret", Guid.Empty.ToString()),
                Subscription = new AzureSubscription(Guid.Empty.ToString(), "Synthetic subscription"),
                ResourceGroupName = "contoso",
                SQLServerName = "contoso-sql"
            };
            Assert.IsTrue(SolutionInstallVerifier.ConfigIsReadyForSqlAutodetection(config));
            config.Subscription = null;
            Assert.IsFalse(SolutionInstallVerifier.ConfigIsReadyForSqlAutodetection(config));
        }

        [TestMethod]
        public void LoginFailure_ExplainsIdentityInsteadOfBlamingFirewall()
        {
            var entra = DatabasePaaSInfo.GetEntraIdConnectionString("contoso-sql.database.windows.net", "analytics");
            var sql = DatabasePaaSInfo.GetConnectionString("contoso-sql.database.windows.net", "analytics", "sqladmin", "SyntheticPassword!");
            StringAssert.Contains(BaseInstallProcess.GetSqlConnectionFailureGuidance(entra, 18456), "installer signs in as its app registration");
            StringAssert.Contains(BaseInstallProcess.GetSqlConnectionFailureGuidance(sql, 18456), "username/password");
            StringAssert.Contains(BaseInstallProcess.GetSqlConnectionFailureGuidance(sql, 40615), "network connectivity");
        }

        // Exercise the real SDK deserialisation and resource routes without any Azure calls.
        sealed class SqlArmFixture : HttpMessageHandler
        {
            const string ServerId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso/providers/Microsoft.Sql/servers/contoso-sql";
            readonly HttpClient _http;
            public bool? EmbeddedEntraOnly { get; set; }
            public bool? EntraOnly { get; set; } = false;
            public HttpStatusCode AuthenticationStatus { get; set; } = HttpStatusCode.OK;
            public HttpStatusCode AdministratorStatus { get; set; } = HttpStatusCode.NotFound;
            public int AuthenticationReads { get; private set; }
            public SqlServerResource Server { get; }

            public SqlArmFixture()
            {
                _http = new HttpClient(this, disposeHandler: false);
                var client = new ArmClient(new FixedTokenCredential(), Guid.Empty.ToString(), new ArmClientOptions
                {
                    Transport = new HttpClientTransport(_http),
                    Retry = { MaxRetries = 0 }
                });
                Server = client.GetSqlServerResource(new ResourceIdentifier(ServerId));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual(HttpMethod.Get, request.Method, "Detection must never change a server's authentication.");
                var path = request.RequestUri.AbsolutePath;
                var status = HttpStatusCode.OK;
                var properties = new JObject();
                if (path.EndsWith("/azureADOnlyAuthentications/Default", StringComparison.OrdinalIgnoreCase))
                {
                    AuthenticationReads++;
                    status = AuthenticationStatus;
                    if (EntraOnly.HasValue) properties["azureADOnlyAuthentication"] = EntraOnly.Value;
                }
                else if (path.EndsWith("/administrators/ActiveDirectory", StringComparison.OrdinalIgnoreCase))
                {
                    status = AdministratorStatus;
                    properties["sid"] = Guid.Empty.ToString();
                    properties["login"] = "Synthetic administrator";
                }
                else if (path.EndsWith("/databases/analytics", StringComparison.OrdinalIgnoreCase))
                {
                    properties["status"] = "Online";
                }
                else
                {
                    Assert.AreEqual(ServerId, path);
                    properties["administratorLogin"] = "sqladmin";
                    properties["fullyQualifiedDomainName"] = "contoso-sql.database.windows.net";
                    if (EmbeddedEntraOnly.HasValue)
                    {
                        properties["administrators"] = new JObject
                        {
                            ["sid"] = Guid.Empty.ToString(),
                            ["login"] = "Synthetic administrator",
                            ["azureADOnlyAuthentication"] = EmbeddedEntraOnly.Value
                        };
                    }
                }
                var body = status == HttpStatusCode.OK
                    ? new JObject { ["id"] = path, ["name"] = path.Substring(path.LastIndexOf('/') + 1), ["location"] = "westeurope", ["properties"] = properties }
                    : new JObject { ["error"] = new JObject { ["code"] = status.ToString(), ["message"] = "Synthetic ARM error" } };
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json") });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _http.Dispose();
                base.Dispose(disposing);
            }
        }

        sealed class FixedTokenCredential : TokenCredential
        {
            public int TokenRequests { get; private set; }
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                TokenRequests++;
                return new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));
            }
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }
    }
}
