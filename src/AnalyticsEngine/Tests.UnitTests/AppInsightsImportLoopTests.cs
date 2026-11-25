using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    [TestClass]
    public class AppInsightsImportLoopTests
    {
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
