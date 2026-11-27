using Azure;
using Azure.Data.Tables;
using Common.Entities.State;
using Common.Entities.Copilot;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.UserScope.Purge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;

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
        public async Task ConcurrentDailyPhaseCompletionsPublishOnlyTheirOwnRetainedDataEvidence()
        {
            var partition = "Phase" + Guid.NewGuid().ToString("N");
            var table = _table ?? Skip();
            PersistedActivityReportsPhaseStore Reopen() => new PersistedActivityReportsPhaseStore(new AzureTableKeyValueStore(table, partition));
            var start = new DateTime(2026, 6, 20, 23, 59, 59, DateTimeKind.Utc);
            DailyPhaseFacts Facts(int index) => index % 2 == 0 ? DailyPhaseFacts.RowsPresent : DailyPhaseFacts.ExpectedEmpty;
            await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Reopen().SavePhaseAsync(start.AddTicks(i), Facts(i))));
            var reader = Reopen();
            var winner = (await reader.GetLastDT()).Value;
            Assert.AreEqual(Facts((int)(winner - start).Ticks), await reader.ReadFactsAsync(winner),
                "The last timestamp and last receipt must be from the same transaction.");
            Assert.IsNull(await reader.ReadFactsAsync(start.AddHours(-1)));

            // Clock resolution or a clock correction can put consecutive successes on exactly the same tick.
            await Reopen().SavePhaseAsync(winner, DailyPhaseFacts.ExpectedEmpty);
            Assert.AreEqual(DailyPhaseFacts.ExpectedEmpty, await reader.ReadFactsAsync(winner));
            await Reopen().SavePhaseAsync(winner, DailyPhaseFacts.RowsPresent);
            Assert.AreEqual(DailyPhaseFacts.RowsPresent, await reader.ReadFactsAsync(winner));
        }

        [TestMethod]
        public async Task EnrichmentQueue_ConcurrentEnqueueAndReopen_KeepOneDurableWorkItem()
        {
            var table = _table ?? Skip();
            var partition = "Enrichment" + Guid.NewGuid().ToString("N");
            var queue = new CopilotEnrichmentQueue(table, partition);
            var eventId = Guid.NewGuid();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => queue.EnqueueAsync(eventId)));

            var reopened = new CopilotEnrichmentQueue(table, partition);
            var due = await reopened.ReadDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 25);
            Assert.AreEqual(1, due.Count);
            Assert.AreEqual(eventId, due[0].EventId);
            var claim = await reopened.TryClaimAsync(due[0], DateTimeOffset.UtcNow);
            Assert.IsNotNull(claim);
            await reopened.CompleteAsync(claim);
            Assert.AreEqual(0, (await reopened.ReadDueAsync(DateTimeOffset.UtcNow.AddDays(1), 25)).Count);
            Assert.AreEqual(0, table.Query<TableEntity>(TableClient.CreateQueryFilter($"PartitionKey eq {partition}")).Count(),
                "Completion removes the anchor and its due index together.");
        }

        [TestMethod]
        public async Task EnrichmentQueue_DeferralAndRepeatedEnqueue_DoNotResetBackoffOrDuplicateWork()
        {
            var table = _table ?? Skip();
            var queue = new CopilotEnrichmentQueue(table, "Enrichment" + Guid.NewGuid().ToString("N"));
            var id = Guid.NewGuid();
            await queue.EnqueueAsync(id);
            var now = DateTimeOffset.UtcNow.AddSeconds(1);
            var first = (await queue.ReadDueAsync(now, 25)).Single();
            var claim = await queue.TryClaimAsync(first, now);
            await queue.DeferAsync(claim, now);
            await queue.EnqueueAsync(id);

            Assert.AreEqual(0, (await queue.ReadDueAsync(now.AddMinutes(1), 25)).Count);
            var due = (await queue.ReadDueAsync(now.AddMinutes(3), 25)).Single();
            var retry = await queue.TryClaimAsync(due, now.AddMinutes(3));
            Assert.AreEqual(1, retry.Attempts);
            await queue.CompleteAsync(retry);
        }

        [TestMethod]
        public async Task EnrichmentQueue_CrashedLeaseExpires_AndStaleWorkerCannotDeleteNewOwnersWork()
        {
            var table = _table ?? Skip();
            var partition = "Enrichment" + Guid.NewGuid().ToString("N");
            var queue = new CopilotEnrichmentQueue(table, partition);
            await queue.EnqueueAsync(Guid.NewGuid());
            var now = DateTimeOffset.UtcNow.AddSeconds(1);
            var due = (await queue.ReadDueAsync(now, 25)).Single();
            var abandoned = await queue.TryClaimAsync(due, now);
            Assert.IsNull(await queue.TryClaimAsync(due, now.AddMinutes(1)), "Live leases prevent overlapping Graph work.");
            var reopened = new CopilotEnrichmentQueue(table, partition);
            var replacement = await reopened.TryClaimAsync(due, now.AddMinutes(11));
            Assert.IsNotNull(replacement);
            try
            {
                await queue.CompleteAsync(abandoned);
                Assert.Fail("A stale worker must not acknowledge a reclaimed item.");
            }
            catch (RequestFailedException ex) { Assert.AreEqual(412, ex.Status); }
            Assert.AreEqual(1, (await queue.ReadDueAsync(now.AddDays(1), 25)).Count);
            await reopened.CompleteAsync(replacement);
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

        [TestMethod]
        public async Task PublicationRangesDistinguishChangedETagsExpiryAndStableMisses()
        {
            var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
            var day = now.UtcDateTime.Date;
            var values = new AzureTableKeyValueStore(_table ?? Skip(), "Publication" + Guid.NewGuid().ToString("N"), () => now);
            var store = new UsageReportCoverageStore(values, true);
            await values.SetStringAsync("v1:teams:20260514", "complete");
            await values.SetStringAsync("v1:outlook:20260514", "empty", TimeSpan.FromHours(1));
            await values.SetStringAsync("v1:sharepoint:20260514", "empty");
            await values.SetStringAsync("v1:onedrive:20260513", "partial");
            await values.SetStringAsync("v1:onedrive:20260515", "partial");
            var read = await UsageReportCoverageStore.BeginPublicationAsync(UsageReportCoverageStore.Sources, day, day, () => store);

            await values.SetStringAsync("v1:teams:20260514", "partial");
            await values.SetStringAsync("v1:teams:20260514", "complete");
            now = now.AddHours(2);
            var outcomes = await read.ReadStableOutcomesAsync(UsageReportCoverageStore.Sources);
            Assert.AreEqual("partial", outcomes["teams"][day], "The same text under another ETag is not the same certificate.");
            Assert.AreEqual("partial", outcomes["outlook"][day], "Expiry changes the value even though the row's ETag did not change.");
            Assert.AreEqual("empty", outcomes["sharepoint"][day]);
            Assert.IsNull(outcomes["onedrive"][day], "Adjacent dates are outside this inclusive one-day key range.");
        }

        [TestMethod]
        public async Task UsagePurgeFencesOnlyAffectedDaysAndOnlyAFreshWholeReportCanVerifyThemAgain()
        {
            var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
            var values = new AzureTableKeyValueStore(_table ?? Skip(), "Usage" + Guid.NewGuid().ToString("N"), () => now);
            var store = new UsageReportCoverageStore(values, true);
            var day = now.UtcDateTime.Date.AddDays(-4);
            var request = now.UtcDateTime;
            await store.CompleteDayAsync("teams", day, true, true, false, request, 3);
            await store.StartScopePurgeAsync("first", () => Task.CompletedTask);
            await store.RevokeScopeDaysAsync("first", "teams", new[] { day }, CancellationToken.None);
            await store.BeginDayAsync("teams", day);
            await store.CompleteDayAsync("teams", day, true, true, false, request, 3);
            Assert.IsFalse(await store.IsDayVerifiedAsync("teams", day));
            await store.CompleteDayAsync("outlook", day, true, true, true, request, 3);
            Assert.IsTrue(await store.IsDayVerifiedAsync("outlook", day), "An unrelated settled empty remains valid during the purge.");

            await store.EndScopePurgeAsync("first", request);
            await store.CompleteDayAsync("teams", day, true, true, false, request, 3);
            Assert.IsFalse(await store.IsDayVerifiedAsync("teams", day), "A fetch that overlaps the purge cannot certify.");
            await store.CompleteDayAsync("teams", day, true, true, false, request.AddSeconds(1), 3);
            Assert.IsTrue(await store.IsDayVerifiedAsync("teams", day), "A later complete tenant-wide download can certify.");
            await store.StartScopePurgeAsync("second", () => Task.CompletedTask);
            await store.CompleteDayAsync("teams", day, true, true, false, request.AddSeconds(1), 3);
            Assert.IsTrue(await store.IsDayVerifiedAsync("teams", day), "A later purge has not touched this date.");
            await store.CompleteDayAsync("teams", day, true, true, true, request.AddSeconds(-1), 3);
            Assert.IsFalse(await store.IsDayVerifiedAsync("teams", day), "An older download finishing last cannot restore its certificate.");

            now = now.AddDays(401);
            var outcomes = await store.ReadDailyOutcomesAsync(day, day);
            Assert.IsNull(outcomes["outlook"][day], "Ordinary conditional writes keep their normal expiry.");
            Assert.AreEqual(UsageReportCoverageStore.ScopeUnverified, outcomes["teams"][day],
                "A purged day never becomes a legacy key miss that would trust surviving SQL rows.");
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ScopePurge_SecondTableTransactionFailure_RevokesOnlyDeletedOrUnacknowledgedDays(bool loseResponse)
        {
            const int days = 205;
            var first = new DateTime(2020, 1, 1);
            var table = _table ?? Skip();
            var partition = "Purge" + Guid.NewGuid().ToString("N");
            var values = new AzureTableKeyValueStore(table, partition);
            string DayKey(DateTime d) => "v1:teams:" + d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            await values.SetManyAsync(Enumerable.Range(0, days).Select(i =>
                new KeyValuePair<string, string>(DayKey(first.AddDays(i)), UsageReportCoverageStore.Complete)).ToList());

            using (var db = UserScopePurgeSafetyTests.CreateMigratedDatabase())
            {
                db.Execute("INSERT dbo.users(user_name) VALUES ('outside@contoso.example'), ('inside@contoso.example')");
                var outside = (int)db.Scalar("SELECT id FROM dbo.users WHERE user_name = 'outside@contoso.example'");
                UserScopePurgeSafetyTests.SeedTeamsDays(db, days, "u.user_name IN ('outside@contoso.example', 'inside@contoso.example')");
                var unacknowledged = new HashSet<string>();
                var rejecting = new InterceptTransactions(table.Name, n => n == 2 && !loseResponse
                    ? throw new RequestFailedException(503, "Synthetic rejection before sending the second transaction.") : Task.CompletedTask,
                    (n, actions) =>
                    {
                        if (n != 2 || !loseResponse) return;
                        foreach (var action in actions.Where(a => a.ActionType == TableTransactionActionType.UpsertReplace))
                            unacknowledged.Add(action.Entity.RowKey);
                        throw new RequestFailedException(503, "Synthetic lost acknowledgement after the second transaction committed.");
                    });
                var coverage = new UsageReportCoverageStore(new AzureTableKeyValueStore(rejecting, partition), true);
                var state = new UserScopePurgeStateStore(NewStore(), true);
                var job = await state.CreateAsync("admin@contoso.example", null, 1, 1);
                var engine = new UserScopePurgeEngine(new UserScopePurgeDatabase(db.ConnectionString), state, null, coverage)
                    { CandidatesOverride = new[] { outside } };

                Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await engine.RunAsync(job.Id, null));
                Assert.AreEqual(UserScopePurgeErrorCodes.StateUnavailable, (await state.GetAsync(job.Id)).ErrorCode);
                Assert.AreEqual(2, rejecting.Transactions);
                var remaining = Convert.ToString(db.Scalar($"SELECT STRING_AGG(CONVERT(char(8), [date], 112), ',') FROM dbo.teams_user_activity_log WHERE user_id = {outside}"))
                    .Split(',').ToHashSet();
                var deleted = days - remaining.Count;
                Assert.IsTrue(deleted > 0 && deleted <= 100, $"The first acknowledged Table unit had a matching SQL delete, not {deleted} rows deleted.");
                Assert.AreEqual((long)deleted, engine.Job.RowsAffected["teams_user_activity_log"]);
                for (var i = 0; i < days; i++)
                {
                    var day = first.AddDays(i);
                    Assert.AreEqual(remaining.Contains(day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) && !unacknowledged.Contains(DayKey(day)),
                        await coverage.IsDayVerifiedAsync("teams", day), "Coverage must match the actual retained rows for " + DayKey(day));
                }
                Assert.AreEqual(loseResponse, unacknowledged.Count > 0);
                Assert.IsTrue(unacknowledged.Count < remaining.Count, "Dates beyond the failed unit are still untouched.");
                Assert.AreEqual(days, db.Scalar($"SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id <> {outside}"));

                var again = await state.CreateAsync("admin@contoso.example", null, 1, 1);
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(again.Id, null));
                Assert.AreEqual(0, db.Scalar($"SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = {outside}"));
            }
        }

        [TestMethod]
        public async Task ScopePurge_SlowRevocationReleasesSqlLocksWithoutWaitingForTheTransport()
        {
            var table = _table ?? Skip();
            var sending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new InterceptTransactions(table.Name, async _ => { sending.TrySetResult(true); await release.Task; });
            var coverage = new UsageReportCoverageStore(new AzureTableKeyValueStore(delayed, "Purge" + Guid.NewGuid().ToString("N")), true);
            using (var db = UserScopePurgeSafetyTests.CreateMigratedDatabase())
            {
                db.Execute("INSERT dbo.users(user_name) VALUES ('outside@contoso.example')");
                var outside = (int)db.Scalar("SELECT id FROM dbo.users WHERE user_name='outside@contoso.example'");
                UserScopePurgeSafetyTests.SeedTeamsDays(db, 1, $"u.id = {outside}");
                var state = new UserScopePurgeStateStore(NewStore(), true);
                var job = await state.CreateAsync("admin@contoso.example", null, 1, 1);
                var engine = new UserScopePurgeEngine(new UserScopePurgeDatabase(db.ConnectionString), state, null, coverage)
                    { CandidatesOverride = new[] { outside }, CoverageWriteTimeout = TimeSpan.FromMilliseconds(250) };
                var run = engine.RunAsync(job.Id, null);
                try
                {
                    Assert.AreSame(sending.Task, await Task.WhenAny(sending.Task, run, Task.Delay(TimeSpan.FromSeconds(30))));
                    Assert.AreSame(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))), "The transport is still blocked.");
                    Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await run);
                    Assert.AreEqual(1, db.Scalar($"SET LOCK_TIMEOUT 1000; SELECT COUNT(*) FROM dbo.teams_user_activity_log WITH (READCOMMITTEDLOCK) WHERE user_id = {outside}"));
                }
                finally { release.TrySetResult(true); await run; }
            }
        }

        [TestMethod]
        public async Task ScopePurge_AlreadySentStaleRevocationCannotUndoAReplacementAndAFreshImport()
        {
            var table = _table ?? Skip();
            var partition = "Purge" + Guid.NewGuid().ToString("N");
            var other = new UsageReportCoverageStore(new AzureTableKeyValueStore(table, partition), true);
            var day = new DateTime(2020, 1, 1);
            var ended = DateTime.UtcNow;
            await other.StartScopePurgeAsync("original", () => Task.CompletedTask);
            await other.RevokeScopeDaysAsync("original", "teams", new[] { day }, CancellationToken.None);
            var delayed = new InterceptTransactions(table.Name, async _ =>
            {
                // The old worker already read its fence ETag. Change the actual Table rows before the
                // pending transaction reaches the service; a read/check followed by an ordinary upsert loses here.
                var proofs = 0;
                await other.StartScopePurgeAsync("replacement", async () =>
                {
                    // Another old request lands after the replacement read the ETag. The replacement may
                    // still claim, but only after proving SQL ownership again on the new ETag.
                    if (++proofs == 1) await other.RevokeScopeDaysAsync("original", "teams", new[] { day }, CancellationToken.None);
                }, ownsEarlierFence: owner => owner == "original");
                Assert.AreEqual(2, proofs);
                await other.EndScopePurgeAsync("replacement", ended);
                await other.CompleteDayAsync("teams", day, true, true, false, ended.AddSeconds(1), 3);
                Assert.IsTrue(await other.IsDayVerifiedAsync("teams", day));
            });
            var original = new UsageReportCoverageStore(new AzureTableKeyValueStore(delayed, partition), true);
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() =>
                original.RevokeScopeDaysAsync("original", "teams", new[] { day }, CancellationToken.None));
            Assert.AreEqual(1, delayed.Transactions);
            Assert.IsTrue(await other.IsDayVerifiedAsync("teams", day));
        }

        private sealed class InterceptTransactions : TableClient
        {
            private readonly Func<int, Task> _beforeSend;
            private readonly Action<int, IReadOnlyList<TableTransactionAction>> _afterCommit;
            public int Transactions { get; private set; }
            public InterceptTransactions(string name, Func<int, Task> beforeSend, Action<int, IReadOnlyList<TableTransactionAction>> afterCommit = null)
                : base(DevelopmentStorage, name) { _beforeSend = beforeSend; _afterCommit = afterCommit; }

            public override async Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(IEnumerable<TableTransactionAction> transactionActions,
                CancellationToken cancellationToken = default)
            {
                var n = ++Transactions;
                var actions = transactionActions.ToList();
                await _beforeSend(n);
                var result = await base.SubmitTransactionAsync(actions, cancellationToken);
                _afterCommit?.Invoke(n, actions);
                return result;
            }
        }

        [TestMethod]
        public async Task ScopePurge_AdoptsItsLegacyFenceButNeverAFutureRunOrAnotherJobsFence()
        {
            var state = new UserScopePurgeStateStore(NewStore(), true);
            var ledger = NewStore();
            var coverage = new UsageReportCoverageStore(ledger, true);
            var job = await state.CreateAsync("admin@contoso.example", null, 1, 1);
            job.RunToken = Guid.NewGuid().ToString("N"); // Pre-handoff-format record.
            await state.SaveAsync(job);
            Task Check() => Task.CompletedTask;
            await coverage.StartScopePurgeAsync(job.RunToken, Check);
            var older = await state.TryClaimAsync(job.Id, Check, CancellationToken.None);
            var newer = await state.TryClaimAsync(job.Id, Check, CancellationToken.None);
            await coverage.StartScopePurgeAsync(newer.RunToken, Check, ownsEarlierFence: newer.OwnsEarlierFence);
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() =>
                coverage.StartScopePurgeAsync(older.RunToken, Check, ownsEarlierFence: older.OwnsEarlierFence));
            var unrelated = new UserScopePurgeJob();
            unrelated.ClaimNewRun();
            unrelated.ClaimNewRun();
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() =>
                coverage.StartScopePurgeAsync(unrelated.RunToken, Check, ownsEarlierFence: unrelated.OwnsEarlierFence));
            Assert.AreEqual("active:" + newer.RunToken, await ledger.GetStringAsync("v1:scopePurge"));
        }

        [TestMethod]
        public async Task ScopePurge_RecoveryNamespaceIsolatedFromLegacyFenceAndCertificates()
        {
            var ledger = NewStore();
            var legacy = new UsageReportCoverageStore(ledger, true);
            var day = new DateTime(2020, 1, 1);
            var legacyToken = Guid.NewGuid().ToString("N");
            Task Check() => Task.CompletedTask;
            await legacy.CompleteDayAsync("outlook", day, true, true, true, day.AddDays(5), 3);
            await legacy.StartScopePurgeAsync(legacyToken, Check);
            var fenceBefore = await ledger.GetVersionedAsync("v1:scopePurge");
            var generation = Guid.NewGuid().ToString("N");
            var recovered = new UsageReportCoverageStore(ledger, true, null, generation);
            var reopened = new UsageReportCoverageStore(ledger, true, null, generation);
            var publication = await UsageReportCoverageStore.BeginPublicationAsync(
                new[] { "outlook" }, day, day, () => reopened);
            var outcomes = await publication.ReadStableOutcomesAsync(new[] { "outlook" });
            Assert.AreEqual(UsageReportCoverageStore.Partial, outcomes["outlook"][day], "Missing coverage is not a legacy fallback.");

            await recovered.StartScopePurgeAsync("replacement", Check);
            await reopened.RevokeScopeDaysAsync("replacement", "teams", new[] { day }, CancellationToken.None);
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() => reopened.StartScopePurgeAsync("unrelated", Check));
            var ended = DateTime.UtcNow;
            await recovered.EndScopePurgeAsync("replacement", ended);
            await reopened.CompleteDayAsync("teams", day, true, true, false, ended, 3);
            Assert.IsFalse(await reopened.IsDayVerifiedAsync("teams", day));
            await reopened.CompleteDayAsync("teams", day, true, true, false, ended.AddSeconds(1), 3);
            Assert.IsTrue(await recovered.IsDayVerifiedAsync("teams", day));
            Assert.AreEqual(fenceBefore.VersionToken, (await ledger.GetVersionedAsync("v1:scopePurge")).VersionToken);

            // Even a request still addressed to the old namespace cannot change the replacement's days.
            await legacy.RevokeScopeDaysAsync(legacyToken, "teams", new[] { day }, CancellationToken.None);
            Assert.IsTrue(await recovered.IsDayVerifiedAsync("teams", day));
            Assert.AreEqual(fenceBefore.Value, (await ledger.GetVersionedAsync("v1:scopePurge")).Value);
            Assert.IsTrue(await legacy.IsDayVerifiedAsync("outlook", day));
            Assert.IsFalse(await recovered.IsDayVerifiedAsync("outlook", day));
        }

        [TestMethod]
        public async Task ScopePurge_StaleTerminalWriteCannotReplaceAnotherOwnersState()
        {
            var table = _table ?? Skip();
            var partition = "Purge" + Guid.NewGuid().ToString("N");
            var state = new UserScopePurgeStateStore(new AzureTableKeyValueStore(table, partition), true);
            var queued = await state.CreateAsync("admin@contoso.example", null, 1, 1);
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() => state.SaveOwnedAsync(queued, CancellationToken.None));
            var first = await state.TryClaimAsync(queued.Id, () => Task.CompletedTask, CancellationToken.None);
            var progress = await state.GetAsync(queued.Id);
            progress.RowsAffected["audit_events"] = 1;
            first.State = UserScopePurgeStates.Completed;
            var calls = 0;
            var delayed = new UserScopePurgeStateStore(new AzureTableKeyValueStore(async _ =>
            {
                if (++calls == 2) // The old terminal write already read its ETag, but has not reached the service.
                {
                    var proofs = 0;
                    var next = await state.TryClaimAsync(queued.Id, async () =>
                    {
                        // A valid but delayed old progress write wins the first claim ETag.
                        if (++proofs == 1) await state.SaveOwnedAsync(progress, CancellationToken.None);
                    }, CancellationToken.None);
                    Assert.AreEqual(2, proofs);
                    next.State = UserScopePurgeStates.Failed;
                    next.ErrorCode = UserScopePurgeErrorCodes.ScopeMembershipChanged;
                    await state.SaveOwnedAsync(next, CancellationToken.None);
                }
                return table;
            }, table.Name, partition), true);

            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() => delayed.SaveOwnedAsync(first, CancellationToken.None));
            var saved = await state.GetLatestAsync();
            Assert.AreEqual(UserScopePurgeStates.Failed, saved.State);
            Assert.AreEqual(UserScopePurgeErrorCodes.ScopeMembershipChanged, saved.ErrorCode);
            Assert.AreEqual(1L, saved.RowsAffected["audit_events"]);
            Assert.AreNotEqual(first.RunToken, saved.RunToken);
        }

        [TestMethod]
        public async Task ScopePurge_ReconnectFencesAnUnacknowledgedReplacementClaimBeforeReloadingCandidates()
        {
            var table = _table ?? Skip();
            var partition = "Purge" + Guid.NewGuid().ToString("N");
            var state = new UserScopePurgeStateStore(new AzureTableKeyValueStore(table, partition), true);
            var job = await state.CreateAsync("admin@contoso.example", null, 1, 1);
            using (var db = UserScopePurgeSafetyTests.CreateMigratedDatabase())
            {
                db.Execute("INSERT dbo.users(user_name) VALUES ('outside@contoso.example')");
                var outside = (int)db.Scalar("SELECT id FROM dbo.users WHERE user_name='outside@contoso.example'");
                UserScopePurgeSafetyTests.SeedTeamsDays(db, 2, "u.user_name='outside@contoso.example'");
                var database = new UserScopePurgeDatabase(db.ConnectionString);
                using (var original = await database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
                {
                    var engine = new UserScopePurgeEngine(database, state, null, new UsageReportCoverageStore(NewStore(), true))
                        { CandidatesOverride = new[] { outside }, BatchSizeOverride = 1, StateCheckInterval = TimeSpan.Zero };
                    var rejected = false;
                    engine.AfterWindow = async (step, window) =>
                    {
                        if (window != 1 || UserScopePurgePlan.Steps[step - 1].Table != "teams_user_activity_log") return;
                        original.Connection.Close();
                        using (var replacement = await database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
                        {
                            var sendingClaim = false;
                            var delayed = new UserScopePurgeStateStore(new AzureTableKeyValueStore(async token =>
                            {
                                if (sendingClaim)
                                {
                                    // The replacement read the job ETag and proved its SQL lock, but lost the SQL
                                    // session before Table received the claim. The previous owner now reacquires.
                                    sendingClaim = false;
                                    replacement.Connection.Close();
                                    await original.EnsureOpenAsync(token);
                                }
                                return table;
                            }, table.Name, partition), true);
                            try
                            {
                                await delayed.TryClaimAsync(job.Id, async () =>
                                {
                                    await replacement.CheckLockAsync(CancellationToken.None);
                                    sendingClaim = true;
                                }, CancellationToken.None);
                            }
                            catch (UserScopePurgeLockLostException) { rejected = true; }
                        }
                    };
                    Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(job.Id, null, lockedSession: original));
                    Assert.IsTrue(rejected, "The delayed claim never owned the job, even after its SQL lock became free.");
                    Assert.AreEqual(engine.Job.RunToken, (await state.GetAsync(job.Id)).RunToken);
                    Assert.AreEqual(0, db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id={outside}"));
                }
            }
        }

        [TestMethod]
        public async Task ScopePurge_StopSeenWhileOpeningTheCoverageFencePrecedesScopePreflight()
        {
            var table = _table ?? Skip();
            var state = new UserScopePurgeStateStore(NewStore(), true);
            var job = await state.CreateAsync("admin@contoso.example", null, 1, 1);
            using (var db = ScratchDatabase.Create("PurgeFenceStop"))
            {
                var database = new UserScopePurgeDatabase(db.ConnectionString);
                using (var session = await database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
                {
                    var calls = 0;
                    var coverage = new UsageReportCoverageStore(new AzureTableKeyValueStore(async _ =>
                    {
                        if (++calls == 1)
                        {
                            session.Connection.Close();
                            Assert.IsTrue(await state.RequestCancelAsync(job.Id));
                        }
                        return table;
                    }, table.Name, "Purge" + Guid.NewGuid().ToString("N")), true);
                    var engine = new UserScopePurgeEngine(database, state, null, coverage);
                    Assert.AreEqual(UserScopePurgeRunOutcome.Cancelled, await engine.RunAsync(job.Id, null, lockedSession: session));
                    Assert.IsNull((await state.GetAsync(job.Id)).ErrorCode, "No scope was available, but cancellation was already observed.");
                }
            }
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

        /// <summary>
        /// Several portal instances starting a Copilot audit backfill at once: exactly one job is created and becomes
        /// the latest. The rest lose on the job row's insert (409, the same id in the same second) or on the
        /// latest-job pointer's ETag (412, a different id), and are refused.
        /// </summary>
        [TestMethod]
        public async Task CopilotAuditBackfill_ConcurrentStarts_CreateExactlyOneJob()
        {
            var partition = "Test" + Guid.NewGuid().ToString("N");
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var stores = Enumerable.Range(0, 6).Select(i => new CopilotAuditBackfillStateStore(
                new AzureTableKeyValueStore(_table ?? Skip(), partition), isDurable: true, utcNow: () => now.AddSeconds(i % 2))).ToList();

            var jobs = await Task.WhenAll(stores.Select((store, i) => Task.Run(() =>
                store.TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin" + i + "@contoso.com", copilotImportEnabled: true))));

            var created = jobs.Where(j => j != null).ToList();
            Assert.AreEqual(1, created.Count, "Exactly one start creates a job.");
            var latest = await stores[0].GetLatestAsync();
            Assert.AreEqual(created[0].Id, latest.Id);
            Assert.AreEqual(created[0].RequestedBy, latest.RequestedBy, "The latest job is the one its requester was told was started.");
            Assert.IsNull(await stores[0].TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin@contoso.com", copilotImportEnabled: true),
                "A start while that job is queued is refused.");
        }

        [TestMethod]
        public async Task CopilotAuditBackfill_PurgeCannotRemoveTheRequestBeforeItsRenewedJob()
        {
            var now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
            var partition = "Test" + Guid.NewGuid().ToString("N");
            var values = new AzureTableKeyValueStore(_table ?? Skip(), partition, () => now);
            var state = new CopilotAuditBackfillStateStore(values, true, () => now);
            var job = await state.TryCreateWhenIdleAsync(now.AddDays(-2), now, "admin@contoso.com", true);
            Assert.IsTrue(await state.RequestCancelAsync(job.Id));
            now = now.AddMinutes(1);
            await state.SaveAsync(job);

            now = now.AddDays(180).AddSeconds(-1);
            await values.PurgeExpiredAsync();
            Assert.IsTrue((await state.GetLatestAsync()).CancelRequested);
            now = now.AddSeconds(1);
            Assert.IsNull(await state.GetLatestAsync());
            var next = await state.TryCreateWhenIdleAsync(now.AddDays(-1), now, "admin@contoso.com", true);
            Assert.IsFalse((await state.GetAsync(next.Id)).CancelRequested);

            now = now.AddDays(180);
            await values.PurgeExpiredAsync();
            var retired = await values.GetVersionedAsync("Job:" + job.Id + ":Cancel");
            Assert.IsNull(retired.VersionToken, "The retired marker should no longer occupy a table row.");
        }

        private static AzureTableKeyValueStore NewStore()
        {
            return new AzureTableKeyValueStore(_table ?? Skip(), "Test" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// The sent-email checkpoints of a chunk are written in one round trip rather than one per mailbox. A partition accepts at most
        /// 100 operations per transaction, so 250 keys need several; row keys that must be escaped (a UPN with '#') and a
        /// value too big for one string property (stored gzipped) must work inside a transaction too, and a key repeated in the
        /// batch takes its last value.
        /// </summary>
        [TestMethod]
        public async Task SetMany_WritesAcrossSeveralTransactions_ReplacesExistingRows_AndTheLastValueForAKeyWins()
        {
            var store = NewStore();
            await store.SetStringAsync("SentEmails-user7@contoso.example", "old-token");
            await store.SetStringAsync("SentEmails-untouched@contoso.example", "kept");

            var values = Enumerable.Range(0, 250)
                .Select(i => new System.Collections.Generic.KeyValuePair<string, string>("SentEmails-user" + i + "@contoso.example", "token-" + i))
                .ToList();
            values.Add(new System.Collections.Generic.KeyValuePair<string, string>("SentEmails-alex#sales@contoso.example", "hash-key-token"));
            var huge = string.Join(",", Enumerable.Range(0, 20000).Select(i => "Καλημέρα" + i));
            values.Add(new System.Collections.Generic.KeyValuePair<string, string>("SentEmailsNextPage-huge", huge));
            values.Add(new System.Collections.Generic.KeyValuePair<string, string>("SentEmails-user3@contoso.example", "token-3-newest"));

            await store.SetManyAsync(values);

            for (var i = 0; i < 250; i++)
            {
                var expected = i == 3 ? "token-3-newest" : "token-" + i;
                Assert.AreEqual(expected, await store.GetStringAsync("SentEmails-user" + i + "@contoso.example"), "key " + i);
            }
            Assert.AreEqual("hash-key-token", await store.GetStringAsync("SentEmails-alex#sales@contoso.example"));
            Assert.AreEqual(huge, await store.GetStringAsync("SentEmailsNextPage-huge"));
            Assert.AreEqual("kept", await store.GetStringAsync("SentEmails-untouched@contoso.example"), "Rows that are not in the batch are left alone.");
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

            var job = await state.CreateAsync("admin@contoso.local", "fingerprint", 5, 3, "sha256:membership");
            job.State = UserScopePurgeStates.Running;
            job.RowsAffected["call_sessions.attendee_user_id"] = 2;
            job.RowsAffected["audit_events"] = 1234;
            await state.SaveAsync(job);

            var latest = await state.GetLatestAsync();
            Assert.AreEqual(job.Id, latest.Id);
            Assert.AreEqual(UserScopePurgeStates.Running, latest.State);
            Assert.AreEqual(2L, latest.RowsAffected["call_sessions.attendee_user_id"]);
            Assert.AreEqual(1234L, latest.RowsAffected["audit_events"]);
            Assert.AreEqual(3, latest.ConfirmedOutsideScopeCount, "What the administrator confirmed survives a restart with the record...");
            Assert.AreEqual("sha256:membership", latest.ScopeMembershipFingerprint, "...and so does whom it was confirmed against.");
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
