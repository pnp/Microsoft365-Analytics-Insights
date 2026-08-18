using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers the import cadence gate (issue #161) - the decision logic that daily-gates the
    /// non-fresh Graph imports, and the in-memory last-run store fallback used when Redis is absent.
    /// </summary>
    [TestClass]
    public class ImportCadenceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 06, 25, 12, 00, 00, DateTimeKind.Utc);

        [TestMethod]
        public void ShouldRun_WhenNeverRun_ReturnsTrue()
        {
            Assert.IsTrue(ImportCadenceGate.ShouldRun(null, 24, force: false, nowUtc: Now));
        }

        [TestMethod]
        public void ShouldRun_WhenWithinInterval_ReturnsFalse()
        {
            Assert.IsFalse(ImportCadenceGate.ShouldRun(Now.AddHours(-1), 24, force: false, nowUtc: Now));
        }

        [TestMethod]
        public void ShouldRun_WhenIntervalElapsed_ReturnsTrue()
        {
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddHours(-25), 24, force: false, nowUtc: Now));
        }

        [TestMethod]
        public void ShouldRun_WhenExactlyAtInterval_ReturnsTrue()
        {
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddHours(-24), 24, force: false, nowUtc: Now),
                "Elapsed time >= interval should run.");
        }

        [TestMethod]
        public void ShouldRun_WhenIntervalZeroOrNegative_AlwaysRuns()
        {
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddMinutes(-1), 0, force: false, nowUtc: Now),
                "Interval 0 disables gating.");
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddMinutes(-1), -5, force: false, nowUtc: Now),
                "Negative interval disables gating.");
        }

        [TestMethod]
        public void ShouldRun_WhenForced_BypassesRecentRun()
        {
            Assert.IsTrue(ImportCadenceGate.ShouldRun(Now.AddMinutes(-1), 24, force: true, nowUtc: Now),
                "Force must override a recent run.");
        }

        [TestMethod]
        public async Task InMemoryStore_RoundTripsAndClears()
        {
            var store = new InMemoryImportLastRunStore();
            const string key = "GraphUsersMetadataLastImported";

            Assert.IsNull(await store.GetLastRunUtc(key), "An unseen key should be null.");

            var when = new DateTime(2026, 01, 02, 03, 04, 05, DateTimeKind.Utc);
            await store.SetLastRunUtc(key, when);

            var got = await store.GetLastRunUtc(key);
            Assert.IsNotNull(got);
            Assert.AreEqual(when, got.Value.ToUniversalTime());

            await store.Clear(key);
            Assert.IsNull(await store.GetLastRunUtc(key), "A cleared key should be null again.");
        }

        [TestMethod]
        public async Task InMemoryStore_KeysAreIndependent()
        {
            var store = new InMemoryImportLastRunStore();
            await store.SetLastRunUtc("a", Now);
            Assert.IsNull(await store.GetLastRunUtc("b"), "Distinct keys must not collide.");
        }
    }
}
