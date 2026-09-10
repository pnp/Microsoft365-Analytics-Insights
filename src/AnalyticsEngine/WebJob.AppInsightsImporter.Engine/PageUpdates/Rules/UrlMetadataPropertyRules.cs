namespace WebJob.AppInsightsImporter.Engine.PageUpdates.Rules
{
    /// <summary>
    /// Which of a page's SharePoint metadata properties are worth storing.
    ///
    /// Extracted from <c>PageUpdateManager.UpdateUrlMetadataWith</c> (issue #369) so the filter can be
    /// asserted without a database.
    /// </summary>
    public static class UrlMetadataPropertyRules
    {
        /// <summary>Field names at or above this length are treated as system noise and dropped.</summary>
        public const int MaxFieldNameLength = 100;

        /// <summary>
        /// SharePoint's internal fields arrive with this escaped prefix (an encoded underscore); they are
        /// duplicates of the readable fields and of no analytic value.
        /// </summary>
        public const string SystemFieldNamePrefix = "vti_x005f";

        /// <summary>
        /// True for a simple (non-taxonomy) page property that should be stored.
        ///
        /// Preserved verbatim, including two things worth knowing about:
        /// a <c>null</c> name throws (as it always has - a JSON object cannot have a null key, so this is
        /// unreachable from the importer), and <c>StartsWith(string)</c> is the culture-sensitive overload.
        /// Neither is changed here, because this issue forbids behavioural change.
        /// </summary>
        public static bool IsImportableSimpleProp(string fieldName)
        {
            // Ignore system & over-sized fields (usually the same)
            return fieldName.Length < MaxFieldNameLength && !fieldName.StartsWith(SystemFieldNamePrefix);
        }
    }
}
