using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{
    public class CopilotAuditEvent
    {
        public List<AISystemPlugin> AISystemPlugin { get; set; }
        public List<AccessedResource> AccessedResources { get; set; }
        public List<Message> Messages { get; set; }
    }

    public class AISystemPlugin
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }


    public class Message
    {
        public string Id { get; set; }
        public bool isPrompt { get; set; }
    }

    public class CreditReport
    {
        public int AgentActions { get; set; }
        public int GenerativeTurns { get; set; }
        public int TotalCredits { get; set; }
        public Dictionary<string, int> ResourceTypeBreakdown { get; set; }

        public static CreditReport Analyze(string json)
        {
            var auditEvent = JsonConvert.DeserializeObject<CopilotAuditEvent>(json);

            int agentActions = auditEvent.AISystemPlugin?.Count ?? 0;
            int generativeTurns = auditEvent.Messages?.Count(m => !m.isPrompt) ?? 0;

            var resourceBreakdown = auditEvent.AccessedResources?
                .GroupBy(r => string.IsNullOrEmpty(r.Type) ? "WebPage" : r.Type)
                .ToDictionary(g => g.Key, g => g.Count()) ?? new Dictionary<string, int>();

            return new CreditReport
            {
                AgentActions = agentActions,
                GenerativeTurns = generativeTurns,
                TotalCredits = agentActions + generativeTurns,
                ResourceTypeBreakdown = resourceBreakdown
            };
        }
    }


}
