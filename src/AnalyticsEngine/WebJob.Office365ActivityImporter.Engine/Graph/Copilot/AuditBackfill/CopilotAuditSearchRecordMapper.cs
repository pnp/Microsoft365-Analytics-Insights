using Common.Entities;
using Common.Entities.CopilotAuditBackfill;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
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
            if (record == null || record.AuditData == null || record.AuditData.Type == JTokenType.Null)
            {
                return new CopilotAuditSearchRecordMapping { ErrorCode = "missingAuditData" };
            }

            JObject auditData;
            JObject payload;
            try
            {
                auditData = record.AuditData.Type == JTokenType.String
                    ? JObject.Parse((string)record.AuditData)
                    : (JObject)record.AuditData.DeepClone();
                StripODataProperties(auditData);
                payload = SelectPayload(auditData);
                StripODataProperties(payload);
            }
            catch { return new CopilotAuditSearchRecordMapping { ErrorCode = "invalidAuditData" }; }

            var graphSaysCopilot = IsGraphCopilotInteraction(record);
            WorkloadOnlyAuditLogContent logBase;
            try
            {
                logBase = payload.ToObject<WorkloadOnlyAuditLogContent>();
            }
            catch (Exception ex) when (IsMappingException(ex))
            {
                return new CopilotAuditSearchRecordMapping { ErrorCode = "invalidAuditData" };
            }

            if (logBase == null || logBase.Workload != ActivityImportConstants.WORKLOAD_COPILOT || logBase.RecordType != 261)
            {
                if (!graphSaysCopilot || !TryCompleteCopilotPayloadFromRecord(payload, record))
                {
                    return new CopilotAuditSearchRecordMapping { ErrorCode = graphSaysCopilot ? CopilotAuditBackfillErrorCodes.UnrecognisedAuditData : "notCopilotInteraction" };
                }
            }

            EnsureGuidId(payload, record.Id, out var idMatched);
            AbstractAuditLogContent content;
            try
            {
                content = AuditLogContentDispatcher.Dispatch(payload, payload.ToObject<WorkloadOnlyAuditLogContent>(), logger, importPowerPlatform: false, importCopilot: true, importDlp: false);
            }
            catch (Exception ex) when (IsMappingException(ex))
            {
                return new CopilotAuditSearchRecordMapping { ErrorCode = "invalidAuditData", IdMatched = idMatched };
            }
            if (content != null)
            {
                content.OriginalImportFileContents = payload.ToString(Newtonsoft.Json.Formatting.None);
            }
            return new CopilotAuditSearchRecordMapping { Content = content, IdMatched = idMatched };
        }


        private static JObject SelectPayload(JObject auditData)
        {
            var dynamicProperties = auditData["dynamicProperties"] as JObject;
            if (dynamicProperties != null
                && (dynamicProperties["RecordType"] != null || dynamicProperties["Workload"] != null || dynamicProperties["Id"] != null || dynamicProperties["id"] != null))
            {
                return (JObject)dynamicProperties.DeepClone();
            }

            return auditData;
        }

        private static void StripODataProperties(JObject obj)
        {
            if (obj == null) return;
            foreach (var property in obj.Properties().Where(p => p.Name.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                property.Remove();
            }
        }

        private static bool IsGraphCopilotInteraction(CopilotAuditSearchRecord record)
        {
            return record != null
                && string.Equals(record.AuditLogRecordType, "CopilotInteraction", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Service, ActivityImportConstants.WORKLOAD_COPILOT, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCompleteCopilotPayloadFromRecord(JObject payload, CopilotAuditSearchRecord record)
        {
            if (payload == null || payload["CopilotEventData"] == null || payload["CopilotEventData"].Type != JTokenType.Object)
            {
                return false;
            }

            if (payload["Id"] == null && payload["id"] == null && string.IsNullOrEmpty(record.Id)) return false;
            if (payload["CreationTime"] == null && !record.CreatedDateTime.HasValue) return false;
            if (payload["UserId"] == null && string.IsNullOrEmpty(record.UserPrincipalName)) return false;

            if (payload["Workload"] == null) payload["Workload"] = ActivityImportConstants.WORKLOAD_COPILOT;
            if (payload["RecordType"] == null) payload["RecordType"] = 261;
            if (payload["Operation"] == null) payload["Operation"] = string.IsNullOrEmpty(record.Operation) ? "CopilotInteraction" : record.Operation;
            if (payload["CreationTime"] == null) payload["CreationTime"] = record.CreatedDateTime.Value;
            if (payload["UserId"] == null) payload["UserId"] = record.UserPrincipalName;
            return true;
        }

        private static bool IsMappingException(Exception ex)
        {
            return ex is Newtonsoft.Json.JsonException
                || ex is FormatException
                || ex is InvalidCastException
                || ex is ArgumentException;
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
