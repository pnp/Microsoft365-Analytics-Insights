namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Used to figure out workload of Activity only. Has one prop to figure this out & then full-load with proper class. 
    /// </summary>
    public class WorkloadOnlyAuditLogContent
    {
        public string Workload { get; set; }

    }
}
