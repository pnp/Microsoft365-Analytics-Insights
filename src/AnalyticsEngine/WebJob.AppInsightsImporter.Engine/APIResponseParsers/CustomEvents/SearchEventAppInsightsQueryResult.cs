using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{
    public class SearchEventAppInsightsQueryResult : BaseCustomEventAppInsightsQueryResult
    {
        public SearchEventAppInsightsQueryResult() { }
        public SearchEventAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic) : base(rowColumnVals, propDic)
        {
            if (string.IsNullOrEmpty(this.CustomDimensionsJson))
            {
                this.CustomProperties = new SearchCustomProps();
            }
            else
            {
                this.CustomProperties = JsonConvert.DeserializeObject<SearchCustomProps>(this.CustomDimensionsJson);
            }
        }
        public override bool IsValid => !string.IsNullOrEmpty(this.CustomProperties?.SessionId) && !string.IsNullOrEmpty(this.CustomProperties?.SearchText);

        public override DateTime Timestamp => this.CustomProperties.EventTimestamp.HasValue ? this.CustomProperties.EventTimestamp.Value : base.AppInsightsTimestamp;

        public SearchCustomProps CustomProperties { get; set; } = new SearchCustomProps();

    }
}
