namespace DataUtils.Sql
{
    /// <summary>
    /// Builds copy-pasteable <c>SELECT COUNT(*)</c> queries that reproduce, for a given UPN, the
    /// per-category record counts shown by the admin user-data-lookup feature. The generated SQL is
    /// for display only (so an admin can verify a count in SSMS); it is never executed by the app.
    /// </summary>
    public static class UserDataCountSql
    {
        /// <summary>
        /// Builds the COUNT SQL for the three ways a table links to a user: a direct foreign-key
        /// column, web hits (indirectly via sessions), or an audit sub-type (via
        /// <c>event_id -&gt; audit_events.user_id</c>).
        /// </summary>
        /// <param name="table">The table being counted (or the audit child table for the join form).</param>
        /// <param name="userColumn">The user foreign-key column (direct-FK form only).</param>
        /// <param name="indirectViaSession">True for web hits, which link to a user via sessions.</param>
        /// <param name="viaAuditEvent">True for audit sub-types linked via event_id to audit_events.</param>
        /// <param name="upn">The user principal name to embed (quote-escaped).</param>
        public static string BuildCountSql(string table, string userColumn, bool indirectViaSession, bool viaAuditEvent, string upn)
        {
            var literal = EscapeSqlLiteral(upn);

            if (indirectViaSession)
            {
                return
                    "SELECT COUNT(*) FROM hits\r\n" +
                    "WHERE session_id IN (\r\n" +
                    "    SELECT id FROM sessions\r\n" +
                    "    WHERE user_id = (SELECT id FROM users WHERE user_name = '" + literal + "'));";
            }

            if (viaAuditEvent)
            {
                return
                    "SELECT COUNT(*) FROM " + table + " c\r\n" +
                    "INNER JOIN audit_events e ON c.event_id = e.id\r\n" +
                    "WHERE e.user_id = (SELECT id FROM users WHERE user_name = '" + literal + "');";
            }

            return
                "SELECT COUNT(*) FROM " + table + "\r\n" +
                "WHERE " + userColumn + " = (SELECT id FROM users WHERE user_name = '" + literal + "');";
        }

        /// <summary>Escapes a string literal for safe embedding in the generated SQL (doubles quotes).</summary>
        public static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }
}
