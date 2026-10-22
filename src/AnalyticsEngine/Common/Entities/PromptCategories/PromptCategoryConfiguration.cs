using Common.Entities.Config;
using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.PromptCategories
{
    public static class FoundryPromptSettings
    {
        public static bool IsConfigured(AppConfig settings) => settings != null &&
            IsValidEndpoint(settings.FoundryPromptCategorisationEndpoint) &&
            Regex.IsMatch(settings.FoundryPromptCategorisationDeployment ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z");

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

        /// <summary>
        /// Which saved document this is: a hash of the whole stored document - opt-in, spend cap and taxonomy - so it
        /// changes whenever any of them does, unlike <see cref="Version"/>, which identifies the taxonomy alone. The
        /// portal sends it back with a save as the revision it edited. Never stored: it is worked out from what is, so a
        /// document saved before revisions existed has one too.
        /// </summary>
        [JsonProperty("revision", NullValueHandling = NullValueHandling.Ignore)] public string Revision { get; set; }

        public static PromptCategoryConfiguration Defaults() => new PromptCategoryConfiguration
        {
            Categories = new List<PromptCategoryDefinition>
            {
                Category("meeting-summary", "Meeting summaries", "Summarise a meeting or extract its actions.", "directing"),
                Category("document-editing", "Document editing", "Revise, proofread or improve existing text.", "directing"),
                Category("information-lookup", "Information lookup", "Find information or answer a factual question.", "directing"),
                Category("content-drafting", "Content drafting", "Create new text, presentations or communications.", "directing"),
                Category("analysis", "Analysis", "Analyse information, reason or compare options.", "directing"),
                Category("delegated-research", "Delegated research", "Delegate a multi-step investigation with autonomy to gather evidence, compare options and recommend a plan for human review.", "supervising"),
                Category("delegated-problem-solving", "Delegated problem solving", "Delegate diagnosing a problem, implementing and checking a solution, and returning results for human approval.", "supervising"),
                Category("other", "Other", "A goal that does not match another category.")
            }
        };

        private static PromptCategoryDefinition Category(string id, string name, string description, string humanMode = null) =>
            new PromptCategoryDefinition
            {
                Id = id, Name = name, Description = description, HumanMode = humanMode,
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

    /// <summary>The saved configuration exists but cannot be read or is no longer a valid taxonomy.</summary>
    public sealed class PromptCategoryStoredConfigurationException : Exception
    {
        public PromptCategoryStoredConfigurationException(Exception inner) : base("The saved prompt category configuration is invalid.", inner) { }
    }

    /// <summary>
    /// A save or reset refused because the stored configuration is no longer the revision it was made against: another
    /// administrator saved in between, or the request named no revision at all.
    /// </summary>
    public sealed class PromptCategoryConfigurationConflictException : Exception
    {
        public PromptCategoryConfigurationConflictException(PromptCategoryConfiguration current)
            : base("The prompt category configuration changed since it was read.")
        {
            Current = current;
        }

        /// <summary>The configuration in force now, with its revision; null if it could not be read back.</summary>
        public PromptCategoryConfiguration Current { get; }
    }

    /// <summary>
    /// The prompt categorisation settings: one document under <c>current</c> in the <see cref="StatePartitions.PromptCategories"/>
    /// partition, read by the importer every cycle and edited from the portal.
    /// </summary>
    /// <remarks>
    /// Saves and resets carry the <see cref="PromptCategoryConfiguration.Revision"/> the administrator edited and are refused
    /// with <see cref="PromptCategoryConfigurationConflictException"/> when the document has changed since. The write itself
    /// is conditional on the row's ETag as it was read (<see cref="IConditionalKeyValueStore"/>), so of two saves made against
    /// the same revision exactly one lands, whichever web instance handles them. Without that, a tab left open could
    /// silently switch categorisation - and with it the sending of users' prompts to Azure OpenAI - back on, or undo a
    /// spend cap, after another administrator had changed it.
    /// </remarks>
    public sealed class PromptCategoryConfigurationStore
    {
        private const string CurrentKey = "current";
        private static readonly InMemoryKeyValueStore Fallback = new InMemoryKeyValueStore();
        private readonly IKeyValueStore _store;
        private readonly IConditionalKeyValueStore _conditional;
        public bool IsDurable { get; }

        public PromptCategoryConfigurationStore(IKeyValueStore store, bool durable = true)
        {
            _store = store ?? Fallback;
            _conditional = _store as IConditionalKeyValueStore;
            IsDurable = store != null && durable;
        }

        public static PromptCategoryConfigurationStore Open(AppConfig settings) =>
            new PromptCategoryConfigurationStore(StateStore.TryOpen(settings, StatePartitions.PromptCategories));

        public async Task<PromptCategoryConfiguration> GetAsync(CancellationToken cancellationToken = default)
        {
            var config = Parse(await _store.GetStringAsync(CurrentKey, cancellationToken));
            // A memory-only configuration must never authorise a new external prompt data flow.
            if (!IsDurable) config.Enabled = false;
            return config;
        }

        /// <summary>Saves <paramref name="config"/> if the stored document is still <paramref name="expectedRevision"/>.</summary>
        /// <exception cref="PromptCategoryConfigurationConflictException">It is not, or no revision was given.</exception>
        /// <exception cref="PromptCategoryStoredConfigurationException">The stored document can't be read: only a reset replaces it.</exception>
        public Task<PromptCategoryConfiguration> SaveAsync(PromptCategoryConfiguration config, string expectedRevision, CancellationToken cancellationToken = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return WriteAsync(config, expectedRevision, false, cancellationToken);
        }

        /// <summary>
        /// Replaces the configuration with the defaults, categorisation off, if the stored document is still
        /// <paramref name="expectedRevision"/>. A stored document that can't be read is replaced whatever the revision:
        /// nobody can have edited it, and the defaults never send anything.
        /// </summary>
        /// <exception cref="PromptCategoryConfigurationConflictException">The document changed since, or no revision was given.</exception>
        public Task<PromptCategoryConfiguration> ResetAsync(string expectedRevision, CancellationToken cancellationToken = default)
            => WriteAsync(PromptCategoryConfiguration.Defaults(), expectedRevision, true, cancellationToken);

        private async Task<PromptCategoryConfiguration> WriteAsync(PromptCategoryConfiguration config, string expectedRevision,
            bool replaceUnreadable, CancellationToken cancellationToken)
        {
            if (!IsDurable) throw new InvalidOperationException("Durable storage is required.");
            if (_conditional == null) throw new InvalidOperationException("Saving needs a store with conditional writes.");
            config.ValidateAndVersion();
            var serialized = Serialize(config);

            var stored = await _conditional.GetVersionedAsync(CurrentKey, cancellationToken);
            PromptCategoryConfiguration current = null;
            try { current = Parse(stored.Value); }
            catch (PromptCategoryStoredConfigurationException) when (replaceUnreadable) { }
            if (current != null && !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
                throw new PromptCategoryConfigurationConflictException(current);

            // Content-addressed history: written once per taxonomy version and never replaced, so a later save of the same
            // categories with a different opt-in or cap does not rewrite it.
            await _conditional.TrySetStringAsync("taxonomy-" + config.Version, serialized, null, cancellationToken);
            if (!await _conditional.TrySetStringAsync(CurrentKey, serialized, stored.VersionToken, cancellationToken))
                throw new PromptCategoryConfigurationConflictException(await TryGetAsync(cancellationToken));

            config.Revision = RevisionOf(serialized);
            return config;
        }

        /// <summary>The configuration in force, for a conflict's answer; null rather than a second failure.</summary>
        private async Task<PromptCategoryConfiguration> TryGetAsync(CancellationToken cancellationToken)
        {
            try { return Parse(await _store.GetStringAsync(CurrentKey, cancellationToken)); }
            catch (Exception) { return null; }
        }

        /// <summary>The document as stored: everything but the revision, which is worked out from it.</summary>
        private static string Serialize(PromptCategoryConfiguration config)
        {
            var revision = config.Revision;
            config.Revision = null;
            try { return JsonConvert.SerializeObject(config); }
            finally { config.Revision = revision; }
        }

        private static PromptCategoryConfiguration Parse(string raw)
        {
            PromptCategoryConfiguration config;
            try
            {
                config = raw == null ? PromptCategoryConfiguration.Defaults() :
                    JsonConvert.DeserializeObject<PromptCategoryConfiguration>(raw);
                config.ValidateAndVersion();
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is NullReferenceException)
            {
                throw new PromptCategoryStoredConfigurationException(ex);
            }
            config.Revision = RevisionOf(raw);
            return config;
        }

        /// <summary>A hash of the stored document exactly as it is stored; of the empty string while nothing is.</summary>
        internal static string RevisionOf(string raw)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(raw ?? string.Empty)))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
