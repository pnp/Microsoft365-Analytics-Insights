namespace Common.Entities.Copilot
{
    /// <summary>
    /// SQL fragments that make a report count each Copilot turn once, and keep maker testing out of agent
    /// figures (issue #699). Every report that counts Copilot interactions, or reads agent figures, builds its
    /// SQL from these, so the rules live in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One turn, one interaction.</b> The importer keeps every audit record as a <c>copilot_chats</c> row, and
    /// lists in <c>dbo.copilot_chat_duplicates</c> each record that is an extra record of a turn counted on
    /// another one: the Copilot Studio runtime's twin of a Microsoft 365 Copilot record, and an extra runtime
    /// record logged a few seconds after the first (see <c>common_upsert_copilot_agents.sql</c>).
    /// <see cref="CountedTurn"/> leaves those out. It is a NOT EXISTS on the table's primary key, keyed on
    /// <c>event_id</c>, which is the clustering key of <c>copilot_chats</c> and so is carried by every one of
    /// its indexes: a query that is covered by <c>IX_copilot_chats_time_stamp_user_id</c> stays covered.
    /// </para>
    /// <para>
    /// <b>Maker testing.</b> A chat in the Copilot Studio test pane is logged like any other turn, with
    /// <c>app_host</c> <see cref="MakerTestAppHost"/>. It still counts as Copilot use, but
    /// <see cref="NotMakerTesting"/> keeps it out of agent adoption figures (agent users, verdicts, apps used,
    /// top agents, agents used per person), licence activity and the "by app" charts, as Copilot Studio's
    /// own Monitor page leaves test-panel activity out of its analytics. <c>app_host</c> is INCLUDEd in
    /// <c>IX_copilot_chats_time_stamp_user_id</c>, so this is a residual predicate on rows already being read.
    /// </para>
    /// </remarks>
    public static class CopilotTurnSql
    {
        /// <summary>
        /// The <c>app_host</c> of a chat in the Copilot Studio test pane, where an agent's maker tries it out.
        /// </summary>
        public const string MakerTestAppHost = "Copilot Studio";

        /// <summary>
        /// The table listing every audit record that is an extra record of a turn counted on another record.
        /// </summary>
        public const string DuplicatesTable = "dbo.copilot_chat_duplicates";

        /// <summary>
        /// True when the <c>copilot_chats</c> row aliased <paramref name="chatAlias"/> counts as a turn: it is not
        /// an extra record of a turn counted on another row.
        /// </summary>
        public static string CountedTurn(string chatAlias)
        {
            return $"NOT EXISTS (SELECT 1 FROM {DuplicatesTable} AS turn_dup WHERE turn_dup.event_id = {chatAlias}.event_id)";
        }

        /// <summary>
        /// True unless <paramref name="appHostColumn"/> says the interaction was maker testing in the Copilot
        /// Studio test pane. NULL-safe: an interaction with no host is not maker testing. Compares the value
        /// it is given, so pass the raw column, or the same key a grain table was grouped on.
        /// </summary>
        public static string NotMakerTesting(string appHostColumn)
        {
            return $"({appHostColumn} IS NULL OR {appHostColumn} <> N'{MakerTestAppHost}')";
        }

        /// <summary>
        /// The join that <see cref="ResourceCountedOnce"/> reads: the accessed-resource row's record, when that
        /// record is an extra record of a turn. A small table, so it is hashed once rather than probed per row.
        /// </summary>
        public static string ResourceTurnJoin(string resourceAlias, string duplicateAlias)
        {
            return $"LEFT JOIN {DuplicatesTable} AS {duplicateAlias} ON {duplicateAlias}.event_id = {resourceAlias}.copilot_chat_id";
        }

        /// <summary>
        /// True unless the accessed-resource row <paramref name="resourceAlias"/> is on an extra record of a turn
        /// (joined by <see cref="ResourceTurnJoin"/> as <paramref name="duplicateAlias"/>) and the turn already
        /// has the same resource - the same id, name, site, type, label, action and list item - on a record
        /// that comes first: the record the turn is counted on, then the turn's other extra records in
        /// <c>event_id</c> order. Both records of a Microsoft 365 Copilot pair can list the same web-search
        /// result, and it is one access, so it counts once; a resource only one record lists is counted.
        /// </summary>
        /// <remarks>
        /// Only rows on an extra record reach the NOT EXISTS (the <c>IS NULL</c> branch short-circuits every
        /// other row), and it seeks <c>IX_copilot_event_accessed_resources_dedup</c> on the full tuple.
        /// </remarks>
        public static string ResourceCountedOnce(string resourceAlias, string duplicateAlias)
        {
            var r = resourceAlias;
            var d = duplicateAlias;
            return
                $"({d}.event_id IS NULL OR NOT EXISTS (\r\n" +
                "      SELECT 1\r\n" +
                "      FROM (SELECT " + d + ".counted_event_id AS event_id\r\n" +
                "            UNION ALL\r\n" +
                $"            SELECT earlier.event_id FROM {DuplicatesTable} AS earlier\r\n" +
                $"            WHERE earlier.counted_event_id = {d}.counted_event_id AND earlier.event_id < {d}.event_id) AS turn_record\r\n" +
                "      JOIN dbo.copilot_event_accessed_resources AS turn_resource ON turn_resource.copilot_chat_id = turn_record.event_id\r\n" +
                "      WHERE EXISTS (SELECT turn_resource.resource_id_id, turn_resource.resource_name_id, turn_resource.resource_site_url_id,\r\n" +
                "                           turn_resource.resource_type_id, turn_resource.sensitivity_label_id, turn_resource.action_id,\r\n" +
                "                           turn_resource.list_item_unique_id_id\r\n" +
                "                    INTERSECT\r\n" +
                $"                    SELECT {r}.resource_id_id, {r}.resource_name_id, {r}.resource_site_url_id,\r\n" +
                $"                           {r}.resource_type_id, {r}.sensitivity_label_id, {r}.action_id,\r\n" +
                $"                           {r}.list_item_unique_id_id)))";
        }
    }
}
