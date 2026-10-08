namespace Common.Entities.PromptCategories
{
    /// <summary>Aggregate-only queries; privacy thresholds apply after the reader's global scope, to distinct people.</summary>
    public static class PromptCategoryReportSql
    {
        public const string Versions = @"SELECT c.taxonomy_version AS Version FROM dbo.copilot_prompt_classifications c
            JOIN dbo.copilot_interactions i ON i.id=c.interaction_id
            WHERE i.created_utc>=@from /*scope: AND i.user_id IN {scopeUsers}*/
            GROUP BY c.taxonomy_version HAVING COUNT(DISTINCT i.user_id)>=10
            ORDER BY MAX(i.created_utc) DESC";

        public const string Mix = @"SELECT c.category_id AS CategoryId, COUNT_BIG(*) AS Prompts
            FROM dbo.copilot_prompt_classifications c JOIN dbo.copilot_interactions i ON i.id=c.interaction_id
            WHERE i.created_utc>=@from AND c.taxonomy_version=@version /*scope: AND i.user_id IN {scopeUsers}*/
            GROUP BY c.category_id HAVING COUNT(DISTINCT i.user_id)>=10";

        public const string Trend = @"SELECT c.category_id AS CategoryId,
            DATEADD(day, -(DATEDIFF(day, '19000101', i.created_utc)%7), CAST(i.created_utc AS date)) AS WeekStart,
            COUNT_BIG(*) AS Prompts
            FROM dbo.copilot_prompt_classifications c JOIN dbo.copilot_interactions i ON i.id=c.interaction_id
            WHERE i.created_utc>=@from AND c.taxonomy_version=@version /*scope: AND i.user_id IN {scopeUsers}*/
            GROUP BY c.category_id, DATEADD(day, -(DATEDIFF(day, '19000101', i.created_utc)%7), CAST(i.created_utc AS date))
            HAVING COUNT(DISTINCT i.user_id)>=10";
    }
}
