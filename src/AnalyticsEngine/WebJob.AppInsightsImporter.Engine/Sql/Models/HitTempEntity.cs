using DataUtils;
using DataUtils.Sql;
using System;
using WebJob.AppInsightsImporter.Engine.ApiImporter;

namespace WebJob.AppInsightsImporter.Engine.Sql.Models
{
    /// <summary>
    /// Staging table for AppInsights hits.
    /// </summary>
    [TempTableName(STAGING_TABLENAME)]
    internal class HitTempEntity
    {

#if !DEBUG
        public const string STAGING_TABLENAME = "##import_staging_hit_imports";
#else
        public const string STAGING_TABLENAME = "debug_staging_hit_imports";
#endif
        public HitTempEntity(PageViewAppInsightsQueryResult p)
        {
            this.AppInsightsSessionId = p.CustomProperties?.SessionId;
            this.BrowserName = p.Browser;
            this.City = p.City;
            this.CountryName = p.CountryOrRegion;
            this.DeviceName = p.DeviceModel;
            this.OS = p.ClientOS;
            this.PageRequestId = p.CustomProperties?.PageRequestId.ToString();
            this.PageLoadTime = p.PageLoadInSeconds.ToString();
            this.Province = p.StateOrProvince;
            this.SiteUrl = p.CustomProperties?.SiteUrl;
            this.SPRequestDuration = p.CustomProperties?.SPRequestDuration ?? 0;
            this.Timestamp = p.Timestamp;
            this.Title = p.CustomProperties?.PageTitle;
            this.Url = StringUtils.GetUrlBaseAddressIfValidUrl(p.Url);
            this.Username = p.Username;
            this.WebTitle = p.CustomProperties?.WebTitle;
            this.WebUrl = p.CustomProperties?.WebUrl;
        }

        [Column("url")]
        public string Url { get; set; }

        [Column("hit_timestamp")]
        public DateTime Timestamp { get; set; }

        [Column("title", true)]
        public string Title { get; set; }
        [Column("user_name")]
        public string Username { get; set; }

        [Column("web_url", true)]
        public string WebUrl { get; set; }

        [Column("web_title", true)]
        public string WebTitle { get; set; }

        [Column("site_url", true)]
        public string SiteUrl { get; set; }

        [Column("ai_session_id", ColationOverride = "SQL_Latin1_General_CP1_CS_AS")]
        public string AppInsightsSessionId { get; set; }

        [Column("os_name", true)]
        public string OS { get; set; }

        [Column("device_name", true)]
        public string DeviceName { get; set; }

        [Column("browser_name", true)]
        public string BrowserName { get; set; }

        [Column("country_name", true)]
        public string CountryName { get; set; }

        [Column("page_request_id")]
        public string PageRequestId { get; set; }

        [Column("sp_request_duration", true)]
        public int SPRequestDuration { get; set; }

        [Column("page_load_time", true)]
        public string PageLoadTime { get; set; }

        [Column("city_name", true)]
        public string City { get; set; }

        [Column("province_name", true)]
        public string Province { get; set; }

    }
}
