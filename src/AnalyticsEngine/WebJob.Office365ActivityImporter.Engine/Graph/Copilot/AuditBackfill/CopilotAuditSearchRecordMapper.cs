using Common.Entities;
using Newtonsoft.Json.Linq;
using System;
using System.Security.Cryptography;
using System.Text;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill
{
    public sealed class CopilotAuditSearchRecordMapping
    {
        public AbstractAuditLogContent Content { get; set; }
        public bool IdMatched { get; set; }
        public string ErrorCode { get; set; }
    }

    public static class CopilotAuditSearchRecordMapper
    {
        public static CopilotAuditSearchRecordMapping Map(CopilotAuditSearchRecord record, Microsoft.Extensions.Logging.ILogger logger = null)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.AuditData))
            {
                return new CopilotAuditSearchRecordMapping { ErrorCode = "missingAuditData" };
            }

            JObject payload;
            try { payload = JObject.Parse(record.AuditData); }
            catch { return new CopilotAuditSearchRecordMapping { ErrorCode = "invalidAuditData" }; }

            var logBase = payload.ToObject<WorkloadOnlyAuditLogContent>();
            if (logBase == null || logBase.Workload != ActivityImportConstants.WORKLOAD_COPILOT || logBase.RecordType != 261)
            {
                return new CopilotAuditSearchRecordMapping { ErrorCode = "notCopilotInteraction" };
            }

            EnsureGuidId(payload, record.Id, out var idMatched);
            var content = AuditLogContentDispatcher.Dispatch(payload, payload.ToObject<WorkloadOnlyAuditLogContent>(), logger, importPowerPlatform: false, importCopilot: true, importDlp: false);
            if (content != null)
            {
                content.OriginalImportFileContents = payload.ToString(Newtonsoft.Json.Formatting.None);
            }
            return new CopilotAuditSearchRecordMapping { Content = content, IdMatched = idMatched };
        }

        private static void EnsureGuidId(JObject payload, string graphRecordId, out bool idMatched)
        {
            idMatched = true;
            var payloadId = (string)payload["Id"] ?? (string)payload["id"];
            if (Guid.TryParse(payloadId, out _))
            {
                idMatched = string.IsNullOrEmpty(graphRecordId) || string.Equals(payloadId, graphRecordId, StringComparison.OrdinalIgnoreCase);
                if (payload["Id"] == null) payload["Id"] = payloadId;
                return;
            }

            if (Guid.TryParse(graphRecordId, out var graphGuid))
            {
                payload["Id"] = graphGuid;
                idMatched = string.IsNullOrEmpty(payloadId) || string.Equals(payloadId, graphRecordId, StringComparison.OrdinalIgnoreCase);
                return;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(graphRecordId ?? payload.ToString(Newtonsoft.Json.Formatting.None)));
                var guid = new byte[16];
                Buffer.BlockCopy(bytes, 0, guid, 0, 16);
                payload["Id"] = new Guid(guid);
                idMatched = false;
            }
        }
    }
}
