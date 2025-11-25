namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// Global trace configuration for capturing raw audit log JSON bodies that contain a specific email address.
    /// Populated by Program argument parsing in the WebJob project.
    /// </summary>
    public static class AuditTraceConfig
    {
        /// <summary>
        /// Email address to search for within individual audit log JSON items.
        /// </summary>
        public static string TraceEmail { get; set; } = null;
        /// <summary>
        /// Directory to write matching JSON items to. Must exist / be creatable.
        /// </summary>
        public static string TraceDirectory { get; set; } = null;
    }
}
