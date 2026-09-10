using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Exercises the raw SQL of <see cref="RetireImportDbHacks"/> against LocalDB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This migration exists to bring two objects under the migration chain that used to be created at
    /// runtime by the App Insights web-job: the UNIQUE <c>dbo.hits.IX_PageRequestID</c> (issue #165) and
    /// the NON-unique <c>dbo.sessions.IX_ai_session_id</c> plus its case-sensitive collation.
    /// </para>
    /// <para>
    /// On a real deployment both already exist, so the migration is a no-op - which is exactly why it has
    /// to be tested from a state where they do NOT exist. A test that only ever ran it against an
    /// already-correct database would pass without the migration doing anything at all.
    /// </para>
    /// <para>
    /// Runs <see cref="RetireImportDbHacks.Up_Sql"/>, the exact constant the migration and the manual
    /// upgrade script both use, so these tests cannot drift from what ships.
    /// </para>
    /// </remarks>
    [TestClass]
    public class RetireImportDbHacksMigrationTests
    {
        private const string HitsIndex = "IX_PageRequestID";
        private const string SessionIndex = "IX_ai_session_id";

        /// <summary>
        /// Puts the shared schema back after every test, including one that failed part-way.
        /// </summary>
        /// <remarks>
        /// These tests deliberately drop indexes that the rest of the suite depends on -
        /// HitImportFanoutTests and DuplicateUrlTests both need the unique IX_PageRequestID to exist - and
        /// they share one database with everything else. Restoring here rather than at the end of each
        /// test means a failure cannot leak a half-dismantled schema into whatever runs next.
        /// </remarks>
        [TestCleanup]
        public void RestoreSharedSchema()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                ExecAsync(db, RetireImportDbHacks.Up_Sql).GetAwaiter().GetResult();
            }
        }

        private static Task<int> ExecAsync(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, sql);
        }

        private static Task<T> ScalarAsync<T>(AnalyticsEntitiesContext db, string sql)
        {
            return db.Database.SqlQuery<T>(sql).FirstAsync();
        }

        /// <summary>Forces EF initialisation so the schema exists before the tests manipulate it.</summary>
        private static async Task EnsureSchemaAsync(AnalyticsEntitiesContext db)
        {
            await db.Database.ExecuteSqlCommandAsync("SELECT 1");
        }

        private static Task<int> IndexCountAsync(AnalyticsEntitiesContext db, string table, string index)
        {
            return ScalarAsync<int>(db,
                $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.{table}') AND name = '{index}'");
        }

        private static Task<int> IndexIsUniqueAsync(AnalyticsEntitiesContext db, string table, string index)
        {
            return ScalarAsync<int>(db,
                $"SELECT ISNULL(MAX(CAST(is_unique AS int)), -1) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.{table}') AND name = '{index}'");
        }

        private static Task<int> IndexHasFilterAsync(AnalyticsEntitiesContext db, string table, string index)
        {
            return ScalarAsync<int>(db,
                $"SELECT ISNULL(MAX(CAST(has_filter AS int)), -1) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.{table}') AND name = '{index}'");
        }

        private static Task<string> SessionCollationAsync(AnalyticsEntitiesContext db)
        {
            return ScalarAsync<string>(db,
                "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.sessions') AND name = 'ai_session_id'");
        }

        private static Task<int> SessionIsNullableAsync(AnalyticsEntitiesContext db)
        {
            return ScalarAsync<int>(db,
                "SELECT CAST(is_nullable AS int) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.sessions') AND name = 'ai_session_id'");
        }

        /// <summary>
        /// Removes both objects and reverts the collation, i.e. the state a database is in before the App
        /// Insights importer has ever run against it.
        /// </summary>
        private static async Task ResetToPreMigrationStateAsync(AnalyticsEntitiesContext db)
        {
            await ExecAsync(db, $@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.hits') AND name = N'{HitsIndex}')
    DROP INDEX [{HitsIndex}] ON [dbo].[hits];

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.sessions') AND name = N'{SessionIndex}')
    DROP INDEX [{SessionIndex}] ON [dbo].[sessions];

ALTER TABLE [dbo].[sessions] ALTER COLUMN [ai_session_id] varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL;");
        }

        [TestMethod]
        public async Task Migration_CreatesBothObjects_FromAPreMigrationDatabase()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                Assert.AreEqual(0, await IndexCountAsync(db, "hits", HitsIndex), "Pre-condition: no IX_PageRequestID.");
                Assert.AreEqual(0, await IndexCountAsync(db, "sessions", SessionIndex), "Pre-condition: no IX_ai_session_id.");
                StringAssert.Contains(await SessionCollationAsync(db), "CI_AS", "Pre-condition: case-INsensitive collation.");

                await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                Assert.AreEqual(1, await IndexIsUniqueAsync(db, "hits", HitsIndex),
                    "IX_PageRequestID must exist and be UNIQUE - it is the constraint issue #165 relies on.");
                Assert.AreEqual(0, await IndexIsUniqueAsync(db, "sessions", SessionIndex),
                    "IX_ai_session_id must exist and must NOT be unique - duplicate session ids are legitimate.");
                Assert.AreEqual("SQL_Latin1_General_CP1_CS_AS", await SessionCollationAsync(db),
                    "ai_session_id must end up case-sensitive: App Insights session ids differ only by case.");
            }
        }

        [TestMethod]
        public async Task Migration_PreservesTheNullabilityOfAiSessionId()
        {
            // ALTER COLUMN that omits NULL/NOT NULL makes the column nullable regardless of what it was.
            // sessions.ai_session_id is nullable, and the migration states that explicitly rather than
            // relying on the default - so this asserts the column is not silently redefined.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                Assert.AreEqual(1, await SessionIsNullableAsync(db),
                    "ai_session_id is nullable and must stay nullable.");
            }
        }

        [TestMethod]
        public async Task Migration_RemovesDuplicateHits_KeepingTheLowestId()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                var user = new User { UserPrincipalName = "dedupe" + DateTime.Now.Ticks + "@example.com" };
                db.users.Add(user);
                var session = new UserSession { ai_session_id = Guid.NewGuid().ToString(), user = user };
                db.sessions.Add(session);

                // Deliberately NOT under /sites/example/: UniqueUrlsFullUrlIndexMigrationTests clears that
                // prefix in its own reset, and dbo.hits.url_id is NO_ACTION, so a hit left pointing at such
                // a url makes that cleanup fail with a FK_hits_urls conflict.
                var url = new Url { FullUrl = "https://contoso.sharepoint.com/sites/retire-hacks/Dedupe-" + DateTime.Now.Ticks + ".docx" };
                db.urls.Add(url);
                await db.SaveChangesAsync();

                // Three hits sharing one page_request_id - impossible once the unique index exists, which
                // is precisely why this has to be set up before the migration runs.
                var sharedRequestId = Guid.NewGuid();
                for (var i = 0; i < 3; i++)
                {
                    db.hits.Add(new Hit
                    {
                        session = session,
                        url = url,
                        page_request_id = sharedRequestId,
                        hit_timestamp = DateTime.Now
                    });
                }
                await db.SaveChangesAsync();

                var lowestId = await ScalarAsync<int>(db,
                    $"SELECT MIN(id) FROM dbo.hits WHERE page_request_id = '{sharedRequestId}'");

                try
                {
                    await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                    var remaining = await db.Database.SqlQuery<int>(
                        $"SELECT id FROM dbo.hits WHERE page_request_id = '{sharedRequestId}'").ToListAsync();

                    Assert.AreEqual(1, remaining.Count, "Exactly one hit may survive per page_request_id.");
                    Assert.AreEqual(lowestId, remaining.Single(),
                        "The lowest id must survive - the same row the old cursor kept, so behaviour is unchanged.");
                    Assert.AreEqual(1, await IndexIsUniqueAsync(db, "hits", HitsIndex));
                }
                finally
                {
                    // Leave no hits pointing at this url: dbo.hits.url_id is NO_ACTION, so a stray row here
                    // breaks other classes' url cleanup rather than this one's.
                    await ExecAsync(db, $@"
DELETE FROM dbo.hits WHERE page_request_id = '{sharedRequestId}';
DELETE FROM dbo.urls WHERE id = {url.ID};");
                }
            }
        }

        [TestMethod]
        public async Task Migration_RebuildsIX_ai_session_id_WhenItIsWronglyUnique()
        {
            // A unique IX_ai_session_id would break the hits merge, which depends on being able to hold
            // duplicate session ids (see the ROW_NUMBER fan-out defence and issue #165). The old hack
            // rebuilt it as non-unique if it found it unique; that behaviour must survive the move.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                // The precondition is "an index called IX_ai_session_id whose is_unique = 1". It is set up
                // with a filter matching no rows, because dbo.sessions in this shared database genuinely
                // contains duplicate ai_session_id values - HitImportFanoutTests creates them on purpose -
                // so a real unique index over the whole column cannot be built. That is the very property
                // this test exists to protect.
                await ExecAsync(db, $@"
ALTER TABLE [dbo].[sessions] ALTER COLUMN [ai_session_id] varchar(50) COLLATE SQL_Latin1_General_CP1_CS_AS NULL;
CREATE UNIQUE NONCLUSTERED INDEX [{SessionIndex}] ON [dbo].[sessions] ([ai_session_id] ASC)
    WHERE [ai_session_id] = '00000000-0000-0000-0000-000000000000';");

                Assert.AreEqual(1, await IndexIsUniqueAsync(db, "sessions", SessionIndex),
                    "Pre-condition: the index is (wrongly) unique.");

                await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                Assert.AreEqual(0, await IndexIsUniqueAsync(db, "sessions", SessionIndex),
                    "The migration must rebuild IX_ai_session_id as NON-unique.");
                Assert.AreEqual(0, await IndexHasFilterAsync(db, "sessions", SessionIndex),
                    "The rebuilt index must cover the whole column, not carry the filter it was set up with.");
            }
        }

        [TestMethod]
        public async Task Migration_IsANoOpOnASecondRun()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ResetToPreMigrationStateAsync(db);

                await ExecAsync(db, RetireImportDbHacks.Up_Sql);
                // Must not throw, and must leave the same shape.
                await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                Assert.AreEqual(1, await IndexIsUniqueAsync(db, "hits", HitsIndex));
                Assert.AreEqual(0, await IndexIsUniqueAsync(db, "sessions", SessionIndex));
                Assert.AreEqual("SQL_Latin1_General_CP1_CS_AS", await SessionCollationAsync(db));
            }
        }

        [TestMethod]
        public async Task Migration_IsSafeOnAnAlreadyCorrectDatabase()
        {
            // The case every real deployment is in. It must be a no-op, and must not throw.
            using (var db = new AnalyticsEntitiesContext())
            {
                await EnsureSchemaAsync(db);
                await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                await ExecAsync(db, RetireImportDbHacks.Up_Sql);

                Assert.AreEqual(1, await IndexIsUniqueAsync(db, "hits", HitsIndex));
                Assert.AreEqual(0, await IndexIsUniqueAsync(db, "sessions", SessionIndex));
            }
        }

        [TestMethod]
        public void ManualScriptUsesTheSameSqlAsTheMigration()
        {
            // Rule 7 of the migration conventions: the manual upgrade script must contain the migration's
            // Up SQL verbatim, so a by-hand upgrade cannot diverge from the installer's.
            var manual = ReadManualScript("202609101200001_RetireImportDbHacks");

            StringAssert.Contains(manual, RetireImportDbHacks.Up_Sql,
                "The manual script must embed the migration's Up_Sql verbatim.");

            StringAssert.Contains(manual, "202609101100001_UniqueUrlsFullUrlIndex",
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
                System.IO.Path.GetDirectoryName(typeof(RetireImportDbHacksMigrationTests).Assembly.Location));

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
