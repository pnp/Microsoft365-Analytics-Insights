using Common.Entities.Config;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    [TestClass]
    public class AppInsightsImportLoopTests
    {
        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task OneShotSqlPreflight_FailsForMissingTableAndAcceptsAnEmptyHitsTable()
        {
            using (var db = ScratchDatabase.Create("OneShotPreflight"))
            {
                var settings = new AppConnectionStrings(name => name == "SPOInsightsEntities" ? db.ConnectionString : null);
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                Assert.IsFalse(await settings.TestWebTrafficSqlOnceAsync(logger));
                db.Execute("CREATE TABLE dbo.hits (id int PRIMARY KEY)");
                Assert.IsTrue(await settings.TestWebTrafficSqlOnceAsync(logger));
            }
        }

        [TestMethod]
        [TestCategory("SqlIntegration")]
        public async Task OneShotSqlPreflight_HostStopInterruptsABlockedReadWithoutReportingAnOutage()
        {
            using (var db = ScratchDatabase.Create("OneShotStopping"))
            using (var locker = new SqlConnection(db.ConnectionString))
            using (var stopping = new CancellationTokenSource())
            {
                db.Execute("CREATE TABLE dbo.hits (id int PRIMARY KEY)");
                await locker.OpenAsync();
                using (var transaction = locker.BeginTransaction())
                using (var schemaLock = new SqlCommand("ALTER TABLE dbo.hits ADD hold int NULL;", locker, transaction))
                {
                    await schemaLock.ExecuteNonQueryAsync();
                    var logger = new CapturingLogger();
                    var settings = new AppConnectionStrings(name => name == "SPOInsightsEntities" ? db.ConnectionString : null);
                    var probe = settings.TestWebTrafficSqlOnceAsync(logger, stopping.Token);
                    Assert.IsFalse(probe.IsCompleted, "The schema lock must keep the read from completing.");
                    stopping.Cancel();
                    Assert.AreSame(probe, await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(10))),
                        "Do not wait for the 30-second SQL deadline when Kudu requests shutdown.");
                    Assert.IsFalse(await probe);
                    Assert.AreEqual(0, logger.Lines.Count, "An intentional stop is not a SQL outage.");
                }
            }
        }

        [DataTestMethod]
        [DataRow(AppInsightsCycleOutcome.Succeeded, 0)]
        [DataRow(AppInsightsCycleOutcome.Degraded, 0)]
        [DataRow(AppInsightsCycleOutcome.Failed, HostRuntime.FatalExitCode)]
        public void GuardedOneShot_ReturnsTheCycleResultWithoutRetry(AppInsightsCycleOutcome outcome, int expectedExitCode)
        {
            int cycles = 0, pauses = 0;
            var code = HostRuntime.RunGuarded("Test", () => AppInsightsImportLoop.Run(true,
                () => { cycles++; return outcome; }, () => pauses++, () => cycles > 1));

            Assert.AreEqual(expectedExitCode, code);
            Assert.AreEqual(1, cycles);
            Assert.AreEqual(0, pauses);
        }

        [TestMethod]
        public void ContinuousJob_RetriesAFailedCycleAndExitsCleanlyOnShutdown()
        {
            int cycles = 0, pauses = 0;
            var code = AppInsightsImportLoop.Run(false,
                () => ++cycles == 1 ? AppInsightsCycleOutcome.Failed : AppInsightsCycleOutcome.Succeeded,
                () => pauses++, () => pauses == 2 || cycles > 2);

            Assert.AreEqual(0, code);
            Assert.AreEqual(2, cycles);
            Assert.AreEqual(2, pauses);
        }

        [TestMethod]
        public void GuardedHost_PreservesTheExitCodeAndDistinguishesShutdownFromOtherExceptions()
        {
            Func<int> stop = () => throw new ShutdownRequestedException();
            Func<int> fail = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.AreEqual(7, HostRuntime.RunGuarded("Test", () => 7));
            Assert.AreEqual(7, HostRuntime.RunGuarded("Test", () => Task.FromResult(7)));
            Assert.AreEqual(0, HostRuntime.RunGuarded("Test", stop));
            Assert.AreEqual(HostRuntime.FatalExitCode, HostRuntime.RunGuarded("Test", fail));

            // The Office 365 host still supplies a Task rather than an exit code.
            Assert.AreEqual(0, HostRuntime.RunGuarded("Test", () => Task.CompletedTask));
            Assert.AreEqual(0, HostRuntime.RunGuarded("Test", () => Task.FromException(new ShutdownRequestedException())));
            Assert.AreEqual(HostRuntime.FatalExitCode, HostRuntime.RunGuarded("Test", () => Task.FromException(new InvalidOperationException("Synthetic failure."))));
        }
    }
}
