using Azure;
using Azure.Data.Tables;
using Common.Entities.State;
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

        private static AzureTableKeyValueStore NewStore()
        {
            return new AzureTableKeyValueStore(_table ?? Skip(), "Test" + Guid.NewGuid().ToString("N"));
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
