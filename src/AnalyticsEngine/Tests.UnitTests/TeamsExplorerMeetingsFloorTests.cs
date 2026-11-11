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
    /// The Teams Explorer meetings tab's weekly trend for a reader without See PII (#844). A week whose calls one to four
    /// people attended is withheld; the tab also publishes the calls and call hours (and the mean length) for the whole
    /// window, which the weeks add up to, so the week with the fewest attendees still shown is withheld with it. The
    /// totals less the weeks shown are then always two weeks or more together, never one withheld week's.
    /// </summary>
    /// <remarks>
    /// The synthetic tenant: four weeks of calls, one of which three people attended. A reader with See PII is served
    /// every week, as before.
    /// </remarks>
    [TestClass]
    public class TeamsExplorerMeetingsFloorTests
    {
        private const int Floor = 5;

        private static readonly DateTime Week1 = new DateTime(2026, 10, 5);
        private static readonly DateTime Week2 = new DateTime(2026, 10, 12);
        private static readonly DateTime Week3 = new DateTime(2026, 10, 19);
        private static readonly DateTime Week4 = new DateTime(2026, 10, 26);

        [TestMethod]
        public async Task Meetings_WithoutSeePii_NoTotalLessTheWeeksShown_IsASingleWithheldWeek()
        {
            var store = new MeetingsStore();
            using (var cache = new MemoryCache("meetings-floor"))
            using (var host = Host(store, cache))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/meetings?days=28"));
                var kpis = json["kpis"];
                var trend = json["trend"];

                // Week 2 (three attendees) is withheld, and so is the quietest week shown, week 3 (eight attendees).
                CollectionAssert.AreEqual(new bool?[] { null, true, true, null }, trend.Select(p => (bool?)p["withheld"]).ToArray());
                foreach (var withheld in trend.Where(p => (bool?)p["withheld"] == true))
                {
                    foreach (var field in new[] { "calls", "minutes", "attendees" })
                    {
                        Assert.AreEqual(JTokenType.Null, withheld[field].Type, field + ": withheld, never 0.");
                    }
                }

                var shown = trend.Where(p => (bool?)p["withheld"] != true).ToList();
                Assert.IsTrue(shown.All(p => (int)p["attendees"] >= Floor), "No week shown describes one to four people.");

                // Every total the tab publishes beside the weeks, less the weeks shown, is weeks 2 and 3 together.
                var callsLeft = (long)kpis["calls"] - shown.Sum(p => (long)p["calls"]);
                var minutesLeft = (double)kpis["callHours"] * 60 - shown.Sum(p => (double)p["minutes"]);
                var minutesLeftByMean = (double)kpis["meanDurationMinutes"] * (long)kpis["calls"] - shown.Sum(p => (double)p["minutes"]);
                Assert.AreEqual(6 + 18, callsLeft);
                Assert.AreEqual(180 + 540, minutesLeft, 1e-6);
                Assert.AreEqual(180 + 540, minutesLeftByMean, 1e-6);
                foreach (var week in MeetingsStore.Meetings().Trend.Where(w => w.WeekStart == Week2 || w.WeekStart == Week3))
                {
                    Assert.AreNotEqual(week.Calls, callsLeft, "The calls less the weeks shown are never one withheld week's.");
                    Assert.AreNotEqual(week.Minutes.Value, minutesLeft, 1e-6, "Nor are the call hours.");
                }

                // The attendees are distinct people, per week and over the window, so the window's 30 is not the weeks'
                // sum (51) and no week's count can be taken from it. The calls by type are window totals with no weekly series.
                Assert.AreEqual(30, (int)kpis["attendees"]);
                Assert.AreEqual((long)kpis["calls"], (long)kpis["groupCalls"] + (long)kpis["peerToPeerCalls"]);
                Assert.AreEqual(0, ((JArray)json["topOrganisers"]).Count + ((JArray)json["topAttendees"]).Count);
            }
        }

        [TestMethod]
        public async Task Meetings_ASeePiiReader_IsServedEveryWeekAsItWas_AndTheCachedSectionIsNeverTrimmed_InEitherOrder()
        {
            foreach (var seePiiFirst in new[] { false, true })
            {
                var store = new MeetingsStore();
                using (var cache = new MemoryCache("meetings-floor-order-" + seePiiFirst))
                using (var host = Host(store, cache))
                {
                    var order = seePiiFirst ? new[] { true, false, true } : new[] { false, true, false };
                    foreach (var seePii in order)
                    {
                        host.Principal = seePii ? PortalTestHost.SignedIn(PortalRoles.SeePii) : PortalTestHost.SignedIn();
                        var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/meetings?days=28"));
                        var because = (seePiiFirst ? "See PII first" : "without See PII first") + ", then a reader " + (seePii ? "with" : "without") + " it";

                        if (seePii)
                        {
                            Assert.IsTrue(JToken.DeepEquals(JObject.FromObject(MeetingsStore.Meetings()), json),
                                because + ": a See PII reader's meetings are the store's section, unchanged:" + Environment.NewLine + json);
                        }
                        else
                        {
                            Assert.AreEqual(2, json["trend"].Count(p => (bool?)p["withheld"] == true), because);
                        }
                    }

                    Assert.AreEqual(1, store.MeetingsCalls, "One cached section, trimmed for a reader without See PII on the way out.");
                }
            }
        }

        [TestMethod]
        public void WithoutSmallCallWeeks_WithholdsTheQuietestWeekShown_WheneverAWeekIsWithheld()
        {
            // No small week: every week is shown, as it was.
            var busy = new List<TeamsCallTrendPoint> { Point(Week1, 10, 6), Point(Week2, 20, 9) };
            var kept = TeamsSmallGroups.WithoutSmallCallWeeks(busy, Floor);
            Assert.IsFalse(kept.Any(p => p.Withheld));
            CollectionAssert.AreEqual(new long?[] { 10, 20 }, kept.Select(p => p.Calls).ToArray());

            // A small week takes the week with the fewest attendees shown with it; between two such weeks, the earlier.
            var tied = TeamsSmallGroups.WithoutSmallCallWeeks(
                new List<TeamsCallTrendPoint> { Point(Week1, 30, 12), Point(Week2, 4, 2), Point(Week3, 8, 7), Point(Week4, 9, 7) }, Floor);
            CollectionAssert.AreEqual(new[] { false, true, true, false }, tied.Select(p => p.Withheld).ToArray());

            // A week whose attendees were not counted is treated as small; a week without calls is nobody's, and is neither
            // withheld nor the one that goes with a small week.
            var uncounted = TeamsSmallGroups.WithoutSmallCallWeeks(
                new List<TeamsCallTrendPoint> { Point(Week1, 3, 0), Point(Week2, 0, 0), Point(Week3, 12, 6), Point(Week4, 40, 20) }, Floor);
            CollectionAssert.AreEqual(new[] { true, false, true, false }, uncounted.Select(p => p.Withheld).ToArray());
            Assert.AreEqual(0L, uncounted[1].Calls);

            // When no week has enough attendees to go with a small one, every week with calls is withheld.
            var allSmall = TeamsSmallGroups.WithoutSmallCallWeeks(
                new List<TeamsCallTrendPoint> { Point(Week1, 3, 2), Point(Week2, 5, 4) }, Floor);
            Assert.IsTrue(allSmall.All(p => p.Withheld && p.Calls == null && p.Minutes == null && p.Attendees == null));

            // The cached trend is never edited.
            var cached = new List<TeamsCallTrendPoint> { Point(Week1, 4, 2), Point(Week2, 9, 7) };
            TeamsSmallGroups.WithoutSmallCallWeeks(cached, Floor);
            Assert.IsFalse(cached.Any(p => p.Withheld));
            Assert.AreEqual(4L, cached[0].Calls);

            // Any trend: no week shown describes one to four people, and the totals less the weeks shown are either nothing
            // or two weeks or more - one of them of at least five attendees whenever such a week exists - never one week's.
            var random = new Random(844);
            for (var trial = 0; trial < 500; trial++)
            {
                var weeks = Enumerable.Range(0, random.Next(1, 9))
                    .Select(i => Point(Week1.AddDays(7 * i), random.Next(1, 40), random.Next(0, 3) == 0 ? random.Next(0, 5) : random.Next(0, 30)))
                    .ToList();
                var result = TeamsSmallGroups.WithoutSmallCallWeeks(weeks, Floor);
                var because = "trial " + trial + ": " + string.Join(", ", weeks.Select(w => w.Calls + " calls/" + w.Attendees + " people"));

                Assert.IsTrue(result.Where(p => !p.Withheld).All(p => p.Attendees >= Floor), because);
                var withheld = weeks.Where((w, i) => result[i].Withheld).ToList();
                if (withheld.Count == 0) continue;

                Assert.IsTrue(withheld.Count >= 2 || weeks.Count == 1, because + ": a single week withheld beside weeks shown is the totals less them.");
                if (weeks.Any(w => w.Attendees >= Floor))
                {
                    Assert.IsTrue(withheld.Any(w => w.Attendees >= Floor), because + ": the withheld weeks include one of at least five attendees.");
                }
                foreach (var shownWeek in weeks.Where((w, i) => !result[i].Withheld))
                {
                    Assert.AreSame(shownWeek, result[weeks.IndexOf(shownWeek)], because + ": a week shown is the cached point.");
                }
            }
        }

        private static TeamsCallTrendPoint Point(DateTime weekStart, long calls, int attendees) =>
            new TeamsCallTrendPoint { WeekStart = weekStart, Calls = calls, Minutes = calls * 30, Attendees = attendees };

        private static PortalTestHost Host(ITeamsExplorerStore store, ObjectCache cache, params string[] roles) =>
            new PortalTestHost(
                new[] { typeof(TeamsExplorerAPIController) },
                PortalTestHost.SignedIn(roles),
                PortalAccessPolicy.Enforcing,
                _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources { UsageReports = true, Calls = true, TeamsAnalytics = true }, null, null, cache));

        /// <summary>Four weeks of calls in a synthetic tenant, one of which three people attended.</summary>
        private sealed class MeetingsStore : ITeamsExplorerStore
        {
            private int _meetingsCalls;

            internal int MeetingsCalls => Volatile.Read(ref _meetingsCalls);

            public Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref _meetingsCalls);
                return Task.FromResult(Meetings());
            }

            internal static TeamsMeetings Meetings()
            {
                var meetings = new TeamsMeetings
                {
                    WorkingDayStartHour = 8,
                    WorkingDayEndHour = 18,
                    Kpis = new TeamsCallKpis
                    {
                        Calls = 94,
                        GroupCalls = 60,
                        PeerToPeerCalls = 34,
                        Attendees = 30,
                        CallHours = 47,
                        AttendeeHours = 120,
                        MeanDurationMinutes = 30,
                        MeanAttendees = 4.2,
                        AfterHoursPct = 10,
                        WeekendPct = 2,
                        OrganiserConcentrationPct = 40,
                        AttendeeEngagementPct = 80,
                    },
                };
                meetings.Trend.AddRange(new[]
                {
                    new TeamsCallTrendPoint { WeekStart = Week1, Calls = 40, Minutes = 1200, Attendees = 25 },
                    new TeamsCallTrendPoint { WeekStart = Week2, Calls = 6, Minutes = 180, Attendees = 3 },
                    new TeamsCallTrendPoint { WeekStart = Week3, Calls = 18, Minutes = 540, Attendees = 8 },
                    new TeamsCallTrendPoint { WeekStart = Week4, Calls = 30, Minutes = 900, Attendees = 15 },
                });
                meetings.SizeDistribution.Add(new TeamsBucketRow { Key = "3-5", Label = "3-5", Count = 94, SharePct = 100 });
                meetings.TopOrganisers.Add(new TeamsNamedCountRow { Name = "organiser@contoso.com", Count = 12 });
                meetings.TopAttendees.Add(new TeamsNamedCountRow { Name = "attendee@contoso.com", Count = 20 });
                return meetings;
            }

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(30, 30));
            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources) => Task.FromResult(new TeamsOverview());
            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsAdoption());
            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsCollaboration());
            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable) => Task.FromResult(new TeamsConversations());
            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsPeople());
        }
    }
}
