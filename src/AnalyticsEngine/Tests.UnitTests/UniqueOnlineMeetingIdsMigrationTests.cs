using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// <see cref="UniqueOnlineMeetingIds"/>: the duplicate <c>dbo.online_meetings</c> rows the pre-fix Teams merge
    /// stored are collapsed onto one survivor per meeting id, every reference follows it, <c>meeting_id</c> becomes
    /// a UNIQUE nvarchar(850) - and none of that loses a link, even with an importer writing at the same time.
    /// </summary>
    /// <remarks>
    /// Each test builds its own database with the production shapes (migration CopilotEvents, renamed by
    /// CopilotExtendedData) and runs <see cref="UniqueOnlineMeetingIds.Up_Sql"/> - the constant the migration and
    /// the manual script both run - so the tests cannot drift from what ships.
    /// </remarks>
    [TestClass]
    public class UniqueOnlineMeetingIdsMigrationTests
    {
        private const string MigrationId = "202610221500003_UniqueOnlineMeetingIds";
        private const string ReleasedId = "202610080900001_AddPromptCategories";
        // The two migrations before this one are not in this branch: the fixture fakes their stamps (StampPredecessors).
        private const string FirstSiblingId = "202610221500001_CopilotAgentNameAndFlagRecency";
        private const string PredecessorId = "202610221500002_EventFileExtensionUnicode";
        private const string ReleasedModel = "0x1F8B0800AABBCCDD";

        // Synthetic subject in Greek: online_meetings.name is nvarchar, and the survivor must keep it exactly.
        private const string GreekSubject = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5 - Contoso planning";

        private static string MeetingId(string tag) =>
            "00000000-0000-0000-0000-000000000000_19:meeting_" + tag.PadRight(48, 'x') + "@thread.v2";

        #region Fixture

        private static ScratchDatabase NewDatabase(bool linkKeyIncludesMeeting = false)
        {
            var db = ScratchDatabase.Create("onlinemeetings");
            db.Execute(@"
CREATE TABLE dbo.copilot_chats (event_id uniqueidentifier NOT NULL CONSTRAINT [PK_dbo.copilot_chats] PRIMARY KEY);
CREATE TABLE dbo.online_meetings (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_dbo.online_meetings] PRIMARY KEY,
    created datetime NOT NULL,
    meeting_id nvarchar(max) NULL,
    name nvarchar(100) NULL);
CREATE TABLE dbo.copilot_event_meetings (
    copilot_chat_id uniqueidentifier NOT NULL,
    meeting_id int NOT NULL,
    CONSTRAINT [PK_dbo.event_copilot_meetings] PRIMARY KEY (" + (linkKeyIncludesMeeting ? "copilot_chat_id, meeting_id" : "copilot_chat_id") + @"),
    CONSTRAINT [FK_dbo.event_copilot_meetings_dbo.online_meetings_meeting_id]
        FOREIGN KEY (meeting_id) REFERENCES dbo.online_meetings (id) ON DELETE CASCADE,
    CONSTRAINT [FK_dbo.event_copilot_meetings_dbo.event_copilot_chats_copilot_chat_id]
        FOREIGN KEY (copilot_chat_id) REFERENCES dbo.copilot_chats (event_id));
CREATE INDEX IX_copilot_chat_id ON dbo.copilot_event_meetings (copilot_chat_id);
CREATE INDEX IX_meeting_id ON dbo.copilot_event_meetings (meeting_id);");
            return db;
        }

        private static string Sql(string value) => value == null ? "NULL" : "N'" + value.Replace("'", "''") + "'";

        private static int AddMeeting(ScratchDatabase db, string meetingId, string name) =>
            Convert.ToInt32(db.Scalar(
                $"INSERT INTO dbo.online_meetings (created, meeting_id, name) OUTPUT INSERTED.id VALUES ('2026-04-01T09:30:00', {Sql(meetingId)}, {Sql(name)});"));

        private static Guid Link(ScratchDatabase db, int meetingRow, Guid? chat = null)
        {
            var id = chat ?? Guid.NewGuid();
            db.Execute($@"IF NOT EXISTS (SELECT 1 FROM dbo.copilot_chats WHERE event_id = '{id}') INSERT INTO dbo.copilot_chats (event_id) VALUES ('{id}');
                          INSERT INTO dbo.copilot_event_meetings (copilot_chat_id, meeting_id) VALUES ('{id}', {meetingRow});");
            return id;
        }

        private static int Count(ScratchDatabase db, string sql) => Convert.ToInt32(db.Scalar(sql));

        private static int RowsFor(ScratchDatabase db, string meetingId) =>
            Count(db, $"SELECT COUNT(*) FROM dbo.online_meetings WHERE meeting_id = {Sql(meetingId)};");

        private static List<int> LinkedRows(ScratchDatabase db, Guid chat)
        {
            var rows = new List<int>();
            using (var connection = new SqlConnection(db.ConnectionString))
            using (var cmd = new SqlCommand("SELECT meeting_id FROM dbo.copilot_event_meetings WHERE copilot_chat_id = @chat ORDER BY meeting_id;", connection))
            {
                cmd.Parameters.AddWithValue("@chat", chat);
                connection.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) rows.Add(reader.GetInt32(0));
                }
            }
            return rows;
        }

        private static void RunMigration(ScratchDatabase db) => db.Execute(UniqueOnlineMeetingIds.Up_Sql);

        private static void AssertMigratedShape(ScratchDatabase db)
        {
            Assert.AreEqual("nvarchar:1700:1", db.Scalar(
                @"SELECT t.name + ':' + CAST(c.max_length AS varchar(10)) + ':' + CAST(c.is_nullable AS varchar(1))
                  FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                  WHERE c.object_id = OBJECT_ID('dbo.online_meetings') AND c.name = 'meeting_id'"),
                "meeting_id is a NULLable nvarchar(850): Unicode-safe and indexable.");
            Assert.IsTrue(db.IndexHasColumn("online_meetings", "UX_online_meetings_meeting_id", "meeting_id", 1, included: false));
            Assert.AreEqual(1, db.IndexColumnCount("online_meetings", "UX_online_meetings_meeting_id"), "Key only - no INCLUDE.");
            Assert.AreEqual(1, Count(db,
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.online_meetings') AND name = 'UX_online_meetings_meeting_id' AND is_unique = 1 AND ignore_dup_key = 1 AND has_filter = 0"));
        }

        private static int DuplicateGroups(ScratchDatabase db) =>
            Count(db, "SELECT COUNT(*) FROM (SELECT meeting_id FROM dbo.online_meetings WHERE meeting_id IS NOT NULL GROUP BY meeting_id HAVING COUNT(*) > 1) AS d;");

        private static int OrphanLinks(ScratchDatabase db) =>
            Count(db, "SELECT COUNT(*) FROM dbo.copilot_event_meetings AS l LEFT JOIN dbo.online_meetings AS m ON m.id = l.meeting_id WHERE m.id IS NULL;");

        #endregion

        #region De-duplication

        [TestMethod]
        public void Migration_LeavesOneRowPerMeeting_TheOldestCopyUnlessOnlyALaterOneHasASubject()
        {
            using (var db = NewDatabase())
            {
                var a1 = AddMeeting(db, MeetingId("a"), GreekSubject);
                AddMeeting(db, MeetingId("a"), GreekSubject);
                AddMeeting(db, MeetingId("a"), "Contoso planning (renamed)");
                var b1 = AddMeeting(db, MeetingId("b"), "Contoso weekly");
                AddMeeting(db, MeetingId("c"), "");
                var c2 = AddMeeting(db, MeetingId("c"), "Contoso retro");
                AddMeeting(db, MeetingId("c"), null);
                Assert.AreEqual(2, DuplicateGroups(db), "Pre-condition: the database holds copies, as older builds stored them.");

                RunMigration(db);

                Assert.AreEqual(0, DuplicateGroups(db), "One row per meeting id.");
                Assert.AreEqual(3, Count(db, "SELECT COUNT(*) FROM dbo.online_meetings;"));
                Assert.AreEqual(a1, Count(db, $"SELECT id FROM dbo.online_meetings WHERE meeting_id = {Sql(MeetingId("a"))}"), "The oldest copy survives.");
                Assert.AreEqual(GreekSubject, db.Scalar($"SELECT name FROM dbo.online_meetings WHERE id = {a1}"), "Its subject is kept exactly.");
                Assert.AreEqual(b1, Count(db, $"SELECT id FROM dbo.online_meetings WHERE meeting_id = {Sql(MeetingId("b"))}"));
                Assert.AreEqual(c2, Count(db, $"SELECT id FROM dbo.online_meetings WHERE meeting_id = {Sql(MeetingId("c"))}"),
                    "A copy with a subject beats an older one without.");
                AssertMigratedShape(db);
            }
        }

        [TestMethod]
        public void Migration_MovesEveryLinkOntoTheSurvivor_AndLosesNone()
        {
            using (var db = NewDatabase())
            {
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a3 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var b1 = AddMeeting(db, MeetingId("b"), "Contoso review");
                var onFirst = Link(db, a1);
                var onSecond = Link(db, a2);
                var onThird = new[] { Link(db, a3), Link(db, a3) };
                var onOther = Link(db, b1);

                RunMigration(db);

                Assert.AreEqual(5, Count(db, "SELECT COUNT(*) FROM dbo.copilot_event_meetings;"), "No link is lost.");
                foreach (var chat in new[] { onFirst, onSecond }.Concat(onThird))
                {
                    CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, chat), "Every interaction in meeting a now links to its survivor.");
                }
                CollectionAssert.AreEqual(new[] { b1 }, LinkedRows(db, onOther));
                Assert.AreEqual(0, OrphanLinks(db));
            }
        }

        [TestMethod]
        public void Migration_InteractionLinkedToTwoCopies_KeepsOneLink()
        {
            // copilot_event_meetings' key is copilot_chat_id, so this can't happen there; a referencing table whose
            // key does include the meeting can, and the repoint would then fail on its own primary key.
            using (var db = NewDatabase(linkKeyIncludesMeeting: true))
            {
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a3 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var onBothCopies = Link(db, a2);
                Link(db, a3, onBothCopies);
                var onSurvivorAndCopy = Link(db, a3);
                Link(db, a1, onSurvivorAndCopy);

                RunMigration(db);

                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, onBothCopies));
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, onSurvivorAndCopy));
                Assert.AreEqual(0, DuplicateGroups(db));
            }
        }

        private static void CreateCustomReferences(ScratchDatabase db)
        {
            db.Execute(@"EXEC (N'CREATE SCHEMA contoso');");
            db.Execute(@"
CREATE TABLE contoso.meeting_notes (
    meeting_ref int NOT NULL CONSTRAINT PK_contoso_meeting_notes PRIMARY KEY
        CONSTRAINT FK_contoso_meeting_notes_online_meetings REFERENCES dbo.online_meetings (id),
    note nvarchar(100) NOT NULL);
CREATE TABLE contoso.meeting_note_items (
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    note_ref int NOT NULL CONSTRAINT FK_contoso_meeting_note_items_notes REFERENCES contoso.meeting_notes (meeting_ref)
        ON DELETE CASCADE ON UPDATE CASCADE,
    item nvarchar(50) NOT NULL);
CREATE TABLE contoso.meeting_tags (
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    meeting_ref int NOT NULL CONSTRAINT FK_contoso_meeting_tags_online_meetings REFERENCES dbo.online_meetings (id),
    tag nvarchar(50) NOT NULL);");
        }

        private static string Note(ScratchDatabase db, int meetingRow) =>
            db.Scalar($"SELECT note FROM contoso.meeting_notes WHERE meeting_ref = {meetingRow}") as string;

        [TestMethod]
        public void Migration_CustomTableRowsThatWouldCollide_StopTheMigration_LoseNothing_AndARerunAfterTheyAreMergedConverges()
        {
            using (var db = NewDatabase())
            {
                StampPredecessors(db);
                CreateCustomReferences(db);
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a3 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                db.Execute($@"INSERT INTO contoso.meeting_notes (meeting_ref, note) VALUES ({a2}, N'note on the second copy'), ({a3}, N'note on the third copy');
                              INSERT INTO contoso.meeting_note_items (note_ref, item) VALUES ({a3}, N'agenda'), ({a3}, N'minutes');
                              INSERT INTO contoso.meeting_tags (meeting_ref, tag) VALUES ({a2}, N'planning'), ({a3}, N'planning'), ({a3}, N'finance');");
                var onSecond = Link(db, a2);
                var onThird = Link(db, a3);

                void AssertNothingLost()
                {
                    Assert.AreEqual("note on the second copy", Note(db, a2), "Both notes keep their own text.");
                    Assert.AreEqual("note on the third copy", Note(db, a3));
                    Assert.AreEqual(2, Count(db, $"SELECT COUNT(*) FROM contoso.meeting_note_items WHERE note_ref = {a3}"),
                        "Deleting a note would have cascaded to its items.");
                    Assert.AreEqual(3, Count(db, "SELECT COUNT(*) FROM contoso.meeting_tags"));
                    Assert.AreEqual(3, RowsFor(db, MeetingId("a")), "Every copy is still there.");
                    Assert.AreEqual(0, Count(db, "SELECT COUNT(*) FROM dbo.online_meetings WHERE meeting_id IS NULL"),
                        "The batch's copies are not left with their meeting id cleared.");
                    CollectionAssert.AreEqual(new[] { a2 }, LinkedRows(db, onSecond), "Links are not moved by a rolled-back batch.");
                    CollectionAssert.AreEqual(new[] { a3 }, LinkedRows(db, onThird));
                }

                var ex = Assert.ThrowsException<SqlException>(() => RunMigration(db));
                Assert.AreEqual(50001, ex.Number);
                StringAssert.Contains(ex.Message, "contoso.meeting_notes");
                StringAssert.Contains(ex.Message, "PK_contoso_meeting_notes");
                Assert.IsFalse(ex.Message.Contains("note on the"), "The message names the table and index, never row values.");
                AssertNothingLost();

                ex = Assert.ThrowsException<SqlException>(() => db.ExecuteScript(ManualScript(), quotedIdentifierOn: false));
                Assert.AreEqual(50001, ex.Number);
                Assert.AreEqual(0, StampCount(db), "The manual script does not stamp a migration that stopped.");
                AssertNothingLost();

                // What the DBA does: keep one note per meeting, merging the other's text and items into it.
                db.Execute($@"UPDATE contoso.meeting_notes SET note = N'note on the second copy; note on the third copy' WHERE meeting_ref = {a2};
                              UPDATE contoso.meeting_note_items SET note_ref = {a2} WHERE note_ref = {a3};
                              DELETE FROM contoso.meeting_notes WHERE meeting_ref = {a3};");

                RunMigration(db);

                Assert.AreEqual(1, Count(db, "SELECT COUNT(*) FROM contoso.meeting_notes"));
                Assert.AreEqual("note on the second copy; note on the third copy", Note(db, a1), "The merged note moved onto the survivor.");
                Assert.AreEqual(2, Count(db, $"SELECT COUNT(*) FROM contoso.meeting_note_items WHERE note_ref = {a1}"));
                Assert.AreEqual(3, Count(db, $"SELECT COUNT(*) FROM contoso.meeting_tags WHERE meeting_ref = {a1}"),
                    "A reference without a unique key is moved, and loses nothing.");
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, onSecond));
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, onThird));
                Assert.AreEqual(0, DuplicateGroups(db));
                AssertMigratedShape(db);
            }
        }

        [TestMethod]
        public void Migration_CustomTableRowsThatDoNotCollide_AreMovedOntoTheSurvivor()
        {
            using (var db = NewDatabase())
            {
                CreateCustomReferences(db);
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a3 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var b1 = AddMeeting(db, MeetingId("b"), "Contoso review");
                db.Execute($@"INSERT INTO contoso.meeting_notes (meeting_ref, note) VALUES ({a2}, N'note on the second copy'), ({b1}, N'note on meeting b');
                              INSERT INTO contoso.meeting_note_items (note_ref, item) VALUES ({a2}, N'agenda');
                              INSERT INTO contoso.meeting_tags (meeting_ref, tag) VALUES ({a2}, N'planning'), ({a3}, N'planning'), ({a3}, N'finance');");

                RunMigration(db);

                Assert.AreEqual("note on the second copy", Note(db, a1), "The only note on meeting a moved onto its survivor.");
                Assert.AreEqual("note on meeting b", Note(db, b1));
                Assert.AreEqual(1, Count(db, $"SELECT COUNT(*) FROM contoso.meeting_note_items WHERE note_ref = {a1}"));
                Assert.AreEqual(3, Count(db, $"SELECT COUNT(*) FROM contoso.meeting_tags WHERE meeting_ref = {a1}"));
                Assert.AreEqual(0, DuplicateGroups(db));
                AssertMigratedShape(db);
            }
        }

        [TestMethod]
        public void Migration_LinkTableWithAddedColumns_StopsOnACollisionInsteadOfDeleting()
        {
            // The link table's own collisions are pruned only while it holds nothing but the link: a column added to
            // it could hold data, so a collision there stops the migration like any other table's.
            using (var db = NewDatabase(linkKeyIncludesMeeting: true))
            {
                db.Execute("ALTER TABLE dbo.copilot_event_meetings ADD contoso_comment nvarchar(100) NULL;");
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var chat = Link(db, a1);
                Link(db, a2, chat);

                var ex = Assert.ThrowsException<SqlException>(() => RunMigration(db));

                Assert.AreEqual(50001, ex.Number);
                StringAssert.Contains(ex.Message, "dbo.copilot_event_meetings");
                CollectionAssert.AreEqual(new[] { a1, a2 }, LinkedRows(db, chat), "Neither link is deleted.");
                Assert.AreEqual(2, RowsFor(db, MeetingId("a")));
            }
        }

        [TestMethod]
        public void Migration_LinkTableThatAnotherTableReferences_StopsOnACollisionInsteadOfDeleting()
        {
            // The historical composite link key, referenced by a customer table: deleting the duplicate link would
            // cascade to that table's row.
            using (var db = NewDatabase(linkKeyIncludesMeeting: true))
            {
                db.Execute(@"EXEC (N'CREATE SCHEMA contoso');");
                db.Execute(@"
CREATE TABLE contoso.link_notes (
    id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    copilot_chat_id uniqueidentifier NOT NULL,
    meeting_id int NOT NULL,
    note nvarchar(100) NOT NULL,
    CONSTRAINT FK_contoso_link_notes_links FOREIGN KEY (copilot_chat_id, meeting_id)
        REFERENCES dbo.copilot_event_meetings (copilot_chat_id, meeting_id) ON DELETE CASCADE ON UPDATE CASCADE);");
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var chat = Link(db, a1);
                Link(db, a2, chat);
                db.Execute($"INSERT INTO contoso.link_notes (copilot_chat_id, meeting_id, note) VALUES ('{chat}', {a2}, N'note on the copied link');");

                var ex = Assert.ThrowsException<SqlException>(() => RunMigration(db));

                Assert.AreEqual(50001, ex.Number);
                StringAssert.Contains(ex.Message, "dbo.copilot_event_meetings");
                Assert.AreEqual("note on the copied link", db.Scalar($"SELECT note FROM contoso.link_notes WHERE meeting_id = {a2}"),
                    "The note on the copied link is kept.");
                CollectionAssert.AreEqual(new[] { a1, a2 }, LinkedRows(db, chat), "Neither link is deleted.");
                Assert.AreEqual(2, RowsFor(db, MeetingId("a")));
            }
        }

        [TestMethod]
        public void Migration_LinkTableWithAUniqueKeyOnTheMeetingAlone_StopsInsteadOfDeletingAnotherInteractionsLink()
        {
            // A unique key on meeting_id alone makes two DIFFERENT interactions collide on the survivor: pruning would
            // delete one interaction's only link.
            using (var db = NewDatabase())
            {
                db.Execute("CREATE UNIQUE INDEX UX_contoso_one_link_per_meeting ON dbo.copilot_event_meetings (meeting_id);");
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var first = Link(db, a1);
                var second = Link(db, a2);

                var ex = Assert.ThrowsException<SqlException>(() => RunMigration(db));

                Assert.AreEqual(50001, ex.Number);
                StringAssert.Contains(ex.Message, "UX_contoso_one_link_per_meeting");
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, first));
                CollectionAssert.AreEqual(new[] { a2 }, LinkedRows(db, second), "The other interaction keeps its only link.");
                Assert.AreEqual(2, RowsFor(db, MeetingId("a")));
            }
        }

        [TestMethod]
        public void Migration_LinkTableWithADeleteTrigger_StopsOnACollisionInsteadOfRunningIt()
        {
            // Deleting the duplicate link would run the customer's trigger, which here deletes their own data.
            using (var db = NewDatabase(linkKeyIncludesMeeting: true))
            {
                db.Execute(@"EXEC (N'CREATE SCHEMA contoso');");
                db.Execute("CREATE TABLE contoso.link_payload (copilot_chat_id uniqueidentifier NOT NULL, meeting_id int NOT NULL, payload nvarchar(100) NOT NULL);");
                db.Execute(@"CREATE TRIGGER dbo.contoso_link_deleted ON dbo.copilot_event_meetings AFTER DELETE AS
BEGIN
    SET NOCOUNT ON;
    DELETE p FROM contoso.link_payload AS p
    INNER JOIN deleted AS d ON d.copilot_chat_id = p.copilot_chat_id AND d.meeting_id = p.meeting_id;
END");
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var a2 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var chat = Link(db, a1);
                Link(db, a2, chat);
                db.Execute($"INSERT INTO contoso.link_payload (copilot_chat_id, meeting_id, payload) VALUES ('{chat}', {a2}, N'payload on the copied link');");

                var ex = Assert.ThrowsException<SqlException>(() => RunMigration(db));

                Assert.AreEqual(50001, ex.Number);
                StringAssert.Contains(ex.Message, "dbo.copilot_event_meetings");
                Assert.AreEqual(1, Count(db, "SELECT COUNT(*) FROM contoso.link_payload"), "The trigger never ran, so its payload is kept.");
                CollectionAssert.AreEqual(new[] { a1, a2 }, LinkedRows(db, chat), "Neither link is deleted.");
            }
        }

        [TestMethod]
        public void Migration_RunTwice_SecondRunChangesNothing()
        {
            using (var db = NewDatabase())
            {
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var copy = AddMeeting(db, MeetingId("a"), "Contoso sync");
                Link(db, copy);
                RunMigration(db);
                const string snapshot = "SELECT CHECKSUM_AGG(CHECKSUM(id, meeting_id, name)) FROM dbo.online_meetings";
                const string links = "SELECT CHECKSUM_AGG(CHECKSUM(copilot_chat_id, meeting_id)) FROM dbo.copilot_event_meetings";
                var meetingsAfterFirst = db.Scalar(snapshot);
                var linksAfterFirst = db.Scalar(links);

                RunMigration(db);

                Assert.AreEqual(meetingsAfterFirst, db.Scalar(snapshot));
                Assert.AreEqual(linksAfterFirst, db.Scalar(links));
                Assert.AreEqual(1, db.NonClusteredIndexCount("online_meetings"), "The index is not built twice.");
                AssertMigratedShape(db);
                Assert.AreEqual(a1, Count(db, "SELECT MIN(id) FROM dbo.online_meetings"));
            }
        }

        [TestMethod]
        public void Migration_InterruptedAfterNarrowing_ConvergesOnReRun()
        {
            using (var db = NewDatabase())
            {
                // As if a run stopped after narrowing the column: copies remain, and no index.
                db.Execute("ALTER TABLE dbo.online_meetings ALTER COLUMN meeting_id nvarchar(850) NULL;");
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var copy = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var chat = Link(db, copy);

                RunMigration(db);

                Assert.AreEqual(0, DuplicateGroups(db));
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, chat));
                AssertMigratedShape(db);
            }
        }

        [TestMethod]
        public void Migration_CancelledMidBatch_RollsTheBatchBack_AndARerunConverges()
        {
            // An SSMS Stop, or a client timeout, ends the batch without running its CATCH. Unless XACT_ABORT is ON, the
            // batch's transaction then stays open - its copies already NULL, their locks held, the importer blocked - and
            // SSMS offers to COMMIT it when the window closes.
            using (var db = NewDatabase())
            {
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var aCopy = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var b1 = AddMeeting(db, MeetingId("b"), "Contoso review");
                var bCopy = AddMeeting(db, MeetingId("b"), "Contoso review");
                var onACopy = Link(db, aCopy);
                var onBCopy = Link(db, bCopy);

                // Without MARS, as SSMS connects: under MARS a transaction still open when its batch ends is rolled back.
                var noMars = new SqlConnectionStringBuilder(db.ConnectionString) { MultipleActiveResultSets = false }.ConnectionString;
                using (var blocker = new SqlConnection(noMars))
                using (var upgrade = new SqlConnection(noMars))
                {
                    blocker.Open();
                    upgrade.Open();

                    // Lock a link the first batch has to move, so the upgrade stops inside that batch's transaction.
                    var held = blocker.BeginTransaction();
                    using (var hold = new SqlCommand(
                        "UPDATE dbo.copilot_event_meetings WITH (ROWLOCK, HOLDLOCK) SET meeting_id = meeting_id WHERE copilot_chat_id = @chat;",
                        blocker, held))
                    {
                        hold.Parameters.AddWithValue("@chat", onACopy);
                        hold.ExecuteNonQuery();
                    }

                    using (var migrate = new SqlCommand(UniqueOnlineMeetingIds.Up_Sql, upgrade) { CommandTimeout = 3 })
                    {
                        var cancelled = Assert.ThrowsException<SqlException>(() => migrate.ExecuteNonQuery());
                        Assert.AreEqual(-2, cancelled.Number, "Pre-condition: the client gave up while the batch waited on the locked link.");
                    }

                    Assert.AreEqual(0, Convert.ToInt32(new SqlCommand("SELECT @@TRANCOUNT;", upgrade).ExecuteScalar()),
                        "The cancelled batch's transaction must not be left open on the session.");
                    Assert.AreEqual(0, Convert.ToInt32(new SqlCommand("SELECT COUNT(*) FROM dbo.online_meetings WHERE meeting_id IS NULL;", upgrade).ExecuteScalar()),
                        "The batch's copies must be untouched.");

                    held.Rollback();
                }

                RunMigration(db);

                Assert.AreEqual(0, DuplicateGroups(db));
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, onACopy));
                CollectionAssert.AreEqual(new[] { b1 }, LinkedRows(db, onBCopy));
                Assert.AreEqual(0, OrphanLinks(db));
                AssertMigratedShape(db);
            }
        }

        [TestMethod]
        public void Migration_IdLongerThanTheIndexKey_IsTrimmedTo850Characters()
        {
            using (var db = NewDatabase())
            {
                var longId = MeetingId("a") + new string('z', 900);
                var first = AddMeeting(db, longId, "Contoso sync");
                AddMeeting(db, longId, "Contoso sync");

                RunMigration(db);

                Assert.AreEqual(1, Count(db, "SELECT COUNT(*) FROM dbo.online_meetings"));
                Assert.AreEqual(longId.Substring(0, 850), db.Scalar($"SELECT meeting_id FROM dbo.online_meetings WHERE id = {first}"));
                AssertMigratedShape(db);
            }
        }

        [TestMethod]
        public void Migration_RowsWithoutAMeetingId_KeepTheirLinks_AndDoNotStopTheUniqueIndex()
        {
            using (var db = NewDatabase())
            {
                var unknown1 = AddMeeting(db, null, "Contoso sync");
                var unknown2 = AddMeeting(db, null, "Contoso review");
                var chat1 = Link(db, unknown1);
                var chat2 = Link(db, unknown2);

                RunMigration(db);

                Assert.AreEqual(2, Count(db, "SELECT COUNT(*) FROM dbo.online_meetings"), "Rows without an id are different meetings, not copies.");
                Assert.AreEqual("unidentified-meeting:" + unknown1, db.Scalar($"SELECT meeting_id FROM dbo.online_meetings WHERE id = {unknown1}"));
                CollectionAssert.AreEqual(new[] { unknown1 }, LinkedRows(db, chat1));
                CollectionAssert.AreEqual(new[] { unknown2 }, LinkedRows(db, chat2));
                AssertMigratedShape(db);
            }
        }

        /// <summary>
        /// The importer keeps running during the upgrade, and a previous-build importer keeps storing copies. Every
        /// copy it stores while the de-duplication runs must be mopped up before the unique index is built, and no
        /// link it commits - even one to a copy that is about to be removed - may be lost to ON DELETE CASCADE.
        /// </summary>
        /// <remarks>
        /// The writer waits out the migration's offline column change and index build, as the importer does; on a
        /// busy machine that is longer than SqlClient's default 30 seconds.
        /// </remarks>
        [TestMethod]
        public async Task Migration_WithAWriterStoringCopiesMeanwhile_MopsThemUpAndLosesNoLink()
        {
            const int WriterTimeoutSeconds = 600;
            using (var db = NewDatabase())
            {
                const int meetings = 15000;
                db.Execute($@"
;WITH n AS (SELECT TOP ({meetings}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT INTO dbo.online_meetings (created, meeting_id, name)
SELECT '2026-04-01T09:30:00', N'00000000-0000-0000-0000-000000000000_19:meeting_' + RIGHT(REPLICATE(N'0', 48) + CAST(n.k AS nvarchar(10)), 48) + N'@thread.v2', N'Contoso sync'
FROM n CROSS JOIN (VALUES (1), (2), (3)) AS copies(c)
ORDER BY c, k;
INSERT INTO dbo.copilot_chats (event_id) SELECT NEWID() FROM dbo.online_meetings;
;WITH m AS (SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM dbo.online_meetings),
      c AS (SELECT event_id, ROW_NUMBER() OVER (ORDER BY event_id) AS rn FROM dbo.copilot_chats)
INSERT INTO dbo.copilot_event_meetings (copilot_chat_id, meeting_id) SELECT c.event_id, m.id FROM m INNER JOIN c ON c.rn = m.rn;");
                var seededLinks = Count(db, "SELECT COUNT(*) FROM dbo.copilot_event_meetings");
                var ids = Enumerable.Range(1, meetings).Select(k => "00000000-0000-0000-0000-000000000000_19:meeting_" + k.ToString().PadLeft(48, '0') + "@thread.v2").ToArray();

                var committedLinks = new ConcurrentDictionary<Guid, string>();
                var copiesStoredDuringMigration = 0;
                var migrationRunning = 0;
                using (var stop = new CancellationTokenSource())
                {
                    var writer = Task.Run(() =>
                    {
                        var random = new Random(1);
                        using (var connection = new SqlConnection(db.ConnectionString))
                        {
                            connection.Open();
                            while (!stop.IsCancellationRequested)
                            {
                                var meetingId = ids[random.Next(ids.Length)];
                                var chat = Guid.NewGuid();
                                try
                                {
                                    // A previous-build merge: store the meeting again, then link the interaction to that copy.
                                    object copy;
                                    using (var insert = new SqlCommand("INSERT INTO dbo.online_meetings (created, meeting_id, name) OUTPUT INSERTED.id VALUES ('2026-04-01T09:30:00', @m, N'Contoso sync');", connection) { CommandTimeout = WriterTimeoutSeconds })
                                    {
                                        insert.Parameters.AddWithValue("@m", meetingId);
                                        copy = insert.ExecuteScalar();
                                    }
                                    if (copy != null && Volatile.Read(ref migrationRunning) == 1) Interlocked.Increment(ref copiesStoredDuringMigration);
                                    using (var link = new SqlCommand(@"
INSERT INTO dbo.copilot_chats (event_id) VALUES (@chat);
INSERT INTO dbo.copilot_event_meetings (copilot_chat_id, meeting_id)
SELECT @chat, ISNULL(@copy, (SELECT MIN(id) FROM dbo.online_meetings WHERE meeting_id = @m));", connection) { CommandTimeout = WriterTimeoutSeconds })
                                    {
                                        link.Parameters.AddWithValue("@chat", chat);
                                        link.Parameters.AddWithValue("@m", meetingId);
                                        link.Parameters.AddWithValue("@copy", copy ?? (object)DBNull.Value);
                                        link.ExecuteNonQuery();
                                    }
                                    committedLinks[chat] = meetingId;
                                }
                                catch (SqlException ex) when (ex.Number == 547 || ex.Number == 1205 || ex.Number == 515)
                                {
                                    // The copy went while this batch was linking to it: the importer retries the batch.
                                }
                            }
                        }
                    });

                    await Task.Delay(200);
                    Volatile.Write(ref migrationRunning, 1);
                    await Task.Run(() => RunMigration(db));
                    Volatile.Write(ref migrationRunning, 0);
                    await Task.Delay(200);
                    stop.Cancel();
                    await writer;
                }

                if (copiesStoredDuringMigration == 0)
                {
                    Assert.Inconclusive("The writer stored no copy while the migration ran, so this run proved nothing about concurrency.");
                }
                AssertMigratedShape(db);
                Assert.AreEqual(0, DuplicateGroups(db), $"Every copy stored meanwhile ({copiesStoredDuringMigration}) was mopped up.");
                Assert.AreEqual(0, OrphanLinks(db));
                Assert.AreEqual(seededLinks + committedLinks.Count, Count(db, "SELECT COUNT(*) FROM dbo.copilot_event_meetings"), "No committed link was lost.");
                using (var connection = new SqlConnection(db.ConnectionString))
                {
                    connection.Open();
                    foreach (var link in committedLinks)
                    {
                        using (var cmd = new SqlCommand("SELECT m.meeting_id FROM dbo.copilot_event_meetings AS l INNER JOIN dbo.online_meetings AS m ON m.id = l.meeting_id WHERE l.copilot_chat_id = @chat;", connection))
                        {
                            cmd.Parameters.AddWithValue("@chat", link.Key);
                            Assert.AreEqual(link.Value, cmd.ExecuteScalar(), "A link committed during the migration still points at its own meeting.");
                        }
                    }
                }
            }
        }

        #endregion

        #region Manual upgrade script

        private static string ManualScript()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Common", "Entities", "Migrations", MigrationId + ".manual.sql");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {MigrationId}.manual.sql by walking up from the test assembly.");
            return null;
        }

        /// <summary>The two sibling migrations don't exist in this branch: fake their stamps as copies of the last released one.</summary>
        private static void StampPredecessors(ScratchDatabase db)
        {
            db.Execute($@"
CREATE TABLE dbo.__MigrationHistory (
    MigrationId nvarchar(150) NOT NULL,
    ContextKey nvarchar(300) NOT NULL,
    Model varbinary(max) NOT NULL,
    ProductVersion nvarchar(32) NOT NULL,
    CONSTRAINT [PK_dbo.__MigrationHistory] PRIMARY KEY (MigrationId, ContextKey));
INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
VALUES (N'{ReleasedId}', N'Common.Entities.Migrations.Configuration', {ReleasedModel}, N'6.5.1');
INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
SELECT N'{FirstSiblingId}', ContextKey, Model, ProductVersion FROM dbo.__MigrationHistory WHERE MigrationId = N'{ReleasedId}';
INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
SELECT N'{PredecessorId}', ContextKey, Model, ProductVersion FROM dbo.__MigrationHistory WHERE MigrationId = N'{ReleasedId}';");
        }

        private static int StampCount(ScratchDatabase db) =>
            Count(db, $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'");

        [TestMethod]
        public void ManualScript_EmbedsTheMigrationsUpSqlVerbatim()
        {
            StringAssert.Contains(ManualScript(), UniqueOnlineMeetingIds.Up_Sql.Trim(),
                "A by-hand upgrade must run exactly what the installer runs.");
        }

        [TestMethod]
        public void ManualScript_AppliesStampsWithThePredecessorsModel_AndReRunIsANoOp()
        {
            using (var db = NewDatabase())
            {
                StampPredecessors(db);
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var copy = AddMeeting(db, MeetingId("a"), "Contoso sync");
                var chat = Link(db, copy);

                // sqlcmd's defaults: QUOTED_IDENTIFIER OFF.
                db.ExecuteScript(ManualScript(), quotedIdentifierOn: false);

                Assert.AreEqual(0, DuplicateGroups(db));
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, chat));
                AssertMigratedShape(db);
                Assert.AreEqual(1, StampCount(db));
                Assert.AreEqual("yes", db.Scalar(
                    $"SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}') = " +
                    $"(SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}') THEN 'yes' ELSE 'no' END"),
                    "The EF model is unchanged, so the stamp carries the predecessor's model.");

                const string snapshot = "SELECT CHECKSUM_AGG(CHECKSUM(id, meeting_id, name)) FROM dbo.online_meetings";
                var before = db.Scalar(snapshot);
                db.ExecuteScript(ManualScript(), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db), "Not stamped twice.");
                Assert.AreEqual(before, db.Scalar(snapshot), "A re-run changes nothing.");
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, chat));
            }
        }

        [TestMethod]
        public void ManualScript_WithoutThePredecessor_ChangesNothingAndDoesNotStamp()
        {
            using (var db = NewDatabase())
            {
                StampPredecessors(db);
                db.Execute($"DELETE FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}';");
                AddMeeting(db, MeetingId("a"), "Contoso sync");
                AddMeeting(db, MeetingId("a"), "Contoso sync");

                var ex = Assert.ThrowsException<SqlException>(() => db.ExecuteScript(ManualScript(), quotedIdentifierOn: false));

                StringAssert.Contains(ex.Message, PredecessorId);
                Assert.AreEqual(0, StampCount(db));
                Assert.AreEqual(1, DuplicateGroups(db), "Nothing was changed.");
            }
        }

        #endregion

        #region The Teams merge against the migrated table

        private static string TeamsMergeSql(string stagingTable)
        {
            const string resource = "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql";
            using (var stream = typeof(SqlCopilotStagingWriter).Assembly.GetManifestResourceStream(resource))
            {
                Assert.IsNotNull(stream, "The Teams merge script is an embedded resource of the importer engine.");
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd().Replace("${STAGING_TABLE_ACTIVITY}", stagingTable);
                }
            }
        }

        private static void Stage(ScratchDatabase db, Guid chat, string meetingId, string subject, DateTime? created)
        {
            db.Execute($@"IF OBJECT_ID(N'dbo.stage_teams') IS NULL
    CREATE TABLE dbo.stage_teams ([id] int IDENTITY(1,1) NOT NULL PRIMARY KEY, [event_id] uniqueidentifier NOT NULL,
        [meeting_id] nvarchar(max) NOT NULL, [meeting_created_utc] datetime2 NULL, [meeting_name] nvarchar(max) NULL);
IF NOT EXISTS (SELECT 1 FROM dbo.copilot_chats WHERE event_id = '{chat}') INSERT INTO dbo.copilot_chats (event_id) VALUES ('{chat}');
INSERT INTO dbo.stage_teams (event_id, meeting_id, meeting_created_utc, meeting_name)
VALUES ('{chat}', {Sql(meetingId)}, {(created.HasValue ? "'" + created.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'" : "NULL")}, {Sql(subject)});");
        }

        [TestMethod]
        public void TeamsMerge_AfterTheMigration_StoresEachMeetingOnce_AndLinksEveryInteractionToIt()
        {
            using (var db = NewDatabase())
            {
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                AddMeeting(db, MeetingId("a"), "Contoso sync");
                RunMigration(db);

                // A Graph creation time with 7 fractional digits, which datetime can't hold.
                var created = new DateTime(2026, 4, 1, 9, 30, 0, DateTimeKind.Utc).AddTicks(1234567);
                var inRenamedMeeting = Guid.NewGuid();
                var firstInNewMeeting = Guid.NewGuid();
                var secondInNewMeeting = Guid.NewGuid();
                var withoutMeetingDetails = Guid.NewGuid();
                Stage(db, inRenamedMeeting, MeetingId("a"), "Contoso sync (renamed)", created);
                Stage(db, firstInNewMeeting, MeetingId("n"), GreekSubject, created);
                Stage(db, secondInNewMeeting, MeetingId("n"), "Contoso sync, subject changed mid-batch", created);
                Stage(db, withoutMeetingDetails, MeetingId("a"), null, null);

                db.Execute(TeamsMergeSql("stage_teams"));
                db.Execute(TeamsMergeSql("stage_teams"));   // a retried batch

                Assert.AreEqual(1, RowsFor(db, MeetingId("a")), "A stored meeting is not stored again, whatever its subject says now.");
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, inRenamedMeeting));
                Assert.AreEqual(1, RowsFor(db, MeetingId("n")), "A meeting seen twice in one batch is stored once.");
                var n = Count(db, $"SELECT id FROM dbo.online_meetings WHERE meeting_id = {Sql(MeetingId("n"))}");
                CollectionAssert.AreEqual(new[] { n }, LinkedRows(db, firstInNewMeeting));
                CollectionAssert.AreEqual(new[] { n }, LinkedRows(db, secondInNewMeeting));
                Assert.AreEqual(0, LinkedRows(db, withoutMeetingDetails).Count, "As before: no meeting details, no link.");
            }
        }

        [TestMethod]
        public void TeamsMerge_BeforeTheMigration_LinksToTheOldestCopy_AndStoresNoMore()
        {
            // An importer upgraded before its database: the copies are still there.
            using (var db = NewDatabase())
            {
                var a1 = AddMeeting(db, MeetingId("a"), "Contoso sync");
                AddMeeting(db, MeetingId("a"), "Contoso sync");
                var chat = Guid.NewGuid();
                Stage(db, chat, MeetingId("a"), "Contoso sync", new DateTime(2026, 4, 1, 9, 30, 0, DateTimeKind.Utc));

                db.Execute(TeamsMergeSql("stage_teams"));

                Assert.AreEqual(2, RowsFor(db, MeetingId("a")), "No further copy.");
                CollectionAssert.AreEqual(new[] { a1 }, LinkedRows(db, chat));
            }
        }

        #endregion
    }
}
