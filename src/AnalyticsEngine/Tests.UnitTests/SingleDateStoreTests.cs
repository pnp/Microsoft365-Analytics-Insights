using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the once-a-day throttle store that gates the whole activity/usage-report phase, and the
    /// factory that picks the durable state table vs the in-memory fallback (used when no Storage connection
    /// string is configured).
    /// </summary>
    [TestClass]
    public class SingleDateStoreTests
    {
        [TestMethod]
        public async Task InMemorySingleDateStore_GetLastDT_IsNullUntilSaved()
        {
            var store = new InMemorySingleDateStore();
            Assert.IsNull(await store.GetLastDT(),
                "A fresh in-memory store must report no last date so the first cycle imports.");
        }

        [TestMethod]
        public async Task InMemorySingleDateStore_SaveDT_ThenGetReturnsRecentDate()
        {
            var store = new InMemorySingleDateStore();

            var before = DateTime.Now.AddSeconds(-1);
            await store.SaveDT();
            var after = DateTime.Now.AddSeconds(1);

            var stored = await store.GetLastDT();
            Assert.IsTrue(stored.HasValue, "SaveDT must persist a value for the life of the instance.");
            Assert.IsTrue(stored.Value >= before && stored.Value <= after,
                "SaveDT should store roughly the current time.");
        }

        [TestMethod]
        public async Task InMemorySingleDateStore_DeleteDt_ClearsStoredDate()
        {
            var store = new InMemorySingleDateStore();
            await store.SaveDT();

            await store.DeleteDt();

            Assert.IsNull(await store.GetLastDT(),
                "DeleteDt must clear the stored date so an empty/wiped DB re-imports immediately.");
        }

        [TestMethod]
        public async Task InMemorySingleDateStore_SurvivesRepeatedReads_SoThrottleHoldsAcrossCycles()
        {
            // A single instance is held for the process lifetime (constructed once, outside the cycle loop),
            // so repeated GetLastDT calls across cycles keep seeing the saved timestamp and keep throttling.
            var store = new InMemorySingleDateStore();
            await store.SaveDT();

            var first = await store.GetLastDT();
            var second = await store.GetLastDT();

            Assert.IsTrue(first.HasValue && second.HasValue);
            Assert.AreEqual(first.Value, second.Value,
                "The stored date must be stable across reads so the once-a-day gate stays closed within the window.");
        }

        [TestMethod]
        public void Factory_NoStorage_ReturnsInMemoryStore()
        {
            var config = ConfigWithStorage(null);

            var store = ActivityReportsLastImportedStoreFactory.Create(config, AnalyticsLogger.ConsoleOnlyTracer());

            Assert.IsInstanceOfType(store, typeof(InMemorySingleDateStore),
                "With no Storage configured the factory must fall back to the in-memory once-a-day throttle.");
        }

        [TestMethod]
        public void Factory_WithStorage_ReturnsTheDurableStoreUnderTheOperatorFacingKey()
        {
            // Opening is lazy, so this never touches the (fake) account.
            var config = ConfigWithStorage("DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=FAKEFAKEFAKE==;EndpointSuffix=core.windows.net");

            var store = ActivityReportsLastImportedStoreFactory.Create(config, AnalyticsLogger.ConsoleOnlyTracer());

            Assert.IsInstanceOfType(store, typeof(KeyValueSingleDateStore));
            Assert.AreEqual("UserActivityLastImported", ((KeyValueSingleDateStore)store).Key,
                "Operators delete this row by name to force the usage-report phase - keep it stable.");
        }

        [TestMethod]
        public async Task KeyValueSingleDateStore_RoundTripsInRoundTripFormat_AndDeletes()
        {
            var state = new InMemoryKeyValueStore();
            var store = new KeyValueSingleDateStore(state, "UserActivityLastImported");
            var when = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc);

            await store.SaveDT(when);

            Assert.AreEqual("2026-03-29T00:30:00.0000000Z", await state.GetStringAsync("UserActivityLastImported"),
                "Stored in round-trip format, so the offset survives and the cadence gate compares instants.");
            Assert.AreEqual(when, (await store.GetLastDT()).Value.ToUniversalTime());

            await store.DeleteDt();
            Assert.IsNull(await store.GetLastDT());
        }

        [TestMethod]
        public void Factory_NullConfig_ReturnsInMemoryStore()
        {
            // Defensive: a null config (or null ConnectionStrings) must not throw - just fall back to in-memory.
            var store = ActivityReportsLastImportedStoreFactory.Create(null, AnalyticsLogger.ConsoleOnlyTracer());

            Assert.IsInstanceOfType(store, typeof(InMemorySingleDateStore));
        }

        private static AppConfig ConfigWithStorage(string storageConnectionString)
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.ConnectionStrings = (AppConnectionStrings)FormatterServices.GetUninitializedObject(typeof(AppConnectionStrings));
            config.ConnectionStrings.StorageConnectionString = storageConnectionString;
            return config;
        }
    }
}
