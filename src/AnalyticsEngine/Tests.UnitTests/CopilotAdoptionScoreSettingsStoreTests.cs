extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
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

        #region Concurrent saves

        [TestMethod]
        public async Task Store_TwoSavesFromTheSameVersion_ExactlyOneLands_AndTheOtherIsAConflict()
        {
            // Either may win; repeated so both outcomes are normally exercised. The assertions hold for each.
            for (var run = 0; run < 10; run++)
            {
                var values = new CountingStore();
                var saver = new CopilotAdoptionScoreSettingsStore(values, isDurable: true);
                var resetter = new CopilotAdoptionScoreSettingsStore(values, isDurable: true);
                await saver.SaveAsync(With(s => s.ChampionScore = 90), 0, "setup@contoso.com");

                // Both have read version 1 before either writes: the window a read-then-write check cannot close.
                values.HoldNext(2);
                var save = Outcome(() => saver.SaveAsync(With(s => s.ChampionScore = 80), 1, "saver@contoso.com"));
                var reset = Outcome(() => resetter.ResetAsync(1, "resetter@contoso.com"));
                var outcomes = await Task.WhenAll(save, reset);

                Assert.AreEqual(1, outcomes.Count(o => o == null), "Exactly one write lands.");
                Assert.AreEqual(1, outcomes.Count(o => o == CopilotAdoptionScoreSettingsErrorCodes.VersionConflict), "The other is refused as a conflict.");
                var saveWon = await save == null;

                var stored = await saver.GetAsync();
                Assert.AreEqual(2, stored.Version);
                Assert.AreEqual(2, stored.History.Count, "The refused write left no history entry.");
                Assert.AreEqual(saveWon ? 80 : 75, stored.Settings.ChampionScore);
                Assert.AreEqual(90, stored.History[0].Changes.Single().OldValue, "The winner replaced version 1.");

                // The loser reloads and tries again: its history entry starts from the winner's values, not version 1's.
                var retried = saveWon
                    ? await resetter.ResetAsync(2, "resetter@contoso.com")
                    : await saver.SaveAsync(With(s => s.ChampionScore = 80), 2, "saver@contoso.com");
                var latest = retried.History[0].Changes.Single();
                Assert.AreEqual(3, retried.Version);
                Assert.AreEqual(saveWon ? 80 : 75, latest.OldValue);
                Assert.AreEqual(saveWon ? 75 : 80, latest.NewValue);
                Assert.AreEqual(saveWon ? "resetter@contoso.com" : "saver@contoso.com", retried.History[0].ChangedBy);
            }
        }

        [TestMethod]
        public async Task Store_ConcurrentSavesAndResets_LeaveOneUnbrokenHistory()
        {
            var values = new InMemoryKeyValueStore();
            var stores = Enumerable.Range(0, 4).Select(_ => new CopilotAdoptionScoreSettingsStore(values, isDurable: true)).ToList();

            // Several administrators on several instances, each retrying a conflict against the version it then sees.
            var landed = 0;
            await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
            {
                var store = stores[i % stores.Count];
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    var current = await store.GetAsync();
                    try
                    {
                        if (i % 3 == 0)
                            await store.ResetAsync(current.Version, "admin" + i + "@contoso.com");
                        else
                            await store.SaveAsync(With(s => s.ChampionScore = 76 + i, s => s.DevelopingScore = 2 + (i % 5)), current.Version, "admin" + i + "@contoso.com");
                        Interlocked.Increment(ref landed);
                        return;
                    }
                    catch (CopilotAdoptionScoreSettingsRejectedException ex) when (ex.Code == CopilotAdoptionScoreSettingsErrorCodes.VersionConflict)
                    {
                        await Task.Yield();
                    }
                }
                Assert.Fail("A writer never got its change in.");
            })));

            Assert.AreEqual(24, landed);
            var stored = await stores[0].GetAsync();
            var history = stored.History.AsEnumerable().Reverse().ToList();
            Assert.AreEqual(stored.Version, history.Last().Version);

            // Replay the history from the defaults: every entry's old values must be exactly the previous version's.
            var replay = CopilotAdoptionScoreSettings.Defaults;
            for (var i = 0; i < history.Count; i++)
            {
                if (i > 0) Assert.AreEqual(history[i - 1].Version + 1, history[i].Version, "Versions form one sequence.");
                var fields = replay.Fields().ToDictionary(f => f.Name, f => f.Value);
                foreach (var change in history[i].Changes)
                {
                    Assert.AreEqual(fields[change.Field], change.OldValue, $"Version {history[i].Version}: '{change.Field}' was {fields[change.Field]}, not {change.OldValue}.");
                }
                replay = Replayed(replay, history[i]);
            }
            Assert.AreEqual(stored.Settings, replay, "Replaying the history gives the settings in force.");
        }

        private static async Task<string> Outcome(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (CopilotAdoptionScoreSettingsRejectedException ex)
            {
                return ex.Code;
            }
        }

        private static CopilotAdoptionScoreSettings Replayed(CopilotAdoptionScoreSettings from, CopilotAdoptionScoreSettingsChange change)
        {
            var json = Newtonsoft.Json.Linq.JObject.FromObject(from);
            foreach (var c in change.Changes) json[c.Field] = c.NewValue;
            return json.ToObject<CopilotAdoptionScoreSettings>();
        }

        [TestMethod]
        public void Store_ADurableStoreMustSupportConditionalWrites()
        {
            Assert.ThrowsException<ArgumentException>(() => new CopilotAdoptionScoreSettingsStore(new LastWriterWinsStore(), isDurable: true));
            Assert.IsNotNull(new CopilotAdoptionScoreSettingsStore(new LastWriterWinsStore(), isDurable: false), "Read-only use needs no conditional write.");
        }

        [TestMethod]
        public async Task InMemoryStore_ConditionalWrites_FollowTheTableServiceRules()
        {
            var clock = Now;
            var values = new InMemoryKeyValueStore(() => clock);

            var missing = await values.GetVersionedAsync("k");
            Assert.IsNull(missing.Value);
            Assert.IsNull(missing.VersionToken);
            Assert.IsTrue(await values.TrySetStringAsync("k", "one", null), "Create when absent.");
            Assert.IsFalse(await values.TrySetStringAsync("k", "two", null), "A second create loses.");

            var one = await values.GetVersionedAsync("k");
            Assert.AreEqual("one", one.Value);
            Assert.IsTrue(await values.TrySetStringAsync("k", "two", one.VersionToken));
            Assert.IsFalse(await values.TrySetStringAsync("k", "stale", one.VersionToken), "A write from a stale read loses.");
            Assert.AreEqual("two", await values.GetStringAsync("k"));

            await values.SetStringAsync("ttl", "old", TimeSpan.FromMinutes(1));
            clock = clock.AddMinutes(2);
            var expired = await values.GetVersionedAsync("ttl");
            Assert.IsNull(expired.Value, "Expired reads as missing.");
            Assert.IsTrue(await values.TrySetStringAsync("ttl", "new", expired.VersionToken), "...and can be replaced with its token.");
            Assert.AreEqual("new", await values.GetStringAsync("ttl"));
        }

        private sealed class LastWriterWinsStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            public string Description => _inner.Description;
            public Task<string> GetStringAsync(string key, System.Threading.CancellationToken cancellationToken = default) => _inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, System.Threading.CancellationToken cancellationToken = default) => _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            public Task<bool> DeleteAsync(string key, System.Threading.CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, System.Threading.CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
        }

        #endregion

        #region Provider

        [TestMethod]
        public async Task Provider_ReadsOnEveryRequest_SoAnotherInstancesSaveAppliesToTheNextOne()
        {
            var values = new CountingStore();
            var store = new CopilotAdoptionScoreSettingsStore(values, isDurable: true);
            var provider = new SettingsProvider(() => store);

            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetAsync()));
            Assert.AreEqual(5, values.Reads, "One point read per request - never a read per user or per step.");

            // Another instance saves; no clock moves and nothing is invalidated here.
            await new CopilotAdoptionScoreSettingsStore(values, isDurable: true).SaveAsync(With(s => s.ChampionScore = 90), 0, "admin@contoso.com");
            var next = await provider.GetAsync();
            Assert.IsTrue(next.IsCustomised, "The very next request sees the other instance's save.");
            Assert.AreEqual(1, next.Version);
            Assert.AreSame(next, provider.LastKnown);
        }

        [TestMethod]
        public async Task Provider_DoesNotCacheAFailure()
        {
            var values = new FailingStore();
            var provider = new SettingsProvider(() => new CopilotAdoptionScoreSettingsStore(values, isDurable: true));

            await Unavailable(() => provider.GetAsync());
            values.Fail = false;
            Assert.IsFalse((await provider.GetAsync()).IsCustomised, "The next request reads again and succeeds.");
        }

        [TestMethod]
        public async Task Provider_Publish_RecordsASaveOnThisInstance()
        {
            var store = new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: true);
            var provider = new SettingsProvider(() => store);
            Assert.IsFalse((await provider.GetAsync()).IsCustomised);

            var saved = await store.SaveAsync(With(s => s.ChampionScore = 90), 0, "admin@contoso.com");
            provider.Publish(saved);

            Assert.AreEqual(90, provider.LastKnown.GetValues().ChampionScore);
            Assert.AreEqual(90, (await provider.GetAsync()).GetValues().ChampionScore);
        }

        #endregion
    }
}
