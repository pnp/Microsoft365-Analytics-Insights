using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Reflection;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.ApiImporter
{
    public class PageViewAppInsightsQueryResult : BaseAppInsightsQueryResult
    {
        public PageViewAppInsightsQueryResult() : base()
        {
        }
        public PageViewAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic, ILogger logger) : base(rowColumnVals, propDic)
        {
            if (string.IsNullOrEmpty(this.CustomDimensionsJson))
            {
                this.CustomProperties = new PageViewCustomProps();
            }
            else
            {
                try
                {
                    this.CustomProperties = JsonConvert.DeserializeObject<PageViewCustomProps>(this.CustomDimensionsJson)
                        ?? new PageViewCustomProps();
                }
                catch (JsonException)
                {
                    logger.LogWarning($"Couldn't deserialise page-view custom event data: invalid Json (JsonException)");
                }
                catch (ArgumentException)
                {
                    logger.LogWarning($"Couldn't deserialise page-view custom event data: invalid Json (ArgumentException)");
                }
            }
        }

        #region Props

        [AppInsightsQueryResultProperty("url")]
        public string Url { get; set; }


        [AppInsightsQueryResultProperty("client_Model")]
        public string DeviceModel { get; set; }


        [AppInsightsQueryResultProperty("client_OS")]
        public string ClientOS { get; set; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(Username);

        public override DateTime Timestamp => this.CustomProperties.EventTimestamp.HasValue ? this.CustomProperties.EventTimestamp.Value : base.AppInsightsTimestamp;

        public PageViewCustomProps CustomProperties { get; set; } = new PageViewCustomProps();

        /// <summary>
        /// App Insights' own page-view duration, in milliseconds: the classic <c>pageViews.duration</c> column,
        /// <c>AppPageViews.DurationMs</c> in a Log Analytics workspace.
        /// </summary>
        [AppInsightsQueryResultProperty("duration")]
        public double DurationMS { get; set; }

        /// <summary>
        /// The value stored in <c>hits.page_load_time</c>: <see cref="DurationMS"/> in seconds, or 0 when the view
        /// was not measured.
        /// </summary>
        /// <remarks>
        /// The tracker's own <c>pageLoad</c> property only decides whether the view was measured. It is absent or
        /// "0" when the browser had no load timing to report, as for a SharePoint navigation that doesn't reload
        /// the page. Older builds divided by 10,000, so the rows they wrote hold a tenth of the real time until
        /// <c>src/Repair Page Load Times.sql</c> has been run on the database.
        /// </remarks>
        public double PageLoadInSeconds
        {
            get
            {
                var pageLoad = this.CustomProperties?.PageLoad;
                if (string.IsNullOrEmpty(pageLoad) || pageLoad == "0")
                {
                    return 0;
                }

                return Math.Round(this.DurationMS / 1000d, 2);
            }
        }

        [AppInsightsQueryResultProperty("client_City")]
        public string City { get; set; }

        [AppInsightsQueryResultProperty("client_StateOrProvince")]
        public string StateOrProvince { get; set; }

        [AppInsightsQueryResultProperty("client_CountryOrRegion")]
        public string CountryOrRegion { get; set; }

        [AppInsightsQueryResultProperty("client_Browser")]
        public string Browser { get; set; }

        public bool IsValid { get; set; }

        #endregion
    }
}
