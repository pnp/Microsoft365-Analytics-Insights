using DataUtils;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.StressTesting.FakeLoaders
{
    /// <summary>
    /// Fake activity importer for stress testing with configurable load
    /// </summary>
    public class FakeActivityImporterForStress : ActivityImporter<ActivityReportInfo>
    {
        private FakeActivityReportLoaderForStress _reportLoader;
        private FakeContentMetaDataLoaderForStress _contentMetaDataLoader;
        private FakeActivitySubscriptionManagerForStress _activitySubscriptionManager;

        public FakeActivityImporterForStress(AnalyticsLogger telemetry, int maxSavesPerBatch,
            int reportsPerLoad, int reportsPerTimeSlot, int timeSlotCount)
            : base(FakeAppConfigFactory.Create(), telemetry, maxSavesPerBatch)
        {
            _reportLoader = new FakeActivityReportLoaderForStress(reportsPerLoad);
            _contentMetaDataLoader = new FakeContentMetaDataLoaderForStress(reportsPerTimeSlot, timeSlotCount);
            _activitySubscriptionManager = new FakeActivitySubscriptionManagerForStress();
        }

        public override IActivityReportLoader<ActivityReportInfo> ReportLoader => _reportLoader;

        public override ContentMetaDataLoader<ActivityReportInfo> ContentMetaDataLoader => _contentMetaDataLoader;

        public override IActivitySubscriptionManager ActivitySubscriptionManager => _activitySubscriptionManager;
    }
}
