using Common.Entities.Config;
using DataUtils;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Activity importer that drives the REAL save pipeline (via a real
    /// <c>ActivityReportSqlPersistenceManager</c> passed to <c>LoadReportsAndSave</c>) with fully
    /// deterministic, DB-backed synthetic data - so the COLD/WARM scenarios are comparable and the
    /// blob-checkpoint optimisation can be measured.
    /// </summary>
    public class DeterministicActivityImporterForStress : ActivityImporter<ActivityReportInfo>
    {
        private readonly DeterministicActivityReportLoaderForStress _reportLoader;
        private readonly DeterministicContentMetaDataLoaderForStress _contentMetaDataLoader;
        private readonly FakeActivitySubscriptionManager _activitySubscriptionManager;

        public DeterministicActivityImporterForStress(AppConfig settings, AnalyticsLogger logger,
            int maxSavesPerBatch, StressAuditDataConfig dataConfig, IProcessedBlobStore processedBlobStore = null)
            : base(settings, logger, maxSavesPerBatch, processedBlobStore, dataConfig.MaxConcurrentSaves)
        {
            _reportLoader = new DeterministicActivityReportLoaderForStress(dataConfig);
            _contentMetaDataLoader = new DeterministicContentMetaDataLoaderForStress(dataConfig, logger, settings);
            _activitySubscriptionManager = new FakeActivitySubscriptionManager();
        }

        public override IActivityReportLoader<ActivityReportInfo> ReportLoader => _reportLoader;

        public override ContentMetaDataLoader<ActivityReportInfo> ContentMetaDataLoader => _contentMetaDataLoader;

        public override IActivitySubscriptionManager ActivitySubscriptionManager => _activitySubscriptionManager;

        /// <summary>Number of content blobs actually loaded (downloaded) this run.</summary>
        public long BlobsLoaded => _reportLoader.BlobsLoaded;

        /// <summary>Number of events actually generated (from loaded blobs) this run.</summary>
        public long EventsGenerated => _reportLoader.EventsGenerated;
    }
}
