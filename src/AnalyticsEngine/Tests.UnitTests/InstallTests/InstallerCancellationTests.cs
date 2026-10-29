using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Models;
using App.ControlPanel.Engine.SPO.AppCatalog;
using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Engine.SPO.Rest;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class InstallerCancellationTests
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NestedJobs_CancelTheRunningTaskAndNeverStartTheNext(bool critical)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var active = new WaitingTask(critical);
                var next = new FakeMarkerTask(TaskConfig.NoConfig, NullLogger.Instance);
                var child = new FakeSequentialJob(NullLogger.Instance);
                child.AddTask(active, next);
                var parent = new FakeSequentialJob(NullLogger.Instance) { CancellationToken = cancellation.Token };
                parent.AddTask(new RunChildJobTask(child, NullLogger.Instance));
                await CancelRunningTask(() => parent.Install(), active.Started.Task, cancellation, active.Release);
                Assert.IsFalse(next.WasRun, "Optional tasks must not swallow cancellation either.");
            }
        }

        [TestMethod]
        public async Task ContainerLoader_ReceivesCancellationBeforeAnyTaskStarts()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var loader = new WaitingTask(true);
                var job = new LoaderJob(loader) { CancellationToken = cancellation.Token };
                await CancelRunningTask(() => job.Install(), loader.Started.Task, cancellation, loader.Release);
                Assert.IsFalse(job.HasRun);
            }
        }

        [TestMethod]
        public async Task CancelledContainerLoader_NeverStarts()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var loader = new WaitingTask(true);
                var job = new LoaderJob(loader) { CancellationToken = cancellation.Token };
                cancellation.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => job.Install());
                Assert.IsFalse(loader.Started.Task.IsCompleted);
            }
        }

        [DataTestMethod]
        [DataRow("pause", "pause")]
        [DataRow("stop", "pause,stop,start")]
        [DataRow("schema", "pause,stop,schema,start")]
        [DataRow("content", "pause,stop,schema,start,content")]
        public async Task UpgradeCancellation_RestartsStoppedSiteButSkipsSubsequentWork(string cancelAt, string expected)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var stages = new List<string>();
                Func<string, Task> stage = name =>
                {
                    stages.Add(name);
                    if (name == cancelAt) cancellation.Cancel();
                    return Task.CompletedTask;
                };
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                    ConfigureAzureComponentsTasks.RunUpgradeDeploymentAsync(true, true,
                        () => stage("pause"), () => stage("resume"), () => stage("stop"), () => stage("start"),
                        () => stage("schema"), () => stage("content"), NullLogger.Instance, cancellation.Token));
                Assert.AreEqual(expected, string.Join(",", stages));
            }
        }

        [DataTestMethod]
        [DataRow("azure")]
        [DataRow("sharepoint")]
        [DataRow("appcatalog")]
        [DataRow("kudu")]
        public async Task InFlightRequests_ObserveCancellationWithoutRetryingOrWrappingIt(string kind)
        {
            using (var cancellation = new CancellationTokenSource())
            using (var handler = new WaitingHandler())
            using (var http = new HttpClient(handler))
            {
                Func<Task> run;
                if (kind == "azure")
                {
                    var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
                    options.Retry.MaxRetries = 0;
                    var group = new ArmClient(new SyntheticCredential(), "00000000-0000-0000-0000-000000000000", options)
                        .GetResourceGroupResource(new ResourceIdentifier(
                            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/Contoso"));
                    var task = new StorageAccountInstallTask(TaskConfig.GetConfigForName("contosoanalytics"),
                        NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>())
                    { Container = group, CancellationToken = cancellation.Token };
                    run = () => task.ExecuteTask(null);
                }
                else if (kind == "sharepoint")
                {
                    var rest = new SpoRestClient(new SyntheticAuthenticator(), NullLogger.Instance, http)
                    { CancellationToken = cancellation.Token };
                    run = () => rest.GetAsync("https://contoso.sharepoint.com/_api/web");
                }
                else if (kind == "appcatalog")
                {
                    var catalog = new TenantAppCatalogManager(new SyntheticAuthenticator(), NullLogger.Instance, http)
                    { CancellationToken = cancellation.Token };
                    run = () => catalog.DeployAsync("https://contoso.sharepoint.com/sites/catalog", Guid.Empty);
                }
                else
                {
                    var task = new InstallAppServiceContentsTask(new App.ControlPanel.Engine.Entities.InstallerProxyConfig(),
                        TaskConfig.NoConfig, NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>(), null)
                    { CancellationToken = cancellation.Token };
                    run = () => (Task)typeof(InstallAppServiceContentsTask)
                        .GetMethod("WaitForDeploymentAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(task, new object[] { http, new Uri("https://contoso.scm.azurewebsites.net/api/deployments/latest") });
                }
                await CancelRunningTask(run, handler.Started.Task, cancellation, handler.Release);
                Assert.AreEqual(1, handler.Requests, "A cancelled request must not be retried.");
            }
        }

        [TestMethod]
        public async Task DownloadedInstaller_IsStoppedAndReapedWhenCancelled()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                try
                {
                    var wait = SqlInstallerTasks.WaitForChildExitAsync(process, cancellation.Token, NullLogger.Instance);
                    cancellation.Cancel();
                    Assert.AreSame(wait, await Task.WhenAny(wait, Task.Delay(5000)));
                    try { await wait; Assert.Fail("Expected cancellation."); }
                    catch (OperationCanceledException) { }
                    Assert.IsTrue(process.HasExited, "The child must not continue a database upgrade after returning.");
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ReleaseExtraction_PreservesUnicodeAndDoesNotTouchFilesWhenAlreadyCancelled(bool cancel)
        {
            var name = "ContosoRelease-" + Guid.NewGuid().ToString("N");
            var zipPath = Path.Combine(Path.GetTempPath(), name + ".zip");
            var extractionPath = Path.Combine(DataUtils.StringUtils.TempDirPath, name);
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        zip.CreateEntry("content/");
                        zip.CreateEntry("content/empty/");
                        using (var writer = new StreamWriter(zip.CreateEntry("content/Καλημέρα κόσμε.txt").Open()))
                            writer.Write("Καλημέρα κόσμε");
                    }
                    Directory.CreateDirectory(extractionPath);
                    var sentinel = Path.Combine(extractionPath, "previous.txt");
                    File.WriteAllText(sentinel, "previous release");
                    var source = new LocalStorageBlobInfo { FileLocation = zipPath };
                    if (cancel)
                    {
                        cancellation.Cancel();
                        Assert.ThrowsException<OperationCanceledException>(() =>
                            ZipFileTasks.Unzip(source, NullLogger.Instance, cancellation.Token));
                        Assert.AreEqual("previous release", File.ReadAllText(sentinel));
                    }
                    else
                    {
                        var result = ZipFileTasks.Unzip(source, NullLogger.Instance, cancellation.Token);
                        Assert.AreEqual("Καλημέρα κόσμε", File.ReadAllText(Path.Combine(result.FullName, "Καλημέρα κόσμε.txt")));
                        Assert.IsTrue(Directory.Exists(Path.Combine(result.FullName, "empty")));
                        Assert.IsFalse(File.Exists(sentinel));
                    }
                }
                finally
                {
                    File.Delete(zipPath);
                    if (Directory.Exists(extractionPath)) Directory.Delete(extractionPath, true);
                }
            }
        }

        // What a Cancel and a real failure each look like in the install log. The first step of a run is the Azure
        // sign-in; these tests swap it for something that throws, so the real handling in InstallOrUpdate is what runs.

        [TestMethod]
        public async Task CancelWhileASqlCommandIsRunning_IsReportedAsCancelledNotFatal()
        {
            // The real driver and a real in-flight cancel. SqlClient reports it as a SqlException ("Operation
            // cancelled by user") rather than an OperationCanceledException - which is all the installer used to
            // recognise - so pressing Cancel during a database step was logged as an unexpected FATAL error.
            using (var db = ScratchDatabase.Create("InstallCancel"))
            using (var cancellation = new CancellationTokenSource())
            {
                var logger = new CapturingLogger();
                var installer = new InstallerWhoseSignInRuns(logger, ct =>
                {
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
                    RunSql(db.ConnectionString, "WAITFOR DELAY '00:00:30';", ct);
                });

                await installer.InstallOrUpdate(cancellation.Token);

                AssertReportedAsCancelled(logger, $"The interrupted call threw {installer.Thrown?.GetType().Name}.");
            }
        }

        [TestMethod]
        public async Task AnyFailureAfterCancelWasRequested_IsReportedAsCancelled_AndKeepsWhatTheCallSaid()
        {
            // The same decision without a database: whatever type the interrupted call throws, the token says
            // the user pressed Cancel.
            using (var cancellation = new CancellationTokenSource())
            {
                var logger = new CapturingLogger();
                var installer = new InstallerWhoseSignInRuns(logger, ct =>
                {
                    cancellation.Cancel();
                    throw new InvalidOperationException("A severe error occurred on the current command.");
                });

                await installer.InstallOrUpdate(cancellation.Token);

                AssertReportedAsCancelled(logger, "");
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("[InvalidOperationException] A severe error occurred on the current command.")),
                    "The log must keep what the interrupted call reported, in case a real failure landed at the same moment.");
            }
        }

        [TestMethod]
        public async Task CancelBeforeTheRunStarts_IsStillReportedAsCancelled_AndNothingStarts()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var logger = new CapturingLogger();
                var installer = new InstallerWhoseSignInRuns(logger, ct => throw new InvalidOperationException("Must not be reached."));

                await installer.InstallOrUpdate(cancellation.Token);

                Assert.AreEqual(0, installer.SignIns, "Nothing may start once Cancel has been pressed.");
                AssertReportedAsCancelled(logger, "");
            }
        }

        [TestMethod]
        public async Task AFailureThatIsNotACancel_IsStillFatal()
        {
            var logger = new CapturingLogger();
            var installer = new InstallerWhoseSignInRuns(logger, ct => throw new InvalidOperationException("Synthetic failure."));

            await installer.InstallOrUpdate(CancellationToken.None);

            AssertReportedAsFatal(logger, nameof(InvalidOperationException));
        }

        [TestMethod]
        public async Task ASqlFailureThatIsNotACancel_IsStillFatal()
        {
            // A SqlException on its own is not a cancel: the exception type must never be what decides it.
            using (var db = ScratchDatabase.Create("InstallFail"))
            {
                var logger = new CapturingLogger();
                var installer = new InstallerWhoseSignInRuns(logger,
                    ct => RunSql(db.ConnectionString, "RAISERROR('Synthetic failure.', 16, 1);", ct));

                await installer.InstallOrUpdate(CancellationToken.None);

                Assert.IsInstanceOfType(installer.Thrown, typeof(SqlException));
                AssertReportedAsFatal(logger, nameof(SqlException));
            }
        }

        static void RunSql(string connectionString, string sql, CancellationToken ct)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQueryAsync(ct).GetAwaiter().GetResult();
                }
            }
        }

        static string Describe(CapturingLogger logger)
            => string.Join(Environment.NewLine, logger.Lines.Select(l => $"{l.Level}: {l.Message}"));

        static void AssertReportedAsCancelled(CapturingLogger logger, string context)
        {
            var log = context + Environment.NewLine + Describe(logger);
            Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("FATAL")), "A Cancel is not a fatal error." + Environment.NewLine + log);
            Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.StartsWith("Install cancelled by user")), log);
            Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("before cancellation")), "The closing status line must say it was cancelled." + Environment.NewLine + log);
        }

        static void AssertReportedAsFatal(CapturingLogger logger, string exceptionType)
        {
            var log = Describe(logger);
            Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Error && l.Message.StartsWith($"FATAL: Unexpected error of type '{exceptionType}'")), log);
            Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("Install cancelled") || l.Message.Contains("before cancellation")),
                "A genuine failure must not be reported as a cancel." + Environment.NewLine + log);
        }

        static async Task CancelRunningTask(Func<Task> run, Task started, CancellationTokenSource cancellation,
            TaskCompletionSource<bool> release)
        {
            var operation = run();
            try
            {
                Assert.AreSame(started, await Task.WhenAny(started, operation, Task.Delay(5000)), "The synthetic operation did not start.");
                cancellation.Cancel();
                Assert.AreSame(operation, await Task.WhenAny(operation, Task.Delay(3000)), "Cancellation did not interrupt the running task.");
                try { await operation; Assert.Fail("Expected cancellation."); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                release.TrySetResult(true);
                try { await operation; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>An installer whose Azure sign-in - the first step of a run - runs the given action instead.</summary>
        sealed class InstallerWhoseSignInRuns : SolutionInstaller
        {
            readonly Action<CancellationToken> _signIn;
            public int SignIns;
            public Exception Thrown;

            public InstallerWhoseSignInRuns(ILogger logger, Action<CancellationToken> signIn)
                : base(new SolutionInstallConfig(), logger, new SoftwareReleaseConfig(), new InstallerProxyConfig(), "contoso", "synthetic-password")
            {
                _signIn = signIn;
            }

            internal override SubscriptionResource ConnectToSubscription(CancellationToken ct)
            {
                SignIns++;
                try { _signIn(ct); }
                catch (Exception ex) { Thrown = ex; throw; }
                throw new InvalidOperationException("The sign-in under test is meant to throw.");
            }
        }

        sealed class WaitingTask : ResourceInstallTask<object>
        {
            readonly bool _critical;
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public WaitingTask(bool critical) : base(TaskConfig.NoConfig, NullLogger.Instance) { _critical = critical; }
            public override bool IsCritical => _critical;
            public override async Task<object> ExecuteTaskReturnResult(object contextArg)
            {
                Started.TrySetResult(true);
                using (CancellationToken.Register(() => Release.TrySetCanceled()))
                    await Release.Task;
                return new object();
            }
        }

        sealed class LoaderJob : InstallJobInContainerJob<object>
        {
            public LoaderJob(WaitingTask loader) : base(NullLogger.Instance, loader) { }
        }

        sealed class WaitingHandler : HttpMessageHandler
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Requests;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                Started.TrySetResult(true);
                using (cancellationToken.Register(() => Release.TrySetCanceled()))
                    await Release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
        }

        sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new AccessToken("synthetic", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        sealed class SyntheticAuthenticator : ISpoAuthenticator
        {
            public Task<string> GetAccessTokenAsync(string siteUrl) => Task.FromResult("synthetic");
            public void Dispose() { }
        }
    }
}
