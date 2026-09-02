using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the delta-commit ordering rule (#372) and for the bulk-write port (#371).
    ///
    /// The ordering rule is the single most consequential invariant in the Graph user import:
    /// Graph's <c>/users/delta</c> token is a promise that everything up to that point has been
    /// dealt with, so committing it after a partial cycle makes Graph stop reporting those users
    /// and the missing data is invisible until somebody notices absent users.
    ///
    /// The end-to-end behaviour already had tests - the database-backed
    /// <c>UserMetadataUpdater_SuccessfulImport_DeltaTokenCommitted</c> and
    /// <c>UserMetadataUpdater_LicenseProcessingThrows_DeltaTokenNotAdvanced</c>. What had none was
    /// the rule itself, because until now there was no rule: the invariant was a side effect of an
    /// exception propagating out of the orchestration method.
    /// </summary>
    [TestClass]
    public class UserImportCommitPolicyTests
    {
        private static UserImportPhaseResults AllSucceeded()
        {
            return new UserImportPhaseResults
            {
                InsertPhaseSucceeded = true,
                UpdatePhaseSucceeded = true,
                LicenceRefreshSucceeded = true,
            };
        }

        [TestMethod]
        public void UserImport_AllPhasesSucceed_CommitsDeltaToken()
        {
            Assert.IsTrue(UserImportCommitPolicy.ShouldCommitDelta(AllSucceeded()));
        }

        [TestMethod]
        public void UserImport_InsertPhaseFails_DoesNotCommitDeltaToken()
        {
            var results = AllSucceeded();
            results.InsertPhaseSucceeded = false;

            Assert.IsFalse(UserImportCommitPolicy.ShouldCommitDelta(results),
                "Users that failed to insert must be reoffered by Graph on the next cycle.");
        }

        [TestMethod]
        public void UserImport_UpdatePhaseFails_DoesNotCommitDeltaToken()
        {
            var results = AllSucceeded();
            results.UpdatePhaseSucceeded = false;

            Assert.IsFalse(UserImportCommitPolicy.ShouldCommitDelta(results),
                "Users whose metadata update failed must be reoffered by Graph on the next cycle.");
        }

        [TestMethod]
        public void UserImport_LicenceRefreshFails_DoesNotCommitDeltaToken()
        {
            var results = AllSucceeded();
            results.LicenceRefreshSucceeded = false;

            Assert.IsFalse(UserImportCommitPolicy.ShouldCommitDelta(results),
                "A failed licence refresh must not be sealed in by a committed delta token.");
        }

        [TestMethod]
        public void UserImport_NothingRanYet_DoesNotCommitDeltaToken()
        {
            // The default state. A cycle that fell over before any phase completed must never
            // advance the token.
            Assert.IsFalse(UserImportCommitPolicy.ShouldCommitDelta(new UserImportPhaseResults()));
        }

        [TestMethod]
        public void UserImport_OnlyAllThreePhasesTogether_CommitTheDeltaToken()
        {
            // Exhaustive truth table. The single-phase tests above name the three failures an
            // operator would recognise; this covers the combinations too, so a rewrite that
            // accidentally checked only two of the three flags cannot pass.
            var committed = new List<string>();
            foreach (var insert in new[] { false, true })
                foreach (var update in new[] { false, true })
                    foreach (var licence in new[] { false, true })
                    {
                        var results = new UserImportPhaseResults
                        {
                            InsertPhaseSucceeded = insert,
                            UpdatePhaseSucceeded = update,
                            LicenceRefreshSucceeded = licence,
                        };

                        if (UserImportCommitPolicy.ShouldCommitDelta(results))
                        {
                            committed.Add($"insert={insert} update={update} licence={licence}");
                        }
                    }

            CollectionAssert.AreEqual(
                new[] { "insert=True update=True licence=True" }, committed,
                "Exactly one of the eight states may commit the delta token: the one where every phase succeeded.");
        }
    }

    /// <summary>
    /// Tests for <see cref="IUserBulkUpdateWriter"/> and its SQL adapter (#371). The adapter is the
    /// original <c>SqlConnection</c> + <c>SqlBulkCopy</c> code relocated unchanged, so what is worth
    /// pinning here is the contract around it: an empty batch must not open a connection, and the
    /// column mappings must be driven by the same list the batch is built from.
    /// </summary>
    [TestClass]
    public class UserBulkUpdateWriterTests
    {
        /// <summary>Points at nothing. Any attempt to connect fails rather than reaching a real server.</summary>
        private const string UnreachableConnectionString =
            "Data Source=(localdb)\\NoSuchInstanceForTests;Initial Catalog=NoSuchDb;Integrated Security=true;Connect Timeout=1";

        [TestMethod]
        public async Task UserBulkUpdate_EmptyBatch_PerformsNoWrite()
        {
            // If this ever starts opening a connection it will fail against an unreachable server,
            // which is exactly the signal we want: an import cycle with nothing to update must not
            // pay for a connection, a temp table and an UPDATE.
            var writer = new SqlUserBulkUpdateWriter(UnreachableConnectionString);

            await writer.ExecuteAsync(UserBulkUpdateRules.CreateUpdateTable());
        }

        [TestMethod]
        public void UserBulkUpdate_WriterWithoutAConnectionString_IsRejected()
        {
            Assert.ThrowsException<System.ArgumentNullException>(() => new SqlUserBulkUpdateWriter(null));
            Assert.ThrowsException<System.ArgumentNullException>(() => new SqlUserBulkUpdateWriter(string.Empty));
        }

        [TestMethod]
        public async Task UserBulkUpdate_WriterSnapshotsTheBatch_RatherThanKeepingTheCallersTable()
        {
            var writer = new InMemoryUserBulkUpdateWriter();
            var table = UserBulkUpdateRules.CreateUpdateTable();
            var row = table.NewRow();
            row["id"] = 7;
            row["last_updated"] = System.DateTime.Now;
            table.Rows.Add(row);

            using (table)
            {
                await writer.ExecuteAsync(table);
            }

            // The production caller wraps each batch in a using block and reuses nothing, so a fake
            // that stored the caller's table by reference would report whatever the caller left
            // behind. Disposing a DataTable does not clear its rows, so proving the snapshot is
            // real needs both an identity check and a mutation of the original.
            Assert.AreNotSame(table, writer.Batches[0], "The writer must record its own copy of the batch.");
            table.Rows.Clear();

            Assert.AreEqual(1, writer.Batches.Count);
            Assert.AreEqual(1, writer.TotalRowsWritten, "Clearing the caller's table must not empty what the writer recorded.");
            Assert.AreEqual(7, writer.Batches[0].Rows[0]["id"]);
        }
    }
}
