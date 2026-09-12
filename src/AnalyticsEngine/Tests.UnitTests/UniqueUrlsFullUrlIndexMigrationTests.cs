using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Data.Entity;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Exercises the raw SQL of <see cref="UniqueUrlsFullUrlIndex"/> (issue #167) against LocalDB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs <see cref="UniqueUrlsFullUrlIndex.Up_Sql"/> - the exact constant the migration and the manual
    /// upgrade script both use - so the tests cannot drift from what actually ships.
    /// </para>
    /// <para>
    /// The interesting cases are not "does it delete duplicates". They are the ones that would break an
    /// upgrade for exactly the customers this migration exists for: child rows that collide on their OWN
    /// unique index once repointed, the legacy non-FK reference, non-ASCII URLs, and re-running an already
    /// applied migration.
    /// </para>
    /// </remarks>
    [TestClass]
    public class UniqueUrlsFullUrlIndexMigrationTests
    {
        private const string IndexName = "IX_urls_full_url";

        // "Καλημέρα κόσμε" - the classic Greek charset sample (synthetic; no customer data). A varchar
        // column would corrupt this to '?', and the de-duplication must not mangle it either.
        private const string GreekUrl =
            "https://contoso.sharepoint.com/sites/example/Shared Documents/" +
            "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5.pdf";

        private const string PlainUrl = "https://contoso.sharepoint.com/sites/example/Doc.docx";

        /// <summary>
        /// Leaves dbo.urls fully migrated after every test in this class.
        /// </summary>
        /// <remarks>
        /// These tests deliberately put dbo.urls back into its pre-migration shape (non-unique index,
        /// duplicate rows) and they share a database with the whole suite. Anything that ends without
        /// re-applying the migration - a test that asserts the migration was refused, or one that failed
        /// part-way - hands the next test a dbo.urls that still accepts duplicates, and
        /// DuplicateUrlTests / EntityTests.URLsTest then fail purely because of execution order.
        ///
        /// Restoring here rather than at the end of each test means a failing test cannot leak either.
        /// </remarks>
        [TestCleanup]
        public void RestoreSharedSchema()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                RunMigrationAsync(db).GetAwaiter().GetResult();
            }
        }

        private static Task<int> ExecAsync(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, sql);
        }

        /// <summary>
        /// Runs the migration's real SQL with its concurrency gate switched off.
        /// </summary>
        /// <remarks>
        /// The migration refuses to run when another session holds write locks in the database, because
        /// it deletes rows and repoints references across separately committed statements. That is
        /// correct for a customer upgrade and wrong for these tests: a test run has other pooled
        /// connections open, so the gate fires and the test fails with "ABORTED - N other session(s) are
        /// holding write locks" instead of exercising the de-duplication it is about.
        ///
        /// The opt-out goes in the SAME batch on purpose - session context is per-connection, and EF
        /// hands each ExecuteSqlCommand a pooled connection, so setting it separately would not reliably
        /// still be set when the migration ran.
        ///
        /// The gate itself is covered by
        /// <see cref="Migration_Aborts_WhenAnotherSessionHoldsWriteLocks"/>, so switching it off here
        /// does not leave the behaviour untested.
        /// </remarks>
        private static Task<int> RunMigrationAsync(AnalyticsEntitiesContext db)
        {
            return ExecAsync(db,
                "EXEC sp_set_session_context N'UniqueUrlsFullUrlIndex_SkipConcurrencyCheck', 1;\r\n"
                + UniqueUrlsFullUrlIndex.Up_Sql);
        }

        private static Task<T> ScalarAsync<T>(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.SqlQuery<T>(sql).FirstAsync();
        }

        /// <summary>Forces EF initialisation so the schema exists before the tests manipulate it.</summary>
        private static async Task EnsureSchemaAsync(AnalyticsEntitiesContext db)
        {
            await db.urls.Take(1).ToListAsync();
        }

        /// <summary>
        /// Puts <c>IX_urls_full_url</c> back into its pre-migration NON-unique shape so a test can run the
        /// migration from scratch, and clears any rows left by an earlier run.
        /// </summary>
        private static async Task ResetToPreMigrationStateAsync(AnalyticsEntitiesContext db)
        {
            await ExecAsync(db, $@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}')
    DROP INDEX [{IndexName}] ON [dbo].[urls];

-- dbo.hits.url_id is NO_ACTION, not CASCADE, so any hit still pointing at one of these urls makes the
-- delete below fail with a FK_hits_urls conflict. Clearing the hits first keeps this reset working
-- whatever else the suite has left behind. hits_clicked_elements cascades from hits, so it follows.
DELETE FROM dbo.hits WHERE url_id IN (SELECT id FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%');
DELETE FROM dbo.file_metadata_property_values WHERE url_id IN (SELECT id FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%');
DELETE FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%';

CREATE NONCLUSTERED INDEX [{IndexName}] ON [dbo].[urls] ([full_url]);");
        }

        private static Task<int> InsertUrlAsync(AnalyticsEntitiesContext db, string url)
        {
            return ScalarAsync<int>(db,
                $"INSERT INTO dbo.urls (full_url) OUTPUT INSERTED.id VALUES (N'{url.Replace("'", "''")}')");
        }

        private static Task<bool> IndexIsUniqueAsync(AnalyticsEntitiesContext db)
        {
            return ScalarAsync<int>(db,
                $@"SELECT COUNT(*) FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}' AND is_unique = 1")
                .ContinueWith(t => t.Result > 0);
        }

        private static Task<bool> IndexIgnoresDuplicateKeysAsync(AnalyticsEntitiesContext db)
        {
            return ScalarAsync<int>(db,
                $@"SELECT COUNT(*) FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'{IndexName}' AND ignore_dup_key = 1")
                .ContinueWith(t => t.Result > 0);
        }

        [TestMethod]
        public async Task Migration_RemovesDuplicates_AndMakesTheIndexUnique()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);

                await RunMigrationAsync(db);

                var remaining = await ScalarAsync<int>(db,
                    $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(1, remaining, "Exactly one row per distinct full_url must survive.");

                var survivorId = await ScalarAsync<int>(db,
                    $"SELECT id FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(keepId, survivorId, "The lowest id is the canonical survivor.");

                Assert.IsTrue(await IndexIsUniqueAsync(db), "IX_urls_full_url must be UNIQUE afterwards.");
                Assert.IsTrue(await IndexIgnoresDuplicateKeysAsync(db),
                    "IGNORE_DUP_KEY must be ON so a concurrent importer race skips the duplicate row instead of "
                    + "aborting the whole INSERT statement.");
            }
        }

        [TestMethod]
        public async Task Migration_RepointsReferences_ToTheCanonicalUrl()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                var dupId = await InsertUrlAsync(db, PlainUrl);

                // A reference pointing at the row that is about to be deleted.
                await ExecAsync(db,
                    $"INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) SELECT {dupId}, MIN(id), N'x', GETUTCDATE() FROM dbo.file_field_definitions");

                await RunMigrationAsync(db);

                var orphans = await ScalarAsync<int>(db,
                    @"SELECT COUNT(*) FROM dbo.file_metadata_property_values f
                      LEFT JOIN dbo.urls u ON u.id = f.url_id WHERE u.id IS NULL");
                Assert.AreEqual(0, orphans, "No reference may be left pointing at a deleted URL.");

                var repointed = await ScalarAsync<int>(db,
                    $"SELECT COUNT(*) FROM dbo.file_metadata_property_values WHERE url_id = {keepId}");
                Assert.IsTrue(repointed > 0, "The reference must now point at the canonical URL.");
            }
        }

        [TestMethod]
        public async Task Migration_PrunesRowsThatWouldCollideOnTheirOwnUniqueIndex()
        {
            // The case that would otherwise break the upgrade for everyone who has duplicates:
            // file_metadata_property_values has UNIQUE (url_id, field_id), so repointing two duplicate
            // url_ids onto one canonical id creates a duplicate key and the UPDATE fails.
            //
            // Which row survives is decided by "most recently updated first". field_value holds a
            // SharePoint metadata value, so keeping the stale copy would be a silent data regression.
            // Timestamps are explicit and far apart on purpose: the original version of this test wrote
            // GETUTCDATE() into both rows, which ties on some machines and not others, and so asserted
            // the tie-break rule by accident rather than the rule that actually matters.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                var dupId = await InsertUrlAsync(db, PlainUrl);

                // Same field_id under BOTH urls - these collide once dupId becomes keepId.
                // The DUPLICATE carries the newer value, so "freshest wins" is actually exercised.
                await ExecAsync(db,
                    $@"DECLARE @f int = (SELECT MIN(id) FROM dbo.file_field_definitions);
                       INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) VALUES ({keepId}, @f, N'stale', '2020-01-01T00:00:00');
                       INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) VALUES ({dupId}, @f, N'freshest', '2026-01-01T00:00:00');");

                // Must not throw.
                await RunMigrationAsync(db);

                var rows = await db.Database.SqlQuery<string>(
                    $"SELECT field_value FROM dbo.file_metadata_property_values WHERE url_id = {keepId}").ToListAsync();

                Assert.AreEqual(1, rows.Count, "Exactly one of the colliding rows may survive.");
                Assert.AreEqual("freshest", rows.Single(),
                    "The most recently updated row must survive, so the freshest field_value is kept.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_CollisionOnEqualTimestamps_KeepsTheRowAlreadyOnTheCanonicalUrl()
        {
            // When "most recently updated" cannot decide, the row that already pointed at the canonical
            // URL wins, and the primary key breaks any remaining tie. That keeps the outcome
            // deterministic instead of leaving it to whatever order the engine happens to produce.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var keepId = await InsertUrlAsync(db, PlainUrl);
                var dupId = await InsertUrlAsync(db, PlainUrl);

                await ExecAsync(db,
                    $@"DECLARE @f int = (SELECT MIN(id) FROM dbo.file_field_definitions);
                       INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) VALUES ({keepId}, @f, N'keep-me', '2026-01-01T00:00:00');
                       INSERT INTO dbo.file_metadata_property_values (url_id, field_id, field_value, updated) VALUES ({dupId}, @f, N'collides', '2026-01-01T00:00:00');");

                await RunMigrationAsync(db);

                var rows = await db.Database.SqlQuery<string>(
                    $"SELECT field_value FROM dbo.file_metadata_property_values WHERE url_id = {keepId}").ToListAsync();

                Assert.AreEqual(1, rows.Count, "Exactly one of the colliding rows may survive.");
                Assert.AreEqual("keep-me", rows.Single(),
                    "On an equal timestamp the row that ALREADY pointed at the canonical URL is the one to keep.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_PreservesNonAsciiUrlsExactly()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, GreekUrl);
                await InsertUrlAsync(db, GreekUrl);

                await RunMigrationAsync(db);

                var survivors = await db.Database.SqlQuery<string>(
                    $"SELECT full_url FROM dbo.urls WHERE full_url = N'{GreekUrl.Replace("'", "''")}'").ToListAsync();

                Assert.AreEqual(1, survivors.Count);
                Assert.AreEqual(GreekUrl, survivors.Single(),
                    "A Greek URL must survive de-duplication byte-for-byte - no '?' substitution, no truncation.");
            }
        }

        [TestMethod]
        public async Task Migration_TreatsCaseDifferencesAsDuplicates_MatchingTheIndexItCreates()
        {
            // The database collation is case-insensitive, so the unique index would reject '.../Foo' once
            // '.../foo' exists. The de-duplication must group the same way, or the index creation would fail
            // on rows the grouping considered distinct.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl.ToUpperInvariant().Replace("HTTPS://CONTOSO.SHAREPOINT.COM/SITES/EXAMPLE/", "https://contoso.sharepoint.com/sites/example/"));

                await RunMigrationAsync(db);

                var remaining = await ScalarAsync<int>(db,
                    $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(1, remaining, "Case-only variants are the same URL under this collation.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_IsANoOpOnASecondRun()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);

                await RunMigrationAsync(db);
                var afterFirst = await ScalarAsync<int>(db, "SELECT COUNT(*) FROM dbo.urls");

                // Must not throw and must change nothing.
                await RunMigrationAsync(db);
                var afterSecond = await ScalarAsync<int>(db, "SELECT COUNT(*) FROM dbo.urls");

                Assert.AreEqual(afterFirst, afterSecond, "Re-running an applied migration must be a no-op.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_IsSafeOnADatabaseWithNoDuplicates()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, GreekUrl);

                await RunMigrationAsync(db);

                var count = await ScalarAsync<int>(db,
                    "SELECT COUNT(*) FROM dbo.urls WHERE full_url LIKE N'https://contoso.sharepoint.com/sites/example/%'");
                Assert.AreEqual(2, count, "Nothing may be deleted when there is nothing duplicated.");
                Assert.IsTrue(await IndexIsUniqueAsync(db));
            }
        }

        [TestMethod]
        public async Task AfterMigration_ADuplicateInsertIsSkippedWithoutAbortingTheStatement()
        {
            // The importer-hardening half of #167. Without IGNORE_DUP_KEY a concurrent check-then-insert
            // race would abort the whole INSERT and lose the other new URLs in the same statement.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await RunMigrationAsync(db);

                var brandNew = "https://contoso.sharepoint.com/sites/example/BrandNew.docx";

                // One duplicate and one genuinely new URL in a single statement.
                await ExecAsync(db,
                    $@"INSERT INTO dbo.urls (full_url)
                       SELECT N'{PlainUrl}' UNION ALL SELECT N'{brandNew}';");

                Assert.AreEqual(1, await ScalarAsync<int>(db, $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'"),
                    "The duplicate must have been skipped, not inserted.");
                Assert.AreEqual(1, await ScalarAsync<int>(db, $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{brandNew}'"),
                    "The other row in the same statement must still have been inserted.");
            }
        }

        [TestMethod]
        public async Task Migration_Aborts_WhenAnotherSessionHoldsWriteLocks()
        {
            // The other direction of the gate that RunMigrationAsync switches off. The migration deletes
            // rows and repoints references across separately committed statements, so a writer racing it
            // can have rows cascade-deleted or orphaned. It must therefore refuse to start - and, just as
            // importantly, refuse BEFORE changing anything.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await InsertUrlAsync(db, PlainUrl);
                await InsertUrlAsync(db, PlainUrl);

                var before = await ScalarAsync<int>(db, $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(2, before, "Pre-condition: two duplicate rows to be removed.");

                // A genuinely concurrent writer: a second connection with an uncommitted INSERT, which
                // leaves real write-intent locks in this database for as long as it stays open.
                var cs = db.Database.Connection.ConnectionString;
                using (var blocker = new SqlConnection(cs))
                {
                    await blocker.OpenAsync();
                    using (var tx = blocker.BeginTransaction())
                    {
                        using (var cmd = blocker.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT INTO dbo.urls (full_url) VALUES (N'https://contoso.sharepoint.com/sites/example/Blocker.docx');";
                            await cmd.ExecuteNonQueryAsync();
                        }

                        SqlException caught = null;
                        try
                        {
                            // LOCK_TIMEOUT matters more than it looks. If the gate ever fails to fire,
                            // the migration proceeds and tries to DROP/CREATE the index on dbo.urls while
                            // this blocker holds locks - and migrations run with CommandTimeout = 0, so it
                            // would wait forever and hang the whole CI job rather than failing. With a
                            // timeout the test fails in seconds and says why.
                            await ExecAsync(db, "SET LOCK_TIMEOUT 15000;\r\n" + UniqueUrlsFullUrlIndex.Up_Sql);
                        }
                        catch (SqlException ex)
                        {
                            caught = ex;
                        }

                        Assert.IsNotNull(caught, "The migration must refuse to run while another session holds write locks.");
                        StringAssert.Contains(caught.Message, "ABORTED",
                            "The abort must come from the concurrency gate. A lock-timeout error here instead means the "
                            + "gate did NOT detect the writer and the migration went ahead and blocked on it.");

                        tx.Rollback();
                    }
                }

                // Nothing may have changed: the gate runs before any mutation.
                var after = await ScalarAsync<int>(db, $"SELECT COUNT(*) FROM dbo.urls WHERE full_url = N'{PlainUrl}'");
                Assert.AreEqual(2, after, "The aborted run must not have deleted anything.");
                Assert.IsFalse(await IndexIsUniqueAsync(db), "The aborted run must not have rebuilt the index.");

                // RestoreSharedSchema puts the database back afterwards - this is the one test in the
                // class that deliberately ends with the migration NOT applied.
            }
        }

        [TestMethod]
        public async Task Migration_DoesNotAbort_WhenTheWorkIsAlreadyDone_EvenWithAConcurrentWriter()
        {
            // The POSITIVE direction of the gate covered by
            // Migration_Aborts_WhenAnotherSessionHoldsWriteLocks. A guard tested only against the state it
            // was written to catch looks perfect and blocks every healthy upgrade, so this asserts the
            // other half: on a database that is ALREADY in the target state there is nothing to
            // de-duplicate, therefore nothing a writer can race, therefore no reason to refuse.
            //
            // This is not cosmetic. In the manual upgrade script the gate's abort is a RETURN in the same
            // batch as the __MigrationHistory stamp, so aborting here would leave a database whose schema
            // work had completed recorded as NOT migrated - and because the manual scripts form a
            // prerequisite chain, that unstamped migration would block every later script in the release.
            // That is precisely the DenormaliseCopilotChatUserAndTime failure mode.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);

                // Fully migrated starting point (RestoreSharedSchema leaves the suite here anyway, but
                // this test must not depend on execution order).
                await RunMigrationAsync(db);
                Assert.IsTrue(await IndexIsUniqueAsync(db), "Pre-condition: the migration is already applied.");
                Assert.IsTrue(await IndexIgnoresDuplicateKeysAsync(db), "Pre-condition: IGNORE_DUP_KEY is on.");

                var cs = db.Database.Connection.ConnectionString;
                using (var blocker = new SqlConnection(cs))
                {
                    await blocker.OpenAsync();
                    using (var tx = blocker.BeginTransaction())
                    {
                        using (var cmd = blocker.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT INTO dbo.urls (full_url) VALUES (N'https://contoso.sharepoint.com/sites/example/Writer.docx');";
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // Deliberately WITHOUT the skip flag that RunMigrationAsync sets - the gate is
                        // live, and a live writer is present. LOCK_TIMEOUT so a regression that made this
                        // block on the writer fails in seconds instead of hanging CI.
                        await ExecAsync(db, "SET LOCK_TIMEOUT 15000;\r\n" + UniqueUrlsFullUrlIndex.Up_Sql);

                        tx.Rollback();
                    }
                }

                Assert.IsTrue(await IndexIsUniqueAsync(db),
                    "The no-op run must leave the unique index in place.");
                Assert.IsTrue(await IndexIgnoresDuplicateKeysAsync(db),
                    "The no-op run must leave IGNORE_DUP_KEY in place.");
            }
        }

        [TestMethod]
        public void ManualScriptUsesTheSameSqlAsTheMigration()
        {
            // Rule 7 of the migration conventions: the manual upgrade script must contain the migration's
            // Up SQL verbatim, so a by-hand upgrade cannot diverge from the installer's.
            var manual = ReadManualScript("202609101100001_UniqueUrlsFullUrlIndex");

            StringAssert.Contains(manual, UniqueUrlsFullUrlIndex.Up_Sql,
                "The manual script must embed the migration's Up_Sql verbatim.");

            StringAssert.Contains(manual, "202609101000001_RetireUnusedAuditYammerStreamTables",
                "The stamp must be conditional on the predecessor, so the scripts cannot be applied out of order.");

            StringAssert.Contains(manual, "INSERT INTO dbo.__MigrationHistory",
                "The script must stamp __MigrationHistory or EF will still consider the migration pending.");
        }

        /// <summary>
        /// Reads a <c>&lt;migrationid&gt;.manual.sql</c> from the repository. Walks up from the test binaries
        /// rather than assuming a working directory, so it works under both vstest and the IDE.
        /// </summary>
        private static string ReadManualScript(string migrationId)
        {
            var dir = new System.IO.DirectoryInfo(
                System.IO.Path.GetDirectoryName(typeof(UniqueUrlsFullUrlIndexMigrationTests).Assembly.Location));

            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(
                    dir.FullName, "Common", "Entities", "Migrations", migrationId + ".manual.sql");

                if (System.IO.File.Exists(candidate))
                {
                    return System.IO.File.ReadAllText(candidate);
                }

                dir = dir.Parent;
            }

            Assert.Fail(
                $"Could not find {migrationId}.manual.sql anywhere above the test assembly. Every schema "
                + "migration must ship a manual upgrade script for operators who upgrade by hand.");
            return null;
        }
    }
}
