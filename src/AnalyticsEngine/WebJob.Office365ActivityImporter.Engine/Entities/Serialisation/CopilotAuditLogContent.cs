using Common.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{

    public class CopilotAuditLogContent : AbstractAuditLogContent
    {
        public CopilotEventData CopilotEventData { get; set; } = null;
        public override async Task<bool> ProcessExtendedProperties(SaveSession sessionContext, Office365Event relatedAuditEvent)
        {
            await sessionContext.CopilotEventResolver.SaveSingleCopilotEventToSql(CopilotEventData, relatedAuditEvent);
            return true;
        }
    }

    public class CopilotEventData
    {
        public List<AccessedResource> AccessedResources { get; set; } = new List<AccessedResource>();
        public string AppHost { get; set; } = null;
        public List<Context> Contexts { get; set; } = new List<Context>();
        public List<string> MessageIds { get; set; } = new List<string>();
        public string ThreadId { get; set; } = null;
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
