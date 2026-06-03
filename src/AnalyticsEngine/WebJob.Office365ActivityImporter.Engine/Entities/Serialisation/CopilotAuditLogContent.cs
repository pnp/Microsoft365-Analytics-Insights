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
                // If AgentId is not set, use AppIdentity as the identifier
                if (string.IsNullOrEmpty(thisAuditLogReport.AgentId))
                {
                    thisAuditLogReport.AgentId = thisAuditLogReport.AppIdentity;
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
