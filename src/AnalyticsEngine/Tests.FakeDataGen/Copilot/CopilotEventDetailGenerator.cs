using Common.Entities;
using Common.Entities.Entities.AuditLog;
using System;
using System.Linq;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Fills the Copilot audit detail tables that the importer parses out of a single audit record but that
    /// live alongside <c>copilot_chats</c>: messages, interaction contexts, AI models and AI system plugins.
    /// </summary>
    /// <remarks>
    /// These tables were added when the importer stopped discarding fields it had already parsed. Generating
    /// activity without them leaves every one of them empty, so any report built over them looks correct
    /// against an empty set and breaks on real data.
    /// <para>
    /// Two details are modelled rather than randomised, because they are what the schema exists to express:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Both messages.</b> An interaction produces a prompt row AND a response row.
    /// <c>is_prompt</c> would be a constant if only responses were generated, and <c>size</c> on the prompt is
    /// the only record of the interaction's input volume.</description></item>
    /// <item><description><b>Tuple-keyed dimensions.</b> Models and plugins de-duplicate on the whole
    /// (name/id, provider, version) tuple, so the same model at two versions is deliberately generated to
    /// produce two dimension rows rather than silently reusing one.</description></item>
    /// </list>
    /// </remarks>
    public class CopilotEventDetailGenerator
    {
        private readonly Random _random;

        public CopilotEventDetailGenerator(Random random)
        {
            _random = random;
        }

        /// <summary>
        /// Adds the prompt and response message rows for an interaction.
        /// </summary>
        public void AddMessages(AnalyticsEntitiesContext db, CopilotChat chat)
        {
            // Prompts are short, responses are long - so a report on average message size by is_prompt has a
            // real difference to show rather than two identical bars.
            db.CopilotMessages.Add(new CopilotMessage
            {
                ChatId = chat.EventID,
                RelatedChat = chat,
                MessageId = Guid.NewGuid().ToString(),
                IsPrompt = true,
                Size = 40 + _random.Next(1200)
            });

            db.CopilotMessages.Add(new CopilotMessage
            {
                ChatId = chat.EventID,
                RelatedChat = chat,
                MessageId = Guid.NewGuid().ToString(),
                IsPrompt = false,
                // Microsoft does not populate Size for every host, so leave it null sometimes - a report
                // that assumes it is always present should fail here, not in production.
                Size = _random.Next(100) < 85 ? 500 + _random.Next(9500) : (long?)null
            });
        }

        /// <summary>
        /// Adds the interaction's contexts - where the user was when they used Copilot.
        /// </summary>
        /// <remarks>
        /// More than one context per interaction is generated on purpose. The importer's file/meeting
        /// resolution only acts on the FIRST file or meeting context, so <c>copilot_event_files</c> /
        /// <c>copilot_event_meetings</c> hold at most one; this table is the only place the rest survive, and
        /// it is only worth having if the data can actually have several.
        /// </remarks>
        public void AddContexts(AnalyticsEntitiesContext db, CopilotChat chat)
        {
            int contextCount = _random.Next(100) < 30 ? 2 + _random.Next(2) : 1;

            for (int i = 0; i < contextCount; i++)
            {
                var typeName = CopilotActivityGeneratorConfig.ContextTypes[
                    _random.Next(CopilotActivityGeneratorConfig.ContextTypes.Length)];

                bool isTeams = typeName.StartsWith("Teams", StringComparison.OrdinalIgnoreCase);

                db.CopilotEventContexts.Add(new CopilotEventContext
                {
                    ChatId = chat.EventID,
                    RelatedChat = chat,
                    ContextType = GetOrCreateContextType(db, typeName),
                    ContextRef = isTeams
                        ? $"19:meeting_{Guid.NewGuid():N}@thread.v2"
                        : $"https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Καλημέρα%20κόσμε_{_random.Next(1000)}.{typeName}",
                    ContainerId = isTeams
                        ? $"19:{Guid.NewGuid():N}@thread.tacv2"
                        : $"contoso.sharepoint.com,{Guid.NewGuid()},{Guid.NewGuid()}"
                });
            }
        }

        /// <summary>
        /// Links the interaction to the AI model(s) that produced the answer.
        /// </summary>
        public void AddAIModels(AnalyticsEntitiesContext db, CopilotChat chat)
        {
            int modelCount = _random.Next(100) < 20 ? 2 : 1;
            var used = new System.Collections.Generic.HashSet<int>();

            for (int i = 0; i < modelCount; i++)
            {
                int index = _random.Next(CopilotActivityGeneratorConfig.AIModels.Length);
                if (!used.Add(index))
                    continue;

                var spec = CopilotActivityGeneratorConfig.AIModels[index];
                db.CopilotEventAIModels.Add(new CopilotEventAIModel
                {
                    ChatId = chat.EventID,
                    RelatedChat = chat,
                    AIModel = GetOrCreateAIModel(db, spec.Name, spec.Provider, spec.Version)
                });
            }
        }

        /// <summary>
        /// Links the interaction to the system plugins / connectors that grounded it. Plenty of interactions
        /// use none, which is the case a report needs to handle.
        /// </summary>
        public void AddSystemPlugins(AnalyticsEntitiesContext db, CopilotChat chat)
        {
            if (_random.Next(100) < 45)
                return;

            int pluginCount = _random.Next(100) < 25 ? 2 : 1;
            var used = new System.Collections.Generic.HashSet<int>();

            for (int i = 0; i < pluginCount; i++)
            {
                int index = _random.Next(CopilotActivityGeneratorConfig.AISystemPlugins.Length);
                if (!used.Add(index))
                    continue;

                var spec = CopilotActivityGeneratorConfig.AISystemPlugins[index];
                db.CopilotEventAISystemPlugins.Add(new CopilotEventAISystemPlugin
                {
                    ChatId = chat.EventID,
                    RelatedChat = chat,
                    AISystemPlugin = GetOrCreateSystemPlugin(db, spec.PluginId, spec.Name, spec.Version)
                });
            }
        }

        #region Dimension resolution

        private CopilotContextType GetOrCreateContextType(AnalyticsEntitiesContext db, string name)
        {
            var existing = db.CopilotContextTypes.Local.FirstOrDefault(t => t.Name == name)
                ?? db.CopilotContextTypes.FirstOrDefault(t => t.Name == name);

            if (existing == null)
            {
                existing = new CopilotContextType { Name = name };
                db.CopilotContextTypes.Add(existing);
            }

            return existing;
        }

        /// <summary>
        /// Resolved on the whole (name, provider, version) tuple, matching the importer: the version is part
        /// of a model's identity for AI-transparency reporting, so matching on name alone would collapse two
        /// genuinely different models into one dimension row.
        /// </summary>
        private CopilotAIModel GetOrCreateAIModel(AnalyticsEntitiesContext db, string name, string provider, string version)
        {
            var existing = db.CopilotAIModels.Local
                    .FirstOrDefault(m => m.Name == name && m.ProviderName == provider && m.Version == version)
                ?? db.CopilotAIModels
                    .FirstOrDefault(m => m.Name == name && m.ProviderName == provider && m.Version == version);

            if (existing == null)
            {
                existing = new CopilotAIModel { Name = name, ProviderName = provider, Version = version };
                db.CopilotAIModels.Add(existing);
            }

            return existing;
        }

        /// <summary>Same tuple rule as the models - a plugin upgrade is a new row, not a rewrite.</summary>
        private CopilotAISystemPlugin GetOrCreateSystemPlugin(AnalyticsEntitiesContext db, string pluginId, string name, string version)
        {
            var existing = db.CopilotAISystemPlugins.Local
                    .FirstOrDefault(p => p.PluginId == pluginId && p.Name == name && p.Version == version)
                ?? db.CopilotAISystemPlugins
                    .FirstOrDefault(p => p.PluginId == pluginId && p.Name == name && p.Version == version);

            if (existing == null)
            {
                existing = new CopilotAISystemPlugin { PluginId = pluginId, Name = name, Version = version };
                db.CopilotAISystemPlugins.Add(existing);
            }

            return existing;
        }

        #endregion
    }
}
