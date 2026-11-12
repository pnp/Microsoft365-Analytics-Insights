extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Teams Explorer overview and the rest of the adoption tab for a reader without See PII (#844, S4-1, gap G5 of #872):
    /// read over the people the adoption breakdown is made of - not counting the activity of accounts disabled since - and
    /// holding every week, segment, lifecycle count and platform to five people, with whatever would give a withheld one
    /// back. A reader with See PII is served the section exactly as before, and the two are never served each other's.
    /// </summary>
    /// <remarks>
    /// The synthetic tenant: 120 enabled directory people, 40 of them active, and one account, disabled since, that was
    /// active in the second week and posted 450 channel messages. A reader with See PII is shown that account's activity, as
    /// the page always showed it; the breakdown, built from the directory, never contained it.
    /// </remarks>
    [TestClass]
    public class TeamsExplorerOverviewFloorTests
    {
        private const int Floor = 5;

        private static readonly DateTime Week1 = new DateTime(2026, 10, 5);
        private static readonly DateTime Week2 = new DateTime(2026, 10, 12);
        private static readonly DateTime Week3 = new DateTime(2026, 10, 19);
        private static readonly DateTime Week4 = new DateTime(2026, 10, 26);

        #region The overview, served

        [TestMethod]
        public async Task Overview_WithoutSeePii_IsReadOverTheBreakdownsPopulation_AndNoWeekOrSegmentOrTotalLessTheRowsShownIsUnderFive()
        {
            var store = new PopulationStore();
            using (var cache = new MemoryCache("g5-overview-floor"))
            using (var host = Host(store, cache))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/overview?days=28"));

                CollectionAssert.AreEqual(new[] { true }, store.OverviewPopulations.ToArray(), "Read once, over the directory population.");
                Assert.AreEqual(Floor, (int)json["minimumGroupSize"]);
                Assert.IsTrue((bool)json["weeklyCallsWithheld"]);
                Assert.IsNull(json["usageWithheld"], "Forty people were active, so the KPIs stand.");

                // The KPIs are this population's: the disabled account's 450 channel messages are not in them.
                var kpis = json["kpis"];
                Assert.AreEqual(40, (int)kpis["activeUsers"]);
                Assert.AreEqual(120, (int)kpis["knownUsers"]);
                Assert.AreEqual(1000, (long)kpis["channelMessages"]);
                Assert.AreEqual(90, (long)kpis["calls"], "The calls KPI is the Meetings tab's, as that tab shows it.");

                // The week three people were active in is withheld, and so is the smallest week shown (12 people), so that
                // the period's messages less the weeks shown is those two weeks together, never the three people's.
                var trend = json["trend"];
                CollectionAssert.AreEqual(new bool?[] { true, null, null, true }, trend.Select(p => (bool?)p["withheld"]).ToArray());
                foreach (var withheld in trend.Where(p => (bool?)p["withheld"] == true))
                {
                    foreach (var field in new[] { "activeUsers", "channelMessages", "privateMessages", "meetingsAttended", "calls" })
                    {
                        Assert.AreEqual(JTokenType.Null, withheld[field].Type, field + ": withheld, never 0.");
                    }
                }
                Assert.IsTrue(trend.All(p => p["calls"].Type == JTokenType.Null), "Weekly calls are the Meetings tab's to floor.");
                var shownChannel = trend.Where(p => (bool?)p["withheld"] != true).Sum(p => (long)p["channelMessages"]);
                Assert.AreEqual(150, (long)kpis["channelMessages"] - shownChannel, "Weeks 1 and 4 together: 30 + 120.");
                var shownPrivate = trend.Where(p => (bool?)p["withheld"] != true).Sum(p => (long)p["privateMessages"]);
                Assert.AreEqual(70, (long)kpis["privateMessages"] - shownPrivate);

                // Power (3 people) is withheld, with the smallest active segment shown (Light, 12), so that the active people
                // less the segments shown is 15, never 3. Shares go too: of the measured total, they would give Dormant back.
                var mix = json["segmentMix"].ToDictionary(s => (string)s["segment"]);
                Assert.IsTrue((bool)mix["Power"]["withheld"]);
                Assert.IsTrue((bool)mix["Light"]["withheld"]);
                Assert.AreEqual(25, (int)mix["Regular"]["users"]);
                Assert.AreEqual(78, (int)mix["Dormant"]["users"]);
                Assert.AreEqual(15, (int)kpis["activeUsers"] - (int)mix["Regular"]["users"]);
                Assert.IsTrue(mix.Values.All(s => s["sharePct"].Type == JTokenType.Null));
                Assert.AreEqual(JTokenType.Null, mix["Power"]["users"].Type, "Withheld, never 0.");

                // Two licensed people with no usage row are, like a dormant segment of two, withheld.
                Assert.AreEqual(JTokenType.Null, json["dormancy"]["unmeasuredUsers"].Type);

                AssertNoFigureDescribesOneToFourPeople(json);
                StringAssert.Contains(json.ToString(), "\"reach\"", "The judgements are this population's, and stay.");
            }
        }

        [TestMethod]
        public async Task Overview_ASeePiiReader_IsServedTheSectionAsItWas()
        {
            var store = new PopulationStore();
            using (var cache = new MemoryCache("g5-overview-seepii"))
            using (var host = Host(store, cache, PortalRoles.SeePii))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/overview?days=28"));

                CollectionAssert.AreEqual(new[] { false }, store.OverviewPopulations.ToArray(), "Read as it always was.");
                Assert.IsTrue(JToken.DeepEquals(JObject.FromObject(PopulationStore.Overview(directory: false)), json),
                    "A See PII reader's overview is the store's section, unchanged:" + Environment.NewLine + json);
                Assert.AreEqual(1450, (long)json["kpis"]["channelMessages"], "Including the disabled account's activity, as before.");
                Assert.AreEqual(3, (int)json["trend"][0]["activeUsers"]);
                Assert.AreEqual(3, (int)json["segmentMix"].Single(s => (string)s["segment"] == "Power")["users"]);
                Assert.AreEqual(JTokenType.Null, json["minimumGroupSize"].Type);
            }
        }

        [TestMethod]
        public async Task Overview_ReadersWithAndWithoutSeePii_NeverShareACachedSection_InEitherOrder()
        {
            foreach (var seePiiFirst in new[] { false, true })
            {
                var store = new PopulationStore();
                using (var cache = new MemoryCache("g5-overview-order-" + seePiiFirst))
                using (var host = Host(store, cache))
                {
                    var order = seePiiFirst ? new[] { true, false, true, false } : new[] { false, true, false, true };
                    foreach (var seePii in order)
                    {
                        host.Principal = seePii ? PortalTestHost.SignedIn(PortalRoles.SeePii) : PortalTestHost.SignedIn();
                        var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/overview?days=28"));
                        var because = (seePiiFirst ? "See PII first" : "without See PII first") + ", then a reader " + (seePii ? "with" : "without") + " it";

                        Assert.AreEqual(seePii ? 1450 : 1000, (long)json["kpis"]["channelMessages"], because);
                        Assert.AreEqual(seePii ? JTokenType.Null : JTokenType.Integer, json["minimumGroupSize"].Type, because);
                        Assert.AreEqual(seePii ? JTokenType.Integer : JTokenType.Null, json["trend"][0]["activeUsers"].Type, because);
                    }

                    // One build per kind of reader; the second request of each kind is the cached one.
                    CollectionAssert.AreEquivalent(new[] { false, true }, store.OverviewPopulations.ToArray());
                }
            }
        }

        [TestMethod]
        public async Task Overview_WithoutSeePii_WhenOneToFourPeopleWereActive_WithholdsEveryUsageKpi_AndTheJudgementsWordedFromThem()
        {
            var store = new PopulationStore { DirectoryActiveUsers = 3 };
            using (var cache = new MemoryCache("g5-overview-few"))
            using (var host = Host(store, cache))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/overview?days=28"));

                Assert.IsTrue((bool)json["usageWithheld"]);
                foreach (var kpi in new[]
                {
                    "knownUsers", "activeUsers", "reachPct", "channelMessages", "privateMessages", "openCollaborationPct",
                    "meetingsAttended", "meetingsOrganised", "meetingsPerActiveUser", "audioHours", "videoSharePct", "screenShareSharePct",
                })
                {
                    Assert.AreEqual(JTokenType.Null, json["kpis"][kpi].Type, kpi + ": withheld, never 0.");
                }
                Assert.AreEqual(90, (long)json["kpis"]["calls"]);
                Assert.AreEqual(12, (int)json["kpis"]["activeTeams"]);

                var keys = json["judgements"].Select(j => (string)j["key"]).ToArray();
                Assert.IsFalse(keys.Intersect(TeamsExplorerScoring.UsageJudgementKeys).Any(),
                    "A sentence about the activity of three people is about those three people: " + string.Join(", ", keys));
                CollectionAssert.Contains(keys, "team-sprawl", "Teams are not people.");

                AssertNoFigureDescribesOneToFourPeople(json);
            }
        }

        #endregion

        #region The adoption tab, served

        [TestMethod]
        public async Task Adoption_WithoutSeePii_IsReadOverTheBreakdownsPopulation_AndHoldsRhythmLifecycleSegmentsAndPlatformsToFivePeople()
        {
            var store = new PopulationStore();
            using (var cache = new MemoryCache("g5-adoption-floor"))
            using (var host = Host(store, cache))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle"));

                CollectionAssert.AreEqual(new[] { true }, store.AdoptionPopulations.ToArray(), "Read once, over the directory population.");
                Assert.AreEqual(Floor, (int)json["minimumGroupSize"]);

                // The breakdown is folded as before, and its active people are the 40 the rest of the tab describes.
                var breakdown = json["breakdown"];
                Assert.IsFalse(json.ToString().Contains("Contoso Four Title"));
                Assert.AreEqual(40, breakdown.Sum(r => (int)r["activeUsers"]));

                // Rhythm: four people active in the last week are withheld; the monthly figure is the 40 of the breakdown,
                // not 41 with the disabled account.
                var rhythm = json["rhythm"];
                Assert.AreEqual(JTokenType.Null, rhythm["weeklyActiveUsers"].Type);
                Assert.AreEqual(40, (int)rhythm["monthlyActiveUsers"]);
                Assert.AreEqual(18.5, (double)rhythm["meanDailyActiveUsers"], 0.0001);

                // Lifecycle: three new people are withheld, with the smallest count shown (7 lapsed), so that the 40 less
                // the returning 30 is 10, never 3.
                var lifecycle = json["lifecycle"];
                Assert.AreEqual(JTokenType.Null, lifecycle["newUsers"].Type);
                Assert.AreEqual(JTokenType.Null, lifecycle["lapsedUsers"].Type);
                Assert.AreEqual(30, (int)lifecycle["returningUsers"]);
                Assert.AreEqual(10, breakdown.Sum(r => (int)r["activeUsers"]) - (int)lifecycle["returningUsers"]);

                // A week with a segment of 1-4 people is withheld whole: the overview publishes that week's active people.
                CollectionAssert.AreEqual(new bool?[] { true, null, true, null },
                    json["segmentTrend"].Select(p => (bool?)p["withheld"]).ToArray());
                Assert.AreEqual(JTokenType.Null, json["segmentTrend"][2]["power"].Type);

                // Platforms: Linux (1) and iOS (3) are withheld, and Mobile (any) with iOS; shares go with them.
                var devices = json["devices"].ToDictionary(d => (string)d["platform"]);
                Assert.IsTrue((bool)devices["Linux"]["withheld"]);
                Assert.IsTrue((bool)devices[TeamsDeviceRow.Ios]["withheld"]);
                Assert.IsTrue((bool)devices[TeamsDeviceRow.AnyMobile]["withheld"], "Mobile (any) less Android would bound iOS.");
                Assert.AreEqual(9, (int)devices[TeamsDeviceRow.Android]["users"]);
                Assert.IsTrue(devices.Values.All(d => d["sharePct"].Type == JTokenType.Null));

                // The engagement mix is the overview's, held the same way.
                var mix = json["segmentMix"].ToDictionary(s => (string)s["segment"]);
                Assert.IsTrue((bool)mix["Power"]["withheld"]);
                Assert.IsTrue((bool)mix["Light"]["withheld"]);

                Assert.IsNull(json["periodActiveUsers"], "It only decides; it is never sent.");
                AssertNoFigureDescribesOneToFourPeople(json);
            }
        }

        [TestMethod]
        public async Task Adoption_ASeePiiReader_IsServedTheSectionAsItWas_AndTheTwoReadersNeverShareACachedSection_InEitherOrder()
        {
            foreach (var seePiiFirst in new[] { false, true })
            {
                var store = new PopulationStore();
                using (var cache = new MemoryCache("g5-adoption-order-" + seePiiFirst))
                using (var host = Host(store, cache))
                {
                    var order = seePiiFirst ? new[] { true, false, true, false } : new[] { false, true, false, true };
                    foreach (var seePii in order)
                    {
                        host.Principal = seePii ? PortalTestHost.SignedIn(PortalRoles.SeePii) : PortalTestHost.SignedIn();
                        var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/adoption?days=28&groupBy=jobTitle"));
                        var because = (seePiiFirst ? "See PII first" : "without See PII first") + ", then a reader " + (seePii ? "with" : "without") + " it";

                        if (seePii)
                        {
                            Assert.IsTrue(JToken.DeepEquals(JObject.FromObject(PopulationStore.Adoption(directory: false, top: TeamsExplorerQuery.DefaultTop)), json),
                                because + ": a See PII reader's adoption tab is the store's section, unchanged:" + Environment.NewLine + json);
                            Assert.AreEqual(41, (int)json["rhythm"]["monthlyActiveUsers"], because);
                            Assert.AreEqual(4, (int)json["rhythm"]["weeklyActiveUsers"], because);
                            Assert.AreEqual(2, (int)json["devices"].Single(d => (string)d["platform"] == "Linux")["users"], because);
                        }
                        else
                        {
                            Assert.AreEqual(40, (int)json["rhythm"]["monthlyActiveUsers"], because);
                            Assert.AreEqual(JTokenType.Null, json["rhythm"]["weeklyActiveUsers"].Type, because);
                            Assert.IsTrue((bool)json["devices"].Single(d => (string)d["platform"] == "Linux")["withheld"], because);
                        }
                    }

                    CollectionAssert.AreEquivalent(new[] { false, true }, store.AdoptionPopulations.ToArray(), "One build per kind of reader.");
                }
            }
        }

        #endregion

        #region The rules

        [TestMethod]
        public void WithheldParts_WithholdsOneToFour_AndTheSmallestPartShown_ButNeverAZero()
        {
            CollectionAssert.AreEquivalent(new int[0], TeamsSmallGroups.WithheldParts(new long[] { 0, 5, 40 }, Floor).ToArray(),
                "Nobody is not a group, and five is enough.");
            CollectionAssert.AreEquivalent(new[] { 0, 2 }, TeamsSmallGroups.WithheldParts(new long[] { 3, 40, 12 }, Floor).ToArray(),
                "The total less the parts shown is then 15, not 3.");
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, TeamsSmallGroups.WithheldParts(new long[] { 0, 2, 2 }, Floor).ToArray(),
                "With no part of five to join them, the small ones alone.");
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, TeamsSmallGroups.WithheldParts(new long[] { 4, 9, 9 }, Floor).ToArray(),
                "A tie goes to the first part.");
        }

        [TestMethod]
        public void WithoutSmallSegments_ADormantSegmentOfOneToFour_IsWithheldWithEveryShare_SoTheMeasuredTotalCannotGiveItBack()
        {
            var mix = new List<TeamsSegmentSlice> { Slice("Power", 10, 20), Slice("Regular", 20, 40), Slice("Light", 17, 34), Slice("Dormant", 3, 6) };

            var trimmed = TeamsSmallGroups.WithoutSmallSegments(mix, Floor, hideShares: false);

            Assert.IsTrue(trimmed[3].Withheld);
            Assert.IsNull(trimmed[3].Users);
            Assert.AreEqual(47, trimmed.Take(3).Sum(s => s.Users), "The active segments are all five or more, so all shown.");
            Assert.IsTrue(trimmed.All(s => s.SharePct == null), "10 people at 20% would give the measured 50, and 50 less 47 is the 3.");
            Assert.AreEqual(3, mix[3].Users, "The cached mix is never edited.");
            Assert.AreEqual(20d, mix[0].SharePct);

            var none = TeamsSmallGroups.WithoutSmallSegments(new List<TeamsSegmentSlice> { Slice("Power", 10, 50), Slice("Dormant", 10, 50) }, Floor, hideShares: false);
            Assert.AreEqual(50d, none[0].SharePct, "Nothing withheld: the shares stay.");
        }

        [TestMethod]
        public void WithoutSmallPlatforms_WithholdsAnyMobile_WhenIosOrAndroidIs()
        {
            var rows = new List<TeamsDeviceRow>
            {
                new TeamsDeviceRow { Platform = "Windows", Users = 30, SharePct = 100 },
                new TeamsDeviceRow { Platform = TeamsDeviceRow.Android, Users = 2, SharePct = 6.7 },
                new TeamsDeviceRow { Platform = TeamsDeviceRow.Ios, Users = 8, SharePct = 26.7 },
                new TeamsDeviceRow { Platform = TeamsDeviceRow.AnyMobile, Users = 9, SharePct = 30 },
            };

            var trimmed = TeamsSmallGroups.WithoutSmallPlatforms(rows, Floor).ToDictionary(r => r.Platform);

            Assert.IsTrue(trimmed[TeamsDeviceRow.Android].Withheld);
            Assert.IsTrue(trimmed[TeamsDeviceRow.AnyMobile].Withheld, "9 less iOS's 8 is one person on Android only.");
            Assert.AreEqual(8, trimmed[TeamsDeviceRow.Ios].Users);
            Assert.IsNull(trimmed["Windows"].SharePct);
            Assert.AreEqual(2, rows[1].Users, "The cached list is never edited.");
        }

        [TestMethod]
        public void Devices_CarryAKeyThePortalWordsThemFrom_AndAWithheldPlatformKeepsIt()
        {
            var rows = SqlTeamsExplorerStore.BuildDevices(new TeamsExplorerRows.DevicesRow
            {
                MeasuredUsers = 40, UsedWindows = 38, UsedMac = 6, UsedWeb = 20, UsedIos = 3, UsedAndroid = 9, UsedLinux = 1, UsedChromeOs = 5, UsedMobile = 11,
            });

            // The portal names each platform from its key, in the reader's language (#850): "Mobile (any)" is English prose,
            // and the name is only the fallback for a key the portal does not know. serverAuthoredText.test.ts reads this list.
            CollectionAssert.AreEqual(
                new[] { "windows: Windows", "web: Web", "mobile: Mobile (any)", "android: Android", "mac: Mac", "chromeOs: Chrome OS", "ios: iOS", "linux: Linux" },
                rows.Select(r => r.PlatformKey + ": " + r.Platform).ToArray());
            Assert.AreEqual("mobile", (string)JObject.FromObject(rows.Single(r => r.Platform == TeamsDeviceRow.AnyMobile))["platformKey"]);

            // For a reader without See PII, Linux (1) and iOS (3) are withheld, and the mobile total with iOS, as before: each
            // withheld row is a new one, and keeps its key.
            var trimmed = TeamsSmallGroups.WithoutSmallPlatforms(rows, Floor);
            CollectionAssert.AreEqual(new[] { "mobile", "ios", "linux" }, trimmed.Where(r => r.Withheld).Select(r => r.PlatformKey).ToArray());
            CollectionAssert.AreEqual(rows.Select(r => r.PlatformKey).ToArray(), trimmed.Select(r => r.PlatformKey).ToArray());
            Assert.IsTrue(trimmed.Where(r => r.Withheld).All(r => r.Users == null && r.SharePct == null));
        }

        [TestMethod]
        public void Rhythm_WithoutSmallGroups_WithholdsTheDailyMean_WhenThePeriodsActivePeopleAreFew_OrCannotBeRead()
        {
            var rhythm = new TeamsAdoptionRhythm { MeanDailyActiveUsers = 1.5, WeeklyActiveUsers = 2, MonthlyActiveUsers = 3, StickinessPct = 50 };

            var few = rhythm.WithoutSmallGroups(Floor, periodActiveUsers: 3);
            Assert.IsNull(few.MeanDailyActiveUsers);
            Assert.IsNull(few.WeeklyActiveUsers);
            Assert.IsNull(few.MonthlyActiveUsers);
            Assert.IsNull(few.StickinessPct, "Stickiness is the daily mean over the monthly count.");
            Assert.AreEqual(1.5, rhythm.MeanDailyActiveUsers, "The cached rhythm is never edited.");

            // Three people in the last 28 days of a 90-day period that 30 were active in: the mean averages 30 people's days.
            var month = new TeamsAdoptionRhythm { MeanDailyActiveUsers = 12, WeeklyActiveUsers = 0, MonthlyActiveUsers = 3, StickinessPct = 400 };
            Assert.AreEqual(12d, month.WithoutSmallGroups(Floor, periodActiveUsers: 30).MeanDailyActiveUsers);
            Assert.IsNull(month.WithoutSmallGroups(Floor, periodActiveUsers: 30).StickinessPct, "It would give the monthly 3 back.");

            // The lifecycle query failed, so the period's people are unknown: the mean is shown only when the month proves five.
            Assert.IsNull(month.WithoutSmallGroups(Floor, periodActiveUsers: null).MeanDailyActiveUsers);
            var proven = new TeamsAdoptionRhythm { MeanDailyActiveUsers = 12, WeeklyActiveUsers = 20, MonthlyActiveUsers = 25, StickinessPct = 48 };
            Assert.AreEqual(12d, proven.WithoutSmallGroups(Floor, periodActiveUsers: null).MeanDailyActiveUsers);
        }

        [TestMethod]
        public void ThePopulationMarkers_AreRemovedForASeePiiReader_AndNarrowEveryUsageReadToTheBreakdownsPopulationOtherwise()
        {
            var marked = new Dictionary<string, string>
            {
                ["overview-usage"] = TeamsExplorerSql.OverviewUsage,
                ["adoption-rhythm"] = TeamsExplorerSql.AdoptionRhythm(),
                ["adoption-segment-trend"] = TeamsExplorerSql.SegmentTrendHistogram(),
                ["adoption-lifecycle"] = TeamsExplorerSql.AdoptionLifecycle,
                ["adoption-devices"] = TeamsExplorerSql.AdoptionDevices,
            };

            foreach (var statement in marked)
            {
                StringAssert.Contains(statement.Value, "/*population:", statement.Key);
                var asWritten = TeamsExplorerSql.ApplyPopulation(statement.Value, directoryPopulation: false);
                Assert.IsFalse(asWritten.Contains("population") || asWritten.Contains("pop."), statement.Key + ": a See PII reader's statement is as it was.");
                var directory = TeamsExplorerSql.ApplyPopulation(statement.Value, directoryPopulation: true);
                StringAssert.Contains(directory, "FROM dbo.users AS pop WHERE pop.id = ", statement.Key);
                Assert.IsFalse(directory.Contains("/*population:"), statement.Key);
            }

            // The trend has a statement of its own for the directory population, which adds each person's week up first; a
            // reader with See PII runs the one they always did.
            Assert.IsFalse(TeamsExplorerSql.OverviewTrend().Contains("population") || TeamsExplorerSql.OverviewTrend().Contains("pop."));
            StringAssert.Contains(TeamsExplorerSql.OverviewTrendOverDirectory(), "FROM dbo.users AS pop WHERE pop.id = p.user_id");

            // The breakdown and the engagement mix are built from the directory already.
            Assert.IsFalse(TeamsExplorerSql.AdoptionBreakdown("department").Contains("/*population:"));
            Assert.IsFalse(TeamsExplorerSql.SegmentHistogram.Contains("/*population:"));

            // The same people as reach's denominator and the breakdown: EnabledUsersPredicate, on the users aliased pop.
            var enabled = TeamsExplorerSql.EnabledUsersPredicate.Replace("u.", "pop.");
            StringAssert.Contains(TeamsExplorerSql.UsagePopulation, enabled);
            StringAssert.Contains(TeamsExplorerSql.DevicePopulation, enabled);
            StringAssert.Contains(TeamsExplorerSql.PerPersonPopulation, enabled);
            StringAssert.Contains(TeamsExplorerSql.OverviewTrendOverDirectory(), enabled);
        }

        [TestMethod]
        public void TheDirectoryPopulation_IsCachedApart()
        {
            var query = TeamsExplorerQuery.Create(28, DateTime.UtcNow);
            Assert.AreNotEqual(query.CacheKey("overview"), query.WithDirectoryPopulation().CacheKey("overview"));
            Assert.IsFalse(query.DirectoryPopulation);
            Assert.IsTrue(query.WithDirectoryPopulation().DirectoryPopulation);
        }

        #endregion

        #region Helpers

        private static TeamsSegmentSlice Slice(string segment, int users, double share) =>
            new TeamsSegmentSlice { Segment = segment, Label = segment, Users = users, SharePct = share };

        private static PortalTestHost Host(ITeamsExplorerStore store, ObjectCache cache, params string[] roles) =>
            new PortalTestHost(
                new[] { typeof(TeamsExplorerAPIController) },
                PortalTestHost.SignedIn(roles),
                PortalAccessPolicy.Enforcing,
                _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources { UsageReports = true, Calls = true, TeamsAnalytics = true }, null, null, cache));

        /// <summary>
        /// No count of people - a KPI, a week, a segment, a lifecycle count, a platform, a rhythm figure - is 1 to 4. Every
        /// such field is named here, so a new one has to be added and decided.
        /// </summary>
        private static void AssertNoFigureDescribesOneToFourPeople(JObject json)
        {
            var peopleFields = new HashSet<string>(StringComparer.Ordinal)
            {
                "knownUsers", "activeUsers", "users", "unmeasuredUsers", "teamsLicensedUsers",
                "weeklyActiveUsers", "monthlyActiveUsers", "newUsers", "returningUsers", "lapsedUsers", "power", "regular", "light",
            };

            var few = json.Descendants().OfType<JProperty>()
                .Where(p => peopleFields.Contains(p.Name) && p.Value.Type == JTokenType.Integer)
                .Where(p => (long)p.Value > 0 && (long)p.Value < Floor)
                .Select(p => p.Path + " = " + p.Value)
                .ToList();
            Assert.AreEqual(0, few.Count, "Figures about one to four people: " + string.Join(", ", few));
        }

        /// <summary>
        /// The synthetic tenant, as each population reads it. Read as written (a See PII reader), every usage figure also counts
        /// the account disabled since: 450 channel messages and one active person in week 2, one more monthly active person,
        /// one more lapsed, one more on Linux.
        /// </summary>
        private sealed class PopulationStore : ITeamsExplorerStore
        {
            private readonly List<bool> _overview = new List<bool>();
            private readonly List<bool> _adoption = new List<bool>();

            /// <summary>The people active in the directory population; 40 unless a test needs a handful.</summary>
            internal int DirectoryActiveUsers { get; set; } = 40;

            internal IReadOnlyList<bool> OverviewPopulations { get { lock (_overview) return _overview.ToList(); } }
            internal IReadOnlyList<bool> AdoptionPopulations { get { lock (_adoption) return _adoption.ToList(); } }

            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources)
            {
                lock (_overview) _overview.Add(query.DirectoryPopulation);
                var overview = Overview(query.DirectoryPopulation);
                if (DirectoryActiveUsers != 40) overview.Kpis.ActiveUsers = DirectoryActiveUsers;
                return Task.FromResult(overview);
            }

            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query)
            {
                lock (_adoption) _adoption.Add(query.DirectoryPopulation);
                return Task.FromResult(Adoption(query.DirectoryPopulation, query.Top));
            }

            internal static TeamsOverview Overview(bool directory)
            {
                var disabled = directory ? 0 : 1;
                var overview = new TeamsOverview
                {
                    Kpis = new TeamsOverviewKpis
                    {
                        KnownUsers = 120,
                        ActiveUsers = 40,
                        ReachPct = 100.0 / 3,
                        ChannelMessages = 1000 + 450 * disabled,
                        PrivateMessages = 600,
                        OpenCollaborationPct = directory ? 62.5 : 1450.0 / 2050 * 100,
                        MeetingsAttended = 200,
                        MeetingsOrganised = 50,
                        MeetingsPerActiveUser = 5,
                        AudioHours = 80,
                        VideoSharePct = 40,
                        ScreenShareSharePct = 10,
                        Calls = 90,
                        TotalTeams = 30,
                        ActiveTeams = 12,
                        TotalChannels = 100,
                        ActiveChannels = 40,
                    },
                    Dormancy = new TeamsDormancy
                    {
                        Known = false,
                        Reason = TeamsDormancy.IncompleteImport,
                        ExpectedDays = 28,
                        ImportedDays = 27,
                        TeamsLicensedUsers = 110,
                        UnmeasuredUsers = 2,
                    },
                };
                overview.Trend.AddRange(new[]
                {
                    Week(Week1, 3, 30, 20, 6, 4),
                    Week(Week2, 35 + disabled, 400 + 450 * disabled, 250, 80, 40),
                    Week(Week3, 38, 450, 280, 90, 36),
                    Week(Week4, 12, 120, 50, 24, 10),
                });
                overview.SegmentMix.AddRange(new[]
                {
                    Slice("Power", 3, 3 / 1.18), Slice("Regular", 25, 25 / 1.18), Slice("Light", 12, 12 / 1.18), Slice("Dormant", 78, 78 / 1.18),
                });
                overview.Judgements = TeamsExplorerScoring.Judgements(new TeamsJudgementInputs
                {
                    ActiveUsers = 40,
                    KnownUsers = 120,
                    OpenCollaborationPct = overview.Kpis.OpenCollaborationPct.Value,
                    MeetingsPerActiveUser = 5,
                    TotalTeams = 30,
                    ActiveTeams = 12,
                    UsageReportsAvailable = true,
                    CallsAvailable = true,
                    TeamsAnalyticsAvailable = true,
                });
                return overview;
            }

            internal static TeamsAdoption Adoption(bool directory, int top)
            {
                var disabled = directory ? 0 : 1;
                var adoption = new TeamsAdoption
                {
                    GroupBy = "jobTitle",
                    Rhythm = new TeamsAdoptionRhythm { MeanDailyActiveUsers = 18.5, WeeklyActiveUsers = 4, MonthlyActiveUsers = 40 + disabled, StickinessPct = 18.5 / (40 + disabled) * 100 },
                    Lifecycle = new TeamsLifecycle { NewUsers = 3, ReturningUsers = 30, LapsedUsers = 7 + disabled },
                    PeriodActiveUsers = 40 + disabled,
                    Dormancy = new TeamsDormancy { Known = true, ExpectedDays = 28, ImportedDays = 28, TeamsLicensedUsers = 110 },
                };
                adoption.Breakdown.AddRange(new[]
                    {
                        new TeamsDemographicRow { Name = "Contoso Analysts", KnownUsers = 60, ActiveUsers = 25, ReachPct = 41.7, MessagesPerActiveUser = 30 },
                        new TeamsDemographicRow { Name = "Contoso Engineers", KnownUsers = 56, ActiveUsers = 12, ReachPct = 21.4, MessagesPerActiveUser = 40 },
                        new TeamsDemographicRow { Name = "Contoso Four Title", KnownUsers = 4, ActiveUsers = 3, ReachPct = 75, MessagesPerActiveUser = 99 },
                    }.Take(top));
                adoption.SegmentMix.AddRange(new[] { Slice("Power", 3, 2.5), Slice("Regular", 25, 21.2), Slice("Light", 12, 10.2), Slice("Dormant", 78, 66.1) });
                adoption.SegmentTrend.AddRange(new[]
                {
                    new TeamsSegmentTrendPoint { WeekStart = Week1, Power = 0, Regular = 1, Light = 2 },
                    new TeamsSegmentTrendPoint { WeekStart = Week2, Power = 10, Regular = 15, Light = 10 + disabled },
                    new TeamsSegmentTrendPoint { WeekStart = Week3, Power = 12, Regular = 2, Light = 24 },
                    new TeamsSegmentTrendPoint { WeekStart = Week4, Power = 0, Regular = 5, Light = 7 },
                });
                adoption.Devices.AddRange(new[]
                {
                    new TeamsDeviceRow { Platform = "Windows", Users = 38, SharePct = 95 },
                    new TeamsDeviceRow { Platform = TeamsDeviceRow.AnyMobile, Users = 11, SharePct = 27.5 },
                    new TeamsDeviceRow { Platform = TeamsDeviceRow.Android, Users = 9, SharePct = 22.5 },
                    new TeamsDeviceRow { Platform = "Mac", Users = 6, SharePct = 15 },
                    new TeamsDeviceRow { Platform = TeamsDeviceRow.Ios, Users = 3, SharePct = 7.5 },
                    new TeamsDeviceRow { Platform = "Linux", Users = 1 + disabled, SharePct = 2.5 },
                });
                return adoption;
            }

            private static TeamsTrendPoint Week(DateTime weekStart, int active, long channel, long privateMessages, long meetings, long calls) =>
                new TeamsTrendPoint
                {
                    WeekStart = weekStart,
                    ActiveUsers = active,
                    ChannelMessages = channel,
                    PrivateMessages = privateMessages,
                    MeetingsAttended = meetings,
                    Calls = calls,
                };

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(30, 30));
            public Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsMeetings());
            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsCollaboration());
            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable) => Task.FromResult(new TeamsConversations());
            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsPeople());
        }

        #endregion
    }
}
