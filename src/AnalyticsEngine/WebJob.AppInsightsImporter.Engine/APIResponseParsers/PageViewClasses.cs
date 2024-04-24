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
        public PageViewAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic, ILogger debugTracer) : base(rowColumnVals, propDic)
        {
            if (string.IsNullOrEmpty(this.CustomDimensionsJson))
            {
                this.CustomProperties = new PageViewCustomProps();
            }
            else
            {
                try
                {
                    this.CustomProperties = JsonConvert.DeserializeObject<PageViewCustomProps>(this.CustomDimensionsJson);
                }
                catch (JsonException)
                {
                    debugTracer.LogWarning($"Couldn't deserialise page-view custom event data: invalid Json (JsonException)");
                }
                catch (ArgumentException)
                {
                    debugTracer.LogWarning($"Couldn't deserialise page-view custom event data: invalid Json (ArgumentException)");
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

        [AppInsightsQueryResultProperty("duration")]
        public double DurationMS { get; set; }

        public double PageLoadInSeconds
        {
            get
            {
                double pageLoadTimeInSeconds = 0;
                if (!string.IsNullOrEmpty(this.CustomProperties.PageLoad) && this.CustomProperties.PageLoad != "0")
                {
                    double.TryParse(this.CustomProperties.PageLoad, out pageLoadTimeInSeconds);

                    // Convert hit values 
                    const int ONE_THOUSAND = 1000;

                    pageLoadTimeInSeconds = Math.Round(this.DurationMS / (double)(ONE_THOUSAND * 10), 2);
                }

                return pageLoadTimeInSeconds;
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
