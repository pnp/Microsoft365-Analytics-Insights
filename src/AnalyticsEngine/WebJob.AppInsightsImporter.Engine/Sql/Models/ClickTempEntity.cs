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
                // Keep the SharePoint URL within the urls.full_url column width (nvarchar(850)): strip
                // the volatile xsdata token, else reduce to the page path. See issue #122.
                this.Url = StringUtils.EnsureUrlWithinLength(p.CustomProperties.HRef, Common.Entities.Url.FullUrlMaxLength);
                this.Username = p.Username;
                this.ClassNames = StringUtils.EnsureMaxLength(p.CustomProperties.ClassNames, 800);
                this.PageRequestId = p.CustomProperties.PageRequestId.Value;
                this.LinkText = StringUtils.EnsureMaxLength(p.CustomProperties.LinkText, 100);
            }
            else
            {
                throw new ArgumentNullException(nameof(p.CustomProperties.PageRequestId));
            }
        }

        // Must match dbo.urls.full_url (nvarchar(850), see migration ShrinkUrlsFullUrlColumn /
        // issue #122) so the join in "Migrate clicks from staging.sql" can use IX_urls_full_url
        // instead of an implicit type conversion that defeats the index. nvarchar (not varchar)
        // so Unicode URLs (e.g. Greek) aren't corrupted. See #122 (#108/#109).
        [Column("url", true, SqlTypeOverride = "nvarchar(850)")]
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
