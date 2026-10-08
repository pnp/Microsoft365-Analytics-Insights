using Common.Entities.Config;
using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Common.Entities.PromptCategories
{
    public static class FoundryPromptSettings
    {
        public static bool IsConfigured(AppConfig settings) => settings != null &&
            IsValidEndpoint(settings.FoundryPromptEndpoint) &&
            Regex.IsMatch(settings.FoundryPromptDeployment ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z");

        public static bool IsValidEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/") return false;
            foreach (var suffix in new[] { ".openai.azure.com", ".cognitiveservices.azure.com" })
            {
                if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(uri.Host.Substring(0, uri.Host.Length - suffix.Length), @"\A[a-zA-Z0-9][a-zA-Z0-9-]{1,62}\z"))
                    return true;
            }
            return false;
        }
    }

    public sealed class PromptCategoryDefinition
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("humanMode")] public string HumanMode { get; set; }
        [JsonProperty("nameKey")] public string NameKey { get; set; }
        [JsonProperty("descriptionKey")] public string DescriptionKey { get; set; }
    }

    public sealed class PromptCategoryConfiguration
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("maxPromptsPerCycle")] public int MaxPromptsPerCycle { get; set; } = 1000;
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("categories")] public List<PromptCategoryDefinition> Categories { get; set; }

        public static PromptCategoryConfiguration Defaults() => new PromptCategoryConfiguration
        {
            Categories = new List<PromptCategoryDefinition>
            {
                Category("meeting-summary", "Meeting summaries", "Summarise a meeting or extract its actions."),
                Category("document-editing", "Document editing", "Revise, proofread or improve existing text."),
                Category("information-lookup", "Information lookup", "Find information or answer a factual question."),
                Category("content-drafting", "Content drafting", "Create new text, presentations or communications."),
                Category("analysis", "Analysis", "Analyse information, reason or compare options."),
                Category("other", "Other", "A goal that does not match another category.")
            }
        };

        private static PromptCategoryDefinition Category(string id, string name, string description) =>
            new PromptCategoryDefinition
            {
                Id = id, Name = name, Description = description,
                NameKey = "promptCategories.defaults." + id + ".name",
                DescriptionKey = "promptCategories.defaults." + id + ".description"
            };

        public void ValidateAndVersion()
        {
            if (MaxPromptsPerCycle < 1 || MaxPromptsPerCycle > 10000 || Categories == null ||
                Categories.Count < 2 || Categories.Count > 20 ||
                Categories.Count(c => c?.Id == "other") != 1)
                throw new ArgumentException("Invalid prompt taxonomy.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in Categories)
            {
                if (category == null || category.Id == null ||
                    !Regex.IsMatch(category.Id, @"\A[a-z][a-z0-9-]{0,39}\z") ||
                    category.Id == "not-classified" || !ids.Add(category.Id) ||
                    (category.Id == "other" && category.Name != "Other") ||
                    string.IsNullOrWhiteSpace(category.Name) || category.Name.Length > 100 ||
                    string.IsNullOrWhiteSpace(category.Description) || category.Description.Length > 500 ||
                    (category.HumanMode != null && category.HumanMode != "directing" && category.HumanMode != "supervising"))
                    throw new ArgumentException("Invalid prompt category.");
                var preset = Defaults().Categories.FirstOrDefault(c => c.Id == category.Id);
                if (category.Id == "other") category.NameKey = preset.NameKey;
                if (category.NameKey != null && (preset == null || category.Name != preset.Name || category.NameKey != preset.NameKey))
                    category.NameKey = null;
                if (category.DescriptionKey != null && (preset == null || category.Description != preset.Description || category.DescriptionKey != preset.DescriptionKey))
                    category.DescriptionKey = null;
            }

            // Content-addressing makes taxonomy labels immutable even across simultaneous saves on different hosts.
            // Opt-in and the spend cap are operational settings, not a different taxonomy.
            var canonical = JsonConvert.SerializeObject(Categories.OrderBy(c => c.Id, StringComparer.Ordinal));
            using (var hash = SHA256.Create())
                Version = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", "").ToLowerInvariant();
        }
    }

    public sealed class PromptCategoryConfigurationStore
    {
        private static readonly InMemoryKeyValueStore Fallback = new InMemoryKeyValueStore();
        private readonly IKeyValueStore _store;
        public bool IsDurable { get; }

        public PromptCategoryConfigurationStore(IKeyValueStore store, bool durable = true)
        {
            _store = store ?? Fallback;
            IsDurable = store != null && durable;
        }

        public static PromptCategoryConfigurationStore Open(AppConfig settings) =>
            new PromptCategoryConfigurationStore(StateStore.TryOpen(settings, StatePartitions.PromptCategories));

        public async Task<PromptCategoryConfiguration> GetAsync()
        {
            var raw = await _store.GetStringAsync("current");
            var config = raw == null ? PromptCategoryConfiguration.Defaults() :
                JsonConvert.DeserializeObject<PromptCategoryConfiguration>(raw);
            config.ValidateAndVersion();
            // A memory-only configuration must never authorise a new external prompt data flow.
            if (!IsDurable) config.Enabled = false;
            return config;
        }

        public async Task<PromptCategoryConfiguration> SaveAsync(PromptCategoryConfiguration config)
        {
            if (!IsDurable) throw new InvalidOperationException("Durable storage is required.");
            config.ValidateAndVersion();
            var serialized = JsonConvert.SerializeObject(config);
            await _store.SetStringAsync("taxonomy-" + config.Version, serialized);
            await _store.SetStringAsync("current", serialized);
            return config;
        }
    }
}
