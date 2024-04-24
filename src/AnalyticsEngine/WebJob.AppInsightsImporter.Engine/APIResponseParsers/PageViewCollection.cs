using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace WebJob.AppInsightsImporter.Engine.ApiImporter
{
    public class PageViewCollection : AppInsightsQueryResultCollection<PageViewAppInsightsQueryResult>
    {

        public PageViewCollection() { }

        public PageViewCollection(AppInsightsTable fromTable, DateTime fromWhen, ILogger debugTracer) : base(fromTable, fromWhen, debugTracer)
        {
        }

        protected override PageViewAppInsightsQueryResult Build(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic)
        {
            return new PageViewAppInsightsQueryResult(rowColumnVals, propDic, _debugTracer);
        }
    }
}
