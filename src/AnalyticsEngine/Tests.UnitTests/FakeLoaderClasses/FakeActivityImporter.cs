using Common.Entities.Config;
using DataUtils;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.UnitTests.FakeLoaderClasses
{
    internal class FakeActivityImporter : ActivityImporter<TestActivitySummary>
    {
        public const int MAX_REPORTS_PER_SUMMARY = 100;
        private FakeActivityReportLoader _reportLoader;
        private FakeContentMetaDataLoader _contentMetaDataLoader;
        private FakeActivitySubscriptionManager _activitySubscriptionManager;
        public FakeActivityImporter(int reportsWanted, AppConfig settings, AnalyticsLogger telemetry) : base(settings, telemetry, 1)
        {
            _reportLoader = new FakeActivityReportLoader(1);
            _contentMetaDataLoader = new FakeContentMetaDataLoader(telemetry, settings, reportsWanted);
            _activitySubscriptionManager = new FakeActivitySubscriptionManager();
        }

        public override IActivityReportLoader<TestActivitySummary> ReportLoader => _reportLoader;

        public override ContentMetaDataLoader<TestActivitySummary> ContentMetaDataLoader => _contentMetaDataLoader;

        public override IActivitySubscriptionManager ActivitySubscriptionManager => _activitySubscriptionManager;
    }

    public class TestActivitySummary : BaseActivityReportInfo
    {
    }
}
