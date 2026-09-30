using Azure.Data.Tables;
using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.Teams;

namespace Tests.UnitTests
{
    /// <summary>
    /// The runtime state store that replaced Azure Cache for Redis: import checkpoints, "last run" stamps, delta
    /// tokens, Teams authorisation tokens and the AI Language result cache, kept in an Azure Table (or in memory when no
    /// Storage connection string is configured). These tests need no network; the Table service itself is exercised by
    /// <see cref="StateStoreAzuriteTests"/>.
    /// </summary>
    [TestClass]
    public class StateStoreTests
    {
        private const string FakeStorage = "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=FAKEFAKEFAKE==;EndpointSuffix=core.windows.net";

        #region Row keys

        [TestMethod]
        public void RowKey_IsTheKeyItself_WhenItIsLegal()
        {
            // Operators find and delete these rows by name, so the common keys must stay readable.
            Assert.AreEqual("GraphUsersMetadataLastImported", AzureTableKeyValueStore.ToRowKey(UserImportCheckpointKeys.LastCompleted));
            Assert.AreEqual("UserDeltaCode-00000000-0000-0000-0000-000000000000-v2", AzureTableKeyValueStore.ToRowKey(UserImportCheckpointKeys.DeltaToken(Guid.Empty)));
            Assert.AreEqual("UserActivityReportLastImported:TeamsUserUsageLoader", AzureTableKeyValueStore.ToRowKey("UserActivityReportLastImported:TeamsUserUsageLoader"));
            Assert.AreEqual("SentEmails-alex.wilber@contoso.com", AzureTableKeyValueStore.ToRowKey("SentEmails-alex.wilber@contoso.com"));
        }

        [TestMethod]
        public void RowKey_EscapesWhatAzureTablesForbids_AndTheEscapeCharacterItself()
        {
            // A guest's user principal name carries '#', which Azure Tables rejects in a row key.
            Assert.AreEqual("SentEmails-alex_contoso.com%23EXT%23@fabrikam.onmicrosoft.com",
                AzureTableKeyValueStore.ToRowKey("SentEmails-alex_contoso.com#EXT#@fabrikam.onmicrosoft.com"));
            Assert.AreEqual("a%2Fb%5Cc%3Fd%25e%09f%7Fg", AzureTableKeyValueStore.ToRowKey("a/b\\c?d%e\tf\u007Fg"));
        }

        [TestMethod]
        public void RowKey_KeepsUnicodeReadable()
        {
            Assert.AreEqual("SentEmails-Καλημέρα κόσμε", AzureTableKeyValueStore.ToRowKey("SentEmails-Καλημέρα κόσμε"));
        }

        [TestMethod]
        public void RowKey_NeverMapsTwoKeysToOneRow()
        {
            // "%" is escaped too, so a key that happens to look like another key's escape stays distinct.
            Assert.AreNotEqual(AzureTableKeyValueStore.ToRowKey("a#b"), AzureTableKeyValueStore.ToRowKey("a%23b"));

            // A hashed key starts with "%%", which an escaped key can never contain.
            var hashed = AzureTableKeyValueStore.ToRowKey(new string('x', 300));
            Assert.AreNotEqual(hashed, AzureTableKeyValueStore.ToRowKey(hashed));
        }

        [TestMethod]
        public void RowKey_HashesKeysTooLongToBeReadable_Deterministically()
        {
            var longKey = new string('x', AzureTableKeyValueStore.MaxReadableRowKeyLength + 1);

            var rowKey = AzureTableKeyValueStore.ToRowKey(longKey);

            StringAssert.StartsWith(rowKey, AzureTableKeyValueStore.HashedRowKeyPrefix);
            Assert.AreEqual(2 + 64, rowKey.Length);
            Assert.AreEqual(rowKey, AzureTableKeyValueStore.ToRowKey(longKey), "The same key must always find its row.");
            Assert.AreNotEqual(rowKey, AzureTableKeyValueStore.ToRowKey(longKey + "x"));
            Assert.AreEqual(new string('x', AzureTableKeyValueStore.MaxReadableRowKeyLength),
                AzureTableKeyValueStore.ToRowKey(new string('x', AzureTableKeyValueStore.MaxReadableRowKeyLength)));
        }

        [TestMethod]
        public void RowKey_RequiresAKey()
        {
            Assert.ThrowsException<ArgumentException>(() => AzureTableKeyValueStore.ToRowKey(null));
            Assert.ThrowsException<ArgumentException>(() => AzureTableKeyValueStore.ToRowKey(string.Empty));
        }

        #endregion

        #region Value encoding

        [TestMethod]
        public void SmallValues_AreStoredAsPlainText_SoOperatorsCanReadThem()
        {
            var entity = new TableEntity("ImportSchedule", "GraphUsersMetadataLastImported");

            AzureTableKeyValueStore.EncodeValue(entity, "GraphUsersMetadataLastImported", "2026-09-01T10:00:00.0000000Z");

            Assert.AreEqual("2026-09-01T10:00:00.0000000Z", entity.GetString(AzureTableKeyValueStore.ValueProperty));
            Assert.IsNull(entity.GetInt32(AzureTableKeyValueStore.CompressedChunkCountProperty));
            Assert.AreEqual("2026-09-01T10:00:00.0000000Z", AzureTableKeyValueStore.DecodeValue(entity, "GraphUsersMetadataLastImported"));
        }

        [TestMethod]
        public void EmptyValues_RoundTrip()
        {
            var entity = new TableEntity("p", "r");
            AzureTableKeyValueStore.EncodeValue(entity, "k", string.Empty);
            Assert.AreEqual(string.Empty, AzureTableKeyValueStore.DecodeValue(entity, "k"));
        }

        [TestMethod]
        public void ALargeCompressibleValue_IsCompressedIntoOneEntity_AndRoundTrips()
        {
            // 60,000 numbered UPNs - far past the 32,000 characters one string column holds, but so regular that gzip
            // fits them in one row. (Real no-mailbox lists compress far less; PersistedSentEmailMailboxSkipList pages them.)
            var skipList = new MailboxSkipList
            {
                GeneratedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                Upns = Enumerable.Range(0, 60000).Select(i => $"user{i:D6}@contoso.onmicrosoft.com").ToList(),
            };
            var json = JsonConvert.SerializeObject(skipList);
            Assert.IsTrue(json.Length > AzureTableKeyValueStore.MaxPlainValueChars);

            var entity = new TableEntity("SentEmails", "SentEmailNoMailboxUsers");
            AzureTableKeyValueStore.EncodeValue(entity, "SentEmailNoMailboxUsers", json);

            Assert.IsNull(entity.GetString(AzureTableKeyValueStore.ValueProperty));
            var chunks = entity.GetInt32(AzureTableKeyValueStore.CompressedChunkCountProperty).Value;
            Assert.IsTrue(chunks >= 1 && chunks <= AzureTableKeyValueStore.MaxCompressedChunks);
            for (var i = 0; i < chunks; i++)
            {
                Assert.IsTrue(entity.GetBinary(AzureTableKeyValueStore.CompressedChunkPropertyPrefix + i).Length <= AzureTableKeyValueStore.CompressedChunkBytes);
            }
            Assert.AreEqual(json, AzureTableKeyValueStore.DecodeValue(entity, "SentEmailNoMailboxUsers"));
        }

        [TestMethod]
        public void ValuesThatNeedSeveralChunks_RoundTrip_IncludingNonLatinText()
        {
            var text = IncompressibleText(150000, seed: 7) + "Καλημέρα κόσμε";

            var entity = new TableEntity("p", "r");
            AzureTableKeyValueStore.EncodeValue(entity, "k", text);

            Assert.IsTrue(entity.GetInt32(AzureTableKeyValueStore.CompressedChunkCountProperty).Value > 1, "The test must cover more than one chunk.");
            Assert.AreEqual(text, AzureTableKeyValueStore.DecodeValue(entity, "k"));
        }

        [TestMethod]
        public void AValueTooLargeForOneEntity_FailsWithAMessageNamingTheKey()
        {
            var text = IncompressibleText(1500000, seed: 11);

            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AzureTableKeyValueStore.EncodeValue(new TableEntity("p", "r"), "SentEmailNoMailboxUsers", text));

            StringAssert.Contains(ex.Message, "SentEmailNoMailboxUsers");
            StringAssert.Contains(ex.Message, "too large");
        }

        [TestMethod]
        public void ARowWithNoValue_ReadsAsMissing()
        {
            Assert.IsNull(AzureTableKeyValueStore.DecodeValue(new TableEntity("p", "r"), "k"));
        }

        #endregion

        #region In-memory store

        [TestMethod]
        public async Task InMemory_GetSetDeleteExists()
        {
            var store = new InMemoryKeyValueStore();

            Assert.IsNull(await store.GetStringAsync("k"));
            Assert.IsFalse(await store.ExistsAsync("k"));
            Assert.IsFalse(await store.DeleteAsync("k"));

            await store.SetStringAsync("k", "v1");
            await store.SetStringAsync("k", "v2");
            Assert.AreEqual("v2", await store.GetStringAsync("k"), "Last writer wins.");
            Assert.IsTrue(await store.ExistsAsync("k"));

            Assert.IsTrue(await store.DeleteAsync("k"));
            Assert.IsNull(await store.GetStringAsync("k"));
        }

        [TestMethod]
        public async Task InMemory_KeysAreCaseSensitive_LikeRowKeys()
        {
            var store = new InMemoryKeyValueStore();
            await store.SetStringAsync("Key", "upper");

            Assert.IsNull(await store.GetStringAsync("key"));
        }

        [TestMethod]
        public async Task InMemory_SettingNullDeletes()
        {
            var store = new InMemoryKeyValueStore();
            await store.SetStringAsync("k", "v");

            await store.SetStringAsync("k", null);

            Assert.IsFalse(await store.ExistsAsync("k"));
        }

        [TestMethod]
        public async Task InMemory_ValuesExpireAfterTheirTimeToLive()
        {
            var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new InMemoryKeyValueStore(() => now);
            await store.SetStringAsync("cached", "result", TimeSpan.FromDays(1));
            await store.SetStringAsync("forever", "stamp");

            now = now.AddHours(23);
            Assert.AreEqual("result", await store.GetStringAsync("cached"));

            now = now.AddHours(2);
            Assert.IsNull(await store.GetStringAsync("cached"));
            Assert.IsFalse(await store.DeleteAsync("cached"), "An expired value no longer counts as existing.");
            Assert.AreEqual("stamp", await store.GetStringAsync("forever"), "A value without a TTL never expires.");
            Assert.AreEqual(1, store.Count);
        }

        #endregion

        #region StateStore

        [TestMethod]
        public void TheTableAndPartitionNamesAreFixed()
        {
            // They are the address of every stored value, and the wiki tells operators where to look.
            Assert.AreEqual("AnalyticsState", StateStore.TableName);
            Assert.AreEqual("ImportSchedule", StatePartitions.ImportSchedule);
            Assert.AreEqual("UserImport", StatePartitions.UserImport);
            Assert.AreEqual("SentEmails", StatePartitions.SentEmails);
            Assert.AreEqual("TeamsChannels", StatePartitions.TeamsChannels);
            Assert.AreEqual("TeamsAuth", StatePartitions.TeamsAuth);
            Assert.AreEqual("CognitiveCache", StatePartitions.CognitiveCache);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void WithoutStorage_NothingIsOpened_SoCallersFallBackToMemory(string storage)
        {
            var config = Config(storage);

            Assert.IsFalse(StateStore.IsConfigured(config));
            Assert.IsNull(StateStore.TryOpen(config, StatePartitions.ImportSchedule));
            Assert.IsNull(TeamsTokenStore.TryOpen(config));
        }

        [TestMethod]
        public void ANullConfig_IsNotConfigured()
        {
            Assert.IsFalse(StateStore.IsConfigured(null));
            Assert.IsNull(StateStore.TryOpen(null, StatePartitions.ImportSchedule));
        }

        [TestMethod]
        public void WithStorage_TheStoreIsOpenedLazily_AndSaysWhereItKeepsValues()
        {
            // The account is fake: nothing may touch the network until a value is read or written.
            var store = StateStore.TryOpen(Config(FakeStorage), StatePartitions.ImportSchedule);

            Assert.IsInstanceOfType(store, typeof(AzureTableKeyValueStore));
            Assert.AreEqual("Azure Table 'AnalyticsState', partition 'ImportSchedule'", store.Description);
            Assert.AreEqual("TeamsAuth", ((AzureTableKeyValueStore)StateStore.TryOpen(Config(FakeStorage), StatePartitions.TeamsAuth)).PartitionKey);
        }

        [TestMethod]
        public void APartitionIsRequired()
        {
            Assert.ThrowsException<ArgumentException>(() => StateStore.TryOpen(Config(FakeStorage), null));
        }

        [TestMethod]
        public void TheStorageAccountNameIsReportedWithoutAnySecret()
        {
            Assert.AreEqual("contosoanalytics", StorageTableClientFactory.GetAccountName(FakeStorage));
            Assert.AreEqual("devstoreaccount1", StorageTableClientFactory.GetAccountName("UseDevelopmentStorage=true"));
            Assert.AreEqual("contosoanalytics.table.core.windows.net",
                StorageTableClientFactory.GetAccountName("TableEndpoint=https://contosoanalytics.table.core.windows.net/;SharedAccessSignature=sv=fake"));
            Assert.IsNull(StorageTableClientFactory.GetAccountName(null));
            Assert.IsFalse(StorageTableClientFactory.GetAccountName(FakeStorage).Contains("FAKE"));
        }

        #endregion

        #region What is kept in it

        [TestMethod]
        public async Task TeamsTokens_AreStoredPerTeam_AndStatusNeverReadsATokenValue()
        {
            var state = new CountingKeyValueStore();
            var tokens = new TeamsTokenStore(state);

            await tokens.SetRefreshTokenAsync("00000000-0000-0000-0000-000000000001", "synthetic-refresh-token-1");
            await tokens.SetRefreshTokenAsync("00000000-0000-0000-0000-000000000002", "synthetic-refresh-token-2");
            await tokens.RemoveRefreshTokenAsync("00000000-0000-0000-0000-000000000002");

            Assert.AreEqual("synthetic-refresh-token-1", await tokens.GetRefreshTokenAsync("00000000-0000-0000-0000-000000000001"));
            Assert.IsNull(await tokens.GetRefreshTokenAsync("00000000-0000-0000-0000-000000000002"));

            state.Reads = 0;
            var status = await tokens.GetAuthorisationStatusAsync(new[]
            {
                "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002", null, "", "00000000-0000-0000-0000-000000000001",
            });

            Assert.AreEqual(2, status.Count, "Blank and duplicate ids are ignored.");
            Assert.IsTrue(status["00000000-0000-0000-0000-000000000001"]);
            Assert.IsFalse(status["00000000-0000-0000-0000-000000000002"]);
            Assert.AreEqual(0, state.Reads, "Checking which Teams are authorised must not load any token.");
        }

        [TestMethod]
        public void TeamsTokens_NeedATeamAndAToken()
        {
            var tokens = new TeamsTokenStore(new InMemoryKeyValueStore());

            Assert.ThrowsException<ArgumentException>(() => tokens.GetRefreshTokenAsync(null).GetAwaiter().GetResult());
            Assert.ThrowsException<ArgumentException>(() => tokens.SetRefreshTokenAsync("team", "").GetAwaiter().GetResult());
        }

        [TestMethod]
        public async Task TeamsTokens_ManyTeamsAreCheckedConcurrently()
        {
            var state = new CountingKeyValueStore { Delay = TimeSpan.FromMilliseconds(20) };
            var tokens = new TeamsTokenStore(state);
            var teamIds = Enumerable.Range(0, 64).Select(i => $"team-{i}").ToList();

            var started = DateTime.UtcNow;
            var status = await tokens.GetAuthorisationStatusAsync(teamIds);
            var elapsed = DateTime.UtcNow - started;

            Assert.AreEqual(64, status.Count);
            Assert.IsTrue(state.MaxConcurrent > 1, "The checks must overlap.");
            Assert.IsTrue(state.MaxConcurrent <= 16, "...but stay bounded.");
            Assert.IsTrue(elapsed < TimeSpan.FromMilliseconds(64 * 20), $"64 sequential 20 ms checks would take 1.28 s; took {elapsed.TotalMilliseconds:N0} ms.");
        }

        [TestMethod]
        public async Task ChannelDeltaTokens_RoundTripPerTeamAndChannel()
        {
            var state = new InMemoryKeyValueStore();
            var store = new PersistedTeamChannelDeltaTokenStore(state, new RecordingLogger());
            var saved = new TeamChannelDeltaTokenInfo { Token = "synthetic-delta", LastUpdated = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) };

            await store.SetDeltaToken("team-a", "19:general@thread.tacv2", saved);

            var read = await store.GetDeltaToken("team-a", "19:general@thread.tacv2");
            Assert.AreEqual("synthetic-delta", read.Token);
            Assert.AreEqual(saved.LastUpdated, read.LastUpdated.ToUniversalTime());
            Assert.IsNull(await store.GetDeltaToken("team-b", "19:general@thread.tacv2"), "A channel id is only unique within its Team.");
            Assert.IsTrue(await state.ExistsAsync("team-a|19:general@thread.tacv2"));

            await store.RemoveDeltaToken("team-a", "19:general@thread.tacv2");
            Assert.IsNull(await store.GetDeltaToken("team-a", "19:general@thread.tacv2"));
        }

        [TestMethod]
        public async Task AnUnreadableChannelDeltaToken_MeansAFullRead_NotNoRead()
        {
            var state = new InMemoryKeyValueStore();
            await state.SetStringAsync(PersistedTeamChannelDeltaTokenStore.KeyFor("team-a", "channel-1"), "{not json");

            Assert.IsNull(await new PersistedTeamChannelDeltaTokenStore(state, new RecordingLogger()).GetDeltaToken("team-a", "channel-1"));
        }

        [TestMethod]
        public async Task SentEmailDeltaTokens_RoundTrip()
        {
            var store = new PersistedDeltaTokenStore(new InMemoryKeyValueStore());

            await store.SetDeltaToken("SentEmails-alex_contoso.com#EXT#@fabrikam.onmicrosoft.com", "synthetic-delta");

            Assert.AreEqual("synthetic-delta", await store.GetDeltaToken("SentEmails-alex_contoso.com#EXT#@fabrikam.onmicrosoft.com"));
            Assert.IsNull(await store.GetDeltaToken("SentEmails-someone-else@contoso.com"));
        }

        [TestMethod]
        public async Task TheNoMailboxList_RoundTrips_AndAnOutageIsFailOpen()
        {
            var skipList = new MailboxSkipList { GeneratedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Upns = { "a@contoso.com", "b@contoso.com" } };
            var persisted = new PersistedSentEmailMailboxSkipList(new InMemoryKeyValueStore(), new RecordingLogger());

            await persisted.SaveAsync(skipList);
            var loaded = await persisted.LoadAsync();

            CollectionAssert.AreEqual(skipList.Upns, loaded.Upns);
            Assert.AreEqual(skipList.GeneratedUtc, loaded.GeneratedUtc.Value.ToUniversalTime());

            var logger = new RecordingLogger();
            var broken = new PersistedSentEmailMailboxSkipList(new ThrowingKeyValueStore(), logger);
            Assert.AreEqual(0, (await broken.LoadAsync()).Upns.Count, "An unreadable list means every mailbox is checked - never a failed import.");
            await broken.SaveAsync(skipList);
            Assert.AreEqual(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        }

        [TestMethod]
        public async Task ANoMailboxListTooLargeForOneRow_IsSplitIntoPagesThatEachFitARow_AndRoundTrips()
        {
            // Guests and unlicensed accounts have no mailbox, so a 200,000-user tenant can have this many.
            var upns = SyntheticUpns(250000, seed: 1);
            var wholeList = JsonConvert.SerializeObject(upns.OrderBy(u => u, StringComparer.OrdinalIgnoreCase));
            Assert.ThrowsException<InvalidOperationException>(() => AzureTableKeyValueStore.EncodeValue(new TableEntity("p", "r"), "k", wholeList),
                "The list must be one that a single row cannot hold, or this test proves nothing about paging.");

            var state = new InMemoryKeyValueStore();
            var logger = new RecordingLogger();
            var persisted = new PersistedSentEmailMailboxSkipList(state, logger);
            var generated = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

            await persisted.SaveAsync(new MailboxSkipList { GeneratedUtc = generated, Upns = upns });

            var pageCount = (upns.Count + PersistedSentEmailMailboxSkipList.MaxUpnsPerPage - 1) / PersistedSentEmailMailboxSkipList.MaxUpnsPerPage;
            for (var p = 0; p < pageCount; p++)
            {
                var page = await state.GetStringAsync(PersistedSentEmailMailboxSkipList.PageKey(p));
                Assert.IsNotNull(page, $"page {p}");
                AzureTableKeyValueStore.EncodeValue(new TableEntity("p", "r"), PersistedSentEmailMailboxSkipList.PageKey(p), page);
            }
            Assert.IsFalse(await state.ExistsAsync(PersistedSentEmailMailboxSkipList.PageKey(pageCount)));

            var loaded = await persisted.LoadAsync();

            Assert.AreEqual(0, logger.Entries.Count, string.Join("; ", logger.Entries.Select(e => e.Message)));
            Assert.AreEqual(upns.Count, loaded.Upns.Count);
            Assert.IsTrue(loaded.UpnSet.SetEquals(upns));
            Assert.AreEqual(generated, loaded.GeneratedUtc.Value.ToUniversalTime());
        }

        [TestMethod]
        public async Task ASmallerNoMailboxList_RemovesThePagesItNoLongerNeeds()
        {
            var state = new InMemoryKeyValueStore();
            var persisted = new PersistedSentEmailMailboxSkipList(state, new RecordingLogger());
            var generated = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

            await persisted.SaveAsync(new MailboxSkipList { GeneratedUtc = generated, Upns = SyntheticUpns(25000, seed: 2) });
            Assert.IsTrue(await state.ExistsAsync(PersistedSentEmailMailboxSkipList.PageKey(2)), "25,000 UPNs need three pages.");

            await persisted.SaveAsync(new MailboxSkipList { GeneratedUtc = generated, Upns = { "a@contoso.com", "A@CONTOSO.COM" } });

            Assert.IsFalse(await state.ExistsAsync(PersistedSentEmailMailboxSkipList.PageKey(1)));
            Assert.IsFalse(await state.ExistsAsync(PersistedSentEmailMailboxSkipList.PageKey(2)));
            CollectionAssert.AreEqual(new[] { "a@contoso.com" }, (await persisted.LoadAsync()).Upns,
                "UPNs are case-insensitive, so the two spellings are one user.");
        }

        [TestMethod]
        public async Task AnEmptyNoMailboxList_StillRemembersWhenItWasSwept()
        {
            var state = new InMemoryKeyValueStore();
            var persisted = new PersistedSentEmailMailboxSkipList(state, new RecordingLogger());
            var generated = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

            await persisted.SaveAsync(new MailboxSkipList { GeneratedUtc = generated });
            var loaded = await persisted.LoadAsync();

            Assert.AreEqual(0, loaded.Upns.Count);
            Assert.AreEqual(generated, loaded.GeneratedUtc.Value.ToUniversalTime(),
                "Without the sweep time, every cycle would be a full sweep of every mailbox.");
            Assert.IsFalse(await state.ExistsAsync(PersistedSentEmailMailboxSkipList.PageKey(0)));
        }

        [TestMethod]
        public async Task ANoMailboxListWithAMissingPage_ReadsAsEmpty_SoEveryMailboxIsChecked()
        {
            var state = new InMemoryKeyValueStore();
            await new PersistedSentEmailMailboxSkipList(state, new RecordingLogger()).SaveAsync(
                new MailboxSkipList { GeneratedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Upns = SyntheticUpns(25000, seed: 3) });
            await state.DeleteAsync(PersistedSentEmailMailboxSkipList.PageKey(1));

            var logger = new RecordingLogger();
            var loaded = await new PersistedSentEmailMailboxSkipList(state, logger).LoadAsync();

            Assert.AreEqual(0, loaded.Upns.Count);
            Assert.IsNull(loaded.GeneratedUtc, "No sweep time, so the next run is a full sweep that rebuilds the list.");
            StringAssert.Contains(logger.Entries.Single(e => e.Level == LogLevel.Warning).Message, "page 2 of 3 is missing");
        }

        [TestMethod]
        public async Task ImportCadenceStamps_RoundTripAsUtc_AndAnOutageNeverSkipsAnImport()
        {
            var store = new PersistedImportLastRunStore(new InMemoryKeyValueStore(), new RecordingLogger());
            var when = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

            await store.SetLastRunUtc("GraphTeamsLastImported", when);
            Assert.AreEqual(when, await store.GetLastRunUtc("GraphTeamsLastImported"));
            await store.Clear("GraphTeamsLastImported");
            Assert.IsNull(await store.GetLastRunUtc("GraphTeamsLastImported"));

            var logger = new RecordingLogger();
            var broken = new PersistedImportLastRunStore(new ThrowingKeyValueStore(), logger);
            Assert.IsNull(await broken.GetLastRunUtc("GraphTeamsLastImported"), "Unreadable = not yet run, so the import proceeds.");
            await broken.SetLastRunUtc("GraphTeamsLastImported", when);
            await broken.Clear("GraphTeamsLastImported");
            Assert.AreEqual(3, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        }

        [TestMethod]
        public async Task TheCognitiveCache_KeysByHashNotText_AndExpiresAfterADay()
        {
            var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new InMemoryKeyValueStore(() => now);
            var cache = new CognitiveResultCache(state, () => now);
            const string text = "msg-1Synthetic Teams message text";

            await cache.SetAsync(text, "{\"KeyPhrases\":[\"synthetic\"]}", new RecordingLogger());

            Assert.AreEqual("{\"KeyPhrases\":[\"synthetic\"]}", await cache.GetAsync(text, new RecordingLogger()));
            Assert.IsTrue(await state.ExistsAsync(CognitiveResultCache.KeyFor(text)));
            Assert.AreEqual(64, CognitiveResultCache.KeyFor(text).Length);
            Assert.IsFalse(CognitiveResultCache.KeyFor(text).Contains("Synthetic"), "The row key must not carry message text.");

            now = now.Add(CognitiveResultCache.TimeToLive).AddMinutes(1);
            Assert.IsNull(await cache.GetAsync(text, new RecordingLogger()));
        }

        [TestMethod]
        public async Task TheCognitiveCache_IsAMissNeverAFailure_WhenStorageIsDown()
        {
            var logger = new RecordingLogger();
            var cache = new CognitiveResultCache(new ThrowingKeyValueStore());

            Assert.IsNull(await cache.GetAsync("text", logger));
            await cache.SetAsync("text", "{}", logger);

            Assert.AreEqual(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        }

        #endregion

        private static AppConfig Config(string storageConnectionString)
        {
            var config = (AppConfig)FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Empty;
            config.ConnectionStrings = (AppConnectionStrings)FormatterServices.GetUninitializedObject(typeof(AppConnectionStrings));
            config.ConnectionStrings.StorageConnectionString = storageConnectionString;
            return config;
        }

        /// <summary>Printable ASCII from a fixed seed: close to incompressible, so gzip cannot shrink it below a chunk.</summary>
        internal static string IncompressibleText(int length, int seed)
        {
            var random = new Random(seed);
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                sb.Append((char)random.Next(0x21, 0x7F));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Distinct synthetic UPNs that compress like real ones rather than like a numbered sequence (which gzip
        /// shrinks far more than any real directory): random names and numbers, one in ten a guest.
        /// </summary>
        internal static System.Collections.Generic.List<string> SyntheticUpns(int count, int seed)
        {
            var first = new[] { "alex", "sam", "maria", "jose", "li", "wei", "anna", "john", "fatima", "omar", "chen", "yuki", "olga", "ivan", "sara", "david", "laura", "pablo", "nina", "raj" };
            var last = new[] { "smith", "garcia", "muller", "rossi", "wang", "kim", "nguyen", "silva", "kowalski", "novak", "johansson", "martin", "bernard", "dubois", "lopez", "schmidt", "weber", "meyer", "wagner", "becker" };
            var random = new Random(seed);
            var upns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (upns.Count < count)
            {
                var name = $"{first[random.Next(first.Length)]}.{last[random.Next(last.Length)]}{random.Next(1, 999999)}";
                upns.Add(random.Next(10) == 0 ? $"{name}_fabrikam.com#EXT#@contoso.onmicrosoft.com" : $"{name}@contoso.onmicrosoft.com");
            }
            return upns.ToList();
        }

        /// <summary>A store whose every operation fails, like an unreachable storage account.</summary>
        private sealed class ThrowingKeyValueStore : IKeyValueStore
        {
            public string Description => "an unreachable store";

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic storage outage");

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic storage outage");

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic storage outage");

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic storage outage");
        }

        /// <summary>An in-memory store that counts value reads and how many operations overlapped.</summary>
        private sealed class CountingKeyValueStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            private int _inFlight;

            public int Reads;
            public int MaxConcurrent;
            public TimeSpan Delay { get; set; } = TimeSpan.Zero;

            public string Description => _inner.Description;

            public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Reads);
                return await Track(() => _inner.GetStringAsync(key, cancellationToken));
            }

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
                => _inner.SetStringAsync(key, value, timeToLive, cancellationToken);

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Track(() => _inner.ExistsAsync(key, cancellationToken));

            private async Task<T> Track<T>(Func<Task<T>> operation)
            {
                var inFlight = Interlocked.Increment(ref _inFlight);
                int seen;
                while (inFlight > (seen = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, inFlight, seen) != seen)
                {
                }

                try
                {
                    if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
                    return await operation();
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        }
    }
}
