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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
                var tasks = new SqlInstallerTasks(config, null, dbInfo, null, "Synthetic operator", null);
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

        /// <summary>
        /// The schema upgrade and the registration give the downloaded app its credentials in a file: the command line,
        /// which process auditing records, carries only the file's name. The file is gone once the app has exited, and
        /// the configuration snapshot it registers carries no secret, not even an encrypted one.
        /// </summary>
        [TestMethod]
        public async Task DownloadedInstaller_GetsCredentialsInAFileThatIsDeletedAfterwards()
        {
            using (var arm = new SqlArmFixture())
            using (var child = new FakeDownloadedInstaller(readsPayloadFile: true))
            {
                child.SkipUnlessItCanRun();
                var config = SyntheticConfigWithSecrets();
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var dbInfo = new DatabasePaaSInfo(server, database, config);
                var tasks = new SqlInstallerTasks(config, child.Executable, dbInfo, new CapturingLogger(), "Synthetic operator",
                    connectionString => Task.FromResult(true));

                await tasks.UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(child.Executable, new InstallLogCapture());

                var launches = child.Launches();
                CollectionAssert.AreEqual(new[] { InstallerConstants.PARAM_INITDB, InstallerConstants.PARAM_REGISTERCONFIG },
                    launches.Select(l => l.Operation).ToArray());
                foreach (var launch in launches)
                {
                    foreach (var secret in new[] { config.SQLServerAdminPassword, config.InstallerAccount.Secret, config.ActivityAccount.Secret })
                    {
                        Assert.IsFalse(launch.CommandLine.Contains(secret) || launch.PayloadFile.Contains(secret),
                            $"{launch.Operation} put a secret on the command line.");
                    }
                    Assert.IsFalse(File.Exists(launch.PayloadFile), $"{launch.Operation} left its payload file behind.");
                }

                var upgrade = DatabaseUpgradeInfo.GetFromBase64String(child.PayloadSeenBy(launches[0]));
                Assert.AreEqual(dbInfo.ConnectionString, upgrade.ConnectionString, "The upgrade did not get its credentials in the file.");

                var registration = InstallStatus.GetFromBase64String(child.PayloadSeenBy(launches[1]));
                var snapshot = JObject.Parse(registration.ConfigurationJSon);
                Assert.IsFalse(snapshot.Descendants().OfType<JProperty>().Any(p => p.Name.EndsWith("Hash", StringComparison.Ordinal)),
                    "The registered snapshot carries encrypted secrets.");
                Assert.AreEqual("synthetic-client", (string)snapshot["InstallerAccount"]["ClientId"]);
            }
        }

        /// <summary>
        /// A downloaded release older than the installer can take the credentials only on its command line, so it is not
        /// run at all, and the operator is told why.
        /// </summary>
        [TestMethod]
        public async Task DownloadedInstallerTooOldForThePayloadFile_IsNotRun()
        {
            using (var arm = new SqlArmFixture())
            using (var child = new FakeDownloadedInstaller(readsPayloadFile: false))
            {
                var config = SyntheticConfigWithSecrets();
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var tasks = new SqlInstallerTasks(config, child.Executable, new DatabasePaaSInfo(server, database, config), new CapturingLogger(),
                    "Synthetic operator", connectionString => Task.FromResult(true));

                var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => tasks.InitDatabaseSchema(new List<string>()));

                Assert.AreEqual(SqlInstallerTasks.DownloadedBuildTooOldForPayloadFile, ex.Message);
                Assert.AreEqual(0, child.Launches().Count, "The older app was started anyway.");
            }
        }

        /// <summary>
        /// The same refusal, made by the upgrade's preflight before the WebJobs are paused and the site stopped, so a refused
        /// upgrade leaves the imports running. A current build passes it.
        /// </summary>
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task PreflightCheck_RefusesOnlyABuildTooOldForThePayloadFile(bool readsPayloadFile)
        {
            using (var arm = new SqlArmFixture())
            using (var child = new FakeDownloadedInstaller(readsPayloadFile))
            {
                var config = SyntheticConfigWithSecrets();
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var tasks = new SqlInstallerTasks(config, child.Executable, new DatabasePaaSInfo(server, database, config), new CapturingLogger(),
                    "Synthetic operator", connectionString => Task.FromResult(true));

                if (readsPayloadFile)
                {
                    await tasks.CheckDownloadedBuildBeforeUpgradeAsync();
                }
                else
                {
                    var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => tasks.CheckDownloadedBuildBeforeUpgradeAsync());
                    Assert.AreEqual(SqlInstallerTasks.DownloadedBuildTooOldForPayloadFile, ex.Message);
                }
                Assert.AreEqual(0, child.Launches().Count, "The preflight only inspects the build; it never runs it.");
            }
        }

        static SolutionInstallConfig SyntheticConfigWithSecrets() => new SolutionInstallConfig
        {
            SQLServerAdminUsername = "sqladmin",
            SQLServerAdminPassword = "Synthetic-Sql-P@ss1",
            InstallerAccount = new AppRegistrationCredentials("synthetic-client", "Synthetic-Installer-Secret1", Guid.Empty.ToString()),
            ActivityAccount = new AppRegistrationCredentials("synthetic-runtime", "Synthetic-Runtime-Secret1", Guid.Empty.ToString()),
        };

        /// <summary>
        /// Stands in for the downloaded control-panel app: a batch file that records its command line and copies the
        /// payload files it could see, so a test can check what the real app would have received.
        /// </summary>
        sealed class FakeDownloadedInstaller : IDisposable
        {
            readonly DirectoryInfo _folder;

            public FileInfo Executable { get; }

            public FakeDownloadedInstaller(bool readsPayloadFile)
            {
                _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AnalyticsInstallerTest-" + Guid.NewGuid().ToString("N")));

                // What the installer's probe looks for in a downloaded release.
                File.WriteAllText(Path.Combine(_folder.FullName, "App.ControlPanel.Engine.dll"), "synthetic assembly " +
                    (readsPayloadFile ? DownloadedBuildCapabilityProbe.InitDbPayloadFileMarker : "from an older release"));

                Executable = new FileInfo(Path.Combine(_folder.FullName, "AnalyticsInstaller.cmd"));
                File.WriteAllText(Executable.FullName, string.Join("\r\n",
                    "@echo off",
                    ">>\"%~dp0args.txt\" echo %*",
                    "if not exist \"%~dp0copies\" mkdir \"%~dp0copies\"",
                    "copy /y \"%TMP%\\AnalyticsInstaller-*.tmp\" \"%~dp0copies\\\" >nul 2>nul",
                    "copy /y \"%TEMP%\\AnalyticsInstaller-*.tmp\" \"%~dp0copies\\\" >nul 2>nul",
                    "exit /b 0",
                    ""));
            }

            /// <summary>Inconclusive where application control won't run a batch file from the temp folder.</summary>
            public void SkipUnlessItCanRun()
            {
                try
                {
                    using (var process = Process.Start(new ProcessStartInfo(Executable.FullName, "--canary x")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }))
                    {
                        process.WaitForExit();
                    }
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Assert.Inconclusive($"This machine won't run the stand-in installer: {ex.Message}");
                }
                File.Delete(Path.Combine(_folder.FullName, "args.txt"));
            }

            public IReadOnlyList<Launch> Launches()
            {
                var log = Path.Combine(_folder.FullName, "args.txt");
                if (!File.Exists(log)) return new Launch[0];

                return File.ReadAllLines(log).Where(line => line.Trim().Length > 0).Select(line =>
                {
                    var parts = line.Trim().Split(' ');
                    return new Launch(parts[0], line, Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])));
                }).ToList();
            }

            /// <summary>What the app could read from the payload file while it ran.</summary>
            public string PayloadSeenBy(Launch launch) =>
                File.ReadAllText(Path.Combine(_folder.FullName, "copies", Path.GetFileName(launch.PayloadFile)));

            public void Dispose()
            {
                try { _folder.Delete(true); } catch (IOException) { }
            }
        }

        sealed class Launch
        {
            public Launch(string operation, string commandLine, string payloadFile)
            {
                Operation = operation;
                CommandLine = commandLine;
                PayloadFile = payloadFile;
            }

            public string Operation { get; }
            public string CommandLine { get; }
            public string PayloadFile { get; }
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
        internal sealed class SqlArmFixture : HttpMessageHandler
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
