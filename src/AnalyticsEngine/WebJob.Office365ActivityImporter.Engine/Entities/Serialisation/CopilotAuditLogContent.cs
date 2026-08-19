using Common.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{

    public class CopilotAuditLogContent : AbstractAuditLogContent
    {
        public CopilotEventData CopilotEventData { get; set; } = null;
        public string EventRaw { get; set; } = null;

        public string AgentName { get; set; }
        public string AgentId { get; set; }

        /// <summary>
        /// Indicates whether this is a custom engine agent or a declarative agent.
        /// False when AgentId starts with "CopilotStudio.Declarative." (declarative agent).
        /// True when an agent is identified but is not declarative (custom engine agent).
        /// Null when no agent is identified.
        /// </summary>
        public bool? IsCustomAgent { get; set; }

        public CopilotCreditEstimation Cost { get; set; }

        /// <summary>
        /// Parsed audit event containing Messages, AgentActions, AIToolUsages, and FlowActions.
        /// Used for serializing extended event data to staging tables.
        /// </summary>
        public CopilotAuditEvent ParsedAuditEvent { get; set; }

        public string OrganizationId { get; set; }

        public string AppIdentity { get; set; }

        /// <summary>
        /// The Azure region of the Copilot service that handled the interaction. From the
        /// CopilotInteractionAuditRecord schema (https://learn.microsoft.com/office/office-365-management-api/copilot-schema).
        /// </summary>
        public string ClientRegion { get; set; }

        /// <summary>
        /// Version of the Copilot audit log schema for this record.
        /// </summary>
        public string CopilotLogVersion { get; set; }

        public static CopilotAuditLogContent FromJson(string json)
        {
            var thisAuditLogReport = JsonConvert.DeserializeObject<CopilotAuditLogContent>(json);

            // We want to store the CopilotEventData but its current schema may change in the future. Keeping the full CopilotEventData object for now.
            dynamic obj = JsonConvert.DeserializeObject<dynamic>(json);
            thisAuditLogReport.EventRaw = JsonConvert.SerializeObject(obj.CopilotEventData);

            // Parse the event data for structured access (instead of using EventRaw later)
            thisAuditLogReport.ParsedAuditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(thisAuditLogReport.EventRaw);

            // Priority: CopilotEventData.TargetAgentName (custom engine agent) > AgentName (declarative agent) > AppIdentity fallback
            var targetAgentName = thisAuditLogReport.CopilotEventData?.TargetAgentName;
            if (!string.IsNullOrEmpty(targetAgentName))
            {
                // TargetAgentName indicates a custom engine agent
                thisAuditLogReport.AgentName = targetAgentName;
                // If AgentId is not set, identify the agent from the payload. Newer audit records carry an explicit
                // per-agent id in CopilotEventData.TargetPlatformAgentId (e.g. "T_{guid}" Copilot Studio, "P_{guid}",
                // "BuiltIn_..." or short first-party ids like "OutlookDraft"); prefer it. Older records don't include
                // it, so fall back to AppIdentity exactly as before.
                if (string.IsNullOrEmpty(thisAuditLogReport.AgentId))
                {
                    var targetPlatformAgentId = thisAuditLogReport.CopilotEventData?.TargetPlatformAgentId;
                    thisAuditLogReport.AgentId = !string.IsNullOrEmpty(targetPlatformAgentId)
                        ? targetPlatformAgentId
                        : thisAuditLogReport.AppIdentity;
                }
            }
            else if (string.IsNullOrEmpty(thisAuditLogReport.AgentName) &&
                string.IsNullOrEmpty(thisAuditLogReport.AgentId) &&
                !string.IsNullOrEmpty(thisAuditLogReport.AppIdentity) &&
                !string.IsNullOrEmpty(thisAuditLogReport.OrganizationId))
            {
                // Fallback: extract agent name from AppIdentity when neither TargetAgentName nor AgentName are set
                // AppIdentity format: "Copilot.Studio.Default-{OrganizationId}-{AgentName}"
                // Example: "Copilot.Studio.Default-873ca9a3-4805-48f2-b419-fabf868641da-contoso_itAssistant"
                var orgIdIndex = thisAuditLogReport.AppIdentity.IndexOf(thisAuditLogReport.OrganizationId);
                if (orgIdIndex >= 0)
                {
                    // Find the position after the OrganizationId
                    var afterOrgId = orgIdIndex + thisAuditLogReport.OrganizationId.Length;
                    if (afterOrgId < thisAuditLogReport.AppIdentity.Length)
                    {
                        // Extract everything after OrganizationId, skipping the separator (typically a dash)
                        var remainder = thisAuditLogReport.AppIdentity.Substring(afterOrgId);
                        if (remainder.StartsWith("-") && remainder.Length > 1)
                        {
                            thisAuditLogReport.AgentName = remainder.Substring(1);
                            thisAuditLogReport.AgentId = thisAuditLogReport.AppIdentity;
                        }
                        else if (!string.IsNullOrEmpty(remainder) && !remainder.Equals("-"))
                        {
                            thisAuditLogReport.AgentName = remainder;
                            thisAuditLogReport.AgentId = thisAuditLogReport.AppIdentity;
                        }
                    }
                }
            }

            // First-party named agents (e.g. Copilot Cowork, AppIdentity "Copilot.M365Copilot.CoworkChat")
            // carry an AgentName but no AgentId and no TargetAgentName, so neither branch above set an id.
            // Promote AppIdentity to AgentId so the agent is dimensioned in copilot_agents (the agents upsert
            // keys on agent_id, so without an id the interaction imports but shows as unattributed / agent_id NULL).
            //
            // Restricted to a vetted allow-list of first-party AppIdentity prefixes (IsVettedFirstPartyAppIdentity)
            // so we don't silently absorb arbitrary future AgentName + AppIdentity combinations whose AppIdentity
            // may not be a stable per-agent key - which could merge distinct agents onto one id or fragment one
            // agent across ids. Records that don't match the allow-list keep agent_id NULL, exactly as before.
            // See PR #180.
            if (!string.IsNullOrEmpty(thisAuditLogReport.AgentName) &&
                string.IsNullOrEmpty(thisAuditLogReport.AgentId) &&
                IsVettedFirstPartyAppIdentity(thisAuditLogReport.AppIdentity))
            {
                thisAuditLogReport.AgentId = thisAuditLogReport.AppIdentity;
            }

            // Normalise the resolved id (from any branch above or the raw payload) to a single canonical form so
            // the same logical agent is not split across id variants. Microsoft emits some agents under more than
            // one id string - notably SharePoint agents as both "SharePointAgents.Declarative.SPO_..." and bare
            // "SPO_..." - which would otherwise create duplicate copilot_agents rows and double-count usage.
            thisAuditLogReport.AgentId = NormalizeAgentId(thisAuditLogReport.AgentId);

            if (!string.IsNullOrEmpty(thisAuditLogReport.AgentName))
            {
                // Calculate cost from the parsed event for agents
                thisAuditLogReport.Cost = CopilotCreditEstimation.Analyze(thisAuditLogReport.EventRaw, thisAuditLogReport.IsCustomAgent.HasValue && thisAuditLogReport.IsCustomAgent.Value);
            }
            else
            {
                // No agent identified = no cost
                thisAuditLogReport.Cost = CopilotCreditEstimation.NoCost;
            }

            return thisAuditLogReport;
        }

        /// <summary>
        /// Vetted first-party AppIdentity prefixes whose AppIdentity is known to be a stable, agent-specific
        /// identifier. Only these are promoted to AgentId when a named agent arrives without its own AgentId
        /// (see <see cref="FromJson"/>). Keep this list conservative: add a prefix only after confirming from
        /// real payloads that its AppIdentity is a stable per-agent key, not a shared app-level or volatile value.
        /// </summary>
        internal static readonly string[] FirstPartyNamedAgentAppIdentityPrefixes = new[]
        {
            "Copilot.M365Copilot.",   // e.g. "Copilot.M365Copilot.CoworkChat" (Copilot Cowork)
        };

        private const string SharePointDeclarativeAgentIdPrefix = "SharePointAgents.Declarative.";

        /// <summary>
        /// True when <paramref name="appIdentity"/> starts with a vetted first-party prefix from
        /// <see cref="FirstPartyNamedAgentAppIdentityPrefixes"/> and can therefore safely be used as an AgentId.
        /// </summary>
        internal static bool IsVettedFirstPartyAppIdentity(string appIdentity)
        {
            if (string.IsNullOrEmpty(appIdentity))
            {
                return false;
            }

            foreach (var prefix in FirstPartyNamedAgentAppIdentityPrefixes)
            {
                if (appIdentity.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Collapses redundant agent id variants to a single canonical form so the same logical agent is not
        /// dimensioned twice. Currently strips the "SharePointAgents.Declarative." wrapper prefix from SharePoint
        /// agent ids, whose canonical identity is the bare "SPO_..." item id (Microsoft emits both forms for the
        /// same agent). Null/empty and all other ids are returned unchanged.
        /// </summary>
        internal static string NormalizeAgentId(string agentId)
        {
            if (string.IsNullOrEmpty(agentId))
            {
                return agentId;
            }

            if (agentId.StartsWith(SharePointDeclarativeAgentIdPrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                var remainder = agentId.Substring(SharePointDeclarativeAgentIdPrefix.Length);
                if (remainder.StartsWith("SPO_", System.StringComparison.OrdinalIgnoreCase))
                {
                    return remainder;
                }
            }

            return agentId;
        }

        public override async Task<bool> ProcessExtendedProperties(SaveSession sessionContext, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await sessionContext.CopilotEventResolver.SaveSingleCopilotEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }

    /// <summary>
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/copilot-schema#audit-copilot-schema-definitions
    /// </summary>
    public class CopilotEventData
    {
        /// <summary>
        /// References to all the files and documents Copilot used in M365 services like OneDrive and SharePoint Online to respond to the user’s request.
        /// </summary>
        public List<AccessedResource> AccessedResources { get; set; } = new List<AccessedResource>();

        /// <summary>
        /// The type of Copilot used during the interaction.
        /// The current list of values include Bing, Teams, Outlook, Office, DevUI, BashTool, Word, Excel, PowerPoint, OneNote, SharePoint, Loop, Whiteboard, M365App, M365AdminCenter, Planner, VivaEngage, VivaCopilot, Stream, Assist365, VivaGoals.
        /// </summary>
        public string AppHost { get; set; } = null;

        /// <summary>
        /// Context contains a collection of attributes within AppChat around the user interaction to help describe where the user was during the copilot interaction. ID is identifier of the resource that was being used during the copilot interaction. Type is the name of the app or service within context.
        /// Example: Some examples of supported apps and services include M365 Office(docx, pptx, xlsx), TeamsMeeting, TeamsChannel, and TeamsChat.If Copilot is used in Excel, then context will be the identifier of the Excel Spreadsheet and the file type.
        /// </summary>
        public List<Context> Contexts { get; set; } = new List<Context>();

        /// <summary>
        /// The name of the target custom engine agent. Present when the interaction involves a custom engine agent.
        /// </summary>
        public string TargetAgentName { get; set; }

        /// <summary>
        /// Explicit identifier of the target (custom engine) agent that handled the interaction, present in newer
        /// Copilot audit records alongside <see cref="TargetAgentName"/>. Observed forms include "T_{guid}"
        /// (Copilot Studio), "P_{guid}", "BuiltIn_{name}" and short first-party ids such as "OutlookDraft".
        /// Preferred over AppIdentity as the AgentId for custom engine agents when no explicit AgentId is present.
        /// </summary>
        public string TargetPlatformAgentId { get; set; }

        /// <summary>
        /// Identifier of the Copilot conversation thread the interaction belongs to.
        /// </summary>
        public string ThreadId { get; set; }

        /// <summary>
        /// Identifiers of the messages that participated in the interaction.
        /// </summary>
        public List<string> MessageIds { get; set; } = new List<string>();

        /// <summary>
        /// Information about AI system plugins invoked during the interaction.
        /// </summary>
        public List<AISystemPlugin> AISystemPlugin { get; set; } = new List<AISystemPlugin>();
    }

    /// <summary>
    /// Schema element describing an AI system plugin invoked during a Copilot interaction.
    /// </summary>
    public class AISystemPlugin
    {
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>
        /// Version of the plugin, per the audit schema's AISystemPluginData.Version.
        /// </summary>
        public string Version { get; set; }
    }

    public class Context
    {
        public string Id { get; set; } = null;
        public string Type { get; set; } = null;

        /// <summary>
        /// Identifier of the container the context belongs to (e.g. a Teams team or SharePoint container).
        /// </summary>
        public string ContainerId { get; set; }
    }


    public class AccessedResource
    {
        public string Id { get; set; } = null;
        public string Name { get; set; } = null;
        public string SensitivityLabelId { get; set; } = null;
        public string Type { get; set; } = null;
        public string SiteUrl { get; set; }

        /// <summary>
        /// Unique identifier of the SharePoint list item backing this resource, when applicable.
        /// </summary>
        [JsonProperty("listItemUniqueId")]
        public string ListItemUniqueId { get; set; }

        /// <summary>
        /// The action performed against the resource during the Copilot interaction (e.g. Read).
        /// </summary>
        public string Action { get; set; }
    }
}
