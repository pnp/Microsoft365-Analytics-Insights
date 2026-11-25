using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class InstallerWebJobsCleanupTests
    {
        [DataTestMethod]
        [DataRow(null, null)]
        [DataRow("1", null)]
        [DataRow("1", "remove")]
        [DataRow("1", "restore:1")]
        [DataRow("0", "invalid")]
        public async Task Cleanup_RemovesCurrentAndLegacyPauseSettings_AndVerifiesWithoutChangingOtherSettings(string pause, string marker)
        {
            var stored = new AppServiceConfigurationDictionary();
            stored.Properties["ContosoUnmanagedSetting"] = "keep";
            if (pause != null) stored.Properties["webjobs_stopped"] = pause;
            if (marker != null) stored.Properties[ConfigureAzureComponentsTasks.UpgradeWebJobsStateSetting] = marker;
            var reads = 0;
            var writes = 0;
            var logger = new CapturingLogger();

            await ConfigureAzureComponentsTasks.EnsureWebJobsRunningAsync(ct =>
            {
                reads++;
                return Task.FromResult(Clone(stored));
            }, (settings, ct) =>
            {
                writes++;
                stored = Clone(settings);
                return Task.CompletedTask;
            }, logger, CancellationToken.None);

            Assert.AreEqual(pause != null || marker != null ? 1 : 0, writes);
            Assert.AreEqual(writes + 1, reads, "An ARM update must be followed by a fresh read, not trusted as verification.");
            CollectionAssert.AreEquivalent(new[] { "ContosoUnmanagedSetting" }, stored.Properties.Keys.ToArray());
            Assert.AreEqual("keep", stored.Properties["ContosoUnmanagedSetting"]);
            Assert.IsTrue(logger.Lines.Any(line => line.Message.Contains("outcome=Verified")));
        }

        [DataTestMethod]
        [DataRow("read")]
        [DataRow("write")]
        [DataRow("verification")]
        public async Task Cleanup_RetriesAReadOrWriteFailureOrASettingThatReappears(string failureAt)
        {
            var stored = new AppServiceConfigurationDictionary();
            stored.Properties["WEBJOBS_STOPPED"] = "1";
            var failed = false;
            var writes = 0;
            await ConfigureAzureComponentsTasks.EnsureWebJobsRunningAsync(ct =>
            {
                if (failureAt == "read" && !failed)
                {
                    failed = true;
                    throw new InvalidOperationException("Synthetic read failure.");
                }
                return Task.FromResult(Clone(stored));
            }, (settings, ct) =>
            {
                writes++;
                if (!failed)
                {
                    failed = true;
                    if (failureAt == "write") throw new InvalidOperationException("Synthetic write failure.");
                    if (failureAt == "verification") return Task.CompletedTask;
                }
                stored = Clone(settings);
                return Task.CompletedTask;
            }, new CapturingLogger(), CancellationToken.None);
            Assert.IsTrue(failed);
            Assert.IsFalse(stored.Properties.ContainsKey("WEBJOBS_STOPPED"));
            Assert.AreEqual(failureAt == "read" ? 1 : 2, writes);
        }

        [TestMethod]
        public async Task Cleanup_DoesNotClaimSuccessWhenAzureStillReportsThePause()
        {
            var stored = new AppServiceConfigurationDictionary();
            stored.Properties["WEBJOBS_STOPPED"] = "1";
            var writes = 0;
            var logger = new CapturingLogger();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                ConfigureAzureComponentsTasks.EnsureWebJobsRunningAsync(ct => Task.FromResult(Clone(stored)),
                    (settings, ct) => { writes++; return Task.CompletedTask; }, logger, CancellationToken.None));
            Assert.AreEqual(3, writes);
            Assert.IsFalse(logger.Lines.Any(line => line.Message.Contains("outcome=Verified")));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Deployment_CleanupFailureIsReportedWithoutMaskingTheOriginalError(bool deploymentFails)
        {
            var logger = new CapturingLogger();
            var original = new UnexpectedInstallException("Synthetic migration failure.");
            Func<Task> none = () => Task.CompletedTask;
            Func<Task> run = () => ConfigureAzureComponentsTasks.RunUpgradeDeploymentAsync(true, true, none,
                () => throw new InvalidOperationException("Synthetic cleanup failure."), none, none,
                () => deploymentFails ? Task.FromException(original) : Task.CompletedTask, none, logger);
            if (deploymentFails)
                Assert.AreSame(original, await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(run));
            else
                await run();
            Assert.IsTrue(logger.Lines.Any(line => line.Level == LogLevel.Error && line.Message.Contains("stage=resume outcome=Failed")));
            Assert.IsFalse(logger.Lines.Any(line => line.Message.Contains("stage=complete outcome=Succeeded")));
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task Installation_FinalCheckRunsAfterFailureOrCancellation_BeforeTheFinalSummary(bool cancel, bool cleanupFails)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var logger = new CapturingLogger();
                var installer = new FailingInstaller(logger, cancellation, cancel, cleanupFails);
                await installer.InstallOrUpdate(cancellation.Token);
                Assert.AreEqual(1, installer.CleanupCalls);
                var lines = logger.Lines.Select(line => line.Message).ToList();
                var cleanupIndex = lines.FindIndex(line => line == "Synthetic final cleanup.");
                var summaryIndex = lines.FindIndex(line => cancel
                    ? line.StartsWith("Install cancelled ") && line.Contains("before cancellation.")
                    : line.StartsWith("Completed with "));
                Assert.IsTrue(cleanupIndex >= 0 && summaryIndex > cleanupIndex, "Cleanup must precede every final status and summary.");
                Assert.AreEqual(cleanupFails, lines.Any(line => line.Contains("WebJobsCleanup: outcome=Failed")));
                Assert.IsTrue(lines.Any(line => line.Contains("Synthetic install failure.")), "Cleanup must preserve the original error.");
                if (cleanupFails)
                {
                    StringAssert.Contains(lines[summaryIndex], "2 error(s)");
                    Assert.IsTrue(lines.Any(line => line.Contains("WebJobsCleanup: outcome=Failed") && line.Contains("exceptionType=InvalidOperationException")));
                    Assert.IsFalse(lines.Any(line => line.Contains("Synthetic cleanup") || line.Contains("Synthetic inner detail")),
                        "Neither the final log nor its summary may echo arbitrary exception messages.");
                }
            }
        }

        private static AppServiceConfigurationDictionary Clone(AppServiceConfigurationDictionary source)
        {
            var clone = new AppServiceConfigurationDictionary();
            foreach (var setting in source.Properties) clone.Properties.Add(setting);
            return clone;
        }

        private sealed class FailingInstaller : SolutionInstaller
        {
            private readonly CancellationTokenSource _cancellation;
            private readonly bool _cancel;
            private readonly bool _cleanupFails;
            public int CleanupCalls;

            internal FailingInstaller(ILogger logger, CancellationTokenSource cancellation, bool cancel, bool cleanupFails)
                : base(new SolutionInstallConfig(), logger, new SoftwareReleaseConfig(), new InstallerProxyConfig(),
                    "contoso", "synthetic-password")
            {
                _cancellation = cancellation;
                _cancel = cancel;
                _cleanupFails = cleanupFails;
            }

            internal override SubscriptionResource ConnectToSubscription(CancellationToken ct)
            {
                if (_cancel) _cancellation.Cancel();
                throw new InvalidOperationException("Synthetic install failure.");
            }

            internal override Task CheckWebJobsAfterInstallAsync(WebSiteResource webApp, ILogger logger)
            {
                CleanupCalls++;
                logger.LogInformation("Synthetic final cleanup.");
                if (_cleanupFails) throw new InvalidOperationException("Synthetic cleanup failure.", new Exception("Synthetic inner detail."));
                return Task.CompletedTask;
            }
        }
    }
}
