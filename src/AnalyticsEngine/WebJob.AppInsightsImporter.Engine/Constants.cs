namespace WebJob.AppInsightsImporter.Engine
{
    public static class AppInsightsImporterConstants
    {
        public static string PAGE_VIEWS => "PageViews";
        public static string EVENTS => "Event";

        public static string EVENT_NAME_USER_SEARCH => "UserSearch";

        public static string EVENT_NAME_PAGE_EXIT => "PAGE_EXIT";
        public static string EVENT_NAME_CLICK => "LinkClick";
        public static string EVENT_NAME_PAGE_UPDATE => "PageMetadataUpdate";
        public static string ANON_USER_NAME => "Anonymous User";

        public static string STAGING_TABLE_SEARCHES
        {
            get
            {
#if !DEBUG
                return "##import_staging_searches";
#else
                return "debug_staging_searches";
#endif
            }
        }

    }
}
