extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserOrgs;
using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>The change log's keys, its in-memory store, and the Table Storage entity shape.</summary>
    [TestClass]
    public class UserOrgChangeLogTests
    {
        private const string GreekOrgName = "Καλημέρα κόσμε";

        private static UserOrgChangeRecord Change(int userId, string upn, string before, string after)
        {
            return new UserOrgChangeRecord { UserId = userId, Upn = upn, OldValue = before, NewValue = after };
        }

        private static UserOrgChangeLogImport Import(string logId, int jobId = 1)
        {
            return new UserOrgChangeLogImport { LogId = logId, JobId = jobId, OrgTypeId = 7, OrgTypeName = "Cost Centre" };
        }

        private static async Task Write(IUserOrgChangeLog log, string logId, params UserOrgChangeRecord[] changes)
        {
            var import = Import(logId);
            await log.AppendAsync(import, changes, CancellationToken.None);
            import.Added = changes.Count(c => c.Kind == UserOrgChangeKind.Added);
            import.Changed = changes.Count(c => c.Kind == UserOrgChangeKind.Changed);
            import.Cleared = changes.Count(c => c.Kind == UserOrgChangeKind.Cleared);
            import.StoredChanges = import.ChangeCount;
            await log.CompleteAsync(import, CancellationToken.None);
        }

        #region Keys

        [TestMethod]
        public void ALogIdTellsTwoImportsWithTheSameJobIdApart()
        {
            // A storage account kept across a reinstall sees job 1 again. Its changes must not land in the
            // old job 1's list.
            var first = UserOrgChangeLogKeys.LogId(1, new DateTime(2026, 1, 1, 9, 0, 0));
            var second = UserOrgChangeLogKeys.LogId(1, new DateTime(2026, 6, 1, 9, 0, 0));

            Assert.AreNotEqual(first, second);
            StringAssert.StartsWith(first, "1-");
        }

        [TestMethod]
        public void ChangeKeysSortByUpnAndEscapeWhatTableStorageRefuses()
        {
            // A guest's UPN carries #EXT#, and '#' is not allowed in a Table Storage key.
            var guest = UserOrgChangeLogKeys.ChangeRowKey("Alex_Fabrikam.com#EXT#@contoso.onmicrosoft.com", 12);

            Assert.IsFalse(guest.Contains("#"), guest);
            Assert.IsTrue(guest.StartsWith("u:alex_fabrikam.com%23ext%23@contoso.onmicrosoft.com|", StringComparison.Ordinal), guest);
            Assert.AreEqual(
                UserOrgChangeLogKeys.ChangeRowKey("adele@contoso.com", 1),
                UserOrgChangeLogKeys.ChangeRowKey("ADELE@contoso.com", 1),
                "Case must not change the key: UPNs are case-insensitive.");
            Assert.AreNotEqual(
                UserOrgChangeLogKeys.ChangeRowKey("adele@contoso.com", 1),
                UserOrgChangeLogKeys.ChangeRowKey("adele@contoso.com", 2),
                "Two users can never share a key.");
            Assert.IsTrue(
                string.CompareOrdinal(UserOrgChangeLogKeys.ChangeRowKey("adele@contoso.com", 99), UserOrgChangeLogKeys.ChangeRowKey("alex@contoso.com", 1)) < 0,
                "Changes list alphabetically by UPN.");
            Assert.IsTrue(
                string.CompareOrdinal(UserOrgChangeLogKeys.SummaryRowKey, UserOrgChangeLogKeys.ChangePrefix) < 0,
                "The summary row sorts before every change.");
        }

        [TestMethod]
        public void ASearchPrefixCoversExactlyTheKeysThatStartWithIt()
        {
            var prefix = UserOrgChangeLogKeys.SearchPrefix("  Adele ");
            var upper = UserOrgChangeLogKeys.UpperBound(prefix);

            foreach (var upn in new[] { "adele@contoso.com", "adele.vance@contoso.com", "adele~x@contoso.com" })
            {
                var key = UserOrgChangeLogKeys.ChangeRowKey(upn, 1);
                Assert.IsTrue(string.CompareOrdinal(key, prefix) >= 0 && string.CompareOrdinal(key, upper) < 0, upn);
            }

            foreach (var upn in new[] { "adel@contoso.com", "adelf@contoso.com", "alex@contoso.com" })
            {
                var key = UserOrgChangeLogKeys.ChangeRowKey(upn, 1);
                Assert.IsFalse(string.CompareOrdinal(key, prefix) >= 0 && string.CompareOrdinal(key, upper) < 0, upn);
            }

            Assert.AreEqual(UserOrgChangeLogKeys.ChangePrefix, UserOrgChangeLogKeys.SearchPrefix(null), "No search is every change.");
        }

        #endregion

        #region In memory

        [TestMethod]
        public async Task TheMemoryLogListsChangesByUpnInPages()
        {
            var log = new InMemoryUserOrgChangeLog();
            await Write(
                log,
                "1-1",
                Change(3, "carol@contoso.com", "Retail", null),
                Change(1, "alex@contoso.com", null, GreekOrgName),
                Change(2, "bob@contoso.com", "Retail", "Wholesale"));

            var summary = await log.GetImportAsync("1-1", CancellationToken.None);
            Assert.AreEqual(3, summary.StoredChanges);
            Assert.IsFalse(summary.Truncated);

            var first = await log.GetChangesAsync("1-1", null, null, 2, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "alex@contoso.com", "bob@contoso.com" }, first.Items.Select(i => i.Upn).ToArray());
            Assert.AreEqual(GreekOrgName, first.Items[0].After);
            Assert.AreEqual(UserOrgChangeKind.Added, first.Items[0].Kind);
            Assert.AreEqual(UserOrgChangeKind.Changed, first.Items[1].Kind);
            Assert.IsNotNull(first.Continuation);

            var second = await log.GetChangesAsync("1-1", null, first.Continuation, 2, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "carol@contoso.com" }, second.Items.Select(i => i.Upn).ToArray());
            Assert.AreEqual(UserOrgChangeKind.Cleared, second.Items[0].Kind);
            Assert.IsNull(second.Continuation, "The last page says so.");
        }

        [TestMethod]
        public async Task TheMemoryLogSearchesByUpnPrefixCaseInsensitively()
        {
            var log = new InMemoryUserOrgChangeLog();
            await Write(
                log,
                "1-1",
                Change(1, "adele@contoso.com", null, "A"),
                Change(2, "Adele.Vance@contoso.com", null, "B"),
                Change(3, "alex@contoso.com", null, "C"));

            var page = await log.GetChangesAsync("1-1", "ADELE", null, 50, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Adele.Vance@contoso.com", "adele@contoso.com" }, page.Items.Select(i => i.Upn).ToArray(), "Ordinal key order: a full stop sorts before the at sign.");
            Assert.IsNull(page.Continuation);
        }

        [TestMethod]
        public async Task ALogIsOnlyVisibleOnceItIsComplete()
        {
            var log = new InMemoryUserOrgChangeLog();
            await log.AppendAsync(Import("1-1"), new[] { Change(1, "a@contoso.com", null, "X") }, CancellationToken.None);

            Assert.IsNull(await log.GetImportAsync("1-1", CancellationToken.None));
            Assert.AreEqual(0, (await log.GetChangesAsync("1-1", null, null, 50, CancellationToken.None)).Items.Count);
        }

        [TestMethod]
        public async Task WritingALogAgainDoesNotDuplicateItsChanges()
        {
            // An interrupted write is repeated from the start.
            var log = new InMemoryUserOrgChangeLog();
            var changes = new[] { Change(1, "a@contoso.com", null, "X"), Change(2, "b@contoso.com", "Y", null) };
            await log.AppendAsync(Import("1-1"), changes, CancellationToken.None);
            await Write(log, "1-1", changes);
            await Write(log, "1-1", changes);

            Assert.AreEqual(2, (await log.GetChangesAsync("1-1", null, null, 50, CancellationToken.None)).Items.Count);
            Assert.AreEqual(2, log.StoredChanges);
        }

        [TestMethod]
        public async Task TheMemoryLogDropsTheOldestCompleteLogToMakeRoom()
        {
            var log = new InMemoryUserOrgChangeLog(maxChanges: 3);
            await Write(log, "1-1", Change(1, "a@contoso.com", null, "X"), Change(2, "b@contoso.com", null, "X"));
            await Write(log, "2-2", Change(1, "a@contoso.com", "X", "Y"));
            await Write(log, "3-3", Change(3, "c@contoso.com", null, "Z"), Change(4, "d@contoso.com", null, "Z"));

            Assert.IsNull(await log.GetImportAsync("1-1", CancellationToken.None), "The oldest goes first.");
            Assert.IsNotNull(await log.GetImportAsync("2-2", CancellationToken.None));
            Assert.AreEqual(2, (await log.GetImportAsync("3-3", CancellationToken.None)).StoredChanges);
            Assert.IsTrue(log.StoredChanges <= 3);
        }

        [TestMethod]
        public async Task AnImportTooBigForMemoryKeepsWhatFitsAndSaysSo()
        {
            var log = new InMemoryUserOrgChangeLog(maxChanges: 2);
            await Write(
                log,
                "1-1",
                Change(1, "a@contoso.com", null, "X"),
                Change(2, "b@contoso.com", null, "X"),
                Change(3, "c@contoso.com", null, "X"));

            var summary = await log.GetImportAsync("1-1", CancellationToken.None);
            Assert.AreEqual(3, summary.ChangeCount);
            Assert.AreEqual(2, summary.StoredChanges);
            Assert.IsTrue(summary.Truncated, "The portal must be able to say the list is incomplete.");
        }

        [TestMethod]
        public async Task TheMemoryLogKeepsSoManyImportsEvenWhenTheyChangedNobody()
        {
            // Importing the same file again and again changes nobody, so the change cap alone never evicts
            // anything - one summary each would pile up for as long as the web app runs.
            var log = new InMemoryUserOrgChangeLog(maxChanges: 100, maxLogs: 3);
            for (var job = 1; job <= 5; job++)
            {
                await Write(log, job + "-1");
            }

            Assert.IsNull(await log.GetImportAsync("1-1", CancellationToken.None), "The oldest went first.");
            Assert.IsNull(await log.GetImportAsync("2-1", CancellationToken.None));
            foreach (var kept in new[] { "3-1", "4-1", "5-1" })
            {
                Assert.IsNotNull(await log.GetImportAsync(kept, CancellationToken.None), kept);
            }
        }

        [TestMethod]
        public async Task AnUnfinishedLogIsKeptWhileItsWriterMayStillBeWritingIt()
        {
            var now = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
            var log = new InMemoryUserOrgChangeLog(2, 10, () => now);
            await log.AppendAsync(
                Import("1-1"),
                new[] { Change(1, "a@contoso.com", null, "X"), Change(2, "b@contoso.com", null, "X") },
                CancellationToken.None);

            now = now.Add(InMemoryUserOrgChangeLog.AbandonedAfter).AddSeconds(-1);
            await Write(log, "2-1", Change(3, "c@contoso.com", null, "Y"));

            Assert.AreEqual(0, (await log.GetImportAsync("2-1", CancellationToken.None)).StoredChanges, "Nothing could be dropped for it.");
            Assert.AreEqual(2, log.StoredChanges, "The unfinished log was left alone.");
        }

        [TestMethod]
        public async Task AnAbandonedUnfinishedLogMakesRoomAtLast()
        {
            // Its writer lost the lease part-way, and the retry wrote the import on another instance - into
            // that instance's memory. Nothing will ever finish this copy, and nobody can read it.
            var now = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
            var log = new InMemoryUserOrgChangeLog(2, 10, () => now);
            await log.AppendAsync(
                Import("1-1"),
                new[] { Change(1, "a@contoso.com", null, "X"), Change(2, "b@contoso.com", null, "X") },
                CancellationToken.None);

            now = now.Add(InMemoryUserOrgChangeLog.AbandonedAfter);
            await Write(log, "2-1", Change(3, "c@contoso.com", null, "Y"));

            Assert.AreEqual(1, (await log.GetImportAsync("2-1", CancellationToken.None)).StoredChanges);
            Assert.AreEqual(1, log.StoredChanges, "The abandoned log's changes were released.");
        }

        [TestMethod]
        public async Task AChangeThatDoesNotFitHoldsNoMemory()
        {
            // The cap bounds memory only if a change that is dropped keeps nothing. Interning its values
            // before deciding would hold every distinct value of a 500,000-row file of distinct values.
            var log = new InMemoryUserOrgChangeLog(maxChanges: 2);
            var changes = Enumerable.Range(1, 50)
                .Select(i => Change(i, "u" + i + "@contoso.com", "Before " + i, "After " + i))
                .ToArray();

            await Write(log, "1-1", changes);

            Assert.AreEqual(2, log.StoredChanges);
            Assert.AreEqual(4, log.InternedValues, "Two changes stored, each with a before and an after - nothing for the 48 dropped.");
        }

        [TestMethod]
        public async Task AContinuationTheLogDidNotIssueIsRefused()
        {
            var log = new InMemoryUserOrgChangeLog();
            await Write(log, "1-1", Change(1, "a@contoso.com", null, "X"));

            try
            {
                await log.GetChangesAsync("1-1", null, "not-a-key", 50, CancellationToken.None);
                Assert.Fail("A forged continuation must be refused.");
            }
            catch (UserOrgValidationException ex)
            {
                Assert.AreEqual(UserOrgImportRefusalCodes.ChangePageExpired, ex.Code);
            }
        }

        #endregion

        #region Table Storage entities

        [TestMethod]
        public void AChangeRoundTripsThroughItsTableEntity()
        {
            var entity = TableStorageUserOrgChangeLog.ToEntity("5-99", Change(12, "Adele@contoso.com", null, GreekOrgName));

            Assert.AreEqual("5-99", entity.PartitionKey);
            Assert.AreEqual(UserOrgChangeLogKeys.ChangeRowKey("Adele@contoso.com", 12), entity.RowKey);
            Assert.IsFalse(entity.ContainsKey("Before"), "No value is an absent property, not a stored null.");

            var back = TableStorageUserOrgChangeLog.FromEntity(entity);
            Assert.AreEqual("Adele@contoso.com", back.Upn, "The UPN is shown as written, not as the lower-case key.");
            Assert.IsNull(back.Before);
            Assert.AreEqual(GreekOrgName, back.After);
            Assert.AreEqual(UserOrgChangeKind.Added, back.Kind);
        }

        [TestMethod]
        public void ASummaryRoundTripsThroughItsTableEntity()
        {
            var import = new UserOrgChangeLogImport
            {
                LogId = "5-99",
                JobId = 5,
                OrgTypeId = 7,
                OrgTypeName = GreekOrgName,
                Mode = UserOrgImportMode.Replace,
                StartedBy = "admin@contoso.com",
                FileName = "cost-centres.csv",
                QueuedUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
                FinishedUtc = new DateTime(2026, 9, 1, 8, 0, 9, DateTimeKind.Utc),
                RowsTotal = 10,
                RowsUnknownUpn = 1,
                RowsInvalid = 2,
                Added = 3,
                Changed = 4,
                Cleared = 5,
                StoredChanges = 12,
                WrittenUtc = new DateTime(2026, 9, 1, 8, 0, 20, DateTimeKind.Utc),
            };

            var entity = TableStorageUserOrgChangeLog.ToSummaryEntity(import);
            Assert.AreEqual(UserOrgChangeLogKeys.SummaryRowKey, entity.RowKey);

            var back = TableStorageUserOrgChangeLog.FromSummaryEntity(entity);
            Assert.AreEqual(import.LogId, back.LogId);
            Assert.AreEqual(import.OrgTypeName, back.OrgTypeName);
            Assert.AreEqual(UserOrgImportMode.Replace, back.Mode);
            Assert.AreEqual(import.StartedBy, back.StartedBy);
            Assert.AreEqual(import.FileName, back.FileName);
            Assert.AreEqual(import.QueuedUtc, back.QueuedUtc);
            Assert.AreEqual(import.FinishedUtc, back.FinishedUtc);
            Assert.AreEqual(12, back.ChangeCount);
            Assert.AreEqual(12, back.StoredChanges);
            Assert.IsFalse(back.Truncated);
        }

        [TestMethod]
        public void TheConnectionStringParserKeepsBase64Padding()
        {
            var parts = UserOrgChangeLogTableFactory.Parse(
                "DefaultEndpointsProtocol=https;AccountName=contosoanalytics;AccountKey=AAAA==;EndpointSuffix=core.windows.net");

            Assert.AreEqual("AAAA==", parts["AccountKey"]);
            Assert.AreEqual(
                new Uri("https://contosoanalytics.table.core.windows.net"),
                UserOrgChangeLogTableFactory.GetTableEndpoint(parts));
        }

        [TestMethod]
        public async Task AWriteTheTableRefusesIsReportedSoWritesCanPause()
        {
            // Nothing listens on port 1, so every request fails at once - the shape of a firewall or a
            // deleted account. The failure must still reach the caller: the log stays pending.
            var options = new Azure.Data.Tables.TableClientOptions();
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            var client = new Azure.Data.Tables.TableClient(
                new Uri("http://127.0.0.1:1/devstoreaccount1"),
                TableStorageUserOrgChangeLog.TableName,
                new Azure.Data.Tables.TableSharedKeyCredential("devstoreaccount1", Convert.ToBase64String(new byte[64])),
                options);
            var reported = new List<Exception>();
            var log = new TableStorageUserOrgChangeLog(client, reported.Add);

            await Assert.ThrowsExceptionAsync<Azure.RequestFailedException>(
                () => log.AppendAsync(Import("1-1"), new[] { Change(1, "a@contoso.com", null, "X") }, CancellationToken.None));
            await Assert.ThrowsExceptionAsync<Azure.RequestFailedException>(
                () => log.CompleteAsync(Import("1-1"), CancellationToken.None));

            Assert.AreEqual(2, reported.Count, "Each refused write is reported.");
        }

        [TestMethod]
        public async Task AReadTheTableRefusesIsReportedToo()
        {
            // Reads are how the holder learns that a client opened before shared-key access was turned off
            // is refused for good: without the report it was asked again until the web app restarted.
            var options = new Azure.Data.Tables.TableClientOptions();
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
            var client = new Azure.Data.Tables.TableClient(
                new Uri("http://127.0.0.1:1/devstoreaccount1"),
                TableStorageUserOrgChangeLog.TableName,
                new Azure.Data.Tables.TableSharedKeyCredential("devstoreaccount1", Convert.ToBase64String(new byte[64])),
                options);
            var writes = new List<Exception>();
            var reads = new List<Exception>();
            var log = new TableStorageUserOrgChangeLog(client, writes.Add, reads.Add);

            await Assert.ThrowsExceptionAsync<Azure.RequestFailedException>(
                () => log.GetImportAsync("1-1", CancellationToken.None));
            await Assert.ThrowsExceptionAsync<Azure.RequestFailedException>(
                () => log.GetChangesAsync("1-1", null, null, 10, CancellationToken.None));
            await Assert.ThrowsExceptionAsync<UserOrgValidationException>(
                () => log.GetChangesAsync("1-1", null, "stale-continuation", 10, CancellationToken.None));

            Assert.AreEqual(3, reads.Count, "Each refused read is reported - a stale page as much as any other.");
            Assert.AreEqual(0, writes.Count);
        }

        [TestMethod]
        public void OnlyARefusalThatOpeningTheTableAfreshCuresDropsTheClient()
        {
            // Shared-key access turned off since the client was opened: the factory falls back to the
            // service principal, so the next attempt must open it afresh.
            Assert.IsTrue(UserOrgChangeLogs.Reopens(
                new Azure.RequestFailedException(403, "Key based authentication is not permitted.", "KeyBasedAuthenticationNotPermitted", null)));
            Assert.IsTrue(UserOrgChangeLogs.Reopens(
                new Azure.RequestFailedException(403, "Auth type disabled.", "AuthenticationTypeDisabled", null)));
            Assert.IsTrue(UserOrgChangeLogs.Reopens(new AggregateException(
                new Azure.RequestFailedException(403, "Key based authentication is not permitted.", "KeyBasedAuthenticationNotPermitted", null))));

            // The table deleted since: created again.
            Assert.IsTrue(UserOrgChangeLogs.Reopens(new Azure.RequestFailedException(404, "Not found.", "TableNotFound", null)));

            // Nothing a fresh client changes: a missing role, a rotated key, a firewall.
            Assert.IsFalse(UserOrgChangeLogs.Reopens(
                new Azure.RequestFailedException(403, "Permission mismatch.", "AuthorizationPermissionMismatch", null)));
            Assert.IsFalse(UserOrgChangeLogs.Reopens(
                new Azure.RequestFailedException(403, "Authentication failed.", "AuthenticationFailed", null)));
            Assert.IsFalse(UserOrgChangeLogs.Reopens(new Azure.RequestFailedException(0, "Connection refused.")));
            Assert.IsFalse(UserOrgChangeLogs.Reopens(new InvalidOperationException("No usable credential.")));
        }

        #endregion
    }
}
