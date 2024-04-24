using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;

namespace Tests.UnitTests.FakeLoaderClasses
{
    internal class FakeSPSiteIdToUrlCache : SPSiteIdToUrlCache
    {
        private readonly string _fakeUrlForLoadSiteResult;

        public FakeSPSiteIdToUrlCache(Common.Entities.AnalyticsEntitiesContext db, ILogger debugTracer, string fakeUrlForLoadSiteResult) : base(db, debugTracer)
        {
            _fakeUrlForLoadSiteResult = fakeUrlForLoadSiteResult;
        }

        public override Task<Site> LoadSite(string id)
        {
            return Task.FromResult(new Site { WebUrl = _fakeUrlForLoadSiteResult, Id = id });
        }
    }

    internal class MultiPageFakeWeeklyUsageReportLoader : AbstractAggregateWeeklyUsageReportLoader<FakeStats>
    {
        bool _hasSaved = false;
        int _loadCount = 0;
        public MultiPageFakeWeeklyUsageReportLoader(ILogger telemetry) : base(telemetry) { }
        public override string ReportGraphURL => "fake URL";

        public override Task<AggregateResourceUsageDetail<FakeStats>> LoadReportDataForUrl(string requestUrl)
        {
            const string FAKE_PAGE2_URL = "fake page2 URL";
            _loadCount++;
            var reportPage = new AggregateResourceUsageDetail<FakeStats>();
            if (_loadCount == 1)
            {
                // First time we load, we return a report that's not a sunday
                reportPage.Stats = new List<FakeStats> { new FakeStats { RandoId = "1", ReportRefreshDateString = "2024-02-22" } };
                reportPage.NextLink = FAKE_PAGE2_URL;
            }
            else
            {
                if (requestUrl != FAKE_PAGE2_URL)
                {
                    throw new Exception("Expected to load page 2");
                }

                // Second time we load, we return a report that's a sunday
                reportPage.Stats = new List<FakeStats> { new FakeStats { RandoId = "1", ReportRefreshDateString = "2024-02-25" } };
                reportPage.NextLink = null;
            }
            return Task.FromResult(reportPage);
        }

        protected override Task<DateTime?> GetLastStoredResultFor(FakeStats item)
        {
            if (_hasSaved)
            {
                return Task.FromResult<DateTime?>(DateTime.ParseExact("03-03-2024", "dd-MM-yyyy", null));       // A sunday later than last saved date
            }
            else
                return Task.FromResult<DateTime?>(null);
        }

        protected override Task CommitAllChanges()
        {
            _hasSaved = true;
            return Task.CompletedTask;
        }

        public override string ReportName => "Fake Multi-page Usage";
        protected override Task AddItemToSaveList(FakeStats item)
        {
            // Do nothing
            return Task.CompletedTask;
        }

    }
    internal class SundayOrNotFakeWeeklyUsageReportLoader : AbstractAggregateWeeklyUsageReportLoader<FakeStats>
    {
        bool _hasSaved = false;
        int _loadCount = 0;
        public SundayOrNotFakeWeeklyUsageReportLoader(ILogger telemetry) : base(telemetry) { }
        public override string ReportGraphURL => "fake pagable URL";

        public override Task<AggregateResourceUsageDetail<FakeStats>> LoadReportDataForUrl(string requestUrl)
        {
            _loadCount++;
            var reportPage = new AggregateResourceUsageDetail<FakeStats>();
            if (_loadCount == 1)
            {
                // First time we load, we return a report that's not a sunday
                reportPage.Stats = new List<FakeStats> { new FakeStats { RandoId = "1", ReportRefreshDateString = "2024-02-22" } };
            }
            else
            {
                // Second time we load, we return a report that's a sunday
                reportPage.Stats = new List<FakeStats> { new FakeStats { RandoId = "1", ReportRefreshDateString = "2024-02-25" } };
            }
            return Task.FromResult(reportPage);
        }
        protected override Task<DateTime?> GetLastStoredResultFor(FakeStats item)
        {
            if (_hasSaved)
            {
                return Task.FromResult<DateTime?>(DateTime.ParseExact("03-03-2024", "dd-MM-yyyy", null));       // A sunday later than last saved date
            }
            else
                return Task.FromResult<DateTime?>(null);
        }

        protected override Task CommitAllChanges()
        {
            _hasSaved = true;
            return Task.CompletedTask;
        }

        public override string ReportName => "Fake Usage";
        protected override Task AddItemToSaveList(FakeStats item)
        {
            // Do nothing
            return Task.CompletedTask;
        }
    }

    internal class FakeStats : BaseAggregateItemStats
    {
        public string RandoId { get; set; }
        public override string OfficeUniqueIdField => RandoId;
    }
}
