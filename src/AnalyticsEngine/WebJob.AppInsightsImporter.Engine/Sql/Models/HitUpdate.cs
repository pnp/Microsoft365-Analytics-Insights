using Common.DataUtils.Sql;
using System;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql.Models
{
    [TempTableName(STAGING_TABLENAME)]
    public class HitUpdate
    {
#if !DEBUG
        public const string STAGING_TABLENAME = "##import_staging_hit_updates";
#else
        public const string STAGING_TABLENAME = "debug_staging_hit_updates";
#endif

        public HitUpdate() { }
        public HitUpdate(PageExitEventAppInsightsQueryResult pageExitEventAppInsightsQueryResult) : this()
        {
            if (pageExitEventAppInsightsQueryResult is null)
            {
                throw new ArgumentNullException(nameof(pageExitEventAppInsightsQueryResult));
            }

            this.PageRequestId = pageExitEventAppInsightsQueryResult.CustomProperties?.PageRequestId.ToString() ?? string.Empty;
            this.SecondsOnPage = pageExitEventAppInsightsQueryResult.CustomProperties?.ActiveTime ?? 0;
        }


        [Column("page_request_id")]
        public string PageRequestId { get; set; }

        [Column("seconds_on_page")]
        public double SecondsOnPage { get; set; }
    }
}
