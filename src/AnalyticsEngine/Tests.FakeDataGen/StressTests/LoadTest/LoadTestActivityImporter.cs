using Common.Entities.Config;
using DataUtils;
using Tests.FakeDataGen.StressTests.FakeLoaders;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.FakeDataGen.StressTests.LoadTest
{
    /// <summary>
    /// Audit-import driver for the load test. Identical to
    /// <see cref="FakeActivityImporterForStress"/> but lets the harness inject an
    /// <see cref="AppConfig"/> whose <see cref="AppConfig.MaxAuditReportLoadConcurrency"/> has been
    /// set to the aggressiveness preset under test, so the real
    /// <c>ActivityImporter.LoadFullReportsFromActivityApi</c> path fans out at exactly that many
    /// threads (the lever PR #162 added).
    /// </summary>
    public class LoadTestActivityImporter : ActivityImporter<ActivityReportInfo>
    {
        private readonly FakeActivityReportLoaderForStress _reportLoader;
        private readonly FakeContentMetaDataLoaderForStress _contentMetaDataLoader;
        private readonly FakeActivitySubscriptionManagerForStress _activitySubscriptionManager;

        public LoadTestActivityImporter(AppConfig config, AnalyticsLogger telemetry, int maxSavesPerBatch,
            int reportsPerLoad, int reportsPerTimeSlot, int timeSlotCount)
            : base(config, telemetry, maxSavesPerBatch)
        {
            _reportLoader = new FakeActivityReportLoaderForStress(reportsPerLoad);
            _contentMetaDataLoader = new FakeContentMetaDataLoaderForStress(reportsPerTimeSlot, timeSlotCount);
            _activitySubscriptionManager = new FakeActivitySubscriptionManagerForStress();
        }

        public override IActivityReportLoader<ActivityReportInfo> ReportLoader { get { return _reportLoader; } }

        public override ContentMetaDataLoader<ActivityReportInfo> ContentMetaDataLoader { get { return _contentMetaDataLoader; } }

        public override IActivitySubscriptionManager ActivitySubscriptionManager { get { return _activitySubscriptionManager; } }
    }
}
