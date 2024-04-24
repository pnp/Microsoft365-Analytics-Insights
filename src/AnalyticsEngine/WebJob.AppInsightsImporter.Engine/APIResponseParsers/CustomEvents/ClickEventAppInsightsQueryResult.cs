using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{

    public class ClickEventAppInsightsQueryResult : BaseCustomEventAppInsightsQueryResult
    {
        public ClickEventAppInsightsQueryResult() { }
        public ClickEventAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic) : base(rowColumnVals, propDic)
        {
            if (string.IsNullOrEmpty(this.CustomDimensionsJson))
            {
                this.CustomProperties = new ClickCustomProps();
            }
            else
            {
                this.CustomProperties = JsonConvert.DeserializeObject<ClickCustomProps>(this.CustomDimensionsJson);
            }
        }

        public ClickCustomProps CustomProperties { get; set; } = new ClickCustomProps();

        public override DateTime Timestamp => this.CustomProperties.EventTimestamp.HasValue ? this.CustomProperties.EventTimestamp.Value : base.AppInsightsTimestamp;

        public override bool IsValid => this.CustomProperties?.PageRequestId != Guid.Empty &&
            !string.IsNullOrEmpty(CustomProperties?.LinkText) &&
            this.Timestamp > DateTime.MinValue;

    }
}
