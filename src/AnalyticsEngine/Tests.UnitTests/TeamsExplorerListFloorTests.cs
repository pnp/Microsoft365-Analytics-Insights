extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Teams Explorer's ranked lists for a reader without See PII, beyond what one response shows (#844, P4-1): the
    /// power users per department, the team and channel leaderboards and the sentiment per team. Each sits beside a total
    /// the page publishes - the Power segment, the channel messages and reactions, the weekly messages - so what a list
    /// leaves out, or what lies beyond the rows it was read with, is that total less the rows shown. And a reader can ask
    /// for any top: the rows left out must not depend on it, or one answer shows what another leaves out.
    /// </summary>
    /// <remarks>
    /// Synthetic tenants: hundreds of Contoso departments and teams, a few of them small, ranked the way the SQL ranks them
    /// and cut to the top the query asks for, as the SQL is. A reader with See PII is served every row, as before.
    /// </remarks>
    [TestClass]
    public class TeamsExplorerListFloorTests
    {
        private const int Floor = 5;
        private const string Small = "Contoso Small";
        private const string Six = "Contoso Six";

        #region People

        [TestMethod]
        public async Task People_WithoutSeePii_FoldsADepartmentBeyondTheMaximumTop_SoThePowerUsersLessTheDepartmentsShownAreNeverItAlone()
        {
            // 500 departments of five power users, and a 501st of one: the Power segment is 2,501 people. Read at the 500-row
            // cap, the lone department never reached the fold, and 2,501 less every department shown was that one person.
            var store = new ListsStore { Departments = Departments(500, lone: 1) };
            using (var cache = new MemoryCache("people-list-floor"))
            using (var host = Host(store, cache))
            {
                string otherRow = null;
                foreach (var top in new[] { 3, 15, TeamsExplorerQuery.MaximumTop })
                {
                    var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/people?days=28&top=" + top));
                    var departments = json["championsByDepartment"].ToArray();
                    var because = "top=" + top;

                    Assert.IsFalse(json.ToString().Contains("Contoso Lone Department"), because);

                    // The lone power user is folded, topped up with the smallest named department; the same whatever top.
                    var other = departments.SingleOrDefault(d => (bool?)d["other"] == true);
                    Assert.IsNotNull(other, because + ": the lone department is folded, not merely missing.");
                    otherRow = otherRow ?? other.ToString();
                    Assert.AreEqual(otherRow, other.ToString(), because + ": the combined row does not depend on top.");
                    Assert.AreEqual(6, (int)other["count"], because + ": the lone power user and a department of five.");
                    Assert.AreEqual(2, (int)json["otherChampionDepartments"], because);
                    Assert.AreEqual(Math.Min(top, 499), departments.Count(d => (bool?)d["other"] != true), because + ": named departments are cut to top.");
                    Assert.IsTrue(departments.Where(d => (bool?)d["other"] != true).All(d => (int)d["count"] >= Floor), because);

                    if (top == TeamsExplorerQuery.MaximumTop)
                    {
                        // The longest answer shows every named department: the Power segment less them is the combined row.
                        var unaccounted = store.Departments.Sum(d => d.Count) - departments.Sum(d => (long)d["count"]);
                        Assert.AreEqual(0, unaccounted, "Power less every row shown, the combined row included, leaves nobody.");
                    }

                    Assert.IsTrue(store.LastPeopleQuery.EveryGroup, because + ": every department is read.");
                    Assert.IsTrue(store.LastPeopleQuery.DirectoryPopulation, because + ": over the Power segment's population.");
                }
            }
        }

        [TestMethod]
        public async Task People_ASeePiiReader_IsServedTheDepartmentsAsTheyWere_AndTheTwoReadersNeverShareACachedSection_InEitherOrder()
        {
            foreach (var seePiiFirst in new[] { false, true })
            {
                var store = new ListsStore { Departments = Departments(500, lone: 1) };
                using (var cache = new MemoryCache("people-list-order-" + seePiiFirst))
                using (var host = Host(store, cache))
                {
                    var order = seePiiFirst ? new[] { true, false, true } : new[] { false, true, false };
                    foreach (var seePii in order)
                    {
                        host.Principal = seePii ? PortalTestHost.SignedIn(PortalRoles.SeePii) : PortalTestHost.SignedIn();
                        var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/people?days=28&top=" + TeamsExplorerQuery.MaximumTop));
                        var because = (seePiiFirst ? "See PII first" : "without See PII first") + ", then a reader " + (seePii ? "with" : "without") + " it";

                        if (seePii)
                        {
                            var expected = JObject.FromObject(store.People(TeamsExplorerQuery.MaximumTop, everyGroup: false));
                            Assert.IsTrue(JToken.DeepEquals(expected, json), because + ": the store's section, unchanged.");
                            Assert.AreEqual(500, ((JArray)json["championsByDepartment"]).Count, because + ": read at the top asked for, as before.");
                        }
                        else
                        {
                            Assert.AreEqual(1, json["championsByDepartment"].Count(d => (bool?)d["other"] == true), because);
                            Assert.AreEqual(0, ((JArray)json["champions"]).Count, because);
                        }
                    }

                    Assert.AreEqual(2, store.PeopleCalls, "One section per kind of reader, each cached.");
                }
            }
        }

        [TestMethod]
        public void People_ADepartmentListCutShort_TreatsWhatLiesBeyondAsLeftOut()
        {
            // A store that read four departments, and got four: more may lie beyond, unread, and Power less the departments
            // shown would be them. So the smallest named department goes with them, uncounted, and no share is sent (they
            // would give the listed total away).
            var full = new TeamsPeople { ReadTop = 4 };
            full.ChampionsByDepartment.AddRange(Named(("Contoso Nine", 9), ("Contoso Seven", 7), ("Contoso Six", 6), ("Contoso Five", 5)));
            var trimmed = full.WithoutIndividualData(Floor);
            CollectionAssert.AreEqual(new[] { "Contoso Nine", "Contoso Seven", "Contoso Six" }, trimmed.ChampionsByDepartment.Select(d => d.Name).ToArray());
            Assert.IsFalse(trimmed.ChampionsByDepartment.Any(d => d.Other), "Part of the combined row was never read, so it has no count.");
            Assert.IsTrue(trimmed.ChampionsByDepartment.All(d => d.SharePct == null));
            Assert.AreEqual(1, trimmed.OtherChampionDepartments);
            Assert.AreEqual(5, full.ChampionsByDepartment.Last().Count, "The cached section is never edited.");
            Assert.IsNotNull(full.ChampionsByDepartment[0].SharePct);

            // A small department in a list cut short: folded with the smallest named one, and still no count for them.
            var withSmall = new TeamsPeople { ReadTop = 4 };
            withSmall.ChampionsByDepartment.AddRange(Named(("Contoso Nine", 9), ("Contoso Seven", 7), ("Contoso Six", 6), ("Contoso Two", 2)));
            var folded = withSmall.WithoutIndividualData(Floor);
            CollectionAssert.AreEqual(new[] { "Contoso Nine", "Contoso Seven" }, folded.ChampionsByDepartment.Select(d => d.Name).ToArray());
            Assert.AreEqual(2, folded.OtherChampionDepartments);

            // Read whole - fewer rows than were asked for - the departments are folded as before.
            var whole = new TeamsPeople { ReadTop = 5 };
            whole.ChampionsByDepartment.AddRange(Named(("Contoso Nine", 9), ("Contoso Seven", 7), ("Contoso Six", 6), ("Contoso Two", 2)));
            var asBefore = whole.WithoutIndividualData(Floor);
            CollectionAssert.AreEqual(new[] { "Contoso Nine", "Contoso Seven", null }, asBefore.ChampionsByDepartment.Select(d => d.Name).ToArray());
            Assert.AreEqual(8, asBefore.ChampionsByDepartment.Last().Count, "Contoso Two's 2 and Contoso Six's 6.");
            Assert.IsNotNull(asBefore.ChampionsByDepartment[0].SharePct);
        }

        [TestMethod]
        public void People_WhenTheOverviewWithholdsThePowerSegment_TheDepartmentsNeverAddUpToIt()
        {
            // 40 power users in three departments; 3 regular users and 100 light ones. The Overview and Adoption tabs withhold
            // the Regular segment, and with it Power - the smaller of the two others - since the active people less Power and
            // Light would be the three. Here the departments added up to Power, so active less them and Light gave the three
            // back. So their total is withheld too: the smallest goes, uncounted, and with it every share.
            var people = new TeamsPeople { SegmentMix = Mix(power: 40, regular: 3, light: 100) };
            people.ChampionsByDepartment.AddRange(Named(("Contoso Twenty", 20), ("Contoso Twelve", 12), ("Contoso Eight", 8)));
            Assert.IsNull(TeamsSmallGroups.WithoutSmallSegments(people.SegmentMix, Floor, false).Single(s => s.Segment == "Power").Users,
                "The Overview withholds Power.");

            var trimmed = people.WithoutIndividualData(Floor);
            CollectionAssert.AreEqual(new[] { "Contoso Twenty", "Contoso Twelve" }, trimmed.ChampionsByDepartment.Select(d => d.Name).ToArray());
            Assert.AreEqual(32, trimmed.ChampionsByDepartment.Sum(d => d.Count), "Never the 40 the Overview withholds.");
            Assert.IsTrue(trimmed.ChampionsByDepartment.All(d => d.SharePct == null && !d.Other));
            Assert.AreEqual(1, trimmed.OtherChampionDepartments);

            // When the Overview shows Power - Light is the one that goes with the three - the departments are as before.
            var shown = new TeamsPeople { SegmentMix = Mix(power: 40, regular: 3, light: 30) };
            shown.ChampionsByDepartment.AddRange(Named(("Contoso Twenty", 20), ("Contoso Twelve", 12), ("Contoso Eight", 8)));
            var asBefore = shown.WithoutIndividualData(Floor);
            Assert.AreEqual(40, asBefore.ChampionsByDepartment.Sum(d => d.Count));
            Assert.IsTrue(asBefore.ChampionsByDepartment.All(d => d.SharePct != null));

            // The engagement mix could not be read: nothing says the Overview shows Power, so the total is withheld.
            var unread = new TeamsPeople { SegmentMix = new List<TeamsSegmentSlice>() };
            unread.ChampionsByDepartment.AddRange(Named(("Contoso Twenty", 20), ("Contoso Twelve", 12), ("Contoso Eight", 8)));
            Assert.AreEqual(32, unread.WithoutIndividualData(Floor).ChampionsByDepartment.Sum(d => d.Count));

            // Not read at all - a See PII reader's section - it decides nothing.
            var seePii = new TeamsPeople();
            seePii.ChampionsByDepartment.AddRange(Named(("Contoso Twenty", 20), ("Contoso Twelve", 12), ("Contoso Eight", 8)));
            Assert.AreEqual(40, seePii.WithoutIndividualData(Floor).ChampionsByDepartment.Sum(d => d.Count));
        }

        #endregion

        #region Collaboration and conversations

        [TestMethod]
        public async Task Collaboration_WithoutSeePii_LeavesOutTheSameTeamsWhateverTopIsAsked_AndNoTotalLessTheRowsShownIsASmallTeams()
        {
            // Thirty teams; the twentieth by messages has three members, the fifth six. Floored over the rows of each answer,
            // a top of 15 never saw the small team and showed the team of six, which a top of 500 left out with it: the
            // channel messages less the longer answer's rows, less the team of six from the shorter one, were the small
            // team's own. Floored over the export-sized list, both answers leave out the same two teams.
            var store = new ListsStore { Teams = RankedTeams(30, smallAt: 19, sixAt: 4) };
            using (var cache = new MemoryCache("collaboration-list-floor"))
            using (var host = Host(store, cache))
            {
                var answers = new Dictionary<int, JObject>();
                foreach (var top in new[] { 15, TeamsExplorerQuery.MaximumTop })
                {
                    var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/collaboration?days=28&top=" + top));
                    answers[top] = json;
                    var because = "top=" + top;

                    foreach (var list in new[] { "teams", "channels" })
                    {
                        var names = Names(json[list], list);
                        Assert.IsFalse(names.Contains(Small) || names.Contains(Six), because + ", " + list + ": " + string.Join(", ", names));
                        Assert.AreEqual(2, (int)json[list == "teams" ? "withheldTeams" : "withheldChannels"], because + ", " + list);
                        Assert.AreEqual(Math.Min(top, 28), names.Count, because + ", " + list);
                    }
                    Assert.AreEqual(TeamsExplorerQuery.MaximumTop + 1, store.LastCollaborationQuery.Top, because + ": read one row past the export-sized list.");

                    var csv = await host.Client.GetStringAsync("api/TeamsExplorer/export/teams?days=28&top=" + top);
                    Assert.IsFalse(csv.Contains(Small) || csv.Contains(Six), because + ": the export leaves out the same teams.");
                }

                foreach (var list in new[] { "teams", "channels" })
                {
                    var shorter = Names(answers[15][list], list);
                    var longer = Names(answers[TeamsExplorerQuery.MaximumTop][list], list);
                    CollectionAssert.AreEqual(longer.Take(15).ToList(), shorter, list + ": a shorter answer is the start of a longer one.");
                }

                // The channel messages less every team shown: the small team and the team of six together, never one of them.
                var longest = answers[TeamsExplorerQuery.MaximumTop];
                var left = (long)longest["kpis"]["channelMessages"] - longest["teams"].Sum(t => (long)t["messages"]);
                Assert.AreEqual(Messages(store, Small) + Messages(store, Six), left);
                Assert.AreNotEqual(Messages(store, Small), left);
                var channelsLeft = (long)longest["kpis"]["channelMessages"] - longest["channels"].Sum(c => (long)c["messages"]);
                Assert.AreEqual(left, channelsLeft, "The channel list leaves out the same two teams' channels.");
            }
        }

        [TestMethod]
        public async Task Collaboration_WithoutSeePii_ATeamBeyondTheExportSizedList_IsCountedAsLeftOut()
        {
            // 501 teams: 500 of five members or more, and a 501st, the quietest, of two. Read at the 500-row cap, nothing
            // the list held was small, so nothing was left out, and the channel messages less the 500 teams shown were the
            // 501st team's own. Read with one row more, the list is known to be cut short, and the smallest team shown that
            // has messages goes with the rows beyond it.
            var teams = RankedTeams(500, smallAt: -1, sixAt: 250);
            teams.Add(Team(10_000, "Contoso Lone Team", members: 2, messages: 1));
            var store = new ListsStore { Teams = teams };
            using (var cache = new MemoryCache("collaboration-beyond"))
            using (var host = Host(store, cache))
            {
                var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/collaboration?days=28&top=" + TeamsExplorerQuery.MaximumTop));
                var left = (long)json["kpis"]["channelMessages"] - json["teams"].Sum(t => (long)t["messages"]);
                Assert.AreEqual(1 + Messages(store, Six), left, "The lone team and the team of six, never the lone team's alone.");

                var names = Names(json["teams"], "teams");
                Assert.AreEqual(499, names.Count);
                Assert.IsFalse(names.Contains("Contoso Lone Team") || names.Contains(Six));
                Assert.AreEqual(1, (int)json["withheldTeams"], "Of the 500 the list holds, the team of six; the lone team lies beyond it.");
                Assert.AreEqual(TeamsExplorerQuery.MaximumTop + 1, store.LastCollaborationQuery.Top);

                var csv = await host.Client.GetStringAsync("api/TeamsExplorer/export/teams?days=28");
                Assert.IsFalse(csv.Contains("Contoso Lone Team") || csv.Contains(Six), "The export is the same list.");
            }
        }

        [TestMethod]
        public void Collaboration_WithoutSeePii_TheReactionsLessThoseShown_AreNeverAHandfulOfPeoples()
        {
            // Three teams of ten members or more; two people reacted in one of them. Its reactions are withheld - and so are
            // those of the team the fewest people reacted in, of five or more, or the tenant's reactions less those shown
            // would be the two people's.
            var collaboration = new TeamsCollaboration
            {
                Kpis = new TeamsCollaborationKpis { Reactions = 159 },
                Teams =
                {
                    new TeamsTeamRow { Id = 1, Name = "Contoso Big", Members = 40, Messages = 900, Reactions = 120, ReactingUsers = 25 },
                    new TeamsTeamRow { Id = 2, Name = "Contoso Mid", Members = 12, Messages = 300, Reactions = 30, ReactingUsers = 8 },
                    new TeamsTeamRow { Id = 3, Name = "Contoso Few", Members = 10, Messages = 200, Reactions = 9, ReactingUsers = 2 },
                },
                Channels =
                {
                    new TeamsChannelRow { Id = 10, Name = "General", TeamName = "Contoso Big", TeamMembers = 40, Messages = 900, Reactions = 120, ReactingUsers = 25 },
                    new TeamsChannelRow { Id = 11, Name = "Plans", TeamName = "Contoso Mid", TeamMembers = 12, Messages = 300, Reactions = 30, ReactingUsers = 8 },
                    new TeamsChannelRow { Id = 12, Name = "Ideas", TeamName = "Contoso Few", TeamMembers = 10, Messages = 200, Reactions = 9, ReactingUsers = 2 },
                },
            };

            var trimmed = collaboration.WithoutSmallGroups(Floor);
            CollectionAssert.AreEqual(new[] { false, true, true }, trimmed.Teams.Select(t => t.ReactionsWithheld).ToArray());
            CollectionAssert.AreEqual(new[] { false, true, true }, trimmed.Channels.Select(c => c.ReactionsWithheld).ToArray());
            Assert.IsTrue(trimmed.Teams.Skip(1).All(t => t.Reactions == null), "Withheld, never 0.");
            Assert.IsTrue(trimmed.Channels.Skip(1).All(c => c.Reactions == null && c.ReactingUsers == null));
            Assert.AreEqual(159 - 120, collaboration.Kpis.Reactions - trimmed.Teams.Sum(t => t.Reactions ?? 0), "The two teams together.");
            Assert.AreEqual(30L, collaboration.Teams[1].Reactions, "The cached section is never edited.");

            // When the reactions hidden already include five people's, nothing more is withheld.
            collaboration.Teams[2].Members = 3;
            collaboration.Channels[2].TeamMembers = 3;
            collaboration.Teams[2].ReactingUsers = 6;
            collaboration.Channels[2].ReactingUsers = 6;
            var leftOut = collaboration.WithoutSmallGroups(Floor);
            CollectionAssert.AreEqual(new[] { "Contoso Big" }, leftOut.Teams.Select(t => t.Name).ToArray(), "The team of three, and the team of twelve with it.");
            Assert.IsFalse(leftOut.Teams[0].ReactionsWithheld, "The left-out teams' reactions are six people's or more.");
        }

        [TestMethod]
        public async Task Conversations_WithoutSeePii_LeavesOutTheSameSentimentWhateverTopIsAsked_AndTheWeeksMessagesLessThoseShownAreNeverOneSmallTeams()
        {
            var store = new ListsStore { Teams = RankedTeams(30, smallAt: 19, sixAt: 4) };
            using (var cache = new MemoryCache("conversations-list-floor"))
            using (var host = Host(store, cache))
            {
                var answers = new Dictionary<int, JObject>();
                foreach (var top in new[] { 15, TeamsExplorerQuery.MaximumTop })
                {
                    var json = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/conversations?days=28&top=" + top));
                    answers[top] = json;
                    foreach (var list in new[] { "sentimentByTeam", "sentimentByChannel" })
                    {
                        var names = json[list].Select(r => (string)r["name"]).ToList();
                        Assert.IsFalse(names.Any(n => n.StartsWith(Small, StringComparison.Ordinal) || n.StartsWith(Six, StringComparison.Ordinal)),
                            "top=" + top + ", " + list + ": " + string.Join(", ", names));
                        Assert.AreEqual(Math.Min(top, 28), names.Count);
                    }
                    Assert.AreEqual(2, (int)json["withheldSentimentTeams"]);
                    Assert.AreEqual(2, (int)json["withheldSentimentChannels"]);
                    Assert.AreEqual(TeamsExplorerQuery.MaximumTop + 1, store.LastConversationsQuery.Top);
                }

                foreach (var list in new[] { "sentimentByTeam", "sentimentByChannel" })
                {
                    CollectionAssert.AreEqual(
                        answers[TeamsExplorerQuery.MaximumTop][list].Take(15).Select(r => (string)r["name"]).ToList(),
                        answers[15][list].Select(r => (string)r["name"]).ToList(),
                        list + ": a shorter answer is the start of a longer one.");
                }

                var longest = answers[TeamsExplorerQuery.MaximumTop];
                var left = longest["sentimentTrend"].Sum(p => (long)p["messages"]) - longest["sentimentByTeam"].Sum(r => (long)r["messages"]);
                Assert.AreEqual(Messages(store, Small) + Messages(store, Six), left, "The weeks' messages less the teams shown: two teams, never one.");
            }
        }

        [TestMethod]
        public async Task CollaborationAndConversations_ASeePiiReader_IsServedTheSectionAsItWas_AndTheTwoReadersNeverShareACachedSection_InEitherOrder()
        {
            foreach (var seePiiFirst in new[] { false, true })
            {
                var store = new ListsStore { Teams = RankedTeams(30, smallAt: 19, sixAt: 4) };
                using (var cache = new MemoryCache("lists-order-" + seePiiFirst))
                using (var host = Host(store, cache))
                {
                    var order = seePiiFirst ? new[] { true, false, true } : new[] { false, true, false };
                    foreach (var seePii in order)
                    {
                        host.Principal = seePii ? PortalTestHost.SignedIn(PortalRoles.SeePii) : PortalTestHost.SignedIn();
                        var because = (seePiiFirst ? "See PII first" : "without See PII first") + ", then a reader " + (seePii ? "with" : "without") + " it";
                        var collaboration = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/collaboration?days=28&top=15"));
                        var conversations = JObject.Parse(await host.Client.GetStringAsync("api/TeamsExplorer/conversations?days=28&top=15"));

                        if (seePii)
                        {
                            Assert.IsTrue(JToken.DeepEquals(JObject.FromObject(store.Collaboration(15)), collaboration), because + ": collaboration as it was.");
                            Assert.IsTrue(JToken.DeepEquals(JObject.FromObject(store.Conversations(15)), conversations), because + ": conversations as it was.");
                            Assert.IsTrue(Names(collaboration["teams"], "teams").Contains(Six), because);
                        }
                        else
                        {
                            Assert.IsFalse(Names(collaboration["teams"], "teams").Contains(Six), because);
                            Assert.IsFalse(conversations["sentimentByTeam"].Any(r => (string)r["name"] == Six), because);
                        }
                    }

                    Assert.AreEqual(2, store.CollaborationCalls, "One section per kind of reader, each cached.");
                    Assert.AreEqual(2, store.ConversationsCalls, "One section per kind of reader, each cached.");
                }
            }
        }

        [TestMethod]
        public void WithoutSmallRowsOfList_LeavesOutWithTheSmallRows_TheSmallestRowShownThatAddsToTheTotals()
        {
            // A team with no messages adds nothing to the totals, so leaving it out would hide nothing: it is never the one
            // that goes with a small team.
            var rows = new List<TeamsSentimentRow>
            {
                Row("Contoso Busy", 40, 900), Row("Contoso Silent", 6, 0), Row("Contoso Pair", 2, 50), Row("Contoso Eight", 8, 20),
            };
            var shown = TeamsSmallGroups.WithoutSmallRowsOfList(rows, r => r.Members, r => r.Messages > 0, r => r.Name, Floor, false, out var leftOut);
            CollectionAssert.AreEqual(new[] { "Contoso Busy", "Contoso Silent" }, shown.Select(r => r.Name).ToArray());
            Assert.AreEqual(2, leftOut);

            // A small row with nothing to add needs nobody to go with it.
            var quiet = TeamsSmallGroups.WithoutSmallRowsOfList(
                new[] { Row("Contoso Busy", 40, 900), Row("Contoso Pair", 2, 0) }, r => r.Members, r => r.Messages > 0, r => r.Name, Floor, false, out leftOut);
            CollectionAssert.AreEqual(new[] { "Contoso Busy" }, quiet.Select(r => r.Name).ToArray());
            Assert.AreEqual(1, leftOut);

            // A list cut short, or longer than the export-sized list, has rows beyond it: the smallest row shown goes too.
            var cut = TeamsSmallGroups.WithoutSmallRowsOfList(
                new[] { Row("Contoso Busy", 40, 900), Row("Contoso Eight", 8, 20) }, r => r.Members, r => r.Messages > 0, r => r.Name, Floor, true, out leftOut);
            CollectionAssert.AreEqual(new[] { "Contoso Busy" }, cut.Select(r => r.Name).ToArray());
            var many = Enumerable.Range(0, TeamsExplorerQuery.MaximumTop + 1).Select(i => Row("Contoso " + i.ToString("D3", CultureInfo.InvariantCulture), 10 + i, 1000 - i)).ToList();
            var longList = TeamsSmallGroups.WithoutSmallRowsOfList(many, r => r.Members, r => r.Messages > 0, r => r.Name, Floor, false, out leftOut);
            Assert.AreEqual(TeamsExplorerQuery.MaximumTop - 1, longList.Count);
            Assert.AreEqual(1, leftOut, "Counted over the export-sized list.");
            Assert.IsFalse(longList.Contains(many[0]), "The smallest of the list's rows goes with the rows beyond it.");

            // Any list: no row shown is small; whenever anything left out adds to the totals, so does a row of five or more
            // left out, unless no row shown adds; a row shown is the cached row; the order is kept.
            var random = new Random(844);
            for (var trial = 0; trial < 500; trial++)
            {
                var list = Enumerable.Range(0, random.Next(1, 12))
                    .Select(i => Row("Contoso " + i.ToString("D2", CultureInfo.InvariantCulture), random.Next(0, 3) == 0 ? random.Next(0, Floor) : random.Next(Floor, 30), random.Next(0, 3) == 0 ? 0 : random.Next(1, 500)))
                    .ToList();
                var cutShort = random.Next(0, 4) == 0;
                var result = TeamsSmallGroups.WithoutSmallRowsOfList(list, r => r.Members, r => r.Messages > 0, r => r.Name, Floor, cutShort, out var count);
                var because = "trial " + trial + (cutShort ? " (cut short)" : string.Empty) + ": " + string.Join(", ", list.Select(r => r.Members + "/" + r.Messages));

                Assert.IsTrue(result.All(r => r.Members >= Floor), because);
                var hidden = list.Except(result).ToList();
                Assert.AreEqual(hidden.Count, count, because);
                CollectionAssert.AreEqual(list.Where(result.Contains).ToList(), result, because + ": the order is kept.");
                if ((cutShort || hidden.Any(r => r.Messages > 0)) && result.Any(r => r.Messages > 0))
                {
                    Assert.IsTrue(hidden.Any(r => r.Members >= Floor && r.Messages > 0), because + ": a row of five or more that adds goes with them.");
                }
                Assert.IsTrue(hidden.Where(r => r.Members >= Floor).All(r => r.Messages > 0), because + ": a row adding nothing is never the one that goes.");
            }
        }

        #endregion

        #region Helpers

        private static PortalTestHost Host(ITeamsExplorerStore store, ObjectCache cache) =>
            new PortalTestHost(
                new[] { typeof(TeamsExplorerAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.Enforcing,
                _ => new TeamsExplorerAPIController(store, () => new TeamsExplorerSources { UsageReports = true, Calls = true, TeamsAnalytics = true, Cognitive = true }, null, null, cache));

        private static List<string> Names(JToken rows, string list) =>
            rows.Select(r => list == "channels" ? (string)r["teamName"] : (string)r["name"]).ToList();

        private static long Messages(ListsStore store, string team) => store.Teams.Single(t => t.Name == team).Messages;

        private static IEnumerable<TeamsNamedCountRow> Named(params (string Name, long Count)[] rows)
        {
            var total = rows.Sum(r => r.Count);
            return rows.Select(r => new TeamsNamedCountRow { Name = r.Name, Count = r.Count, SharePct = TeamsExplorerScoring.Percentage(r.Count, total) });
        }

        private static List<TeamsSegmentSlice> Mix(int power, int regular, int light) => new List<TeamsSegmentSlice>
        {
            new TeamsSegmentSlice { Segment = TeamsUserSegment.Power.ToString(), Users = power },
            new TeamsSegmentSlice { Segment = TeamsUserSegment.Regular.ToString(), Users = regular },
            new TeamsSegmentSlice { Segment = TeamsUserSegment.Light.ToString(), Users = light },
            new TeamsSegmentSlice { Segment = TeamsUserSegment.Dormant.ToString(), Users = 0 },
        };

        private static List<TeamsNamedCountRow> Departments(int departments, int lone)
        {
            var rows = Enumerable.Range(0, departments)
                .Select(i => new TeamsNamedCountRow { Name = "Contoso Department " + i.ToString("D3", CultureInfo.InvariantCulture), Count = 5 })
                .ToList();
            rows.Add(new TeamsNamedCountRow { Name = "Contoso Lone Department", Count = lone });
            return rows;
        }

        /// <summary>
        /// <paramref name="count"/> teams ranked by messages, busiest first, each of ten members or more - except the one at
        /// <paramref name="smallAt"/> (three members) and the one at <paramref name="sixAt"/> (six). -1 for neither.
        /// </summary>
        private static List<TeamsTeamRow> RankedTeams(int count, int smallAt, int sixAt) =>
            Enumerable.Range(0, count)
                .Select(i =>
                {
                    var name = i == smallAt ? Small : i == sixAt ? Six : "Contoso Team " + i.ToString("D3", CultureInfo.InvariantCulture);
                    var members = i == smallAt ? 3 : i == sixAt ? 6 : 10 + i % 7;
                    return Team(i + 1, name, members, messages: 10_000 - i * 10);
                })
                .ToList();

        private static TeamsTeamRow Team(int id, string name, int members, long messages) =>
            new TeamsTeamRow { Id = id, Name = name, Members = members, Messages = messages, Reactions = 0, ReactingUsers = 0, Sentiment = 0.6 };

        private static TeamsSentimentRow Row(string name, int members, long messages) =>
            new TeamsSentimentRow { Name = name, Members = members, Messages = messages, Sentiment = 0.5 };

        /// <summary>
        /// Synthetic Teams sections, each list cut to the top the query asks for, as the SQL cuts it: one General channel
        /// per team, and a sentiment row per team and per channel. The departments are read whole when the query asks for
        /// every group, as the store reads them.
        /// </summary>
        private sealed class ListsStore : ITeamsExplorerStore
        {
            private int _collaborationCalls;
            private int _conversationsCalls;
            private int _peopleCalls;

            internal List<TeamsTeamRow> Teams { get; set; } = new List<TeamsTeamRow>();
            internal List<TeamsNamedCountRow> Departments { get; set; } = new List<TeamsNamedCountRow>();

            internal int CollaborationCalls => Volatile.Read(ref _collaborationCalls);
            internal int ConversationsCalls => Volatile.Read(ref _conversationsCalls);
            internal int PeopleCalls => Volatile.Read(ref _peopleCalls);
            internal TeamsExplorerQuery LastCollaborationQuery { get; private set; }
            internal TeamsExplorerQuery LastConversationsQuery { get; private set; }
            internal TeamsExplorerQuery LastPeopleQuery { get; private set; }

            public Task<TeamsCollaboration> GetCollaborationAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref _collaborationCalls);
                LastCollaborationQuery = query;
                return Task.FromResult(Collaboration(query.Top));
            }

            internal TeamsCollaboration Collaboration(int top) => new TeamsCollaboration
            {
                ReadTop = top,
                Kpis = new TeamsCollaborationKpis
                {
                    TotalTeams = Teams.Count,
                    ActiveTeams = Teams.Count(t => t.Messages > 0),
                    TotalChannels = Teams.Count,
                    ChannelMessages = Teams.Sum(t => t.Messages),
                    Reactions = Teams.Sum(t => t.Reactions ?? 0),
                },
                Teams = Teams.Take(top).ToList(),
                Channels = Teams.Take(top)
                    .Select(t => new TeamsChannelRow { Id = 1000 + t.Id, Name = "General", TeamName = t.Name, TeamMembers = t.Members, Messages = t.Messages, Reactions = 0, ReactingUsers = 0 })
                    .ToList(),
            };

            public Task<TeamsConversations> GetConversationsAsync(TeamsExplorerQuery query, bool cognitiveAvailable)
            {
                Interlocked.Increment(ref _conversationsCalls);
                LastConversationsQuery = query;
                return Task.FromResult(Conversations(query.Top));
            }

            internal TeamsConversations Conversations(int top) => new TeamsConversations
            {
                ReadTop = top,
                CognitiveAvailable = true,
                ScoredChannelDays = 100,
                SentimentTrend = { new TeamsSentimentPoint { WeekStart = new DateTime(2026, 10, 5), Sentiment = 0.6, Messages = Teams.Sum(t => t.Messages) } },
                SentimentByTeam = Teams.Take(top).Select(t => Row(t.Name, t.Members, t.Messages)).ToList(),
                SentimentByChannel = Teams.Take(top).Select(t => Row(t.Name + " / General", t.Members, t.Messages)).ToList(),
            };

            public Task<TeamsPeople> GetPeopleAsync(TeamsExplorerQuery query)
            {
                Interlocked.Increment(ref _peopleCalls);
                LastPeopleQuery = query;
                return Task.FromResult(People(query.Top, query.EveryGroup));
            }

            internal TeamsPeople People(int top, bool everyGroup)
            {
                var departmentTop = everyGroup ? int.MaxValue : top;
                var read = Departments.Take(departmentTop).ToList();
                var total = read.Sum(d => d.Count);
                var people = new TeamsPeople { ReadTop = departmentTop };
                people.Champions.Add(new TeamsPersonRow { UserPrincipalName = "champion@contoso.com", Department = "Contoso Department 000" });
                people.ChampionsByDepartment.AddRange(read.Select(d => new TeamsNamedCountRow
                {
                    Name = d.Name,
                    Count = d.Count,
                    SharePct = TeamsExplorerScoring.Percentage(d.Count, total),
                }));
                return people;
            }

            public Task<Tuple<int, int>> GetTeamCountsAsync() => Task.FromResult(Tuple.Create(1, 1));
            public Task<TeamsOverview> GetOverviewAsync(TeamsExplorerQuery query, TeamsExplorerSources sources) => Task.FromResult(new TeamsOverview());
            public Task<TeamsAdoption> GetAdoptionAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsAdoption());
            public Task<TeamsMeetings> GetMeetingsAsync(TeamsExplorerQuery query) => Task.FromResult(new TeamsMeetings());
        }

        #endregion
    }
}
