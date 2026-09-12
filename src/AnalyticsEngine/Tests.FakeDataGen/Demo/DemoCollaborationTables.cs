namespace Tests.FakeDataGen.Demo
{
    internal static partial class DemoTables
    {
        // Mappings follow the production entities and their explicit migrations, not DbSet names.
        public static readonly DemoTable CallTypes = Named("call_types");
        public static readonly DemoTable CallModalities = Named("call_modalities");
        public static readonly DemoTable CallRecords = T("call_records", true, I("id"), I("organizer_id"),
            I("call_type_id"), N("graph_id"), D("start"), D("end"));
        public static readonly DemoTable CallSessions = T("call_sessions", true, I("id"), I("attendee_user_id"),
            D("start"), D("end"), I("call_record_id"));
        public static readonly DemoTable CallSessionModalities = T("call_session_call_modalities", false,
            I("call_modality_id"), I("call_session_id"));
        public static readonly DemoTable CallFeedback = T("call_feedback", false, N("rating", -1), N("text", -1),
            I("user_id"), I("call_id"));
        public static readonly DemoTable CallFailures = T("call_failures", false, N("reason", -1), N("stage", -1), I("call_id"));

        public static readonly DemoTable TeamDefinitions = T("teams", true, I("id"), D("discovered"),
            B("has_refresh_token"), D("last_refresh"), N("graph_id"), N("name"));
        public static readonly DemoTable TeamChannels = T("teams_channels", true,
            I("id"), N("graph_id"), N("name"), I("team_id"));
        public static readonly DemoTable TeamMemberships = T("team_membership_log", false, I("user_id"), D("date"), I("team_id"));
        public static readonly DemoTable TeamOwners = T("team_owners", false, I("owner_id"), D("discovered"), I("team_id"));
        public static readonly DemoTable TeamTabs = T("teams_tabs", true, I("id"), N("url", -1), N("graph_id"), N("name"));
        public static readonly DemoTable TeamTabLogs = T("teams_channel_tabs_log", false, I("tab_id"), D("date"), I("channel_id"));
        public static readonly DemoTable ChannelStats = T("teams_channel_stats_log", true, I("id"),
            I("chats_count"), F("sentiment_score"), D("date"), I("channel_id"));
        public static readonly DemoTable ChannelKeywords = T("teams_channel_stats_log_keywords", false,
            I("keyword_id"), I("channel_stats_log_id"), I("keyword_count"));
        public static readonly DemoTable ChannelLanguages = T("teams_channel_stats_log_langs", false,
            I("language_id"), I("channel_stats_log_id"));
        public static readonly DemoTable ReactionTypes = Named("teams_reaction_types");
        public static readonly DemoTable ChannelReactions = T("teams_user_channel_reactions", false,
            I("reaction_id"), I("user_id"), I("channel_id"), D("date"));

        public static readonly DemoTable EmailAddresses = T("email_addresses", true, I("id"), N("address", 450));
        public static readonly DemoTable SentEmails = T("sent_emails", true, I("id"), N("subject", 1000),
            D("sent_date"), N("graph_message_id", 450), F("cognitive_score"), I("from_address_id"), I("user_id"));
        public static readonly DemoTable EmailRecipients = T("sent_email_recipients", false,
            I("sent_email_id"), I("recipient_address_id"));

        public static readonly DemoTable PageFields = Named("file_field_definitions");
        public static readonly DemoTable PageMetadata = T("file_metadata_property_values", false,
            I("url_id"), I("field_id"), N("field_value", -1), G("tag_guid"), D("updated"));
        public static readonly DemoTable PageComments = T("page_comments", true, I("id"), N("comment", -1),
            I("language_id"), F("sentiment_score"), I("parent_id"), I("user_id"), I("url_id"), D("created"), I("sp_id"));
        public static readonly DemoTable PageLikes = T("page_likes", false,
            I("user_id"), I("url_id"), D("created"), I("sp_id"));

        public static readonly DemoTable ClickTitles = Named("hits_clicked_element_titles");
        public static readonly DemoTable ClickClasses = T("hits_clicked_element_class_names", true,
            I("id"), N("class_names", 2000));
        public static readonly DemoTable Clicks = T("hits_clicked_elements", false, I("url_id"), I("element_title_id"),
            I("class_names_id"), I("hit_id"), D("timestamp"));
        public static readonly DemoTable SearchTerms = T("search_terms", true, I("id"), N("search_term", 250));
        public static readonly DemoTable Searches = T("searches", false, I("session_id"), I("search_term_id"), D("date_time"));

        public static readonly DemoTable OneDriveStorage = Daily("onedrive_usage_activity_log",
            L("storage_used_bytes"), L("active_file_count"), L("file_count"));
        public static readonly DemoTable EngageGroups = Named("yammer_groups");
        public static readonly DemoTable EngageGroupActivity = T("yammer_group_activity_log", false,
            I("posted_count"), I("read_count"), I("liked_count"), I("member_count"),
            I("yammer_group_id"), D("date"), D("last_activity_date"));
    }
}
