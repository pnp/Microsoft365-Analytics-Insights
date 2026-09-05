using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Tests.FakeDataGen.Demo;
using Tests.FakeDataGen.Seeding;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoGeneratorTests
    {
        public TestContext TestContext { get; set; }
        internal static DemoOptions Options(params string[] extra) => DemoOptions.Parse(
            new[] { "--preview", "--users", "40", "--days", "35", "--as-of", "2026-09-01" }.Concat(extra).ToArray(), DateTime.UtcNow);

        [TestMethod]
        public void Options_RejectUnsafeTargetsAndInvalidOrDuplicateFlags()
        {
            var invalid = new[]
            {
                new string[0], new[] { "--database", "CustomerDatabase" },
                new[] { "--database", "ContosoDemo_x];DROP DATABASE master;--" },
                new[] { "--database", "ContosoDemo_../file" },
                new[] { "--database", "ContosoDemo_Καλημέρα" },
                new[] { "--preview", "--connection", "Server=contoso.example" },
                new[] { "--preview", "--users", "0" }, new[] { "--preview", "--users", "-1" },
                new[] { "--preview", "--days", "30" }, new[] { "--preview", "--skus", "9" },
                new[] { "--preview", "--seed", "2147483648" },
                new[] { "--preview", "--users", "1", "--users", "2" },
                new[] { "--preview", "--mix", "30,30,30,30,30" },
                new[] { "--preview", "--mix", "50,50" },
                new[] { "--preview", "--as-of", "2026-02-30" }, new[] { "--preview", "--batch-size" }
            };
            foreach (var args in invalid)
                Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(args, DateTime.UtcNow), string.Join(" ", args));
            Assert.IsTrue(DemoOptions.Parse(new[] { "--help" }, DateTime.UtcNow).Help);
            var connection = new SqlConnectionStringBuilder(SqlDemoDatabase.LocalConnection("ContosoDemo_Example"));
            Assert.AreEqual(@"(localdb)\MSSQLLocalDB", connection.DataSource);
            Assert.IsTrue(connection.IntegratedSecurity);
            Assert.AreEqual(string.Empty, connection.Password);
        }

        [TestMethod]
        public void Fingerprint_IsIndependentOfDestinationBatchSizeAndClockWhenAsOfIsExplicit()
        {
            var first = Options("--batch-size", "1", "--database", "ContosoDemo_First");
            var second = Options("--batch-size", "1000", "--database", "ContosoDemo_Second", "--output", "unused.json");
            Assert.AreEqual(first.Fingerprint, second.Fingerprint);
            Assert.AreNotEqual(first.Fingerprint, Options("--seed", "43").Fingerprint);
            Assert.AreNotEqual(first.Fingerprint, Options("--no-profiles").Fingerprint);
            Assert.AreEqual(DateTimeKind.Utc, first.AsOf.Kind);
        }

        [TestMethod]
        public void PreviewCommand_WritesANewSummaryAndNeverOverwritesAnExistingFile()
        {
            string directory = ".demo-output-test-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "summary.json");
                string[] arguments = { "--preview", "--users", "1", "--days", "31", "--as-of", "2026-09-01", "--output", path };
                Assert.AreEqual(0, DemoCommand.Run(arguments));
                string original = File.ReadAllText(path);
                var summary = JsonConvert.DeserializeObject<DemoSummary>(original);
                Assert.AreEqual("Preview", summary.Status);
                Assert.AreEqual(1, summary.Users);
                Assert.IsTrue(summary.TotalRows > 0);
                Assert.AreNotEqual(0, DemoCommand.Run(arguments));
                Assert.AreEqual(original, File.ReadAllText(path));
                Assert.AreEqual(2, DemoCommand.Run(new[] { "--preview", "--users", "0" }));
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestMethod]
        public void LicenceMemberships_AreExactDistinctOverlappingAndLinearAt300000Users50Skus()
        {
            var options = DemoOptions.Parse(new[] { "--preview", "--users", "300000", "--skus", "50" }, new DateTime(2026, 9, 1));
            var population = new DemoPopulation(options);
            var counts = new int[options.Skus];
            long assignments = 0;
            for (int user = 1; user <= options.Users; user++)
            {
                int bases = 0;
                for (int sku = 0; sku < counts.Length; sku++)
                    if (population.Skus[sku].Includes(user, options.Users))
                    {
                        counts[sku]++; assignments++;
                        if (sku >= 2 && sku <= 4) bases++;
                    }
                Assert.AreEqual(1, bases, "E3/E5/Business are alternatives, not duplicate base subscriptions.");
            }
            CollectionAssert.AreEqual(population.Skus.Select(s => s.Members).ToArray(), counts);
            CollectionAssert.AreEqual(new[] { 1, 5, 25, 50, 100 }, counts.Skip(5).Take(5).ToArray());
            Assert.AreEqual(options.Users, counts[0]);
            Assert.IsTrue(assignments > 3000000, "Exercise millions of overlapping relationships, not fifty isolated populations.");
            Assert.AreEqual(1, CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(population.Skus.Select(s =>
                new LicenceTypeRow { Id = s.Id, Name = s.Name, SkuPartNumber = s.PartNumber }).ToList(), null).Count);
        }

        [TestMethod]
        public void TinyPopulations_ClampRareSkusWithoutInventingUsers()
        {
            var options = DemoOptions.Parse(new[] { "--preview", "--users", "1" }, new DateTime(2026, 9, 1));
            var population = new DemoPopulation(options);
            foreach (var sku in population.Skus)
                Assert.AreEqual(sku.Members, sku.Includes(1, 1) ? 1 : 0);
        }

        [TestMethod]
        public void OfficeHours_RespectEverySeedLocaleAndSeasonWithoutUtcWeekendLeakage()
        {
            foreach (var asOf in new[] { "2026-02-01", "2026-07-01", "2026-11-15" })
            {
                var options = DemoOptions.Parse(new[] { "--preview", "--days", "35", "--as-of", asOf }, DateTime.UtcNow);
                var calendar = new DemoCalendar(options);
                foreach (var locale in SeedDataCatalogue.Locales)
                {
                    var profile = new SeedDataCatalogue.UserProfile { UsageLocation = locale.UsageLocation, StateOrProvince = locale.StateOrProvince };
                    var zoneId = DemoCalendar.ZoneFor(profile);
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                    for (int d = 0; d < options.Days; d++)
                    {
                        var date = options.Start.AddDays(d);
                        if (!DemoCalendar.IsWorkingDate(date)) continue;
                        foreach (uint jitter in new uint[] { 0, 100, uint.MaxValue })
                        {
                            var stamp = calendar.Timestamp(zoneId, d, jitter);
                            var local = TimeZoneInfo.ConvertTimeFromUtc(stamp, zone);
                            Assert.AreEqual(date, stamp.Date);
                            Assert.IsTrue(DemoCalendar.IsWeekday(local));
                            Assert.IsTrue(local.Hour >= 9 && local.Hour < 17, zoneId);
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Timelines_HaveNoWeekendActionsAndInactiveUsersHaveOnlyHistoricActivity()
        {
            var options = DemoOptions.Parse(new[] { "--preview", "--users", "300", "--as-of", "2026-09-01" }, DateTime.UtcNow);
            var population = new DemoPopulation(options);
            bool sawHistoricInactive = false;
            for (int id = 1; id <= options.Users; id++)
            {
                var user = population.User(id);
                var timeline = new DemoTimeline(options, user);
                Assert.IsTrue(user.Upn.All(c => c <= 127));
                for (int d = 0; d < options.Days; d++)
                {
                    var day = timeline.Days[d];
                    if (!DemoCalendar.IsWorkingDate(options.Start.AddDays(d)) || DemoTimeline.IsOnLeave(id, d) || user.Cohort == DemoCohort.Zero
                        || (user.Cohort == DemoCohort.Inactive && options.Days - d <= 60))
                    {
                        Assert.IsFalse(day.HasWorkloadActivity);
                        Assert.AreEqual(0, day.CopilotTurns);
                    }
                    if (user.Cohort == DemoCohort.Inactive && day.HasWorkloadActivity) sawHistoricInactive = true;
                }
            }
            Assert.IsTrue(sawHistoricInactive);
        }

        [TestMethod]
        public void DailyRows_ExplicitlyCoverEveryUserAndDateWithAccurateCarriedLastActivity()
        {
            var options = Options();
            var tables = new[] { DemoTables.Teams, DemoTables.Outlook, DemoTables.SharePoint, DemoTables.OneDrive };
            var sink = Generate(options, t => tables.Contains(t));
            var population = new DemoPopulation(options);
            foreach (var table in tables)
            {
                var rows = sink.For(table);
                Assert.AreEqual(options.Users * (options.Days - 2), rows.Count);
                Assert.AreEqual(rows.Count, rows.Select(r => r[0] + "|" + ((DateTime)r[1]).Ticks).Distinct().Count());
                foreach (var group in rows.GroupBy(r => (int)r[0]))
                {
                    DateTime? last = null;
                    var timeline = new DemoTimeline(options, population.User(group.Key));
                    foreach (var row in group.OrderBy(r => (DateTime)r[1]))
                    {
                        var date = (DateTime)row[1];
                        var day = timeline.Days[(int)(date - options.Start).TotalDays];
                        int total = table == DemoTables.Teams ? day.Messages + day.Meetings
                            : table == DemoTables.Outlook ? day.Sent + day.Read
                            : table == DemoTables.SharePoint ? day.SharePointFiles : day.OneDriveFiles;
                        if (total > 0) last = date;
                        Assert.AreEqual(last, (DateTime?)row[2]);
                        if (!DemoCalendar.IsWeekday(date)) Assert.IsTrue(row.Skip(3).All(v => Convert.ToInt64(v) == 0));
                    }
                }
            }
            Assert.IsTrue(sink.For(DemoTables.Teams).Any(r => r[2] != null && (DateTime)r[2] < (DateTime)r[1]));
        }

        [TestMethod]
        public void OfficialCopilotSnapshots_AreLicensedOnlyCompleteD28WindowsAndMatchCounters()
        {
            var options = Options();
            var population = new DemoPopulation(options);
            var sink = Generate(options, t => t == DemoTables.CopilotUsage || t == DemoTables.CopilotCounts);
            var details = sink.For(DemoTables.CopilotUsage);
            Assert.AreEqual(population.Skus[1].Members * (options.Days - 29), details.Count);
            Assert.AreEqual(details.Count, details.Select(r => r[0] + "|" + r[1] + "|" + r[3]).Distinct().Count());
            foreach (var group in details.GroupBy(r => (int)r[0]))
            {
                var user = population.User(group.Key);
                Assert.IsTrue(user.CopilotLicensed);
                var timeline = new DemoTimeline(options, user);
                foreach (var row in group)
                {
                    var date = (DateTime)row[1];
                    Assert.IsTrue(date >= options.FirstCopilotReport && date <= options.ReportEnd);
                    int end = (int)(date - options.Start).TotalDays;
                    var window = timeline.Days.Skip(end - 27).Take(28).ToList();
                    Assert.AreEqual(28, row[3]);
                    Assert.AreEqual(window.Sum(d => d.CopilotTurns), row[4]);
                    Assert.AreEqual(window.Count(d => d.CopilotTurns > 0), row[7]);
                    Assert.AreEqual(0, row[6]);
                }
            }
            foreach (var row in sink.For(DemoTables.CopilotCounts).Where(r => (string)r[4] == "Any App"))
            {
                var date = (DateTime)row[1];
                if ((string)row[2] == "Summary")
                {
                    var sameDate = details.Where(r => (DateTime)r[1] == date).ToList();
                    Assert.AreEqual(sameDate.Count(r => (int)r[4] > 0), row[6]);
                    Assert.AreEqual(sameDate.Sum(r => (long)(int)r[4]), row[7]);
                }
                else
                {
                    Assert.IsNull(row[3], "Trend's key is period-independent.");
                    if (!DemoCalendar.IsWeekday(date)) { Assert.AreEqual(0, row[6]); Assert.AreEqual(0L, row[7]); }
                }
            }
        }

        [TestMethod]
        public void CopilotAuditAndPairedMetadata_AgreeOnUsersThreadsAndWeekdayTimestamps()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.Audit || t == DemoTables.Chats || t == DemoTables.Interactions
                || t == DemoTables.InteractionSessions);
            var audits = sink.For(DemoTables.Audit).ToDictionary(r => (Guid)r[0]);
            var sessions = sink.For(DemoTables.InteractionSessions).ToDictionary(r => (int)r[0]);
            var population = new DemoPopulation(options);
            foreach (var chat in sink.For(DemoTables.Chats))
            {
                var audit = audits[(Guid)chat[0]];
                Assert.AreEqual(audit[1], chat[6]);
                Assert.AreEqual(audit[3], chat[7]);
                Assert.IsTrue(DemoCalendar.IsWeekday((DateTime)chat[7]));
                if (!population.User((int)chat[6]).CopilotLicensed) Assert.AreEqual("bizchat", chat[1]);
            }
            foreach (var pair in sink.For(DemoTables.Interactions).GroupBy(r => (string)r[3]))
            {
                Assert.AreEqual(2, pair.Count());
                var prompt = pair.Single(r => (int)r[4] == 1);
                var response = pair.Single(r => (int)r[4] == 2);
                Assert.IsTrue(population.User((int)prompt[2]).CopilotLicensed);
                Assert.IsNull(prompt[14]);
                Assert.AreEqual((DateTime)prompt[7] + TimeSpan.FromMilliseconds((int)response[14]), response[7]);
                Assert.AreEqual(prompt[2], response[2]);
                Assert.AreEqual(prompt[2], sessions[(int)prompt[1]][2]);
                Assert.IsTrue((DateTime)response[7] < options.AsOf);
            }
        }

        [TestMethod]
        public void SeededStreams_AreIdenticalAcrossBatchSizesAndChangeWithSeed()
        {
            var one = Generate(Options("--batch-size", "1"), _ => true);
            var many = Generate(Options("--batch-size", "1000"), _ => true);
            Assert.AreEqual(JsonConvert.SerializeObject(one.Rows), JsonConvert.SerializeObject(many.Rows));
            var other = Generate(Options("--seed", "100"), _ => true);
            Assert.AreNotEqual(JsonConvert.SerializeObject(one.Rows), JsonConvert.SerializeObject(other.Rows));
        }

        [TestMethod]
        public void DefaultPopulation_ProducesAllAdoptionBandsWithTheActualScorer()
        {
            var options = DemoOptions.Parse(new[] { "--preview", "--as-of", "2026-09-01" }, DateTime.UtcNow);
            var summary = DemoCommand.NewSummary(options);
            using (var sink = new CountingDemoSink(summary)) new DemoGenerator(options).Generate(sink, summary, null);
            Assert.AreEqual(600, summary.AdoptionBands.Values.Sum());
            Assert.AreEqual(6, summary.AdoptionBands.Count);
            Assert.AreEqual(5, summary.Cohorts.Count);
            Assert.IsTrue(summary.AdoptionBands.Values.All(n => n > 0));
        }

        [TestMethod]
        public void DefaultAgentInventory_ContainsEveryHealthVerdictAndCowork()
        {
            var options = DemoOptions.Parse(new[] { "--preview", "--as-of", "2026-09-01" }, DateTime.UtcNow);
            var population = new DemoPopulation(options);
            var first = new DateTime?[6];
            var last = new DateTime?[6];
            var people = Enumerable.Range(0, 6).Select(_ => new HashSet<int>()).ToArray();
            for (int id = 1; id <= options.Users; id++)
            {
                var timeline = new DemoTimeline(options, population.User(id));
                for (int day = options.Days - CopilotAdoptionOptions.Default.AgentHistoryDays; day < options.Days; day++)
                    for (int slot = 0; slot < timeline.Days[day].CopilotTurns; slot++)
                    {
                        int agent = timeline.Agent(day, slot);
                        if (agent == 0) continue;
                        var date = options.Start.AddDays(day);
                        if (!first[agent].HasValue || first[agent] > date) first[agent] = date;
                        if (!last[agent].HasValue || last[agent] < date) last[agent] = date;
                        people[agent].Add(id);
                    }
            }
            var expected = new[] { AgentHealth.Keep, AgentHealth.Review, AgentHealth.Retire, AgentHealth.New };
            for (int agent = 1; agent <= 4; agent++)
            {
                Assert.IsTrue(people[agent].Count > 0);
                var scored = CopilotAdoptionScoring.ScoreAgent(new AgentUsageQueryRow
                {
                    AgentId = agent, IsCustomAgent = true, Users = people[agent].Count,
                    FirstUsedUtc = first[agent], LastUsedUtc = last[agent]
                }, options.AsOf);
                Assert.AreEqual(expected[agent - 1], scored.Health);
            }
            Assert.IsTrue(people[5].Count > 0, "The Cowork surface must be present in the default demo.");
        }

        [TestMethod]
        public void OfficeAppAndDeviceRows_IncludeTheAppsActuallyUsedForCopilot()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.Platforms || t == DemoTables.TeamsDevices || t == DemoTables.Chats);
            var apps = sink.For(DemoTables.Platforms).ToDictionary(r => ((int)r[0], (DateTime)r[1]));
            var devices = sink.For(DemoTables.TeamsDevices).ToDictionary(r => ((int)r[0], (DateTime)r[1]));
            var field = new Dictionary<string, string>
            {
                ["Teams"] = "teams", ["Word"] = "word", ["Outlook"] = "outlook",
                ["Excel"] = "excel", ["PowerPoint"] = "powerpoint", ["OneNote"] = "onenote"
            };
            foreach (var chat in sink.For(DemoTables.Chats))
            {
                var date = ((DateTime)chat[7]).Date;
                if (date > options.ReportEnd || !field.TryGetValue((string)chat[1], out var column)) continue;
                var key = ((int)chat[6], date);
                int index = DemoTables.Platforms.Columns.Select(c => c.Name).ToList().IndexOf(column);
                Assert.AreEqual(true, apps[key][index]);
                Assert.AreEqual(date, apps[key][2]);
                if ((string)chat[1] == "Teams") Assert.AreEqual(date, devices[key][2]);
            }
        }

        [TestMethod]
        public void PageTitles_AreUniqueAndSharedAcrossSitesWithValidHitReferences()
        {
            var sink = Generate(Options(), t => t == DemoTables.Titles || t == DemoTables.Hits || t == DemoTables.Urls);
            var titles = sink.For(DemoTables.Titles);
            Assert.AreEqual(3, titles.Count);
            Assert.AreEqual(titles.Count, titles.Select(r => (string)r[1]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.AreEqual(SeedDataCatalogue.Departments.Length * 3, sink.For(DemoTables.Urls).Count);
            var keys = new HashSet<int>(titles.Select(r => (int)r[0]));
            var hits = sink.For(DemoTables.Hits);
            Assert.IsTrue(hits.Any(r => (int)r[0] > 3), "Exercise other sites, not only the first three URLs.");
            foreach (var hit in hits)
            {
                Assert.IsTrue(keys.Contains((int)hit[3]));
                Assert.AreEqual(((int)hit[0] - 1) % 3 + 1, hit[3]);
            }
        }

        [TestMethod]
        public void NeverActivePopulation_HasCoverageButNoInventedActions()
        {
            var options = Options("--mix", "0,0,0,100,0");
            var sink = Generate(options, t => t == DemoTables.Teams || t == DemoTables.CopilotUsage || t == DemoTables.Audit || t == DemoTables.Platforms);
            Assert.AreEqual(0, sink.For(DemoTables.Audit).Count);
            Assert.AreEqual(options.Users * (options.Days - 2), sink.For(DemoTables.Teams).Count);
            foreach (var row in sink.For(DemoTables.Teams))
            {
                Assert.IsNull(row[2]);
                Assert.IsTrue(row.Skip(3).All(v => Convert.ToInt64(v) == 0));
            }
            foreach (var row in sink.For(DemoTables.Platforms))
            {
                Assert.IsNull(row[2]);
                Assert.IsTrue(row.Skip(3).All(v => !(bool)v));
            }
            foreach (var row in sink.For(DemoTables.CopilotUsage)) { Assert.IsNull(row[2]); Assert.AreEqual(0, row[4]); Assert.AreEqual(0, row[7]); }
        }

        [TestMethod]
        public void DeclaredRows_StayWithinParameterAndTextLimitsAndPreserveUnicode()
        {
            var sink = Generate(Options(), _ => true);
            foreach (var table in DemoTables.All)
            {
                Assert.IsTrue(table.BatchLimit(1000) <= 1000);
                Assert.IsTrue(table.BatchLimit(1000) * table.Columns.Count <= 2000);
                foreach (var row in sink.For(table))
                {
                    table.ValidateValues(row);
                    Assert.AreEqual(table.Columns.Count, row.Length);
                    for (int i = 0; i < row.Length; i++)
                        if (row[i] is string text && table.Columns[i].Size > 0)
                            Assert.IsTrue(text.Length <= table.Columns[i].Size, table.Name + "." + table.Columns[i].Name);
                }
            }
            Assert.IsTrue(sink.For(DemoTables.Urls).Any(r => ((string)r[1]).Contains("Καλημέρα")));
            Assert.IsTrue(sink.For(DemoTables.States).Any(r => (string)r[1] == "Αττική"));
            Assert.ThrowsException<InvalidOperationException>(() => DemoTables.WebCities.ValidateValues(new object[] { 1, "Αθήνα" }));
            DemoTables.States.ValidateValues(new object[] { 1, "Αττική" });
        }

        [TestMethod]
        [TestCategory("Scale")]
        public void StreamingGeneration_300000Users50Skus_OptInBenchmark()
        {
            if (Environment.GetEnvironmentVariable("RUN_DEMO_SCALE") != "1")
                Assert.Inconclusive("Set RUN_DEMO_SCALE=1 and select this test explicitly; this streams a large synthetic history without SQL.");
            var options = DemoOptions.Parse(new[] { "--preview", "--users", "300000", "--skus", "50", "--days", "31",
                "--as-of", "2026-09-01" }, DateTime.UtcNow);
            var summary = DemoCommand.NewSummary(options);
            var stopwatch = Stopwatch.StartNew();
            using (var sink = new CountingDemoSink(summary)) new DemoGenerator(options).Generate(sink, summary, null);
            stopwatch.Stop();
            summary.Status = "Preview";
            summary.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            using (var process = Process.GetCurrentProcess()) summary.PeakWorkingSetBytes = process.PeakWorkingSet64;
            Assert.AreEqual(300000L * 29, summary.Rows["teams_user_activity_log"]);
            Assert.AreEqual(300000L * 29, summary.Rows["outlook_user_activity_log"]);
            Assert.AreEqual(300000L * 29, summary.Rows["sharepoint_user_activity_log"]);
            Assert.AreEqual(300000L * 29, summary.Rows["onedrive_user_activity_log"]);
            Assert.IsTrue(summary.Rows["user_license_type_lookups"] > 3000000);
            Assert.AreEqual(180000, summary.AdoptionBands.Values.Sum());
            TestContext.WriteLine(JsonConvert.SerializeObject(summary, Formatting.Indented));
        }

        private static RecordingSink Generate(DemoOptions options, Func<DemoTable, bool> capture)
        {
            var recording = new RecordingSink(capture);
            var summary = DemoCommand.NewSummary(options);
            using (var sink = new CountingDemoSink(summary, recording)) new DemoGenerator(options).Generate(sink, summary, null);
            return recording;
        }

        internal sealed class RecordingSink : IDemoSink
        {
            private readonly Func<DemoTable, bool> _capture;
            public Dictionary<string, List<object[]>> Rows { get; } = new Dictionary<string, List<object[]>>();
            public RecordingSink(Func<DemoTable, bool> capture) { _capture = capture; }
            public void Write(DemoTable table, params object[] values)
            {
                if (!_capture(table)) return;
                if (!Rows.TryGetValue(table.Name, out var rows)) { rows = new List<object[]>(); Rows.Add(table.Name, rows); }
                rows.Add(values);
            }
            public List<object[]> For(DemoTable table) => Rows.TryGetValue(table.Name, out var rows) ? rows : new List<object[]>();
            public void Flush() { }
            public void Dispose() { }
        }
    }
}
