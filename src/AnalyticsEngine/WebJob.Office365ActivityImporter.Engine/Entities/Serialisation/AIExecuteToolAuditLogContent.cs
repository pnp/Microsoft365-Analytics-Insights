using Common.Entities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Audit log content for AIExecuteTool events. 
    /// Represents a Copilot agent tool execution that can be linked back to a copilot chat message.
    /// </summary>
    public class AIExecuteToolAuditLogContent : AbstractAuditLogContent
    {
        public CopilotEventData CopilotEventData { get; set; } = null;
        public string EventRaw { get; set; } = null;

        /// <summary>
        /// Parsed audit event containing Messages and AISystemPlugin data.
        /// Messages contain the message IDs that link back to copilot chat events.
        /// AISystemPlugin contains the tool names that were executed.
        /// </summary>
        public CopilotAuditEvent ParsedAuditEvent { get; set; }

        public static AIExecuteToolAuditLogContent FromJson(string json)
        {
            var report = JsonConvert.DeserializeObject<AIExecuteToolAuditLogContent>(json);

            dynamic obj = JsonConvert.DeserializeObject<dynamic>(json);
            if (obj.CopilotEventData != null)
            {
                report.EventRaw = JsonConvert.SerializeObject(obj.CopilotEventData);
                report.ParsedAuditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(report.EventRaw);
            }

            return report;
        }

        public override async Task<bool> ProcessExtendedProperties(SaveSession sessionContext, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await sessionContext.CopilotEventResolver.SaveToolExecutionToSqlStaging(this, relatedAuditEvent);
            return true;
        }

        /// <summary>
        /// Gets the message IDs from the parsed audit event (response messages only).
        /// These IDs link back to copilot_event_messages from the original CopilotInteraction event.
        /// </summary>
        public List<string> GetResponseMessageIds()
        {
            if (ParsedAuditEvent?.Messages == null)
                return new List<string>();

            return ParsedAuditEvent.Messages
                .Where(m => !m.IsPrompt && !string.IsNullOrEmpty(m.Id))
                .Select(m => m.Id)
                .ToList();
        }

        /// <summary>
        /// Gets the tool names from the AI system plugins in the event.
        /// </summary>
        public List<string> GetToolNames()
        {
            if (ParsedAuditEvent?.AISystemPlugin == null)
                return new List<string>();

            return ParsedAuditEvent.AISystemPlugin
                .Where(p => !string.IsNullOrEmpty(p.Name))
                .Select(p => p.Name)
                .ToList();
        }
    }
}
