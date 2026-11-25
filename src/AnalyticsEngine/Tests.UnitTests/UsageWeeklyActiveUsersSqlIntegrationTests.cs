extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.Entities.Teams;
using Common.Entities.State;
using Common.Entities.UserFilters;
using Common.Entities.UserScope.Purge;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration = Common.Entities.Migrations.Configuration;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs the Reports page's weekly active-user query (<c>api/Reports/usage</c>) against a real, throwaway
    /// database seeded the way Graph's per-date usage reports actually arrive.
    /// </summary>
    /// <remarks>
    /// The importer asks for each report one date at a time, and Microsoft documents that form as returning the
    /// users who performed activity on that date. So a date's rows are POSITIVE-ONLY: the seed below never writes
    /// a row for someone who did nothing that day. The week's figure must therefore be everyone with a row on ANY
    /// of its days - the query used to read one date per week (the last one), which counted the people active on
    /// that one day as the whole week's. All seed data is synthetic.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class UsageWeeklyActiveUsersSqlIntegrationTests
    {
        private static string _database;
        private static string _connectionString;

        private const string MasterConnection =
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=true;TrustServerCertificate=True";

        /// <summary>Every day imported; one person active on Monday only, one on Friday only, one on Sunday (and the days between).</summary>
        private static readonly DateTime CompleteWeek = new DateTime(2026, 3, 2);

        /// <summary>Wednesday's report never imported.</summary>
        private static readonly DateTime WeekMissingADay = new DateTime(2026, 3, 9);

        /// <summary>No report imported on any day.</summary>
        private static readonly DateTime WeekNeverImported = new DateTime(2026, 3, 16);

        /// <summary>Every day imported; one person active on all seven.</summary>
        private static readonly DateTime EverydayWeek = new DateTime(2026, 3, 23);

        private static int _mondayOnly;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            Assert.AreEqual(DayOfWeek.Monday, CompleteWeek.DayOfWeek, "Weeks must start on Mondays to match the week spine.");

            _database = "UsageWeeklyActive_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _connectionString =
                $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_database};Integrated Security=true;MultipleActiveResultSets=True;TrustServerCertificate=True";

            Execute(MasterConnection, ScratchDatabase.CreateDatabaseSql(_database));

            // The full migration chain, so the query runs against the schema - and IX_date - customers actually get.
            new DbMigrator(new Configuration
            {
                TargetDatabase = new System.Data.Entity.Infrastructure.DbConnectionInfo(_connectionString, "Microsoft.Data.SqlClient"),
            }).Update();

            Seed();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            if (_database == null) return;
            SqlConnection.ClearAllPools();
            Execute(MasterConnection,
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
        }

        private static void Seed()
        {
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var monday = new User { UserPrincipalName = "monday.only@contoso.com", AccountEnabled = true };
                var friday = new User { UserPrincipalName = "friday.only@contoso.com", AccountEnabled = true };
                var sunday = new User { UserPrincipalName = "sunday@contoso.com", AccountEnabled = true };
                var everyday = new User { UserPrincipalName = "everyday@contoso.com", AccountEnabled = true };
                db.LicenseTypes.Add(new LicenseType { Name = "Contoso test licence" });
                db.users.AddRange(new[] { monday, friday, sunday, everyday });
                db.SaveChanges();
                _mondayOnly = monday.ID;

                // The complete week: Monday's and Friday's reports list one person each; the third person is active on
                // every other day, Sunday included, so every day of the week has a report and three people were active.
                Active(db, monday, CompleteWeek);
                Active(db, friday, CompleteWeek.AddDays(4));
                foreach (var offset in new[] { 1, 2, 3, 5, 6 }) Active(db, sunday, CompleteWeek.AddDays(offset).AddHours(12));

                // Wednesday's report is missing: there is no row for anyone that day.
                foreach (var offset in new[] { 0, 1, 3, 4, 5, 6 }) Active(db, everyday, WeekMissingADay.AddDays(offset));

                // Nothing at all for WeekNeverImported.

                for (var offset = 0; offset < 7; offset++) Active(db, everyday, EverydayWeek.AddDays(offset));

                db.SaveChanges();
            }
        }

        private static void Active(AnalyticsEntitiesContext db, User user, DateTime day)
        {
            db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
            {
                UserID = user.ID,
                Date = day,
                LastActivityDate = day,
                PrivateChatMessageCount = 1,
            });
        }

        [TestMethod]
        public void PeopleActiveOnDifferentDaysOfAWeekAreAllCounted()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            // Read from its Sunday alone, this week used to count one person.
            Assert.AreEqual(3d, weeks[CompleteWeek].Value,
                "Monday-only, Friday-only and Sunday users are three weekly active users, not one.");
            Assert.AreEqual(7, weeks[CompleteWeek].ImportedDays);
            Assert.IsFalse(weeks[CompleteWeek].Incomplete);
        }

        [TestMethod]
        public void SomeoneActiveEveryDayIsCountedOnce()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            Assert.AreEqual(1d, weeks[EverydayWeek].Value);
            Assert.AreEqual(7, weeks[EverydayWeek].ImportedDays);
        }

        [TestMethod]
        public void AWeekMissingADaysReportIsReportedAsIncomplete()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            var week = weeks[WeekMissingADay];
            Assert.AreEqual(6, week.ImportedDays, "Wednesday has no report, so the week is incomplete.");
            Assert.AreEqual(7, week.ExpectedDays);
            Assert.IsTrue(week.Incomplete);
            Assert.AreEqual(1d, week.Value, "The week is still counted from the days that were imported.");
        }

        [TestMethod]
        public void ANoRowsDayWithConfirmedEmptyProvenanceIsNotReportedAsMissing()
        {
            var outcomes = Enumerable.Range(0, 7).ToDictionary(
                day => WeekMissingADay.AddDays(day),
                day => day == 2 ? UsageReportCoverageStore.Empty : UsageReportCoverageStore.Complete);

            var week = Run("dbo.teams_user_activity_log", outcomes: outcomes)[WeekMissingADay];

            Assert.AreEqual(6, week.ImportedDays);
            Assert.IsFalse(week.Incomplete,
                "The empty Wednesday report is confirmed coverage, not a missed import.");
            Assert.AreEqual(1d, week.Value, "Rows from the other days remain charted.");
        }

        [TestMethod]
        public void AWeekWithNoReportAtAllIsAGapNotAZero()
        {
            var weeks = Run("dbo.teams_user_activity_log");

            Assert.IsFalse(weeks.ContainsKey(WeekNeverImported),
                "A week with no report on any day is unknown, so it must come back as no row (a gap), never as zero.");
        }

        [TestMethod]
        public void AWeekWithSevenConfirmedEmptyReportsIsARealZero()
        {
            var outcomes = Enumerable.Range(0, 7).ToDictionary(
                day => WeekNeverImported.AddDays(day),
                _ => UsageReportCoverageStore.Empty);

            var week = Run("dbo.teams_user_activity_log", outcomes: outcomes)[WeekNeverImported];

            Assert.AreEqual(0d, week.Value);
            Assert.AreEqual(0, week.ImportedDays);
            Assert.IsFalse(week.Incomplete);
        }

        [TestMethod]
        public void OldEmptyCertificateWithSqlRowsKeepsTheObservationButCannotVerifyTheWeek()
        {
            var outcomes = Enumerable.Range(0, 7).ToDictionary(day => CompleteWeek.AddDays(day),
                _ => UsageReportCoverageStore.Complete);
            outcomes[CompleteWeek.AddDays(6)] = UsageReportCoverageStore.Empty;

            // Sunday's contradictory row is outside the reader's filter. Coverage is tenant-wide;
            // the filtered Monday-only count is still a lower bound, just like an explicit partial.
            var scope = ReportUserScope.ForUsers(new[] { _mondayOnly });
            var week = Run("dbo.teams_user_activity_log", scope, outcomes, CompleteWeek, CompleteWeek)[CompleteWeek];
            Assert.AreEqual(1d, week.Value);
            Assert.IsTrue(week.Incomplete);
            outcomes[CompleteWeek.AddDays(6)] = UsageReportCoverageStore.Complete;
            Assert.IsFalse(Run("dbo.teams_user_activity_log", scope, outcomes, CompleteWeek, CompleteWeek)[CompleteWeek].Incomplete,
                "A row at noon on the last settled date is inside the report window.");
        }

        [TestMethod]
        public async Task HistoricalAdoptionChecksCertificatesAgainstTenantRowsAtBothWindowEdges()
        {
            foreach (var days in new[] { 90, 180, 366 })
            {
                var from = new DateTime(2000 + days / 30, 2, 1, 0, 0, 0, DateTimeKind.Utc);
                var through = from.AddDays(days - 1);
                using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
                {
                    var inside = db.users.Find(_mondayOnly);
                    foreach (var date in new[] { from.AddDays(-1), from, through.AddHours(12), through.AddDays(1) })
                        Active(db, inside, date);
                    db.SaveChanges();
                }
                var store = new UsageReportCoverageStore(new InMemoryKeyValueStore(), true);
                foreach (var source in UsageReportCoverageStore.Sources)
                    for (var day = from; day <= through; day = day.AddDays(1))
                        await store.CompleteDayAsync(source, day, true, true, !(source == "teams" && day == through),
                            day.AddDays(4), 3);
                var options = CopilotAdoptionOptions.Default;
                options.UsesExplicitDates = true;
                options.FromUtc = from;
                options.ToUtc = through;
                options.ToExclusiveUtc = through.AddDays(1);
                var service = new CopilotAdoptionService(options,
                    new ConnectionStringAnalyticsDbContextFactory(_connectionString), usageReportCoverage: store);

                var analysis = await service.AnalyseAsync();
                var sources = analysis.Summary.DataSources;
                var teams = sources.WorkloadCoverage.Single(c => c.Source == "teams");
                Assert.IsFalse(sources.M365UsageReportsAvailable);
                Assert.AreEqual("partial", teams.Status);
                Assert.AreEqual(days - 1, teams.VerifiedDays, "A non-empty day at the inclusive upper edge still verifies.");
                Assert.AreEqual(days - 2, teams.EmptyDays);
                Assert.AreEqual(1, teams.PartialDays);
                Assert.IsTrue(sources.WorkloadCoverage.Where(c => c.Source != "teams").All(c => c.Status == "empty"));
                Assert.IsTrue(analysis.Summary.WarningDetails.Any(w => w.Key == CopilotAdoptionWarningKeys.WorkloadCoverageUnverified));
                if (days == 90)
                    StringAssert.Contains(CopilotAdoptionWorkbookText.Of(CopilotAdoptionWorkbook.Build(analysis)),
                        "89 of 90 days verified; 88 successfully empty days");

                await store.CompleteDayAsync("teams", from, true, true, false, from.AddDays(4), 3);
                var verified = await service.AnalyseAsync();
                Assert.IsTrue(verified.Summary.DataSources.M365UsageReportsAvailable);
                Assert.IsFalse(verified.Summary.WarningDetails.Any(w => w.Key == CopilotAdoptionWarningKeys.WorkloadCoverageUnverified));
            }
        }

        [TestMethod]
        public async Task ScopePurgeDoesNotTurnDeletedActivityIntoAConfirmedEmptyWeek()
        {
            var first = CompleteWeek.AddDays(-56);
            var mixed = first.AddDays(7);
            var intact = mixed.AddDays(7);
            var empty = intact.AddDays(7);
            var never = empty.AddDays(7);
            var storage = new FirstRevocationFails();
            var store = new UsageReportCoverageStore(storage, true);
            int removed;
            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var outside = db.users.Add(new User { UserPrincipalName = "outside@contoso.example" });
                db.SaveChanges();
                removed = outside.ID;
                var inside = db.users.Find(_mondayOnly);
                Active(db, outside, first);
                Active(db, outside, mixed);
                Active(db, inside, mixed);
                Active(db, inside, intact);
                for (var i = 0; i < 7; i++)
                    db.YammerUserActivityLogs.Add(new YammerUserActivityLog
                        { UserID = inside.ID, Date = mixed.AddDays(i), LastActivityDate = mixed.AddDays(i), PostedCount = 1 });
                db.YammerUserActivityLogs.Add(new YammerUserActivityLog { UserID = outside.ID, Date = mixed, LastActivityDate = mixed, PostedCount = 1 });
                db.SaveChanges();
            }
            for (var day = first; day < never; day = day.AddDays(1))
                await store.CompleteDayAsync("teams", day, true, true,
                    day != first && day != mixed && day != intact, day.AddDays(4), 3);

            async Task<Dictionary<DateTime, Week>> ReadWeeks()
            {
                var outcomes = await store.ReadDailyOutcomesAsync(first, never.AddDays(6));
                return Run("dbo.teams_user_activity_log", outcomes: outcomes["teams"], from: first, through: never);
            }
            var before = await ReadWeeks();
            Assert.AreEqual(1d, before[first].Value);
            Assert.AreEqual(2d, before[mixed].Value);
            Assert.IsFalse(before[mixed].Incomplete);
            var vivaBefore = (await store.ReadWeeklyChartOutcomesAsync(mixed, mixed.AddDays(6)))["yammer"];
            var originalViva = Run("dbo.yammer_user_activity_log", outcomes: vivaBefore, from: mixed, through: mixed)[mixed];
            Assert.AreEqual(2d, originalViva.Value);
            Assert.IsFalse(originalViva.Incomplete, "Viva Engage still uses its historical row-presence fallback before a purge.");

            var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), true);
            UserScopePurgeEngine Engine() => new UserScopePurgeEngine(new UserScopePurgeDatabase(_connectionString), state, null, store)
                { CandidatesOverride = new[] { removed } };
            var failed = await state.CreateAsync("admin@contoso.example", null, 1, 1);
            Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await Engine().RunAsync(failed.Id, null));
            Assert.AreEqual(UserScopePurgeErrorCodes.StateUnavailable, (await state.GetAsync(failed.Id)).ErrorCode);
            var retained = await ReadWeeks();
            Assert.AreEqual(2d, retained[mixed].Value);
            Assert.AreEqual(1d, retained[first].Value, "No selected report fact was deleted before its revocation succeeded.");

            var job = await state.CreateAsync("admin@contoso.example", null, 1, 1);
            using (var stopping = new CancellationTokenSource())
            {
                var firstRun = Engine();
                firstRun.BatchSizeOverride = 1;
                firstRun.AfterWindow = (index, window) =>
                {
                    if (UserScopePurgePlan.Steps[index - 1].Table == "teams_user_activity_log") stopping.Cancel();
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await firstRun.RunAsync(job.Id, null, stopToken: stopping.Token));
                Assert.AreEqual(1L, firstRun.Job.RowsAffected["teams_user_activity_log"]);
            }
            var purge = Engine();
            var options = CopilotAdoptionOptions.Default;
            options.UsesExplicitDates = true;
            options.FromUtc = first;
            options.ToUtc = never.AddDays(-1);
            options.ToExclusiveUtc = never;
            var report = await new PurgeBeforeOpportunityRead(options, store, async () =>
                Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await purge.RunAsync(job.Id, null))).AnalyseAsync();
            Assert.AreEqual(2, report.Summary.DataSources.WorkloadCoverage.Single(c => c.Source == "teams").ScopeUnverifiedDays,
                "A purge during an analysis cannot combine the reduced observations with pre-purge coverage.");
            Assert.AreEqual(1, purge.Job.UsersDeleted);
            var after = await ReadWeeks();
            Assert.IsFalse(after.ContainsKey(first), "Deleting the day's only activity must not manufacture a zero.");
            Assert.AreEqual(1d, after[mixed].Value);
            Assert.IsTrue(after[mixed].Incomplete, "Surviving people's activity is a lower bound.");
            Assert.AreEqual(1d, after[intact].Value);
            Assert.IsFalse(after[intact].Incomplete);
            Assert.AreEqual(0d, after[empty].Value);
            Assert.IsFalse(after[empty].Incomplete);
            Assert.IsFalse(after.ContainsKey(never));
            var vivaAfter = (await store.ReadWeeklyChartOutcomesAsync(mixed, mixed.AddDays(6)))["yammer"];
            var viva = Run("dbo.yammer_user_activity_log", outcomes: vivaAfter, from: mixed, through: mixed)[mixed];
            Assert.AreEqual(1d, viva.Value);
            Assert.IsTrue(viva.Incomplete, "The legacy-only chart must not certify the reduced count either.");
        }

        private sealed class PurgeBeforeOpportunityRead : CopilotAdoptionService
        {
            private readonly Func<Task> _run;
            public PurgeBeforeOpportunityRead(CopilotAdoptionOptions options, UsageReportCoverageStore store, Func<Task> run)
                : base(options, new ConnectionStringAnalyticsDbContextFactory(_connectionString), usageReportCoverage: store) => _run = run;

            protected override async Task<List<T>> QueryAsync<T>(string sql, CancellationToken token, params SqlParameter[] parameters)
            {
                if (typeof(T) == typeof(UnlicensedUserSignalRow)) await _run();
                return await base.QueryAsync<T>(sql, token, parameters);
            }
        }

        private sealed class FirstRevocationFails : IExpiringConditionalKeyValueStore, IBatchKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            private bool _fail = true;
            public string Description => _inner.Description;
            public Task SetManyAsync(IReadOnlyList<KeyValuePair<string, string>> values, CancellationToken token)
            {
                if (!_fail) return _inner.SetManyAsync(values, token);
                _fail = false;
                throw new InvalidOperationException("Synthetic coverage write failure.");
            }
            public Task<string> GetStringAsync(string k, CancellationToken t) => _inner.GetStringAsync(k, t);
            public Task SetStringAsync(string k, string v, TimeSpan? ttl, CancellationToken t) => _inner.SetStringAsync(k, v, ttl, t);
            public Task<bool> DeleteAsync(string k, CancellationToken t) => _inner.DeleteAsync(k, t);
            public Task<bool> ExistsAsync(string k, CancellationToken t) => _inner.ExistsAsync(k, t);
            public Task<VersionedValue> GetVersionedAsync(string k, CancellationToken t) => _inner.GetVersionedAsync(k, t);
            public Task<bool> TryDeleteAsync(string k, string etag, CancellationToken t) => _inner.TryDeleteAsync(k, etag, t);
            public Task<bool> TrySetStringAsync(string k, string v, string etag, CancellationToken t) => _inner.TrySetStringAsync(k, v, etag, t);
            public Task<bool> TrySetExpiringStringAsync(string k, string v, string etag, TimeSpan? ttl, CancellationToken t) =>
                _inner.TrySetExpiringStringAsync(k, v, etag, ttl, t);
        }

        [TestMethod]
        public void ImportCoverageIsNotNarrowedToTheReadersScope()
        {
            // A scope holding only the Monday-only person: the activity narrows, but whether each day was imported
            // is a fact about the import. Narrowing it would turn the six days this person was not active into
            // "not imported" and flag a complete week.
            var weeks = Run("dbo.teams_user_activity_log", ReportUserScope.ForUsers(new[] { _mondayOnly }));

            Assert.AreEqual(1d, weeks[CompleteWeek].Value);
            Assert.AreEqual(7, weeks[CompleteWeek].ImportedDays);
            Assert.AreEqual(0d, weeks[EverydayWeek].Value, "An imported week with nobody in scope active is a real zero.");
            Assert.AreEqual(7, weeks[EverydayWeek].ImportedDays);
        }

        [TestMethod]
        public void TheQueryRunsAgainstEveryWorkloadTable()
        {
            foreach (var table in new[]
            {
                "dbo.outlook_user_activity_log",
                "dbo.onedrive_user_activity_log",
                "dbo.sharepoint_user_activity_log",
                "dbo.yammer_user_activity_log",
            })
            {
                Assert.AreEqual(0, Run(table).Count, $"{table} holds no rows, so no week has a report.");
            }
        }

        private static Dictionary<DateTime, Week> Run(
            string table, ReportUserScope scope = null, IDictionary<DateTime, string> outcomes = null,
            DateTime? from = null, DateTime? through = null)
        {
            scope = scope ?? ReportUserScope.Everyone;
            var sql = ReportScopeSql.Apply(ReportsAPIController.UsageWeeklyActiveUsersQuery(table), scope);
            var dailyRows = new List<ReportsAPIController.UsageWeekRow>();

            using (var connection = new SqlConnection(_connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 120;
                command.Parameters.Add(new SqlParameter("@from", SqlDbType.Date) { Value = from ?? CompleteWeek });
                command.Parameters.Add(new SqlParameter("@through", SqlDbType.Date) { Value = through ?? EverydayWeek });
                command.Parameters.Add(new SqlParameter("@settled", SqlDbType.Date) { Value = (through ?? EverydayWeek).AddDays(6) });
                var scopeParameter = ReportScopeSql.CreateParameter(scope);
                if (scopeParameter != null) command.Parameters.Add(scopeParameter);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        dailyRows.Add(new ReportsAPIController.UsageWeekRow
                        {
                            WeekStart = Convert.ToDateTime(reader["WeekStart"]).Date,
                            Value = Convert.ToDouble(reader["Value"]),
                            CoverageDate = Convert.ToDateTime(reader["CoverageDate"]).Date,
                            HasRows = Convert.ToBoolean(reader["HasRows"]),
                        });
                    }
                }
            }

            var aggregation = ReportsAPIController.AggregateUsageWeekRows(dailyRows, outcomes);
            var weeks = aggregation.WeeklyRows.ToDictionary(
                row => row.WeekStart.Date,
                row =>
                {
                    var days = dailyRows.Where(day => day.WeekStart.Date == row.WeekStart.Date).ToList();
                    return new Week
                    {
                        Value = row.Value,
                        ImportedDays = days.Count(day => day.HasRows),
                        ExpectedDays = days.Count,
                        Incomplete = aggregation.IncompleteWeeks.Contains(row.WeekStart.Date),
                    };
                });
            return weeks;
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        private sealed class Week
        {
            public double Value { get; set; }
            public int ImportedDays { get; set; }
            public int ExpectedDays { get; set; }
            public bool Incomplete { get; set; }
        }
    }
}
