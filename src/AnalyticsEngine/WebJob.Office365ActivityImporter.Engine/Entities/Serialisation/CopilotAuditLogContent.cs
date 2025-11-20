using Common.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{

    public class CopilotAuditLogContent : AbstractAuditLogContent
    {
        public CopilotEventData CopilotEventData { get; set; } = null;
        public string EventRaw { get; set; } = null;

        public string AgentName { get; set; }
        public string AgentId { get; set; }


        public CreditReport Cost { get; set; }

        public string OrganizationId { get; set; }

        public string AppIdentity { get; set; }

        public static CopilotAuditLogContent FromJson(string json)
        {
            var thisAuditLogReport = JsonConvert.DeserializeObject<CopilotAuditLogContent>(json);

            // We want to store the CopilotEventData but its current schema may change in the future. Keeping the full CopilotEventData object for now.
            dynamic obj = JsonConvert.DeserializeObject<dynamic>(json);
            thisAuditLogReport.EventRaw = JsonConvert.SerializeObject(obj.CopilotEventData);

            thisAuditLogReport.Cost = CreditReport.Analyze(thisAuditLogReport.EventRaw);

            // If AgentName and AgentId are not set, but AppIdentity has a value, extract from AppIdentity
            if (string.IsNullOrEmpty(thisAuditLogReport.AgentName) &&
                string.IsNullOrEmpty(thisAuditLogReport.AgentId) &&
                !string.IsNullOrEmpty(thisAuditLogReport.AppIdentity) &&
                !string.IsNullOrEmpty(thisAuditLogReport.OrganizationId))
            {
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
                            // Only set AgentId if we successfully extracted an AgentName
                            thisAuditLogReport.AgentId = thisAuditLogReport.AppIdentity;
                        }
                        else if (!string.IsNullOrEmpty(remainder) && !remainder.Equals("-"))
                        {
                            thisAuditLogReport.AgentName = remainder;
                            // Only set AgentId if we successfully extracted an AgentName
                            thisAuditLogReport.AgentId = thisAuditLogReport.AppIdentity;
                        }
                    }
                }
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
    }

    public class Context
    {
        public string Id { get; set; } = null;
        public string Type { get; set; } = null;
    }


    public class AccessedResource
    {
        public string Id { get; set; } = null;
        public string Name { get; set; } = null;
        public string SensitivityLabelId { get; set; } = null;
        public string Type { get; set; } = null;
        public string SiteUrl { get; set; }

        [JsonIgnore]
        public bool IsValidOffice365Data => !string.IsNullOrEmpty(Id);
    }


}
