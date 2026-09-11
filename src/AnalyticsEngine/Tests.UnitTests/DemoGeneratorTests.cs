using Common.Entities.CopilotAdoption;
using Common.Entities.Entities.AgentCosts;
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
                    var day = timeline.Day(d);
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
                        var day = timeline.Day((int)(date - options.Start).TotalDays);
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
            // The official report now covers every reported day, exactly like the M365 daily tables: the
            // rolling 28-day counters are warmed up before the window starts rather than inside it.
            Assert.AreEqual(population.Skus[1].Members * (options.Days - 2), details.Count);
            Assert.AreEqual(details.Count, details.Select(r => r[0] + "|" + r[1] + "|" + r[3]).Distinct().Count());
            foreach (var group in details.GroupBy(r => (int)r[0]))
            {
                var user = population.User(group.Key);
                Assert.IsTrue(user.CopilotLicensed);
                var timeline = new DemoTimeline(options, user);
                foreach (var row in group)
                {
                    var date = (DateTime)row[1];
                    Assert.IsTrue(date >= options.Start && date <= options.ReportEnd);
                    int end = (int)(date - options.Start).TotalDays;
                    var window = Enumerable.Range(end - 27, 28).Select(timeline.Day).ToList();
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
                    for (int slot = 0; slot < timeline.Day(day).CopilotTurns; slot++)
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
        public void DlpEvents_AreAMinorityOfInteractionsAndSeparateBlockedFromAuditedOnly()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.Chats || t == DemoTables.CopilotDlpEvents
                || t == DemoTables.DlpRuleMatches || t == DemoTables.DlpPolicies || t == DemoTables.DlpRules
                || t == DemoTables.DlpActions || t == DemoTables.SensitivityLabels);

            var chats = sink.For(DemoTables.Chats);
            var dlp = sink.For(DemoTables.CopilotDlpEvents);
            var tenant = sink.For(DemoTables.DlpRuleMatches);

            Assert.IsTrue(chats.Count > 0, "The fixture must produce Copilot interactions to attach DLP events to.");
            Assert.IsTrue(dlp.Count > 0, "The demo database must contain DLP events, or the DLP page renders empty.");

            // A healthy tenant blocks a small minority of Copilot use. A demo database where most
            // interactions were blocked would misrepresent the product to whoever is being shown it.
            Assert.IsTrue(dlp.Count < chats.Count / 4,
                $"DLP events ({dlp.Count}) should be a small minority of interactions ({chats.Count}).");

            // The page's central distinction. Without both present the "Blocked" vs "Audited only"
            // columns can't be demonstrated - and a bug collapsing them would go unnoticed.
            const int blockedIndex = 7;
            Assert.IsTrue(dlp.Any(r => (bool)r[blockedIndex]), "Some DLP events must be genuine blocks.");
            Assert.IsTrue(dlp.Any(r => !(bool)r[blockedIndex]),
                "Some DLP events must be audited-only, so the report's simulation case is demonstrated.");

            // Every event must point at a real chat and real dimensions, or the merge/report joins drop it.
            var chatIds = new HashSet<Guid>(chats.Select(r => (Guid)r[0]));
            int policyCount = sink.For(DemoTables.DlpPolicies).Count;
            int ruleCount = sink.For(DemoTables.DlpRules).Count;
            int actionCount = sink.For(DemoTables.DlpActions).Count;
            int labelCount = sink.For(DemoTables.SensitivityLabels).Count;
            Assert.IsTrue(policyCount > 1 && ruleCount == policyCount && actionCount > 0 && labelCount > 0);

            foreach (var row in dlp)
            {
                Assert.IsTrue(chatIds.Contains((Guid)row[0]), "Every DLP event must belong to a generated interaction.");
                Assert.IsTrue((int)row[1] >= 1 && (int)row[1] <= policyCount, "policy id out of range");
                Assert.IsTrue((int)row[2] >= 1 && (int)row[2] <= ruleCount, "rule id out of range");
                Assert.IsTrue((int)row[3] >= 1 && (int)row[3] <= actionCount, "action id out of range");
                Assert.IsTrue((int)row[6] >= 1 && (int)row[6] <= labelCount, "sensitivity label id out of range");
            }

            // The tenant-wide feed is populated too, so the page's second section isn't empty.
            Assert.AreEqual(dlp.Count, tenant.Count);

            // A rule in "Audit only" mode must never be recorded as a block - that is exactly the
            // classification CopilotDlpRules makes, and the demo data has to agree with the importer.
            var rulesByPolicy = sink.For(DemoTables.DlpRules).ToDictionary(r => (int)r[0], r => (string)r[5]);
            foreach (var row in dlp)
            {
                if (rulesByPolicy[(int)row[2]] == "Audit only")
                    Assert.IsFalse((bool)row[blockedIndex], "An 'Audit only' rule withheld nothing and must not be a block.");
            }
        }

        [TestMethod]
        public void AgentCosts_FollowTheGeneratedAgentTrafficAndCoverEveryBillingDimension()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.Chats || t == DemoTables.StudioCredits);
            var credits = sink.For(DemoTables.StudioCredits);
            Assert.IsTrue(credits.Count > 0, "The demo must bill the agent traffic it generated.");

            // What the audit half of the demo says happened, so the billing half can be checked against it
            // rather than against itself. Cowork is excluded: it is not a Copilot Studio agent.
            var traffic = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var chat in sink.For(DemoTables.Chats))
            {
                if (chat[Col(DemoTables.Chats, "agent_id")] == null) continue;
                int agent = (int)chat[Col(DemoTables.Chats, "agent_id")];
                if (agent > DemoAgentCosts.Agents.Length) continue;
                string key = TrafficKey(DemoAgentCosts.Agents[agent - 1].AgentId,
                    ((DateTime)chat[Col(DemoTables.Chats, "time_stamp")]).Date);
                if (!traffic.TryGetValue(key, out var users)) traffic.Add(key, users = new HashSet<int>());
                users.Add((int)chat[Col(DemoTables.Chats, "user_id")]);
            }
            Assert.IsTrue(traffic.Count > 0, "This population must use agents, or the check below proves nothing.");

            var billedDays = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var harnesses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in credits)
            {
                var date = (DateTime)row[Col(DemoTables.StudioCredits, "usage_date")];
                var agentId = (string)row[Col(DemoTables.StudioCredits, "agent_id")];
                string key = TrafficKey(agentId, date);
                Assert.IsTrue(traffic.ContainsKey(key), "Billed credits must have real synthetic usage behind them.");
                billedDays.Add(key);

                var feature = (string)row[Col(DemoTables.StudioCredits, "feature_name")];
                Assert.AreEqual(CopilotStudioHarnessClassifier.Classify(feature),
                    (string)row[Col(DemoTables.StudioCredits, "harness")],
                    "The demo must classify a harness exactly as the importer would.");
                harnesses.Add((string)row[Col(DemoTables.StudioCredits, "harness")]);

                int users = (int)row[Col(DemoTables.StudioCredits, "distinct_users")];
                Assert.IsTrue(users >= 1 && users <= traffic[key].Count,
                    "A slice cannot have been used by more people than used the agent that day.");

                var billed = (decimal)row[Col(DemoTables.StudioCredits, "billed_credits")];
                Assert.IsTrue(billed > 0m, "A zero-credit row is not something the API reports.");
                var waived = row[Col(DemoTables.StudioCredits, "non_billed_credits")];
                if (waived != null) Assert.IsTrue((decimal)waived <= billed);

                Assert.IsTrue((DateTime)row[Col(DemoTables.StudioCredits, "last_refreshed_utc")]
                    <= (DateTime)row[Col(DemoTables.StudioCredits, "imported_utc")],
                    "Microsoft cannot have refreshed a row after we read it.");

                // The (usage_date, dimension_hash) unique index is the upsert key: a duplicate here would
                // fail the insert against the real schema rather than merely look odd.
                Assert.IsTrue(keys.Add(date.ToString("yyyy-MM-dd") + "|"
                    + (string)row[Col(DemoTables.StudioCredits, "dimension_hash")]), "Duplicate upsert key.");
            }
            CollectionAssert.AreEquivalent(traffic.Keys.ToList(), billedDays.ToList(),
                "Every agent-day of synthetic usage must produce billed credits, and no other day may.");

            // The page pivots on harness, and two of the three values it can show are only reachable through
            // the feature names below - an all-"StandardOrCopilotChat" demo would never exercise them.
            CollectionAssert.IsSubsetOf(
                new[] { CopilotStudioHarness.StandardOrCopilotChat, CopilotStudioHarness.GitHubCopilot, CopilotStudioHarness.Unknown },
                DemoAgentCosts.Agents.SelectMany(a => a.Slices)
                    .Select(s => CopilotStudioHarnessClassifier.Classify(s.FeatureName)).Distinct().ToList());
            Assert.IsTrue(harnesses.Contains(CopilotStudioHarness.StandardOrCopilotChat));

            // Every filter the page offers must have something behind it somewhere in the window.
            foreach (var column in new[] { "environment_id", "channel_id", "llm_model", "tool_invoked", "knowledge_sources" })
            {
                Assert.IsTrue(credits.Any(r => r[Col(DemoTables.StudioCredits, column)] != null),
                    "No value was generated for the filter dimension " + column);
            }
        }

        [TestMethod]
        public void AgentCostUserRows_AreOnePerPersonPerDayAndResolveToRealUsersExceptTheDepartedOne()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.StudioUserCredits || t == DemoTables.Users);
            var rows = sink.For(DemoTables.StudioUserCredits);
            Assert.IsTrue(rows.Count > 0);

            var objectIdsByUser = sink.For(DemoTables.Users)
                .ToDictionary(r => (int)r[Col(DemoTables.Users, "id")], r => (string)r[Col(DemoTables.Users, "azure_ad_id")]);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            int unresolved = 0;
            foreach (var row in rows)
            {
                var date = (DateTime)row[Col(DemoTables.StudioUserCredits, "usage_date")];
                var objectId = (string)row[Col(DemoTables.StudioUserCredits, "entra_object_id")];
                var userId = (int?)row[Col(DemoTables.StudioUserCredits, "user_id")];

                if (userId.HasValue) Assert.AreEqual(objectIdsByUser[userId.Value], objectId,
                    "The billing identifier must be the same Entra object id the user import stored.");
                else { Assert.AreEqual(DemoAgentCosts.DepartedEntraObjectId, objectId); unresolved++; }

                Assert.IsNull(row[Col(DemoTables.StudioUserCredits, "agent_id")],
                    "The per-user endpoint reports no agent, so inventing one would misrepresent the source.");
                Assert.AreEqual(DemoAgentCosts.UserCreditUnit, (string)row[Col(DemoTables.StudioUserCredits, "unit")]);
                Assert.IsTrue((decimal)row[Col(DemoTables.StudioUserCredits, "billed_credits")] > 0m);
                Assert.IsTrue(keys.Add(date.ToString("yyyy-MM-dd") + "|"
                    + (string)row[Col(DemoTables.StudioUserCredits, "dimension_hash")]), "Duplicate upsert key.");
            }
            Assert.IsTrue(unresolved > 0, "The report has an unresolved-user path; the demo must exercise it.");
            Assert.IsTrue(rows.Count > unresolved, "Most billed people must still resolve to a user.");
        }

        [TestMethod]
        public void AzureCosts_BillFixedMetersEveryDayAndTokenMetersOnlyWhereThereWasTraffic()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.AzureCosts || t == DemoTables.StudioCredits);
            var rows = sink.For(DemoTables.AzureCosts);
            Assert.IsTrue(rows.Count > 0);

            int fixedMeters = DemoAgentCosts.AzureMeters.Count(m => m.CostPerDay > 0m);
            var byDate = rows.GroupBy(r => (DateTime)r[Col(DemoTables.AzureCosts, "usage_date")]).ToList();
            Assert.AreEqual(options.Days, byDate.Count,
                "Deployed resources cost money on every day of the window, including weekends.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var day in byDate)
            {
                Assert.IsTrue(day.Count() >= fixedMeters, "Every fixed meter bills every day.");
                bool estimated = day.Key >= options.AsOf.AddDays(-DemoAgentCosts.EstimatedTrailingDays);
                foreach (var row in day)
                {
                    Assert.AreEqual(estimated, (bool)row[Col(DemoTables.AzureCosts, "is_estimated")],
                        "Only the days Microsoft has not closed yet are estimates.");
                    Assert.AreEqual(DemoAgentCosts.Currency, (string)row[Col(DemoTables.AzureCosts, "currency")]);
                    Assert.IsTrue((decimal)row[Col(DemoTables.AzureCosts, "cost")] > 0m);
                    Assert.IsTrue(((string)row[Col(DemoTables.AzureCosts, "resource_id")])
                        .StartsWith(DemoAgentCosts.Scope + "/resourceGroups/", StringComparison.Ordinal));
                    Assert.IsTrue(keys.Add(day.Key.ToString("yyyy-MM-dd") + "|"
                        + (string)row[Col(DemoTables.AzureCosts, "row_hash")]), "Duplicate upsert key.");
                }
            }

            // Token meters follow usage. A day with no agent traffic must not invent inference spend.
            var busyDates = new HashSet<DateTime>(sink.For(DemoTables.StudioCredits)
                .Select(r => (DateTime)r[Col(DemoTables.StudioCredits, "usage_date")]));
            var tokenMeter = DemoAgentCosts.AzureMeters.First(m => m.CostPerDay == 0m && m.CostPerTurn > 0m).MeterName;
            foreach (var day in byDate)
            {
                bool billedTokens = day.Any(r => (string)r[Col(DemoTables.AzureCosts, "meter_name")] == tokenMeter);
                Assert.AreEqual(busyDates.Contains(day.Key), billedTokens,
                    "Inference meters must appear exactly on the days the agents were used.");
            }

            // More than one value on every Azure pivot the report offers, or its pickers are dead ends.
            foreach (var column in new[] { "resource_group", "service_name", "meter_category", "meter_sub_category", "meter_name", "resource_id" })
                Assert.IsTrue(rows.Select(r => (string)r[Col(DemoTables.AzureCosts, column)]).Distinct().Count() > 1, column);
        }

        [TestMethod]
        public void CapacitySnapshotsAndImportLogs_ReportAnHonestEntitlementAndACleanRun()
        {
            var options = Options();
            var sink = Generate(options, t => t == DemoTables.StudioCapacity || t == DemoTables.AgentCostImports
                || t == DemoTables.StudioCredits);

            var capacity = sink.For(DemoTables.StudioCapacity);
            Assert.AreEqual(options.Days, capacity.Count, "One entitlement snapshot per day.");
            var entitled = (decimal)capacity[0][Col(DemoTables.StudioCapacity, "entitled")];
            Assert.IsTrue(entitled > 0m);
            foreach (var row in capacity)
            {
                var consumed = (decimal)row[Col(DemoTables.StudioCapacity, "consumed")];
                var available = (decimal)row[Col(DemoTables.StudioCapacity, "available")];
                var overage = (decimal)row[Col(DemoTables.StudioCapacity, "pay_as_you_go_consumed")];
                Assert.AreEqual(entitled, (decimal)row[Col(DemoTables.StudioCapacity, "entitled")],
                    "Purchased capacity does not move day to day.");
                Assert.AreEqual("MonthToDate", (string)row[Col(DemoTables.StudioCapacity, "consumption_type")]);
                Assert.AreEqual(overage > 0m ? "Overage" : "WithinCapacity", (string)row[Col(DemoTables.StudioCapacity, "status")]);
                Assert.AreEqual(Math.Max(0m, entitled - consumed), available);
                Assert.IsTrue((DateTime)row[Col(DemoTables.StudioCapacity, "snapshot_utc")]
                    >= (DateTime)row[Col(DemoTables.StudioCapacity, "consumption_as_of")],
                    "Consumption is always as of a day we have already reached.");
            }

            // Month-to-date restarts with the calendar month rather than running away over the window.
            var consumedByDate = capacity.ToDictionary(
                r => (DateTime)r[Col(DemoTables.StudioCapacity, "consumption_as_of")],
                r => (decimal)r[Col(DemoTables.StudioCapacity, "consumed")]);
            foreach (var pair in consumedByDate)
            {
                var previous = pair.Key.AddDays(-1);
                if (pair.Key.Day == 1 || !consumedByDate.ContainsKey(previous)) continue;
                Assert.IsTrue(pair.Value >= consumedByDate[previous], "Month-to-date consumption cannot fall mid-month.");
            }
            Assert.IsTrue(consumedByDate.Any(p => p.Key.Day == 1 && p.Value < entitled));

            var logs = sink.For(DemoTables.AgentCostImports);
            var names = new[]
            {
                AgentCostImportNames.CopilotStudioCredits, AgentCostImportNames.CopilotStudioUserCredits,
                AgentCostImportNames.CopilotStudioCapacity, AgentCostImportNames.AzureCostManagement,
            };
            CollectionAssert.AreEquivalent(names,
                logs.Select(r => (string)r[Col(DemoTables.AgentCostImports, "import_name")]).Distinct().ToList(),
                "Every agent-cost import needs a log row, or the report cannot tell 'never ran' from 'nothing to import'.");
            Assert.AreEqual(names.Length * DemoAgentCosts.ImportLogDays, logs.Count);
            foreach (var row in logs)
            {
                Assert.IsNull(row[Col(DemoTables.AgentCostImports, "error")], "The demo's imports all succeeded.");
                Assert.AreEqual((int)row[Col(DemoTables.AgentCostImports, "rows_read")],
                    (int)row[Col(DemoTables.AgentCostImports, "rows_saved")]);
            }

            // The report reads this setting, not the rows, to decide whether to explain an empty page.
            var settings = new Common.Entities.ImportTaskSettings(DemoPortalReadiness.RequiredImportJobSettings);
            Assert.IsTrue(settings.CopilotStudioCredits);
            Assert.IsTrue(settings.AzureCostManagement);
        }

        [TestMethod]
        public void PopulationThatNeverUsesAnAgent_IsBilledForInfrastructureButNeverForCredits()
        {
            var options = Options("--mix", "0,0,0,100,0");
            var sink = Generate(options, t => t == DemoTables.StudioCredits || t == DemoTables.StudioUserCredits
                || t == DemoTables.StudioCapacity || t == DemoTables.AzureCosts || t == DemoTables.AgentCostImports);

            Assert.AreEqual(0, sink.For(DemoTables.StudioCredits).Count, "No usage, no billed credits.");
            Assert.AreEqual(0, sink.For(DemoTables.StudioUserCredits).Count);
            Assert.AreEqual(0, sink.For(DemoTables.StudioCapacity).Count,
                "An entitlement this product never observed must not be invented.");

            // Azure is the deliberate exception: the resources exist and are billed whether or not anyone
            // talks to the agents. The log rows are written too, so the page can say the import ran cleanly
            // and simply found nothing rather than leaving an unexplained zero.
            Assert.IsTrue(sink.For(DemoTables.AzureCosts).Count > 0);
            Assert.IsTrue(sink.For(DemoTables.AgentCostImports).Count > 0);
            Assert.IsTrue(sink.For(DemoTables.AgentCostImports)
                .Where(r => (string)r[Col(DemoTables.AgentCostImports, "import_name")] == AgentCostImportNames.CopilotStudioCredits)
                .All(r => (int)r[Col(DemoTables.AgentCostImports, "rows_saved")] == 0));
        }

        private static string TrafficKey(string agentId, DateTime date) =>
            agentId + "|" + date.ToString("yyyy-MM-dd");

        private static int Col(DemoTable table, string column)
        {
            for (int i = 0; i < table.Columns.Count; i++)
                if (table.Columns[i].Name == column) return i;
            throw new InvalidOperationException("No column " + column + " on " + table.Name);
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
            Assert.IsTrue(sink.For(DemoTables.StudioCredits)
                .Any(r => ((string)r[Col(DemoTables.StudioCredits, "knowledge_sources")] ?? string.Empty).Contains("Καλημέρα")),
                "A knowledge source is customer-named text, so the demo must prove that column carries Unicode.");
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
