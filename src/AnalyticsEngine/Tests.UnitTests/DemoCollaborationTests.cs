using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoCollaborationTests
    {
        private static readonly DemoTable[] TeamsTables =
        {
            DemoTables.CallTypes, DemoTables.CallModalities, DemoTables.CallRecords, DemoTables.CallSessions,
            DemoTables.CallSessionModalities, DemoTables.CallFeedback, DemoTables.CallFailures,
            DemoTables.TeamDefinitions, DemoTables.TeamChannels, DemoTables.TeamMemberships, DemoTables.TeamOwners,
            DemoTables.TeamTabs, DemoTables.TeamTabLogs, DemoTables.ChannelStats, DemoTables.ChannelKeywords,
            DemoTables.ChannelLanguages, DemoTables.ReactionTypes, DemoTables.ChannelReactions, DemoTables.Keywords
        };
        private static readonly DemoTable[] EmailTables =
            { DemoTables.EmailAddresses, DemoTables.SentEmails, DemoTables.EmailRecipients };
        private static readonly DemoTable[] PageTables =
            { DemoTables.PageFields, DemoTables.PageMetadata, DemoTables.PageComments, DemoTables.PageLikes };

        /// <summary>
        /// Tables the collaboration generator no longer owns.
        /// </summary>
        /// <remarks>
        /// Sessions, hits, searches and element clicks moved to <c>DemoWebActivityGenerator</c>, which
        /// walks a real navigation model rather than emitting a fixed three-page path. They are
        /// asserted against the full generator in
        /// <see cref="Collaboration_WebClicksAndSearchesJoinTheBaseNavigationPath"/>, because a click
        /// can only be checked against the hit it belongs to.
        /// </remarks>
        private static readonly DemoTable[] WebFactTables =
            { DemoTables.ClickTitles, DemoTables.ClickClasses, DemoTables.Clicks, DemoTables.SearchTerms, DemoTables.Searches };
        private static readonly DemoTable[] OneDriveTables = { DemoTables.OneDriveStorage };
        private static readonly DemoTable[] EngageTables = { DemoTables.EngageGroups, DemoTables.EngageGroupActivity };

        private static DemoOptions Options(string areas = "all", string mix = "30,35,20,8,7", int users = 80) =>
            DemoOptions.Parse(new[] { "--preview", "--users", users.ToString(CultureInfo.InvariantCulture),
                "--days", "90", "--as-of", "2026-09-01", "--areas", areas, "--mix", mix }, DateTime.UtcNow);

        [TestMethod]
        public void Collaboration_AllImportedTablesHaveDeterministicRowsAndValidWidths()
        {
            var options = Options();
            var first = Generate(options);
            var second = Generate(options);
            var expected = TeamsTables.Concat(EmailTables).Concat(PageTables)
                .Concat(OneDriveTables).Concat(EngageTables).Concat(new[] { DemoTables.Languages }).ToArray();
            CollectionAssert.AreEquivalent(expected.Select(t => t.Name).ToArray(), first.Rows.Keys.Select(t => t.Name).ToArray());
            foreach (var table in expected)
            {
                Assert.IsTrue(first.For(table).Count > 0, table.Name);
                Assert.AreEqual(first.For(table).Count, second.For(table).Count, table.Name);
                for (int i = 0; i < first.For(table).Count; i++)
                    CollectionAssert.AreEqual(first.For(table)[i], second.For(table)[i], table.Name);
            }
            foreach (var table in new[] { DemoTables.ChannelStats, DemoTables.SentEmails, DemoTables.PageComments })
            {
                string score = table == DemoTables.SentEmails ? "cognitive_score" : "sentiment_score";
                var scores = first.For(table).Select(r => first.Value(table, r, score)).ToArray();
                Assert.IsTrue(scores.Any(s => s == null), table.Name + ": unknown cognitive results");
                Assert.IsTrue(scores.Where(s => s != null).Select(Convert.ToDouble).Distinct().Count() >= 3,
                    table.Name + ": varied sentiment");
            }
        }

        [TestMethod]
        public void Collaboration_EachAreaWritesOnlyItsOwnFactsAndRequiredLookups()
        {
            var full = Generate(Options());
            var groups = new Dictionary<string, DemoTable[]>
            {
                ["teams"] = TeamsTables.Concat(new[] { DemoTables.Languages }).ToArray(),
                ["sent-email"] = EmailTables,
                ["sharepoint"] = new DemoTable[0],
                ["web"] = PageTables.Concat(new[] { DemoTables.Languages }).ToArray(),
                ["onedrive"] = OneDriveTables,
                ["engage"] = EngageTables,
                ["copilot-history"] = new[] { DemoTables.Keywords, DemoTables.Languages }
            };
            foreach (var group in groups)
            {
                var capture = Generate(Options(group.Key));
                CollectionAssert.AreEquivalent(group.Value.Select(t => t.Name).ToArray(),
                    capture.Rows.Keys.Select(t => t.Name).ToArray(), group.Key);
                foreach (var table in group.Value)
                {
                    Assert.AreEqual(full.For(table).Count, capture.For(table).Count, table.Name);
                    for (int row = 0; row < full.For(table).Count; row++)
                        CollectionAssert.AreEqual(full.For(table)[row], capture.For(table)[row],
                            table.Name + ": selecting an area must preserve logical ids and row contents");
                }
            }
            foreach (string unrelated in new[] { "directory", "outlook", "office", "copilot", "powerapps" })
                Assert.AreEqual(0, Generate(Options(unrelated)).Rows.Count, unrelated);
        }

        [TestMethod]
        public void Collaboration_NeverActiveUsersHaveNoActionsEvenWithOtherUsersReceivingMail()
        {
            var options = Options(mix: "0,0,0,100,0");
            var capture = Generate(options);
            foreach (var table in new[]
            {
                DemoTables.CallRecords, DemoTables.CallSessions, DemoTables.CallFeedback, DemoTables.CallFailures,
                DemoTables.ChannelStats, DemoTables.ChannelKeywords, DemoTables.ChannelLanguages, DemoTables.ChannelReactions,
                DemoTables.SentEmails, DemoTables.EmailRecipients, DemoTables.PageComments, DemoTables.PageLikes,
                DemoTables.OneDriveStorage, DemoTables.EngageGroupActivity
            })
                Assert.AreEqual(0, capture.For(table).Count, table.Name);
        }

        [TestMethod]
        public void Collaboration_UserActionsFollowWorkdaysLeaveInactiveWindowsAndCommonWorkloads()
        {
            var options = Options();
            var capture = Generate(options);
            var population = new DemoPopulation(options);
            var users = Enumerable.Range(1, options.Users).ToDictionary(i => i, population.User);
            var timelines = users.ToDictionary(p => p.Key, p => new DemoTimeline(options, p.Value));
            var mappings = new[]
            {
                Tuple.Create(DemoTables.CallRecords, "organizer_id", "start", "teams"),
                Tuple.Create(DemoTables.CallSessions, "attendee_user_id", "start", "teams"),
                Tuple.Create(DemoTables.ChannelReactions, "user_id", "date", "messages"),
                Tuple.Create(DemoTables.SentEmails, "user_id", "sent_date", "email"),
                Tuple.Create(DemoTables.PageComments, "user_id", "created", "sharepoint"),
                Tuple.Create(DemoTables.PageLikes, "user_id", "created", "sharepoint"),
                Tuple.Create(DemoTables.OneDriveStorage, "user_id", "date", "onedrive")
            };
            foreach (var map in mappings)
                foreach (var row in capture.For(map.Item1))
                {
                    int id = (int)capture.Value(map.Item1, row, map.Item2);
                    var time = (DateTime)capture.Value(map.Item1, row, map.Item3);
                    var user = users[id];
                    int day = (time.Date - options.Start).Days;
                    Assert.IsTrue(time >= options.Start && time < options.AsOf, map.Item1.Name);
                    Assert.IsTrue(DemoCalendar.IsWorkingDate(time), map.Item1.Name);
                    Assert.IsFalse(DemoTimeline.IsOnLeave(id, day), map.Item1.Name);
                    Assert.AreNotEqual(DemoCohort.Zero, user.Cohort);
                    if (user.Cohort == DemoCohort.Inactive) Assert.IsTrue(options.Days - day > 60);
                    var activity = timelines[id].Day(day);
                    int count = map.Item4 == "teams" ? activity.Meetings : map.Item4 == "messages" ? activity.Messages
                        : map.Item4 == "email" ? activity.Sent : map.Item4 == "onedrive" ? activity.OneDriveFiles : activity.SharePointFiles;
                    Assert.IsTrue(count > 0, map.Item1.Name + ": no invented user activity");
                }
        }

        [TestMethod]
        public void Collaboration_CallsHaveDistinctAttendeesBoundedIntervalsAndValidModalities()
        {
            var capture = Generate(Options("teams"));
            var calls = capture.For(DemoTables.CallRecords).ToDictionary(r => (int)r[0]);
            var sessions = capture.For(DemoTables.CallSessions).ToDictionary(r => (int)r[0]);
            var participants = new HashSet<string>();
            foreach (var session in sessions.Values)
            {
                int user = (int)session[1], callId = (int)session[4];
                var call = calls[callId];
                Assert.AreNotEqual((int)call[1], user, "Importer stores the organizer on the call, not as their own attendee.");
                Assert.IsTrue(participants.Add(callId + ":" + user), "Duplicate attendee");
                Assert.IsTrue((DateTime)session[2] >= (DateTime)call[4]);
                Assert.IsTrue((DateTime)session[3] <= (DateTime)call[5]);
                Assert.IsTrue((DateTime)session[2] < (DateTime)session[3]);
            }
            foreach (var call in calls.Values) Assert.IsTrue((DateTime)call[4] < (DateTime)call[5]);
            var links = capture.For(DemoTables.CallSessionModalities);
            Assert.AreEqual(links.Count, links.Select(r => r[0] + ":" + r[1]).Distinct().Count());
            foreach (var session in sessions)
                Assert.IsTrue(links.Any(r => (int)r[0] == 1 && (int)r[1] == session.Key), "Every session carries audio.");
            foreach (var row in capture.For(DemoTables.CallFeedback))
                Assert.IsTrue(participants.Contains(row[3] + ":" + row[2]));
            foreach (var row in capture.For(DemoTables.CallFailures)) Assert.IsTrue(calls.ContainsKey((int)row[2]));
            CollectionAssert.AreEquivalent(new[] { "peerToPeer", "groupCall" },
                capture.For(DemoTables.CallTypes).Select(r => (string)r[1]).ToArray());
        }

        [TestMethod]
        public void Collaboration_ChannelAndGroupSummariesAreUniqueAndDerivedFromUserDays()
        {
            var options = Options();
            var capture = Generate(options);
            var population = new DemoPopulation(options);
            var channels = new Dictionary<string, int>();
            var groups = new Dictionary<string, int[]>();
            for (int id = 1; id <= options.Users; id++)
            {
                var user = population.User(id);
                var timeline = new DemoTimeline(options, user);
                for (int day = 0; day < options.Days; day++)
                {
                    var activity = timeline.Day(day);
                    if (activity.Messages > 0)
                    {
                        int channel = user.Department * 2 + (int)(DemoRandom.Value(options.Seed, id, day, 1020) % 2) + 1;
                        string key = channel + ":" + day;
                        channels.TryGetValue(key, out int count);
                        channels[key] = count + Math.Max(1, activity.Messages / 4);
                    }
                    if (activity.EngageRead > 0 && options.Start.AddDays(day) <= options.ReportEnd)
                    {
                        string key = (user.Department + 1) + ":" + day;
                        if (!groups.TryGetValue(key, out var counts)) groups.Add(key, counts = new int[3]);
                        counts[0] += activity.EngageRead / 3;
                        counts[1] += activity.EngageRead;
                        counts[2] += activity.EngageRead / 2;
                    }
                }
            }
            var stats = capture.For(DemoTables.ChannelStats);
            Assert.AreEqual(channels.Count, stats.Count);
            var seen = new HashSet<string>();
            foreach (var row in stats)
            {
                string key = row[4] + ":" + (((DateTime)row[3] - options.Start).Days);
                Assert.IsTrue(seen.Add(key), "Only one channel/day row");
                Assert.AreEqual(channels[key], (int)row[1]);
            }
            var groupRows = capture.For(DemoTables.EngageGroupActivity);
            Assert.AreEqual(groups.Count, groupRows.Count);
            seen.Clear();
            foreach (var row in groupRows)
            {
                string key = row[4] + ":" + (((DateTime)row[5] - options.Start).Days);
                Assert.IsTrue(seen.Add(key), "Only one group/day row");
                CollectionAssert.AreEqual(groups[key], new[] { (int)row[0], (int)row[1], (int)row[2] });
                Assert.IsTrue((int)row[3] > 0);
                Assert.AreEqual(row[5], row[6]);
            }
        }

        [TestMethod]
        public void Collaboration_EmailRecipientBridgeAndPageMetadataHaveValidUniqueKeys()
        {
            var capture = Generate(Options());
            var addresses = capture.For(DemoTables.EmailAddresses).ToDictionary(r => (int)r[0], r => (string)r[1]);
            var emails = capture.For(DemoTables.SentEmails).ToDictionary(r => (int)r[0]);
            var recipients = capture.For(DemoTables.EmailRecipients);
            Assert.AreEqual(recipients.Count, recipients.Select(r => r[0] + ":" + r[1]).Distinct().Count());
            foreach (var row in recipients)
            {
                Assert.IsTrue(emails.ContainsKey((int)row[0]));
                Assert.IsTrue(addresses.ContainsKey((int)row[1]));
            }
            Assert.AreEqual(emails.Count, emails.Values.Select(r => r[3]).Distinct().Count(), "Graph message ids are unique.");
            foreach (var row in emails.Values)
            {
                Assert.AreEqual(row[5], row[6], "Sender address and user identify the same synthetic person.");
                Assert.IsTrue(recipients.Any(r => (int)r[0] == (int)row[0]));
                Assert.IsTrue(addresses[(int)row[5]].All(c => c < 128), "UPNs are ASCII.");
            }
            var metadata = capture.For(DemoTables.PageMetadata);
            Assert.AreEqual(metadata.Count, metadata.Select(r => r[0] + ":" + r[1]).Distinct().Count());
            Assert.IsTrue(metadata.Any(r => r[3] is Guid), "Taxonomy ids are actually imported as tag_guid.");
            Assert.IsTrue(metadata.Any(r => ((string)r[2]).Any(c => c > 127)), "Unicode in nvarchar fields");
            var comments = capture.For(DemoTables.PageComments).ToDictionary(r => (int)r[0]);
            Assert.IsTrue(comments.Values.Any(r => r[4] != null), "Include comment replies.");
            foreach (var comment in comments.Values.Where(r => r[4] != null))
            {
                var parent = comments[(int)comment[4]];
                Assert.AreEqual(parent[6], comment[6]);
                Assert.IsTrue((DateTime)parent[7] < (DateTime)comment[7]);
            }
            var likes = capture.For(DemoTables.PageLikes);
            Assert.AreEqual(likes.Count, likes.Select(r => r[0] + ":" + r[1]).Distinct().Count(), "One like/user/page");
        }

        [TestMethod]
        public void Collaboration_WebClicksAndSearchesJoinTheBaseNavigationPath()
        {
            var options = Options("web");
            var capture = new Capture();
            new DemoGenerator(options).Generate(capture, new DemoSummary(), null);
            Assert.IsTrue(DemoTables.Hits.SupplyIdentity, "Clicks require the base generator's logical hit ids.");

            foreach (var table in WebFactTables)
                Assert.IsTrue(capture.For(table).Count > 0, table.Name + ": the web area must fill its own facts.");

            var hits = capture.For(DemoTables.Hits).ToDictionary(r => (int)capture.Value(DemoTables.Hits, r, "id"));
            var sessions = capture.For(DemoTables.Sessions).ToDictionary(r => (int)r[0]);
            var terms = new HashSet<int>(capture.For(DemoTables.SearchTerms).Select(r => (int)r[0]));
            var urls = new HashSet<int>(capture.For(DemoTables.Urls).Select(r => (int)r[0]));

            foreach (var click in capture.For(DemoTables.Clicks))
            {
                var hit = hits[(int)click[3]];
                var hitTime = (DateTime)capture.Value(DemoTables.Hits, hit, "hit_timestamp");
                Assert.IsTrue((DateTime)click[4] > hitTime);
                Assert.IsTrue(((DateTime)click[4] - hitTime).TotalSeconds
                    < (double)capture.Value(DemoTables.Hits, hit, "seconds_on_page"));
                Assert.IsTrue(sessions.ContainsKey((int)capture.Value(DemoTables.Hits, hit, "session_id")));
                Assert.AreEqual(capture.Value(DemoTables.Hits, hit, "url_id"), click[0],
                    "A click belongs to the page it was recorded on.");
            }

            // A search is what put the visitor on the results page, so it must sit just BEFORE a hit
            // in the same visit. If it landed after the visit's last hit, every search would look
            // like a dead end and the report's search-effectiveness measure would be meaningless.
            var hitsBySession = capture.For(DemoTables.Hits)
                .GroupBy(r => (int)capture.Value(DemoTables.Hits, r, "session_id"))
                .ToDictionary(g => g.Key, g => g.Select(r => (DateTime)capture.Value(DemoTables.Hits, r, "hit_timestamp"))
                    .OrderBy(t => t).ToList());

            int searchesFollowedByAPageView = 0;
            foreach (var search in capture.For(DemoTables.Searches))
            {
                int session = (int)search[0];
                Assert.IsTrue(sessions.ContainsKey(session));
                Assert.IsTrue(terms.Contains((int)search[1]));
                var when = (DateTime)search[2];
                var sessionHits = hitsBySession[session];
                Assert.IsTrue(when >= sessionHits[0].AddMinutes(-1) && when <= sessionHits[sessionHits.Count - 1],
                    "A search happens inside the visit it belongs to.");
                if (sessionHits.Any(t => t > when.AddSeconds(Common.Entities.SpoWebActivity.WebActivitySql.SearchDeadEndGraceSeconds)))
                    searchesFollowedByAPageView++;
            }

            Assert.IsTrue(searchesFollowedByAPageView > 0, "Some searches must lead somewhere...");
            Assert.IsTrue(searchesFollowedByAPageView < capture.For(DemoTables.Searches).Count,
                "...and some must not, or the report's dead-end measure has nothing to find.");

            foreach (var hit in capture.For(DemoTables.Hits))
            {
                Assert.IsTrue(urls.Contains((int)capture.Value(DemoTables.Hits, hit, "url_id")));
                Assert.AreEqual(capture.Value(DemoTables.Hits, hit, "url_id"),
                    capture.Value(DemoTables.Hits, hit, "page_title_id"),
                    "Every page has exactly one title, so the two ids are the same.");
            }
        }

        [TestMethod]
        public void Collaboration_WebNavigationHasTheShapeTheActivityReportMeasures()
        {
            var capture = new Capture();
            new DemoGenerator(Options("web")).Generate(capture, new DemoSummary(), null);

            var bySession = capture.For(DemoTables.Hits)
                .GroupBy(r => (int)capture.Value(DemoTables.Hits, r, "session_id"))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(r => (DateTime)capture.Value(DemoTables.Hits, r, "hit_timestamp"))
                          .ThenBy(r => (int)capture.Value(DemoTables.Hits, r, "id"))
                          .ToList());

            Assert.IsTrue(bySession.Count > 0);

            // Bounce rate is a headline figure on the report, so the demo has to contain both kinds of
            // visit. A generator that always emitted the same depth made it a constant.
            Assert.IsTrue(bySession.Values.Any(v => v.Count == 1), "Single-page visits (bounces).");
            Assert.IsTrue(bySession.Values.Any(v => v.Count > 2), "Multi-page visits.");

            var entries = bySession.Values
                .Select(v => (int)capture.Value(DemoTables.Hits, v[0], "url_id")).Distinct().Count();
            var exits = bySession.Values
                .Select(v => (int)capture.Value(DemoTables.Hits, v[v.Count - 1], "url_id")).Distinct().Count();
            Assert.IsTrue(entries > 1, "Entry pages must vary or the entry-page table is one row.");
            Assert.IsTrue(exits > 1, "Exit pages must vary or the exit-page table is one row.");

            var steps = bySession.Values
                .SelectMany(v => v.Zip(v.Skip(1), (a, b) =>
                    (int)capture.Value(DemoTables.Hits, a, "url_id") + "->"
                    + (int)capture.Value(DemoTables.Hits, b, "url_id")))
                .Distinct().Count();
            Assert.IsTrue(steps > 5, "Journeys must branch, or the transitions table shows one route.");

            var loads = capture.For(DemoTables.Hits)
                .Select(r => (double)capture.Value(DemoTables.Hits, r, "page_load_time")).ToList();
            Assert.IsTrue(loads.Max() > 3.0, "A genuinely slow page, for the slowest-pages table.");
            Assert.IsTrue(loads.Min() < 1.5, "And fast pages, so the distribution has a shape.");

            var dwell = capture.For(DemoTables.Hits)
                .Select(r => (double)capture.Value(DemoTables.Hits, r, "seconds_on_page")).Distinct().Count();
            Assert.IsTrue(dwell > 20, "Dwell time must vary; a constant column proves nothing.");

            // Mobile share, unresolved locations and multiple browser versions are each a panel on the
            // report, and each is empty if the generator only ever emits one value.
            var devices = capture.For(DemoTables.Devices).ToDictionary(r => (int)r[0], r => (string)r[1]);
            var deviceIds = capture.For(DemoTables.Hits)
                .Select(r => (int)capture.Value(DemoTables.Hits, r, "device_id")).Distinct().ToList();
            Assert.IsTrue(deviceIds.Any(id => DemoWebCatalogue.IsMobile(devices[id])), "Some mobile traffic.");
            Assert.IsTrue(deviceIds.Any(id => !DemoWebCatalogue.IsMobile(devices[id])), "And some desktop traffic.");

            Assert.IsTrue(capture.For(DemoTables.Hits)
                .Any(r => capture.Value(DemoTables.Hits, r, "city_id") == null),
                "Some page views have no resolved location, as real geo-IP does.");
            Assert.IsTrue(capture.For(DemoTables.Hits)
                .Any(r => capture.Value(DemoTables.Hits, r, "location_province_id") != null),
                "And most do, so the regions panel is not empty.");

            Assert.IsTrue(capture.For(DemoTables.Hits)
                .Select(r => (int)capture.Value(DemoTables.Hits, r, "agent_id")).Distinct().Count() > 2,
                "Several browser versions, so the browser table can show an old build.");

            var pageViews = capture.For(DemoTables.Hits)
                .GroupBy(r => (int)capture.Value(DemoTables.Hits, r, "url_id"))
                .ToDictionary(g => g.Key, g => g.Count());
            Assert.IsTrue(pageViews.Values.Max() > pageViews.Values.Min() * 10,
                "Traffic must be concentrated, so the quiet-page list has candidates.");
        }

        [TestMethod]
        public void Collaboration_SchemaUsesImportedSpellingsAndDependencyOrder()
        {
            Assert.AreEqual("team_membership_log", DemoTables.TeamMemberships.Name);
            Assert.AreEqual("teams_user_channel_reactions", DemoTables.ChannelReactions.Name);
            Assert.AreEqual("teams_channel_stats_log_langs", DemoTables.ChannelLanguages.Name);
            Assert.AreEqual("call_session_call_modalities", DemoTables.CallSessionModalities.Name);
            Assert.AreEqual("onedrive_usage_activity_log", DemoTables.OneDriveStorage.Name);
            Assert.AreEqual("yammer_group_activity_log", DemoTables.EngageGroupActivity.Name);
            Assert.AreEqual("file_metadata_property_values", DemoTables.PageMetadata.Name);
            Assert.AreEqual("searches", DemoTables.Searches.Name);
            var all = DemoTables.All.ToList();
            Assert.AreEqual(all.Count, all.Select(t => t.Name).Distinct().Count(), "No duplicate shared lookup registration.");
            foreach (var pair in new[]
            {
                Tuple.Create(DemoTables.Users, DemoTables.CallRecords),
                Tuple.Create(DemoTables.CallTypes, DemoTables.CallRecords),
                Tuple.Create(DemoTables.CallRecords, DemoTables.CallSessions),
                Tuple.Create(DemoTables.CallSessions, DemoTables.CallSessionModalities),
                Tuple.Create(DemoTables.TeamDefinitions, DemoTables.TeamChannels),
                Tuple.Create(DemoTables.TeamChannels, DemoTables.ChannelStats),
                Tuple.Create(DemoTables.ChannelStats, DemoTables.ChannelKeywords),
                Tuple.Create(DemoTables.EmailAddresses, DemoTables.SentEmails),
                Tuple.Create(DemoTables.SentEmails, DemoTables.EmailRecipients),
                Tuple.Create(DemoTables.Hits, DemoTables.Clicks),
                Tuple.Create(DemoTables.Sessions, DemoTables.Searches)
            }) Assert.IsTrue(all.IndexOf(pair.Item1) < all.IndexOf(pair.Item2), pair.Item2.Name);
            Assert.IsFalse(all.Any(t => t.Name == "teams_channel_user_log"), "Unregistered entity has no production import.");
            Assert.IsFalse(all.Any(t => t.Name.StartsWith("teams_addons", StringComparison.Ordinal)), "Deprecated add-on installs are not synthetic successes.");
        }

        /// <summary>
        /// The flush order must not depend on the order DemoTables' partial files are compiled in. This
        /// project links DemoTables.cs after the other two parts - the order Tests.FakeDataGen's own glob
        /// compiles them on Windows - so the core tables register LAST, and only the block ordering puts
        /// the parents back in front of their children.
        /// </summary>
        [TestMethod]
        public void Tables_FlushInDeclaringFileOrderWhateverTheCompileOrder()
        {
            var all = DemoTables.All.ToList();
            Assert.IsTrue(all.All(t => t.Block > 0), "Every table belongs to a declared block.");
            CollectionAssert.AreEqual(all.OrderBy(t => t.Block).ToList(), all, "All is ordered by block.");
            Assert.AreEqual(DemoTables.CoreBlock, DemoTables.Users.Block);
            Assert.AreEqual(DemoTables.CoreBlock, DemoTables.Hits.Block);
            Assert.AreEqual(DemoTables.CollaborationBlock, DemoTables.Clicks.Block);
            Assert.AreEqual(DemoTables.PowerPlatformBlock, DemoTables.PowerApps.Block);
        }

        [TestMethod]
        public void Collaboration_SinglePersonNeverCreatesAnUnknownCallAttendee()
        {
            var options = Options("teams,sent-email", "100,0,0,0,0", 1);
            var capture = Generate(options);
            Assert.IsTrue(capture.For(DemoTables.CallRecords).Count > 0);
            Assert.AreEqual(0, capture.For(DemoTables.CallSessions).Count, "Organizer is the only participant.");
            Assert.IsTrue(capture.For(DemoTables.SentEmails).Count > 0);
            Assert.IsTrue(capture.For(DemoTables.CallRecords).All(r => (int)r[1] == 1));
        }

        private static Capture Generate(DemoOptions options)
        {
            var capture = new Capture();
            var population = new DemoPopulation(options);
            var generator = new DemoCollaborationGenerator(options, population, new DemoCalendar(options), capture);
            generator.WriteDimensions();
            for (int id = 1; id <= options.Users; id++)
            {
                var user = population.User(id);
                var timeline = new DemoTimeline(options, user);
                for (int day = 0; day < options.Days; day++) generator.WriteDay(user, day, timeline.Day(day));
            }
            generator.WriteSummaries();
            return capture;
        }

        private sealed class Capture : IDemoSink
        {
            public Dictionary<DemoTable, List<object[]>> Rows { get; } = new Dictionary<DemoTable, List<object[]>>();
            public List<object[]> For(DemoTable table) => Rows.TryGetValue(table, out var rows) ? rows : new List<object[]>();
            public object Value(DemoTable table, object[] row, string column) =>
                row[table.Columns.Select(c => c.Name).ToList().IndexOf(column)];
            public void Write(DemoTable table, params object[] values)
            {
                table.ValidateValues(values);
                if (!Rows.TryGetValue(table, out var rows)) Rows.Add(table, rows = new List<object[]>());
                rows.Add((object[])values.Clone());
            }
            public void Flush() { }
            public void Dispose() { }
        }
    }
}
