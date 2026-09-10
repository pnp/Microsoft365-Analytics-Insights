using Common.Entities;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// A Data Loss Prevention record from the Office 365 Management Activity API's <c>DLP.All</c>
    /// content type.
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#dlp-schema
    /// </summary>
    /// <remarks>
    /// <para>
    /// These records cover Exchange Online, SharePoint/OneDrive and Endpoint DLP. They are routed by
    /// <c>RecordType</c>, not by <c>Workload</c>: a DLP record carries the workload where the match was
    /// DETECTED (e.g. "SharePoint", "Exchange"), so routing it by workload would deserialise it as an
    /// ordinary SharePoint or Exchange audit event and silently lose every policy field.
    /// </para>
    /// <para>
    /// Note what these records CANNOT tell you: <c>UserKey</c> is always the literal "DlpAgent" and there
    /// is no agent identity anywhere in the schema, so a DLP record can never be attributed to a Copilot
    /// agent. Microsoft also documents the DLP feed's workloads as Exchange Online, Endpoint and
    /// SharePoint/OneDrive only - Copilot is absent - so DLP policies scoped to the "Microsoft 365
    /// Copilot and Copilot Chat" location do not appear here at all. Copilot's own block signal is
    /// embedded in the CopilotInteraction record instead; see <c>CopilotDlpRules</c>.
    /// </para>
    /// </remarks>
    public class DlpAuditLogContent : AbstractAuditLogContent
    {
        /// <summary>
        /// The policies that matched, each with the rules that fired. Mandatory in the published schema,
        /// but treated as optional here so a record with an unexpected shape degrades to "no policy
        /// detail" rather than throwing away the audit event.
        /// </summary>
        public List<AccessedResourcePolicyDetail> PolicyDetails { get; set; }

        /// <summary>
        /// Whether the record includes the actual matched sensitive values. Requires the separate
        /// "Read DLP policy events including sensitive details" permission, which this product
        /// deliberately does not ask for - we report that a policy fired, never the content that
        /// triggered it. Recorded only so the flag's meaning is documented where someone will look.
        /// </summary>
        public bool? SensitiveInfoDetectionIsIncluded { get; set; }

        public override async Task<bool> ProcessExtendedProperties(SaveSession sessionContext, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            await sessionContext.DlpEventResolver.SaveSingleDlpEventToSqlStaging(this, relatedAuditEvent);
            return true;
        }
    }
}
