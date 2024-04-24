using Newtonsoft.Json;
using System.Collections.Generic;
using System.Reflection;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{
    public class PageExitEventAppInsightsQueryResult : BaseCustomEventAppInsightsQueryResult
    {
        public PageExitEventAppInsightsQueryResult()
        {
            this.CustomProperties = new PageExitCustomProps();
        }
        public PageExitEventAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic) : base(rowColumnVals, propDic)
        {
            if (string.IsNullOrEmpty(this.CustomDimensionsJson))
            {
                this.CustomProperties = new PageExitCustomProps();
            }
            else
            {
                this.CustomProperties = JsonConvert.DeserializeObject<PageExitCustomProps>(this.CustomDimensionsJson);
            }
        }

        public override bool IsValid => this.CustomProperties?.PageRequestId != null && this.CustomProperties.ActiveTime > 0;

        public PageExitCustomProps CustomProperties { get; set; }

    }
}
