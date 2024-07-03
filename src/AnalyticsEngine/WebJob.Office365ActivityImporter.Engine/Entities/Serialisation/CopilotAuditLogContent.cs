using Common.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{

    public class CopilotAuditLogContent : AbstractAuditLogContent
    {
        public CopilotEventData CopilotEventData { get; set; } = null;
        public string EventRaw { get; set; } = null;
        public override async Task<bool> ProcessExtendedProperties(SaveSession sessionContext, Office365Event relatedAuditEvent)
        {
            await sessionContext.CopilotEventResolver.SaveSingleCopilotEventToSql(CopilotEventData, relatedAuditEvent);
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
    }

}
