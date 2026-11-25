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
INSERT dbo.users VALUES (1, 'reader@contoso.com');
INSERT dbo.sessions VALUES (1, 'session-a', 1), (2, 'session-b', 1);");
                const string token = "${STAGING_TABLE_SEARCHES}";
                const string staging = "staged_searches";
                db.Execute(SearchSql.Create_Searches_Import_Temp_Table.Replace(token, staging));
                var merge = SearchSql.Migrate_Searches_Import.Replace(token, staging);

                db.Execute(@"INSERT dbo.staged_searches (ai_session_id, user_name, search_term, date_time)
VALUES ('session-a', 'reader@contoso.com', N'Καλημέρα', '2000-01-01T09:00:00');");
                db.Execute(merge);
                db.Execute(merge);
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
            }
        }
    }
}
