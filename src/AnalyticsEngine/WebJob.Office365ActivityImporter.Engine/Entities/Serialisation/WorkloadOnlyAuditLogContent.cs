namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Used to figure out workload of Activity only. Has one prop to figure this out & then full-load with proper class. 
    /// </summary>
    public class WorkloadOnlyAuditLogContent
    {
        public string Workload { get; set; }

        /// <summary>
        /// The audit operation name (e.g. "ViewReport", "LaunchPowerApp"). Read at the
        /// workload-routing stage so the loader can filter operations before doing the full
        /// workload-specific deserialisation.
        /// </summary>
        public string Operation { get; set; }

        /// <summary>
        /// The common-schema <c>RecordType</c> enum value.
        /// </summary>
        /// <remarks>
        /// Needed at the routing stage because Data Loss Prevention records cannot be identified by
        /// <see cref="Workload"/>: a DLP record carries the workload where the match was DETECTED
        /// ("SharePoint", "Exchange", "Endpoint"), so routing it by workload would deserialise it as an
        /// ordinary SharePoint or Exchange audit event and silently discard every policy field. Its
        /// RecordType (11, 13, 33, 63, 107) is what actually identifies it.
        /// https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema
        /// </remarks>
        public int? RecordType { get; set; }
    }
}
