using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.PromptCategories
{
    public sealed class PromptCategoryFact
    {
        public int InteractionId { get; set; }
        public string CategoryId { get; set; }
        public string TaxonomyVersion { get; set; }
        public string HumanMode { get; set; }
    }

    public sealed class PromptCategoryRun
    {
        [JsonProperty("sent")] public int Sent { get; set; }
        [JsonProperty("httpAttempts")] public int HttpAttempts { get; set; }
        [JsonProperty("classified")] public int Classified { get; set; }
        [JsonProperty("other")] public int Other { get; set; }
        [JsonProperty("notClassified")] public int NotClassified { get; set; }
        [JsonProperty("capped")] public int Capped { get; set; }
        [JsonProperty("failed")] public int Failed { get; set; }
        [JsonProperty("inputTokens")] public int InputTokens { get; set; }
        [JsonProperty("outputTokens")] public int OutputTokens { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; } = "disabled";
        [JsonProperty("taxonomyVersion")] public string TaxonomyVersion { get; set; }
    }

    /// <summary>Raw additive tables deliberately outside the EF model; no customer message content enters these APIs.</summary>
    public static class PromptCategorySql
    {
        public static async Task SaveFactsAsync(AnalyticsEntitiesContext db,
            PromptCategoryConfiguration taxonomy, IReadOnlyList<PromptCategoryFact> facts)
        {
            if (taxonomy == null || facts.Count == 0) return;
            var allowed = new HashSet<string>(taxonomy.Categories.Select(c => c.Id), StringComparer.Ordinal);
            foreach (var fact in facts)
                if (fact.TaxonomyVersion != taxonomy.Version ||
                    (!allowed.Contains(fact.CategoryId) && fact.CategoryId != "not-classified"))
                    throw new ArgumentException("Invalid classification fact.");

            await db.Database.ExecuteSqlCommandAsync(
                @"IF NOT EXISTS (SELECT 1 FROM dbo.copilot_prompt_taxonomies WITH (UPDLOCK,HOLDLOCK) WHERE version = @version)
                  INSERT dbo.copilot_prompt_taxonomies(version, categories_json) VALUES (@version, @categories);",
                new SqlParameter("@version", SqlDbType.NVarChar, 64) { Value = taxonomy.Version },
                new SqlParameter("@categories", SqlDbType.NVarChar, -1) { Value = JsonConvert.SerializeObject(taxonomy.Categories) });

            // One parameter per chunk, not one statement or EF Add per prompt.
            for (var i = 0; i < facts.Count; i += 500)
            {
                var batch = new List<PromptCategoryFact>(Math.Min(500, facts.Count - i));
                for (var j = i; j < Math.Min(i + 500, facts.Count); j++) batch.Add(facts[j]);
                await db.Database.ExecuteSqlCommandAsync(
                    @"INSERT dbo.copilot_prompt_classifications(interaction_id, category_id, taxonomy_version, human_mode)
                      SELECT f.InteractionId, f.CategoryId, f.TaxonomyVersion, f.HumanMode
                      FROM OPENJSON(@facts) WITH
                      (InteractionId int, CategoryId nvarchar(40), TaxonomyVersion nvarchar(64), HumanMode nvarchar(20)) f
                      WHERE NOT EXISTS (SELECT 1 FROM dbo.copilot_prompt_classifications c WHERE c.interaction_id=f.InteractionId);",
                    new SqlParameter("@facts", SqlDbType.NVarChar, -1) { Value = JsonConvert.SerializeObject(batch) });
            }
        }
    }
}
