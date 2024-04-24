using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests.FakeLoaderClasses
{
    internal class FakeActivityReportLoader : IActivityReportLoader<TestActivitySummary>
    {
        private int _reportsCount;

        public FakeActivityReportLoader(int reportsCount)
        {
            _reportsCount = reportsCount;
        }

        public Task<WebActivityReportSet> Load(ActivityReportInfo metadata)
        {
            return Task.FromResult(new WebActivityReportSet(metadata));
        }

        public Task<ActivityReportSet> Load(TestActivitySummary metadata)
        {
            var reports = new WebActivityReportSet();
            for (int i = 0; i < _reportsCount; i++)
            {
                reports.Add(Tests.UnitTests.FakeEntities.DataGenerators.GetRandomSharePointLog());
            }
            return Task.FromResult((ActivityReportSet)reports);
        }
    }
}
