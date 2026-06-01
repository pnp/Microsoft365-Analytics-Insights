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
    }
}
