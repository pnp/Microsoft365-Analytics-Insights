extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using SettingsProvider = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionScoreSettingsProvider;

namespace Tests.UnitTests
{
    public partial class CopilotAdoptionScoreSettingsTests
    {
        #region Store

        [TestMethod]
        public async Task Store_WithNothingSaved_ReturnsVersionZeroDefaults()
        {
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true);
            var document = await store.GetAsync();

            Assert.AreEqual(0, document.Version);
            Assert.IsTrue(document.Settings.IsDefault);
            Assert.AreEqual(0, document.History.Count);
            Assert.IsFalse(document.ToEffective().IsCustomised);
        }

        [TestMethod]
        public async Task Store_SaveAndReset_KeepAnAuditTrailOfOldAndNewValues()
        {
            var clock = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true, () => clock);

            var saved = await store.SaveAsync(With(s => s.ChampionScore = 90, s => s.FrequencyWeightPercent = 40, s => s.DepthWeightPercent = 40), 0, "  admin@contoso.com ");
            Assert.AreEqual(1, saved.Version);
            Assert.AreEqual("admin@contoso.com", saved.UpdatedBy);
            Assert.AreEqual(clock, saved.UpdatedUtc);
            var entry = saved.History.Single();
            Assert.AreEqual(CopilotAdoptionScoreSettingsActions.Save, entry.Action);
            Assert.AreEqual(1, entry.Version);
            var champion = entry.Changes.Single(c => c.Field == CopilotAdoptionScoreSettingsFields.ChampionScore);
            Assert.AreEqual(75, champion.OldValue);
            Assert.AreEqual(90, champion.NewValue);
            Assert.AreEqual(3, entry.Changes.Count);

            clock = clock.AddHours(1);
            var reset = await store.ResetAsync(1, "Καλημέρα Admin");
            Assert.AreEqual(2, reset.Version);
            Assert.IsTrue(reset.Settings.IsDefault);
            Assert.AreEqual(CopilotAdoptionScoreSettingsActions.Reset, reset.History[0].Action);
            Assert.AreEqual("Καλημέρα Admin", reset.History[0].ChangedBy, "The name round-trips in any script.");
            Assert.AreEqual(90, reset.History[0].Changes.Single(c => c.Field == CopilotAdoptionScoreSettingsFields.ChampionScore).OldValue);
            Assert.AreEqual(2, reset.History.Count, "Newest first, the original save kept.");
            Assert.AreEqual(CopilotAdoptionScoreSettingsActions.Save, reset.History[1].Action);

            var reread = await store.GetAsync();
            Assert.AreEqual(2, reread.Version);
            Assert.AreEqual(2, reread.History.Count);
            Assert.AreEqual("Καλημέρα Admin", reread.UpdatedBy);
            Assert.IsFalse(reread.ToEffective().IsCustomised, "Reset is back to the defaults' cache key.");
        }

        [TestMethod]
        public async Task Store_UnchangedSave_IsNotANewVersion()
        {
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true);
            await store.SaveAsync(With(s => s.ChampionScore = 80), 0, "admin@contoso.com");
            var again = await store.SaveAsync(With(s => s.ChampionScore = 80), 1, "admin@contoso.com");
            var resetOfDefaults = await new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true).ResetAsync(0, "admin@contoso.com");

            Assert.AreEqual(1, again.Version);
            Assert.AreEqual(1, again.History.Count);
            Assert.AreEqual(0, resetOfDefaults.Version);
            Assert.AreEqual(0, resetOfDefaults.History.Count);
        }

        [TestMethod]
        public async Task Store_HistoryIsBounded()
        {
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true);
            CopilotAdoptionScoreSettingsDocument document = null;
            for (var i = 0; i < CopilotAdoptionScoreSettingsStore.MaxHistory + 5; i++)
            {
                var champion = 76 + (i % 20);
                document = await store.SaveAsync(With(s => s.ChampionScore = champion, s => s.DevelopingScore = 2 + (i % 2)), i, "admin" + i + "@contoso.com");
            }

            Assert.AreEqual(CopilotAdoptionScoreSettingsStore.MaxHistory + 5, document.Version);
            Assert.AreEqual(CopilotAdoptionScoreSettingsStore.MaxHistory, document.History.Count);
            Assert.AreEqual(document.Version, document.History[0].Version, "Newest first.");
        }

        [TestMethod]
        public async Task Store_RefusesAStaleVersion_InvalidSettings_AndANonDurableStore()
        {
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true);
            await store.SaveAsync(With(s => s.ChampionScore = 80), 0, "first@contoso.com");

            var stale = await Rejected(() => store.SaveAsync(With(s => s.ChampionScore = 85), 0, "second@contoso.com"));
            Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.VersionConflict, stale.Code);
            Assert.AreEqual(80, (await store.GetAsync()).Settings.ChampionScore, "The stale write did not land.");

            var staleReset = await Rejected(() => store.ResetAsync(5, "second@contoso.com"));
            Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.VersionConflict, staleReset.Code);

            var invalid = await Rejected(() => store.SaveAsync(With(s => s.FrequencyWeightPercent = 90), 1, "first@contoso.com"));
            Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.WeightsMustTotal100, invalid.Code);
            CollectionAssert.Contains(invalid.ValidationErrors.ToList(), CopilotAdoptionScoreSettingsErrorCodes.WeightsMustTotal100);

            var memoryOnly = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: false);
            var refused = await Rejected(() => memoryOnly.SaveAsync(With(s => s.ChampionScore = 80), 0, "first@contoso.com"));
            Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.StorageNotConfigured, refused.Code);
            Assert.AreEqual(CopilotAdoptionScoreSettingsErrorCodes.StorageNotConfigured, (await Rejected(() => memoryOnly.ResetAsync(0, "x"))).Code);
            Assert.IsTrue((await memoryOnly.GetAsync()).Settings.IsDefault, "Without storage the report still runs on the defaults.");
        }

        [TestMethod]
        public async Task Store_ReadFailuresAreReported_NeverReplacedByTheDefaults()
        {
            var unreachable = new CopilotAdoptionScoreSettingsStore(new FailingStore(), isDurable: true);
            await Unavailable(() => unreachable.GetAsync());
            await Unavailable(() => unreachable.SaveAsync(With(s => s.ChampionScore = 80), 0, "a@contoso.com"));

            foreach (var json in new[]
            {
                "{not json",
                "{\"schemaVersion\":1,\"version\":3,\"settings\":null}",
                "{\"schemaVersion\":99,\"version\":3,\"settings\":{}}",
                "{\"schemaVersion\":1,\"version\":3,\"settings\":{\"frequencyWeightPercent\":90,\"depthWeightPercent\":30,\"breadthWeightPercent\":20,\"developingScore\":25,\"establishedScore\":50,\"championScore\":75}}",
            })
            {
                var values = new InMemoryKeyValueStore();
                await values.SetStringAsync(CopilotAdoptionScoreSettingsStore.DocumentKey, json);
                await Unavailable(() => new CopilotAdoptionScoreSettingsStore(values, isDurable: true).GetAsync());
            }
        }

        #endregion

        #region Provider

        [TestMethod]
        public async Task Provider_CachesForItsInterval_AndThenRereads()
        {
            var clock = Now;
            var values = new CountingStore();
            var store = new CopilotAdoptionScoreSettingsStore(values, isDurable: true);
            var provider = new SettingsProvider(() => store, TimeSpan.FromSeconds(15), () => clock);

            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetAsync()));
            Assert.AreEqual(1, values.Reads, "Concurrent requests share one read.");

            // Another instance saves.
            await new CopilotAdoptionScoreSettingsStore(values, isDurable: true).SaveAsync(With(s => s.ChampionScore = 90), 0, "admin@contoso.com");
            Assert.IsFalse((await provider.GetAsync()).IsCustomised, "Within the interval the cached value stands.");

            clock = clock.AddSeconds(16);
            var refreshed = await provider.GetAsync();
            Assert.IsTrue(refreshed.IsCustomised, "After the interval this instance sees the other's save.");
            Assert.AreEqual(1, refreshed.Version);
            Assert.AreSame(refreshed, provider.LastKnown);
        }

        [TestMethod]
        public async Task Provider_DoesNotCacheAFailure()
        {
            var values = new FailingStore();
            var provider = new SettingsProvider(() => new CopilotAdoptionScoreSettingsStore(values, isDurable: true), TimeSpan.FromMinutes(5), () => Now);

            await Unavailable(() => provider.GetAsync());
            values.Fail = false;
            Assert.IsFalse((await provider.GetAsync()).IsCustomised, "The next request reads again and succeeds.");
        }

        [TestMethod]
        public async Task Provider_Publish_AppliesASaveOnThisInstanceAtOnce()
        {
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true);
            var provider = new SettingsProvider(() => store, TimeSpan.FromMinutes(5), () => Now);
            Assert.IsFalse((await provider.GetAsync()).IsCustomised);

            var saved = await store.SaveAsync(With(s => s.ChampionScore = 90), 0, "admin@contoso.com");
            provider.Publish(saved);

            Assert.AreEqual(90, (await provider.GetAsync()).GetValues().ChampionScore);
        }

        #endregion
    }
}
