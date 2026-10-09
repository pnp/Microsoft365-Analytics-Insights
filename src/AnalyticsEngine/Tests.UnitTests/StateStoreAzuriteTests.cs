using Azure;
using Azure.Data.Tables;
using Common.Entities.State;
using Common.Entities.UserScope.Purge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// <see cref="AzureTableKeyValueStore"/> against a real Table service: Azurite, the local Azure Storage emulator.
    /// What the in-memory tests cannot prove - that the service accepts the escaped row keys and the compressed chunks,
    /// and that expiry and purge behave on real entities - is proved here.
    /// </summary>
    /// <remarks>
    /// CI starts Azurite and sets <c>REQUIRE_AZURITE=true</c>, so these fail there if it is missing. Locally, without
    /// Azurite listening on 127.0.0.1:10002, they report Inconclusive. Visual Studio ships Azurite
    /// (<c>Common7\IDE\Extensions\Microsoft\Azure Storage Emulator\azurite.exe</c>), or <c>npm install -g azurite</c>.
    /// Every run uses its own table, deleted afterwards.
    /// </remarks>
    [TestClass]
    [TestCategory("Azurite")]
    public class StateStoreAzuriteTests
    {
        private const string DevelopmentStorage = "UseDevelopmentStorage=true";

        private static TableClient _table;
        private static string _unavailableReason;

        [ClassInitialize]
        public static void OpenTable(TestContext context)
        {
            if (!AzuriteIsListening())
            {
                _unavailableReason = "Azurite (the Azure Storage emulator) is not listening on 127.0.0.1:10002.";
                if (string.Equals(Environment.GetEnvironmentVariable("REQUIRE_AZURITE"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Fail(_unavailableReason + " REQUIRE_AZURITE is set, so this is a failure rather than a skip.");
                }
                return;
            }

            _table = StorageTableClientFactory.CreateAndEnsureTable(DevelopmentStorage, "StateTest" + Guid.NewGuid().ToString("N"),
                null, null, null, null, "state store integration test table");
        }

        [ClassCleanup]
        public static void DeleteTable()
        {
            _table?.Delete();
        }

        [TestMethod]
        public async Task Values_RoundTrip_AndDeleteReportsWhetherOneExisted()
        {
            var store = NewStore();

            Assert.IsNull(await store.GetStringAsync("GraphUsersMetadataLastImported"));
            Assert.IsFalse(await store.ExistsAsync("GraphUsersMetadataLastImported"));
            Assert.IsFalse(await store.DeleteAsync("GraphUsersMetadataLastImported"), "Deleting a missing row is not an error.");

            await store.SetStringAsync("GraphUsersMetadataLastImported", "2026-09-01T10:00:00.0000000Z");
            await store.SetStringAsync("GraphUsersMetadataLastImported", "2026-09-02T10:00:00.0000000Z");

            Assert.AreEqual("2026-09-02T10:00:00.0000000Z", await store.GetStringAsync("GraphUsersMetadataLastImported"));
            Assert.IsTrue(await store.ExistsAsync("GraphUsersMetadataLastImported"));
            Assert.IsTrue(await store.DeleteAsync("GraphUsersMetadataLastImported"));
            Assert.IsNull(await store.GetStringAsync("GraphUsersMetadataLastImported"));
        }

        [TestMethod]
        public async Task TheRowIsReadableInStorageExplorer()
        {
            var store = NewStore();

            await store.SetStringAsync("UserActivityLastImported", "2026-09-01T10:00:00.0000000+02:00");

            var row = (await _table.GetEntityAsync<TableEntity>(store.PartitionKey, "UserActivityLastImported")).Value;
            Assert.AreEqual("UserActivityLastImported", row.GetString(AzureTableKeyValueStore.KeyProperty));
            Assert.AreEqual("2026-09-01T10:00:00.0000000+02:00", row.GetString(AzureTableKeyValueStore.ValueProperty));
        }

        [TestMethod]
        public async Task KeysAzureTablesForbid_AndNonLatinKeys_AndOverlongKeys_AllWork()
        {
            var store = NewStore();
            var keys = new[]
            {
                "SentEmails-alex_contoso.com#EXT#@fabrikam.onmicrosoft.com",
                "a/b\\c?d%e\tf",
                "SentEmails-Καλημέρα κόσμε@contoso.com",
                "x" + new string('y', 400),
            };

            foreach (var key in keys)
            {
                await store.SetStringAsync(key, "value of " + key);
            }

            foreach (var key in keys)
            {
                Assert.AreEqual("value of " + key, await store.GetStringAsync(key), key);
                Assert.IsTrue(await store.DeleteAsync(key), key);
            }
        }

        [TestMethod]
        public async Task ALargeValue_IsStoredCompressedInOneRow_AndShrinkingItBackLeavesNoChunksBehind()
        {
            var store = NewStore();
            var large = StateStoreTests.IncompressibleText(150000, seed: 3) + "Καλημέρα κόσμε";

            await store.SetStringAsync("SentEmailNoMailboxUsers", large);
            Assert.AreEqual(large, await store.GetStringAsync("SentEmailNoMailboxUsers"));

            var row = (await _table.GetEntityAsync<TableEntity>(store.PartitionKey, "SentEmailNoMailboxUsers")).Value;
            Assert.IsTrue(row.GetInt32(AzureTableKeyValueStore.CompressedChunkCountProperty) > 1);

            await store.SetStringAsync("SentEmailNoMailboxUsers", "small again");
            row = (await _table.GetEntityAsync<TableEntity>(store.PartitionKey, "SentEmailNoMailboxUsers")).Value;
            Assert.AreEqual("small again", row.GetString(AzureTableKeyValueStore.ValueProperty));
            Assert.IsFalse(row.Keys.Any(k => k.StartsWith(AzureTableKeyValueStore.CompressedChunkPropertyPrefix, StringComparison.Ordinal)),
                "A replace must drop the old chunks.");
        }

        [TestMethod]
        public async Task ANoMailboxListTooLargeForOneRow_RoundTripsThroughTheTableService()
        {
            var store = NewStore();
            var upns = StateStoreTests.SyntheticUpns(250000, seed: 5);
            var logger = new FakeLoaderClasses.RecordingLogger();
            var persisted = new WebJob.Office365ActivityImporter.Engine.Graph.Email.PersistedSentEmailMailboxSkipList(store, logger);

            await persisted.SaveAsync(new WebJob.Office365ActivityImporter.Engine.Graph.Email.MailboxSkipList
            {
                GeneratedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                Upns = upns,
            });
            var loaded = await persisted.LoadAsync();

            Assert.AreEqual(0, logger.Entries.Count, "The service rejected part of the list: " + string.Join("; ", logger.Entries.Select(e => e.Message)));
            Assert.AreEqual(upns.Count, loaded.Upns.Count);
            Assert.IsTrue(loaded.UpnSet.SetEquals(upns));
        }

        [TestMethod]
        public async Task ExpiredValues_ReadAsMissing_AreDeletedWhenMet_AndPurged()
        {
            var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var partition = "Test" + Guid.NewGuid().ToString("N");
            var store = new AzureTableKeyValueStore(_table ?? Skip(), partition, () => now);

            await store.SetStringAsync("met", "result", TimeSpan.FromDays(1));
            await store.SetStringAsync("never-read-again", "result", TimeSpan.FromDays(1));
            await store.SetStringAsync("no-ttl", "stamp");
            Assert.AreEqual("result", await store.GetStringAsync("met"));

            now = now.AddDays(2);

            Assert.IsNull(await store.GetStringAsync("met"));
            Assert.IsFalse(await store.ExistsAsync("never-read-again"));
            Assert.AreEqual(1, await store.PurgeExpiredAsync(), "Only the expired row nobody read is left to purge.");
            Assert.AreEqual("stamp", await store.GetStringAsync("no-ttl"), "A value without a TTL is never purged.");

            var remaining = _table.Query<TableEntity>(e => e.PartitionKey == partition).Select(e => e.RowKey).ToList();
            CollectionAssert.AreEquivalent(new[] { "no-ttl" }, remaining);
        }

        [TestMethod]
        public async Task APurgeNeverDeletesAValueRewrittenAfterItFoundItExpired()
        {
            var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var partition = "Test" + Guid.NewGuid().ToString("N");
            var store = new AzureTableKeyValueStore(_table ?? Skip(), partition, () => now);
            await store.SetStringAsync("rewritten", "stale result", TimeSpan.FromDays(1));
            await store.SetStringAsync("expired", "stale result", TimeSpan.FromDays(1));

            now = now.AddDays(2);
            var found = await store.FindExpiredAsync(100, CancellationToken.None);
            Assert.AreEqual(2, found.Count);

            // Between the purge's query and its delete, another Teams worker caches a fresh result for the same text.
            await store.SetStringAsync("rewritten", "fresh result", TimeSpan.FromDays(1));

            Assert.AreEqual(1, await store.DeleteUnlessChangedAsync(found, CancellationToken.None));
            Assert.AreEqual("fresh result", await store.GetStringAsync("rewritten"), "The purge must not delete a value written after it looked.");
            var remaining = _table.Query<TableEntity>(e => e.PartitionKey == partition).Select(e => e.RowKey).ToList();
            CollectionAssert.AreEquivalent(new[] { "rewritten" }, remaining, "The unchanged expired row in the same batch is still deleted.");
        }

        [TestMethod]
        public async Task PartitionsAreIndependent()
        {
            var first = NewStore();
            var second = NewStore();

            await first.SetStringAsync("same-key", "first");

            Assert.IsNull(await second.GetStringAsync("same-key"));
        }

        [TestMethod]
        public async Task StateStore_OpensAndCreatesItsTable_OnFirstUse()
        {
            if (_table == null) Skip();

            var config = (Common.Entities.Config.AppConfig)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Common.Entities.Config.AppConfig));
            config.ConnectionStrings = (Common.Entities.Config.AppConnectionStrings)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Common.Entities.Config.AppConnectionStrings));
            config.ConnectionStrings.StorageConnectionString = DevelopmentStorage;
            var partition = "Test" + Guid.NewGuid().ToString("N");

            var store = StateStore.TryOpen(config, partition);
            await store.SetStringAsync("probe", "value");

            try
            {
                Assert.AreEqual("value", await store.GetStringAsync("probe"));
                var row = new TableClient(DevelopmentStorage, StateStore.TableName).GetEntity<TableEntity>(partition, "probe").Value;
                Assert.AreEqual("value", row.GetString(AzureTableKeyValueStore.ValueProperty));
            }
            finally
            {
                await store.DeleteAsync("probe");
            }
        }

        /// <summary>
        /// The Copilot Adoption score settings (#683, #684) in a real table: a save, its audit history and a reset
        /// survive a fresh store instance - which is what another web app instance sees.
        /// </summary>
        [TestMethod]
        public async Task CopilotAdoptionScoreSettings_RoundTripThroughTheTable()
        {
            var partition = "Test" + Guid.NewGuid().ToString("N");
            var writer = new Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettingsStore(
                new AzureTableKeyValueStore(_table ?? Skip(), partition), isDurable: true);

            var custom = Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettings.Defaults;
            custom.FrequencyWeightPercent = 40;
            custom.DepthWeightPercent = 40;
            custom.ChampionScore = 90;
            await writer.SaveAsync(custom, 0, "admin@contoso.com");

            var reader = new Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettingsStore(
                new AzureTableKeyValueStore(_table, partition), isDurable: true);
            var stored = await reader.GetAsync();
            Assert.AreEqual(1, stored.Version);
            Assert.AreEqual(custom, stored.Settings);
            Assert.AreEqual("admin@contoso.com", stored.History.Single().ChangedBy);
            Assert.AreEqual(3, stored.History.Single().Changes.Count);

            await reader.ResetAsync(1, "other.admin@contoso.com");
            var reset = await writer.GetAsync();
            Assert.AreEqual(2, reset.Version);
            Assert.IsTrue(reset.Settings.IsDefault);
            Assert.AreEqual(2, reset.History.Count);
        }

        /// <summary>The ETag-conditional writes the score settings rely on, against the real Table service.</summary>
        [TestMethod]
        public async Task ConditionalWrites_InsertOnlyWhenAbsent_AndReplaceOnlyAnUnchangedRow()
        {
            var store = NewStore();

            var missing = await store.GetVersionedAsync("settings");
            Assert.IsNull(missing.Value);
            Assert.IsNull(missing.VersionToken);
            Assert.IsTrue(await store.TrySetStringAsync("settings", "one", null));
            Assert.IsFalse(await store.TrySetStringAsync("settings", "rival", null), "409: another writer created it first.");

            var one = await store.GetVersionedAsync("settings");
            Assert.AreEqual("one", one.Value);
            Assert.IsTrue(await store.TrySetStringAsync("settings", "two", one.VersionToken));
            Assert.IsFalse(await store.TrySetStringAsync("settings", "stale", one.VersionToken), "412: the row changed since it was read.");
            Assert.AreEqual("two", await store.GetStringAsync("settings"));

            var two = await store.GetVersionedAsync("settings");
            await store.DeleteAsync("settings");
            Assert.IsFalse(await store.TrySetStringAsync("settings", "gone", two.VersionToken), "404: the row was deleted since it was read.");

            var large = new string('x', AzureTableKeyValueStore.MaxPlainValueChars + 10);
            Assert.IsTrue(await store.TrySetStringAsync("large", large, null), "A value stored as compressed chunks can be written conditionally too.");
            Assert.AreEqual(large, (await store.GetVersionedAsync("large")).Value);
        }

        /// <summary>
        /// Several web instances saving the score settings against one version at once: exactly one lands, the rest are
        /// conflicts, and the history holds only the change that was committed.
        /// </summary>
        [TestMethod]
        public async Task CopilotAdoptionScoreSettings_ConcurrentSavesFromOneVersion_ExactlyOneLands()
        {
            var partition = "Test" + Guid.NewGuid().ToString("N");
            var stores = Enumerable.Range(0, 6).Select(_ => new Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettingsStore(
                new AzureTableKeyValueStore(_table ?? Skip(), partition), isDurable: true)).ToList();

            var outcomes = await Task.WhenAll(stores.Select((store, i) => Task.Run(async () =>
            {
                var settings = Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettings.Defaults;
                settings.ChampionScore = 80 + i;
                try
                {
                    await store.SaveAsync(settings, 0, "admin" + i + "@contoso.com");
                    return (string)null;
                }
                catch (Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettingsRejectedException ex)
                {
                    return ex.Code;
                }
            })));

            Assert.AreEqual(1, outcomes.Count(o => o == null), "Exactly one save lands.");
            Assert.IsTrue(outcomes.Where(o => o != null).All(o => o == Common.Entities.CopilotAdoption.CopilotAdoptionScoreSettingsErrorCodes.VersionConflict));
            var stored = await stores[0].GetAsync();
            Assert.AreEqual(1, stored.Version);
            var change = stored.History.Single().Changes.Single();
            Assert.AreEqual(75, change.OldValue);
            Assert.AreEqual(stored.Settings.ChampionScore, change.NewValue);
            Assert.AreEqual("admin" + (stored.Settings.ChampionScore - 80) + "@contoso.com", stored.History.Single().ChangedBy, "The history names the winner.");
        }

        private static AzureTableKeyValueStore NewStore()
        {
            return new AzureTableKeyValueStore(_table ?? Skip(), "Test" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// The User scope purge's records in a real table: a record with its per-table counts, which purge is the latest,
        /// and a stop request under a key of its own.
        /// </summary>
        [TestMethod]
        public async Task UserScopePurgeRecords_RoundTripThroughTheTable()
        {
            var state = new UserScopePurgeStateStore(NewStore(), isDurable: true);
            Assert.IsTrue(state.IsDurable);

            var job = await state.CreateAsync("admin@contoso.local", "fingerprint", 5);
            job.State = UserScopePurgeStates.Running;
            job.RowsAffected["call_sessions.attendee_user_id"] = 2;
            job.RowsAffected["audit_events"] = 1234;
            await state.SaveAsync(job);

            var latest = await state.GetLatestAsync();
            Assert.AreEqual(job.Id, latest.Id);
            Assert.AreEqual(UserScopePurgeStates.Running, latest.State);
            Assert.AreEqual(2L, latest.RowsAffected["call_sessions.attendee_user_id"]);
            Assert.AreEqual(1234L, latest.RowsAffected["audit_events"]);
            Assert.IsFalse(latest.CancelRequested);

            Assert.IsTrue(await state.RequestCancelAsync(job.Id));
            Assert.IsTrue((await state.GetAsync(job.Id)).CancelRequested);
        }

        private static TableClient Skip()
        {
            Assert.Inconclusive(_unavailableReason ?? "Azurite is not available.");
            return null;
        }

        private static bool AzuriteIsListening()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync("127.0.0.1", 10002);
                    return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
                }
            }
            catch (Exception ex) when (ex is SocketException || ex is AggregateException || ex is RequestFailedException)
            {
                return false;
            }
        }
    }
}
