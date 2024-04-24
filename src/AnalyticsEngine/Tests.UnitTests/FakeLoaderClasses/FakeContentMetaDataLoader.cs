using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace Tests.UnitTests.FakeLoaderClasses
{
    internal class FakeContentMetaDataLoader : ContentMetaDataLoader<TestActivitySummary>
    {
        private readonly int _reportsSummaryCountWanted;

        public FakeContentMetaDataLoader(ILogger debugTracer, AppConfig settings, int reportsCountWanted) : base(debugTracer, settings)
        {
            _reportsSummaryCountWanted = reportsCountWanted;
        }

        protected override Task<List<TestActivitySummary>> LoadAllActivityReports(string auditContentType, TimePeriod chunk, int batchId)
        {
            var list = new List<TestActivitySummary>();
            for (int i = 0; i < _reportsSummaryCountWanted; i++)
            {
                list.Add(new TestActivitySummary { Created = System.DateTime.Now.AddSeconds(i) });
            }
            return Task.FromResult(list);
        }
    }
}
