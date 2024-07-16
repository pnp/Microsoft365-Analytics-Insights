using DataUtils;
using DataUtils.Sql;
using System;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql.Models
{

    [TempTableName(ClickTempEntity.STAGING_TABLENAME)]
    internal class ClickTempEntity
    {

#if !DEBUG
        public const string STAGING_TABLENAME = "##import_staging_clicks";
#else
        public const string STAGING_TABLENAME = "debug_staging_clicks";

#endif

        public ClickTempEntity(ClickEventAppInsightsQueryResult p)
        {
            if (p.CustomProperties.PageRequestId.HasValue)
            {
                this.Timestamp = p.Timestamp;
                this.Url = p.CustomProperties.HRef;
                this.Username = p.Username;
                this.ClassNames = StringUtils.EnsureMaxLength(p.CustomProperties.ClassNames, 2000);
                this.PageRequestId = p.CustomProperties.PageRequestId.Value;
                this.LinkText = StringUtils.EnsureMaxLength(p.CustomProperties.LinkText, 100);
            }
            else
            {
                throw new ArgumentNullException(nameof(p.CustomProperties.PageRequestId));
            }
        }

        [Column("url", true)]
        public string Url { get; set; }

        [Column("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// We don't actually use this yet
        /// </summary>
        [Column("alt_text", true)]
        public string AltText { get; set; }

        [Column("link_text")]
        public string LinkText { get; set; }


        [Column("user_name", true)]
        public string Username { get; set; }


        [Column("page_request_id")]
        public Guid PageRequestId { get; set; }

        [Column("class_names")]
        public string ClassNames { get; set; }
    }
}
