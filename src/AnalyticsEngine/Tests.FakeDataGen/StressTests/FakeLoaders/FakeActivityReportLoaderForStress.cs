using System.Threading.Tasks;
using Tests.UnitTests.FakeEntities;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.FakeDataGen.StressTests.FakeLoaders
{
    /// <summary>
    /// Fake activity report loader that generates random activity for stress testing
    /// </summary>
    public class FakeActivityReportLoaderForStress : IActivityReportLoader<ActivityReportInfo>
    {
        private readonly int _reportsPerLoad;

        public FakeActivityReportLoaderForStress(int reportsPerLoad)
        {
            _reportsPerLoad = reportsPerLoad;
        }

        public Task<ActivityReportSet> Load(ActivityReportInfo metadata)
        {
            var reportSet = new WebActivityReportSet
            {
                OriginalMetadata = metadata
            };

            for (int i = 0; i < _reportsPerLoad; i++)
            {
                var log = DataGenerators.GetRandomSharePointLog();
                log.OriginalImportFileContents = "fake";
                reportSet.Add(log);
            }

            return Task.FromResult<ActivityReportSet>(reportSet);
        }
    }
}
