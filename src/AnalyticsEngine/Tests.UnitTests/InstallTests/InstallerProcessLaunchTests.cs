using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class InstallerProcessLaunchTests
    {
        [DataTestMethod]
        [DataRow(true, true, 5)]
        [DataRow(true, false, 1260)]
        [DataRow(false, true, 577)]
        [DataRow(false, false, 5)]
        public async Task BlockedDownloadedInstaller_ContinuesDeploymentAndRetainsPauseOnlyForDeferredSchema(
            bool upgradeSchema, bool installContent, int nativeError)
        {
            using (var arm = new SqlAuthenticationDetectionTests.SqlArmFixture())
            {
                var config = new SolutionInstallConfig { SQLServerAdminPassword = "synthetic-password" };
                config.TasksConfig.UpgradeSchema = upgradeSchema;
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var logger = new CapturingLogger();
                var tasks = new LaunchControlledTasks(config, new DatabasePaaSInfo(server, database, config), logger,
                    process => { throw new Win32Exception(nativeError); });
                var stages = new List<string>();
                var settings = new Dictionary<string, string>();
                Func<string, Task> stage = name => { stages.Add(name); return Task.CompletedTask; };

                await ConfigureAzureComponentsTasks.RunUpgradeDeploymentAsync(installContent, upgradeSchema,
                    () => { ConfigureAzureComponentsTasks.PauseUpgradeWebJobs(settings); return stage("pause"); },
                    () => { ConfigureAzureComponentsTasks.ResumeUpgradeWebJobs(settings); return stage("resume"); },
                    () => stage("stop"), () => stage("start"),
                    () => tasks.UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(tasks.Executable, new List<InstallLogEventArgs>()),
                    () => stage("content"), logger, schemaUpgradeDeferred: () => tasks.SchemaUpgradeDeferred);
                stages.Add("remaining-tasks");

                Assert.AreEqual(upgradeSchema ? 2 : 1, tasks.Launches, "Registration is independent of the schema checkbox.");
                Assert.AreEqual(upgradeSchema, tasks.SchemaUpgradeDeferred);
                Assert.AreEqual(installContent, stages.Contains("content"));
                Assert.AreEqual(installContent && !upgradeSchema, stages.Contains("resume"));
                Assert.AreEqual(upgradeSchema, settings.TryGetValue("WEBJOBS_STOPPED", out var stopped) && stopped == "1");
                Assert.AreEqual("remaining-tasks", stages.Last());
                Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("not registered")));
                Assert.AreEqual(upgradeSchema, logger.Lines.Any(l => l.Level == LogLevel.Error && l.Message.Contains("Database initialisation did not run")));
                Assert.IsFalse(logger.Lines.Any(l => l.Level == LogLevel.Critical));
                Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("synthetic-password")));
                if (upgradeSchema)
                {
                    Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("outcome=Succeeded")));
                    Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("outcome=Deferred")));
                    StringAssert.Contains(logger.Lines.Single(l => l.Level == LogLevel.Error).Message, "manual SQL");
                }
                Assert.IsFalse(File.Exists(tasks.RegistrationFile), "The temporary registration payload must be removed after a blocked launch.");
            }
        }

        [TestMethod]
        public async Task RunningMigrationFailure_RemainsFatalAndDoesNotDeployContent()
        {
            using (var arm = new SqlAuthenticationDetectionTests.SqlArmFixture())
            {
                var config = new SolutionInstallConfig { SQLServerAdminPassword = "synthetic-password" };
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var logger = new CapturingLogger();
                var tasks = new LaunchControlledTasks(config, new DatabasePaaSInfo(server, database, config), logger, process =>
                {
                    process.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                    process.StartInfo.Arguments = "-NoProfile -NonInteractive -Command \"Write-Output 'Synthetic migration failure'; exit 7\"";
                    process.Start();
                });
                var deployed = false;
                var resumed = false;
                var restarted = false;
                await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() =>
                    ConfigureAzureComponentsTasks.RunUpgradeDeploymentAsync(true, true,
                        () => Task.CompletedTask, () => { resumed = true; return Task.CompletedTask; },
                        () => Task.CompletedTask, () => { restarted = true; return Task.CompletedTask; },
                        () => tasks.UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(tasks.Executable, new List<InstallLogEventArgs>()),
                        () => { deployed = true; return Task.CompletedTask; }, logger,
                        schemaUpgradeDeferred: () => tasks.SchemaUpgradeDeferred));
                Assert.IsFalse(tasks.SchemaUpgradeDeferred);
                Assert.IsTrue(restarted);
                Assert.IsFalse(deployed);
                Assert.IsFalse(resumed);
                Assert.AreEqual(1, tasks.Launches, "Registration must not follow a failed migration.");
            }
        }

        [TestMethod]
        public async Task CancelDuringBlockedLaunch_DoesNotContinueToRegistration()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var arm = new SqlAuthenticationDetectionTests.SqlArmFixture())
            {
                var config = new SolutionInstallConfig { SQLServerAdminPassword = "synthetic-password" };
                var server = (await arm.Server.GetAsync()).Value;
                var database = (await server.GetSqlDatabases().GetAsync("analytics")).Value;
                var tasks = new LaunchControlledTasks(config, new DatabasePaaSInfo(server, database, config), new CapturingLogger(),
                    process => { cancellation.Cancel(); throw new Win32Exception(5); })
                { CancellationToken = cancellation.Token };
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                    tasks.UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(tasks.Executable, new List<InstallLogEventArgs>()));
                Assert.AreEqual(1, tasks.Launches);
                Assert.IsFalse(tasks.SchemaUpgradeDeferred);
            }
        }

        [DataTestMethod]
        [DataRow("win32")]
        [DataRow("access")]
        [DataRow("security")]
        public void BlockedBrowserLaunch_LogsManualUrlAndDoesNotThrow(string failure)
        {
            var logger = new CapturingLogger();
            const string url = "https://contoso.azurewebsites.net/";
            SolutionInstaller.OpenAdminSite(logger, url, target =>
            {
                Assert.AreEqual(url, target);
                if (failure == "win32") throw new Win32Exception(5);
                if (failure == "access") throw new UnauthorizedAccessException();
                throw new SecurityException();
            });
            StringAssert.Contains(logger.Single(LogLevel.Warning), url);
            StringAssert.Contains(logger.Single(LogLevel.Warning), "manually");
        }

        private sealed class LaunchControlledTasks : SqlInstallerTasks
        {
            private readonly Action<Process> _launch;
            internal FileInfo Executable { get; } = new FileInfo(@"C:\Contoso\AnalyticsInstaller.exe");
            internal int Launches { get; private set; }
            internal string RegistrationFile { get; private set; }

            internal LaunchControlledTasks(SolutionInstallConfig config, DatabasePaaSInfo database, ILogger logger, Action<Process> launch)
                : base(config, new FileInfo(@"C:\Contoso\AnalyticsInstaller.exe"), database, logger,
                    "Synthetic operator", "synthetic-password", connection => Task.FromResult(true))
            {
                _launch = launch;
            }

            internal override void StartChildProcess(Process process)
            {
                Launches++;
                if (process.StartInfo.Arguments.StartsWith(InstallerConstants.PARAM_REGISTERCONFIG + " ", StringComparison.Ordinal))
                {
                    var encoded = process.StartInfo.Arguments.Substring(InstallerConstants.PARAM_REGISTERCONFIG.Length + 1);
                    RegistrationFile = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    Assert.IsTrue(File.Exists(RegistrationFile));
                }
                _launch(process);
            }
        }
    }
}
