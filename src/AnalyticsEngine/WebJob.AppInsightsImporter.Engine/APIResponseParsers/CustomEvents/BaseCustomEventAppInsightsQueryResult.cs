using System.Collections.Generic;
using System.Reflection;
using WebJob.AppInsightsImporter.Engine.ApiImporter;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{
    public abstract class BaseCustomEventAppInsightsQueryResult : BaseAppInsightsQueryResult
    {
        public BaseCustomEventAppInsightsQueryResult()
        {
        }
        public BaseCustomEventAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic) : base(rowColumnVals, propDic)
        {
        }

        [AppInsightsQueryResultProperty("name")]
        public string Name { get; set; }

        public abstract bool IsValid { get; }

    }

    public class NameOnlyCustomEventAppInsightsQueryResult : BaseCustomEventAppInsightsQueryResult
    {

        public override bool IsValid => !string.IsNullOrEmpty(this.Name);

        public NameOnlyCustomEventAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic) : base(rowColumnVals, propDic)
        {
        }
    }
}
