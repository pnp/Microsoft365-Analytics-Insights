using Common.Entities;
using Common.Entities.ActivityReports;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Entity;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// Report row for the in-memory daily-loader tests. Derives from the real
    /// <see cref="UserRelatedAbstractUsageActivity"/> so the lookup id is the MAPPED <c>user_id</c> column
    /// (as in production) rather than an unmapped property, which is what makes the dirty check
    /// representative. See issue #375.
    /// </summary>
    [Table("fake_user_activity_log")]
    public class FakeUserUsageActivityLog : UserRelatedAbstractUsageActivity
    {
        [Column("thing_count")]
        public int ThingCount { get; set; }
    }

    /// <summary>A Graph report page for <see cref="FakeUserUsageActivityLog"/>.</summary>
    public class FakeUserActivityDetail : AbstractUserActivityUserDetailWithUpn
    {
        [JsonProperty("thingCount")]
        public int ThingCount { get; set; }
    }

    /// <summary>
    /// Concrete <see cref="AbstractDailyActivityLoader{,,,}"/> whose Graph paging and storage are both
    /// supplied by the test, so the entire save loop - the lookup/scope filters, the (date, lookup) upsert,
    /// the LastActivityDate parse, the dirty check, the batch boundary and the per-day release - runs with
    /// zero SQL Server and zero HTTP. See issue #375.
    ///
    /// <para>
    /// <c>GetTable</c> returns null and the context passed to the base is null; both are safe ONLY because
    /// callers set <c>ReportStore</c> (and, where relevant, <c>StorageInspector</c>), which short-circuits
    /// the <c>?? new Sql...(db)</c> defaults so neither adapter is ever constructed.
    /// </para>
    /// </summary>
    public class InMemoryDailyActivityLoader
        : AbstractUserDailyActivityLoader<FakeUserUsageActivityLog, FakeUserActivityDetail>
    {
        public InMemoryDailyActivityLoader(ILogger logger)
            : base(null, new MockUserGroupsCache(null, logger), new UserGroupsFilterModel(null), logger)
        {
        }

        /// <summary>Canned Graph pages, keyed by the DATE (time component ignored) they answer for.</summary>
        public Dictionary<DateTime, List<FakeUserActivityDetail>> PagesByDate { get; }
            = new Dictionary<DateTime, List<FakeUserActivityDetail>>();

        /// <summary>Every date actually requested from "Graph", in order.</summary>
        public List<DateTime> GraphRequests { get; } = new List<DateTime>();

        /// <summary>Optional scope filter, standing in for the Entra group-membership check.</summary>
        public Func<string, bool> InScopeRule { get; set; }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/thisReportDoesNotExist";

        public override DbSet<FakeUserUsageActivityLog> GetTable(AnalyticsEntitiesContext context) => null;

        protected override Task<List<FakeUserActivityDetail>> LoadReportPageForDateFromGraph(DateTime date)
        {
            GraphRequests.Add(date.Date);
            PagesByDate.TryGetValue(date.Date, out var page);
            return Task.FromResult(page ?? new List<FakeUserActivityDetail>());
        }

        protected override Task<bool> IdInScope(string lookupId)
            => Task.FromResult(InScopeRule == null || InScopeRule(lookupId));

        protected override void PopulateReportSpecificMetadata(FakeUserUsageActivityLog todaysLog, FakeUserActivityDetail page)
        {
            todaysLog.ThingCount = page.ThingCount;
        }

        protected override long CountActivity(FakeUserActivityDetail activityPage) => activityPage.ThingCount;
    }
}
