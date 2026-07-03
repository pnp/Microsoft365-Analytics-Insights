using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pure-logic tests for the user data lookup COUNT-SQL builder (used to show, per category, the
    /// SQL behind each count). These don't touch the database.
    /// </summary>
    [TestClass]
    public class UserDataLookupTests
    {
        private const string Upn = "jane.doe@contoso.com";

        [TestMethod]
        public void BuildCountSql_DirectForeignKey_UsesTableAndUserColumn()
        {
            var sql = UserDataCountSql.BuildCountSql("sent_emails", "user_id", false, false, Upn);

            StringAssert.Contains(sql, "SELECT COUNT(*) FROM sent_emails");
            StringAssert.Contains(sql, "WHERE user_id = (SELECT id FROM users WHERE user_name = 'jane.doe@contoso.com')");
            // Must not use the indirect / audit-event join forms.
            Assert.IsFalse(sql.Contains("INNER JOIN audit_events"));
            Assert.IsFalse(sql.Contains("session_id IN"));
        }

        [TestMethod]
        public void BuildCountSql_DifferentUserColumn_IsHonoured()
        {
            var sql = UserDataCountSql.BuildCountSql("team_owners", "owner_id", false, false, Upn);

            StringAssert.Contains(sql, "SELECT COUNT(*) FROM team_owners");
            StringAssert.Contains(sql, "WHERE owner_id = (SELECT id FROM users WHERE user_name = 'jane.doe@contoso.com')");
        }

        [TestMethod]
        public void BuildCountSql_WebHits_UsesSessionsSubquery()
        {
            var sql = UserDataCountSql.BuildCountSql("hits", null, true, false, Upn);

            StringAssert.Contains(sql, "SELECT COUNT(*) FROM hits");
            StringAssert.Contains(sql, "WHERE session_id IN (");
            StringAssert.Contains(sql, "SELECT id FROM sessions");
            StringAssert.Contains(sql, "WHERE user_id = (SELECT id FROM users WHERE user_name = 'jane.doe@contoso.com')");
        }

        [TestMethod]
        public void BuildCountSql_AuditSubType_JoinsThroughAuditEvents()
        {
            var sql = UserDataCountSql.BuildCountSql("copilot_chats", null, false, true, Upn);

            StringAssert.Contains(sql, "SELECT COUNT(*) FROM copilot_chats c");
            StringAssert.Contains(sql, "INNER JOIN audit_events e ON c.event_id = e.id");
            StringAssert.Contains(sql, "WHERE e.user_id = (SELECT id FROM users WHERE user_name = 'jane.doe@contoso.com')");
        }

        [TestMethod]
        public void EscapeSqlLiteral_DoublesSingleQuotes()
        {
            Assert.AreEqual("o''brien@contoso.com", UserDataCountSql.EscapeSqlLiteral("o'brien@contoso.com"));
            Assert.AreEqual("plain@contoso.com", UserDataCountSql.EscapeSqlLiteral("plain@contoso.com"));
            Assert.AreEqual(string.Empty, UserDataCountSql.EscapeSqlLiteral(null));
        }

        [TestMethod]
        public void BuildCountSql_EscapesUpnInGeneratedSql()
        {
            var sql = UserDataCountSql.BuildCountSql("sent_emails", "user_id", false, false, "o'brien@contoso.com");

            // The embedded literal must be quote-escaped so the generated SQL stays valid.
            StringAssert.Contains(sql, "user_name = 'o''brien@contoso.com'");
        }
    }
}
