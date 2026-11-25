using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SearchSql = WebJob.AppInsightsImporter.Engine.Properties.Resources;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class AppInsightsSearchMergeSqlTests
    {
        [TestMethod]
        public void SearchMerge_DeduplicatesBySessionTermAndStoredTimestamp_WithinAndAcrossBatches()
        {
            using (var db = ScratchDatabase.Create("SearchMerge"))
            {
                db.Execute(@"
CREATE TABLE dbo.users (id int PRIMARY KEY, user_name varchar(250) NOT NULL);
CREATE TABLE dbo.sessions (id int PRIMARY KEY, ai_session_id varchar(50) COLLATE SQL_Latin1_General_CP1_CS_AS, user_id int NOT NULL);
CREATE TABLE dbo.search_terms (id int IDENTITY PRIMARY KEY, search_term nvarchar(250) NOT NULL);
CREATE TABLE dbo.searches (id int IDENTITY PRIMARY KEY, session_id int NOT NULL, search_term_id int NOT NULL, date_time datetime NULL);
INSERT dbo.users VALUES (1, 'reader@contoso.com'), (2, 'other@contoso.com');
INSERT dbo.sessions VALUES (1, 'session-a', 1), (2, 'session-b', 1), (3, 'session-b', 2);");
                const string token = "${STAGING_TABLE_SEARCHES}";
                const string staging = "staged_searches";
                db.Execute(SearchSql.Create_Searches_Import_Temp_Table.Replace(token, staging));
                var merge = SearchSql.Migrate_Searches_Import.Replace(token, staging);

                db.Execute(@"INSERT dbo.staged_searches (ai_session_id, user_name, search_term, date_time)
VALUES ('session-a', 'reader@contoso.com', N'Καλημέρα', '2000-01-01T09:00:00');");
                using (var connection = new SqlConnection(db.ConnectionString))
                using (var command = new SqlCommand(merge, connection))
                {
                    connection.Open();
                    Assert.AreEqual(2, command.ExecuteNonQuery(), "Only the new term and search count as inserted rows.");
                    Assert.AreEqual(0, command.ExecuteNonQuery(), "Replaying on the same connection must report no inserts.");
                    command.CommandText = "SET NOCOUNT ON;\n" + merge + "\nSELECT @@OPTIONS & 512;";
                    Assert.AreEqual(512, command.ExecuteScalar(), "A caller that disabled row counts must keep its setting.");
                }
                Assert.AreEqual(1, db.Scalar("SELECT COUNT(*) FROM dbo.searches"), "Replaying a saved search must not add a row.");

                db.Execute(@"TRUNCATE TABLE dbo.staged_searches;
INSERT dbo.staged_searches (ai_session_id, user_name, search_term, date_time) VALUES
('session-b', 'reader@contoso.com', N'Καλημέρα', '2000-01-01T09:00:00'),
('session-b', 'reader@contoso.com', N'Καλημέρα', '2000-01-01T09:00:00.001'),
('session-a', 'reader@contoso.com', N'Καλημέρα', '2000-01-01T09:00:01'),
('session-a', 'reader@contoso.com', N'Δοκιμή',   '2000-01-01T09:00:00');");
                db.Execute(merge);

                const string count = @"SELECT COUNT(*) FROM dbo.searches s
JOIN dbo.search_terms t ON t.id = s.search_term_id WHERE ";
                Assert.AreEqual(1, db.Scalar(count + "s.session_id = 2 AND t.search_term = N'Καλημέρα' AND s.date_time = '2000-01-01T09:00:00'"),
                    "Another session's search must survive; both staging rows round to the same SQL datetime.");
                Assert.AreEqual(1, db.Scalar(count + "s.session_id = 1 AND t.search_term = N'Καλημέρα' AND s.date_time = '2000-01-01T09:00:01'"));
                Assert.AreEqual(1, db.Scalar(count + "s.session_id = 1 AND t.search_term = N'Δοκιμή' AND s.date_time = '2000-01-01T09:00:00'"));
                Assert.AreEqual(4, db.Scalar("SELECT COUNT(*) FROM dbo.searches"));

                db.Execute("UPDATE dbo.staged_searches SET date_time = NULL WHERE ai_session_id = 'session-b';");
                db.Execute(merge);
                db.Execute(merge);
                Assert.AreEqual(1, db.Scalar(count + "s.session_id = 2 AND t.search_term = N'Καλημέρα' AND s.date_time IS NULL"),
                    "The batch's date bounds must not exclude an undated replay.");
            }
        }
    }
}
