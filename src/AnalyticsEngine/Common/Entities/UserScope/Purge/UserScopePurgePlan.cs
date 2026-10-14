using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserScope.Purge
{
    /// <summary>How a purge step treats the rows it matches.</summary>
    public enum UserScopePurgeStepKind
    {
        /// <summary>Delete them.</summary>
        Delete,

        /// <summary>Anonymise them in place (<see cref="UserScopePurgeStep.Set"/>): someone else's record that merely names the person.</summary>
        Update,

        /// <summary>Teams calls: anonymise the people being purged, or delete a call once nobody in scope is left on it.</summary>
        Calls,

        /// <summary>The users themselves - last, and only where nothing still refers to them.</summary>
        Users,
    }

    /// <summary>
    /// One table's share of a purge. Every step walks its table once, in windows of <see cref="Window"/> consecutive
    /// keys of its clustered index, and changes at most <see cref="UserScopePurgePlan.BatchSize"/> rows per statement:
    /// a window bounds how much each statement reads whatever plan the optimiser picks, and the batch cap keeps each
    /// statement under SQL Server's 5,000-lock escalation threshold so the importers are never locked out of a table.
    /// </summary>
    public sealed class UserScopePurgeStep
    {
        internal UserScopePurgeStep(string phase, string table, string keyColumn, UserScopePurgeStepKind kind, string match,
            IReadOnlyList<string> handles, string set = null, int window = UserScopePurgePlan.DefaultWindow)
        {
            Phase = phase;
            Table = table;
            KeyColumn = keyColumn;
            Kind = kind;
            Match = match;
            Handles = handles;
            Set = set;
            Window = window;
        }

        public string Phase { get; }
        public string Table { get; }

        /// <summary>
        /// The leading column of the table's clustered key, which the step walks in order. It need not be unique: a window is
        /// a range of its values, so every row with the window's last value falls in that window.
        /// </summary>
        public string KeyColumn { get; }

        public UserScopePurgeStepKind Kind { get; }

        /// <summary>SQL predicate over the step's rows (alias <c>t</c>) that is true for rows belonging to someone being purged.</summary>
        public string Match { get; }

        /// <summary>For <see cref="UserScopePurgeStepKind.Update"/>: the <c>SET</c> clause that anonymises a matched row.</summary>
        public string Set { get; }

        /// <summary>
        /// The foreign-key references (<c>table.column</c>) this step takes care of, so that the parent row they point at
        /// can be deleted afterwards. Checked against <c>sys.foreign_keys</c> by a test.
        /// </summary>
        public IReadOnlyList<string> Handles { get; }

        /// <summary>Clustered-index keys per window.</summary>
        public int Window { get; }

        /// <summary>
        /// Where this step's rows are counted: the table for a step that deletes, <c>table.column</c> for one that
        /// anonymises - so rows cleared in place are never mistaken for rows removed. The calls step does both, so its
        /// window reports a count per table and column itself and this key only names it in the log.
        /// </summary>
        public string CountKey => Kind == UserScopePurgeStepKind.Update ? Handles[0] : Table;

        public override string ToString() => $"{Kind} {Table}";
    }

    /// <summary>
    /// Everything a purge removes or anonymises, in the order that satisfies the foreign keys: children before parents,
    /// people's own records before the user rows they point at. See the portal's Administration &gt; User scope page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deletes what is about the person: their audit events and everything hanging off them, their web sessions, page
    /// comments and likes, sent-email records, Teams ownership, membership and reactions, usage-report rows, Copilot
    /// interaction statistics, licences, Copilot Studio credits and reclaim exclusions.
    /// </para>
    /// <para>
    /// Anonymises what is someone else's but names them: they become the anonymous "Unknown User" on calls they share
    /// with people in scope (and their call feedback goes), shares made to them lose their recipient, and the people
    /// they managed lose their manager link. That mirrors what the imports store about people outside the scope.
    /// </para>
    /// <para>
    /// Tenant-wide data with no person in it (sites, teams, channels, URLs, the report aggregates) is never touched.
    /// </para>
    /// </remarks>
    public static class UserScopePurgePlan
    {
        /// <summary>Clustered-index keys per window: bounds the rows any one statement reads.</summary>
        public const int DefaultWindow = 20000;

        /// <summary>Rows one statement may change: below the 5,000 locks at which SQL Server escalates to a table lock.</summary>
        public const int BatchSize = 4000;

        /// <summary>Calls are anonymised a window at a time inside a transaction, so their windows are smaller.</summary>
        public const int CallsWindow = 2000;

        /// <summary>
        /// Deleting a user also cascades to any rows the importers added about them since their tables were done, so the
        /// last step takes fewer users per statement.
        /// </summary>
        public const int UsersWindow = 1000;

        /// <summary>The anonymous user the call import records people as when they can't be identified or are outside the scope.</summary>
        public const string UnknownUserName = "Unknown User";

        /// <summary>
        /// The users a running purge removes: a temporary table on the purge's own session
        /// (<see cref="UserScopePurgeSession"/>), never a table in the analytics database.
        /// </summary>
        public const string CandidatesTable = "#purge_candidates";

        /// <summary>The steps, in the order they run.</summary>
        public static IReadOnlyList<UserScopePurgeStep> Steps { get; } = Build();

        /// <summary>The steps plus the first one, working out who to purge.</summary>
        public static int StepCount => Steps.Count + 1;

        /// <summary>The phase a job at <paramref name="stepIndex"/> is in.</summary>
        public static string PhaseOf(int stepIndex)
        {
            if (stepIndex <= 0) return UserScopePurgePhases.Snapshot;
            if (stepIndex > Steps.Count) return UserScopePurgePhases.Done;
            return Steps[stepIndex - 1].Phase;
        }

        // ---- Predicates: "this row belongs to someone being purged" -------------------------------------------

        private static string IsCandidate(string userIdExpression) =>
            $"EXISTS (SELECT 1 FROM {CandidatesTable} c WHERE c.user_id = {userIdExpression})";

        private static string ByUser(string column) => IsCandidate($"t.[{column}]");

        private static string ByAuditEvent(string column) =>
            $"EXISTS (SELECT 1 FROM dbo.audit_events e JOIN {CandidatesTable} c ON c.user_id = e.user_id WHERE e.id = t.[{column}])";

        private static string BySession(string column) =>
            $"EXISTS (SELECT 1 FROM dbo.sessions s JOIN {CandidatesTable} c ON c.user_id = s.user_id WHERE s.id = t.[{column}])";

        private static string ByHit(string column) =>
            $"EXISTS (SELECT 1 FROM dbo.hits h JOIN dbo.sessions s ON s.id = h.session_id JOIN {CandidatesTable} c ON c.user_id = s.user_id WHERE h.id = t.[{column}])";

        private static string BySentEmail(string column) =>
            $"EXISTS (SELECT 1 FROM dbo.sent_emails se JOIN {CandidatesTable} c ON c.user_id = se.user_id WHERE se.id = t.[{column}])";

        private static string ByInteraction(string column) =>
            $"EXISTS (SELECT 1 FROM dbo.copilot_interactions i JOIN {CandidatesTable} c ON c.user_id = i.user_id WHERE i.id = t.[{column}])";

        private static string ByParentComment(string column) =>
            $"EXISTS (SELECT 1 FROM dbo.page_comments p JOIN {CandidatesTable} c ON c.user_id = p.user_id WHERE p.id = t.[{column}])";

        private static UserScopePurgeStep Delete(string phase, string table, string key, string match, params string[] handledColumns)
            => new UserScopePurgeStep(phase, table, key, UserScopePurgeStepKind.Delete, match, handledColumns.Select(c => $"{table}.{c}").ToList());

        private static UserScopePurgeStep Anonymise(string phase, string table, string key, string column, string match)
            => new UserScopePurgeStep(phase, table, key, UserScopePurgeStepKind.Update, match, new[] { $"{table}.{column}" }, set: $"[{column}] = NULL");

        private static IReadOnlyList<UserScopePurgeStep> Build()
        {
            const string audit = UserScopePurgePhases.AuditEvents;
            const string web = UserScopePurgePhases.WebActivity;
            const string usage = UserScopePurgePhases.UsageReports;

            var steps = new List<UserScopePurgeStep>
            {
                // Audit events and everything hanging off them. copilot_chats (and the tables below it) are keyed by the
                // audit event they describe, so the event's user decides - authoritative even where the denormalised
                // copilot_chats.user_id was never back-filled.
                Delete(audit, "copilot_event_files", "copilot_chat_id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                Delete(audit, "copilot_event_meetings", "copilot_chat_id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                // A tool execution is its own audit event, and may also point at the chat it ran in: whichever belongs
                // to someone being purged, the row has to go before that parent does.
                Delete(audit, "copilot_tool_executions", "id",
                    $"({ByAuditEvent("event_id")} OR (t.[copilot_chat_id] IS NOT NULL AND {ByAuditEvent("copilot_chat_id")}))",
                    "event_id", "copilot_chat_id"),
                Delete(audit, "copilot_event_accessed_resources", "id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                Delete(audit, "copilot_event_messages", "id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                Delete(audit, "copilot_event_contexts", "id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                Delete(audit, "copilot_event_ai_models", "id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                Delete(audit, "copilot_event_ai_system_plugins", "id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                Delete(audit, "copilot_dlp_events", "id", ByAuditEvent("copilot_chat_id"), "copilot_chat_id"),
                // An extra audit record of a Copilot turn (#699). Its foreign key cascades, but like the tables
                // above it is cleared first, so the copilot_chats delete never cascades into it.
                Delete(audit, "copilot_chat_duplicates", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "copilot_chats", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "dlp_rule_matches", "id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_power_app_share", "id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_power_automate_flow_share", "id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_azure_ad", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_copilot_studio", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_exchange", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_general", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_power_app", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_power_automate_flow", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_power_bi", "event_id", ByAuditEvent("event_id"), "event_id"),
                Delete(audit, "event_meta_sharepoint", "event_id", ByAuditEvent("event_id"), "event_id"),
                // audit_events.user_id has no foreign key, so nothing else would ever stop these being orphaned.
                Delete(audit, "audit_events", "id", ByUser("user_id"), "user_id"),

                // Web traffic: clicked elements hang off hits, hits and searches off sessions.
                Delete(web, "hits_clicked_elements", "id", ByHit("hit_id"), "hit_id"),
                Delete(web, "hits", "id", BySession("session_id"), "session_id"),
                Delete(web, "searches", "id", BySession("session_id"), "session_id"),
                Delete(web, "sessions", "id", ByUser("user_id"), "user_id"),

                // Calls: shared records, so anonymised - or deleted once nobody in scope is left on them. Then any feedback
                // still attributed to someone being purged.
                new UserScopePurgeStep(UserScopePurgePhases.Calls, "call_records", "id", UserScopePurgeStepKind.Calls, match: null,
                    handles: new[]
                    {
                        "call_records.organizer_id", "call_sessions.attendee_user_id", "call_sessions.call_record_id",
                        "call_session_call_modalities.call_session_id", "call_failures.call_id", "call_feedback.call_id",
                    },
                    window: CallsWindow),
                Delete(UserScopePurgePhases.Calls, "call_feedback", "id", ByUser("user_id"), "user_id"),

                // Page comments: replies by other people stay, no longer linked to the removed comment.
                Anonymise(UserScopePurgePhases.PageComments, "page_comments", "id", "parent_id", ByParentComment("parent_id")),
                Delete(UserScopePurgePhases.PageComments, "page_comments", "id", ByUser("user_id"), "user_id"),
                Delete(UserScopePurgePhases.PageComments, "page_likes", "id", ByUser("user_id"), "user_id"),

                // Sent email: the recipients hang off the email. Recipient addresses are the sender's activity.
                Delete(UserScopePurgePhases.SentEmails, "sent_email_recipients", "id", BySentEmail("sent_email_id"), "sent_email_id"),
                Delete(UserScopePurgePhases.SentEmails, "sent_emails", "id", ByUser("user_id"), "user_id"),

                Delete(UserScopePurgePhases.Teams, "team_owners", "id", ByUser("owner_id"), "owner_id"),
                Delete(UserScopePurgePhases.Teams, "team_membership_log", "id", ByUser("user_id"), "user_id"),
                Delete(UserScopePurgePhases.Teams, "teams_user_channel_reactions", "id", ByUser("user_id"), "user_id"),

                Delete(usage, "teams_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "teams_user_device_usage_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "onedrive_usage_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "onedrive_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "outlook_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "sharepoint_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "platform_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "yammer_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "yammer_device_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "copilot_usage_user_activity_log", "id", ByUser("user_id"), "user_id"),
                Delete(usage, "cowork_usage_user_activity_log", "id", ByUser("user_id"), "user_id"),

                Delete(UserScopePurgePhases.CopilotInteractions, "copilot_interaction_keywords", "id", ByInteraction("interaction_id"), "interaction_id"),
                Delete(UserScopePurgePhases.CopilotInteractions, "copilot_prompt_classifications", "interaction_id", ByInteraction("interaction_id"), "interaction_id"),
                Delete(UserScopePurgePhases.CopilotInteractions, "copilot_interactions", "id", ByUser("user_id"), "user_id", "session_id"),
                Delete(UserScopePurgePhases.CopilotInteractions, "copilot_interaction_sessions", "id", ByUser("user_id"), "user_id"),
                Delete(UserScopePurgePhases.CopilotInteractions, "copilot_interaction_user_watermarks", "id", ByUser("user_id"), "user_id"),

                Delete(UserScopePurgePhases.LicencesAndCredits, "user_license_history", "id", ByUser("user_id"), "user_id"),
                Delete(UserScopePurgePhases.LicencesAndCredits, "user_license_type_lookups", "id", ByUser("user_id"), "user_id"),
                Delete(UserScopePurgePhases.LicencesAndCredits, "copilot_studio_credit_user_daily", "id", ByUser("user_id"), "user_id"),
                Delete(UserScopePurgePhases.LicencesAndCredits, "copilot_adoption_reclaim_exclusions", "id", ByUser("user_id"), "user_id"),

                // Someone in scope shared an app or flow with the person: the share is theirs, so it stays, without its recipient.
                Anonymise(UserScopePurgePhases.SharedWith, "event_meta_power_app_share", "id", "shared_with_user_id", ByUser("shared_with_user_id")),
                Anonymise(UserScopePurgePhases.SharedWith, "event_meta_power_automate_flow_share", "id", "shared_with_user_id", ByUser("shared_with_user_id")),

                // The people they managed keep their record, without a manager.
                Anonymise(UserScopePurgePhases.Managers, "users", "id", "manager_id", ByUser("manager_id")),

                // Their user-organisation values: at most one per organisation type. The foreign key cascades from users, but
                // like every reference to a user it is taken care of here, so the user delete never cascades.
                Delete(UserScopePurgePhases.Users, "user_org_assignments", "user_id", ByUser("user_id"), "user_id"),

                new UserScopePurgeStep(UserScopePurgePhases.Users, "users", "id", UserScopePurgeStepKind.Users, ByUser("id"),
                    handles: Array.Empty<string>(), window: UsersWindow),
            };

            return steps;
        }
    }
}
