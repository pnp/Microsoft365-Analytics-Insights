using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Teams Explorer's judgement layer and window arithmetic.
    /// </summary>
    /// <remarks>
    /// These are the numbers the page asserts things about in prose - "62% of known users", "the
    /// busiest tenth of organisers ran 71% of meetings", "this person is a power user" - so every
    /// threshold behind them is pinned here rather than left to be eyeballed against a chart.
    /// </remarks>
    [TestClass]
    public class TeamsExplorerScoringTests
    {
        #region Dormancy (positive-only usage reports)

        [TestMethod]
        public void TeamsLicences_TheServicePlanDecides_AndRoomAccountsAreNotPeople()
        {
            var partNumbers = new HashSet<string>(TeamsLicences.EmbeddedSkuPartNumbers, StringComparer.OrdinalIgnoreCase);

            Assert.IsTrue(partNumbers.Contains("ENTERPRISEPACK"), "Office 365 E3 carries the Teams service plan.");
            Assert.IsTrue(partNumbers.Contains("Microsoft_Teams_Enterprise_New"), "Teams sold on its own.");
            Assert.IsTrue(partNumbers.Contains("M365_G3_GOV"), "Government clouds have their own Teams plan.");
            Assert.IsFalse(partNumbers.Contains("Microsoft_365_E3_(no_Teams)"), "A suite sold without Teams.");
            Assert.IsFalse(partNumbers.Contains("Microsoft_Teams_Rooms_Pro"), "A meeting-room account is not a person to enable.");
            Assert.IsFalse(partNumbers.Contains("MCOCAP"), "Nor is a shared device.");
            Assert.IsFalse(partNumbers.Contains("POWER_BI_PRO"));
        }

        [TestMethod]
        public void SegmentMix_ALicensedPersonWithNoRowIsDormantOnlyWhenTheWindowWasMeasuredInFull()
        {
            var histogram = new List<TeamsExplorerRows.ActiveDayHistogramRow>
            {
                new TeamsExplorerRows.ActiveDayHistogramRow { ActiveDays = 0, Reported = false, Users = 3 },
                new TeamsExplorerRows.ActiveDayHistogramRow { ActiveDays = 0, Reported = true, Users = 1 },
                new TeamsExplorerRows.ActiveDayHistogramRow { ActiveDays = 20, Reported = true, Users = 4 },
            };

            var known = new TeamsDormancy { Known = true };
            var measured = SqlTeamsExplorerStore.BuildSegmentMix(histogram, 20, known).ToDictionary(s => s.Segment);
            Assert.AreEqual(4, measured["Dormant"].Users, "No row in a fully imported window is no activity.");
            Assert.AreEqual(50, measured["Dormant"].SharePct, 0.001);
            Assert.AreEqual(0, known.UnmeasuredUsers);

            var unknown = new TeamsDormancy { Known = false, Reason = TeamsDormancy.IncompleteImport };
            var partial = SqlTeamsExplorerStore.BuildSegmentMix(histogram, 20, unknown).ToDictionary(s => s.Segment);
            Assert.AreEqual(1, partial["Dormant"].Users, "Only the person the report saw doing nothing counted.");
            Assert.AreEqual(20, partial["Dormant"].SharePct, 0.001, "Shares are of the people actually measured.");
            Assert.AreEqual(3, unknown.UnmeasuredUsers, "The rest are reported as unmeasured, not dropped silently.");
        }

        [TestMethod]
        public void Dormancy_SaysWhyItCannotBeKnown()
        {
            TeamsExplorerRows.UsageCoverageRow Row() => new TeamsExplorerRows.UsageCoverageRow
            {
                ExpectedDays = 28, ImportedDays = 28, TeamsLicensedUsers = 10,
            };

            Assert.IsTrue(TeamsDormancy.From(Row()).Known);

            var missingDay = Row();
            missingDay.ImportedDays = 27;
            Assert.AreEqual(TeamsDormancy.IncompleteImport, TeamsDormancy.From(missingDay).Reason);

            var filtered = Row();
            filtered.GroupFiltered = true;
            Assert.AreEqual(TeamsDormancy.GroupFiltered, TeamsDormancy.From(filtered).Reason);

            var concealed = Row();
            concealed.NamesConcealed = true;
            Assert.AreEqual(TeamsDormancy.NamesConcealed, TeamsDormancy.From(concealed).Reason);

            var unlicensed = Row();
            unlicensed.TeamsLicensedUsers = 0;
            Assert.AreEqual(TeamsDormancy.NoTeamsLicences, TeamsDormancy.From(unlicensed).Reason);

            var failed = TeamsDormancy.From(null);
            Assert.IsFalse(failed.Known);
            Assert.AreEqual(TeamsDormancy.NotChecked, failed.Reason);
        }

        #endregion

        #region Small groups for a reader without See PII

        [TestMethod]
        public void WithoutSmallGroups_AFoldedRowShortOfPeople_TakesTheSmallestShownGroup()
        {
            var adoption = Adoption(Group("Contoso Trio", 3, 0), Group("Contoso Six", 6, 0), Group("Contoso Twenty", 20, 10));

            var trimmed = adoption.WithoutSmallGroups(5);

            CollectionAssert.AreEqual(new[] { "Contoso Twenty", null }, trimmed.Breakdown.Select(r => r.Name).ToArray());
            Assert.AreEqual(9, trimmed.Breakdown[1].KnownUsers);
            Assert.AreEqual(2, trimmed.OtherGroups);
            Assert.AreEqual(5, trimmed.MinimumGroupSize);
        }

        [TestMethod]
        public void WithoutSmallGroups_AFoldedRowShortOfActivePeople_TakesTheSmallestGroupWithActivePeople()
        {
            // Two active people among ten: their messages per active user are those two people's own.
            var adoption = Adoption(
                Group("Contoso Quiet", 10, 2, messagesPerActiveUser: 50),
                Group("Contoso Idle", 6, 0),
                Group("Contoso Busy", 8, 6, messagesPerActiveUser: 10),
                Group("Contoso Big", 40, 30));

            var trimmed = adoption.WithoutSmallGroups(5);

            CollectionAssert.AreEqual(new[] { "Contoso Idle", "Contoso Big", null }, trimmed.Breakdown.Select(r => r.Name).ToArray(),
                "An idle group cannot make up active people, so it keeps its name.");
            var other = trimmed.Breakdown[2];
            Assert.IsTrue(other.Other);
            Assert.AreEqual(18, other.KnownUsers);
            Assert.AreEqual(8, other.ActiveUsers);
            Assert.AreEqual((2 * 50 + 6 * 10) / 8d, other.MessagesPerActiveUser, 1e-9);
            Assert.AreEqual(100d * 8 / 18, other.ReachPct, 1e-9);
            Assert.AreEqual(2, trimmed.OtherGroups);
        }

        [TestMethod]
        public void WithoutSmallGroups_TooFewPeopleEvenTogether_LeavesTheRowOut()
        {
            var adoption = Adoption(Group("Contoso Quiet", 10, 2), Group("Contoso Idle", 6, 0));

            var trimmed = adoption.WithoutSmallGroups(5);

            CollectionAssert.AreEqual(new[] { "Contoso Idle" }, trimmed.Breakdown.Select(r => r.Name).ToArray());
            Assert.AreEqual(1, trimmed.OtherGroups, "Left out, and said so.");
        }

        [TestMethod]
        public void WithoutSmallGroups_IsACopy_AndGroupsOfFiveAreShownAsTheyAre()
        {
            var adoption = Adoption(
                Group("Contoso Five", 5, 5), Group("Contoso Four", 4, 4), Group("Contoso Three", 3, 3, messagesPerActiveUser: 99));

            var trimmed = adoption.WithoutSmallGroups(5);

            CollectionAssert.AreEqual(new[] { "Contoso Five", null }, trimmed.Breakdown.Select(r => r.Name).ToArray());
            Assert.AreSame(adoption.Breakdown[0], trimmed.Breakdown[0], "A group of five is shown unchanged.");
            Assert.AreEqual(7, trimmed.Breakdown[1].KnownUsers);
            Assert.AreEqual(3, adoption.Breakdown.Count, "The cached section is never edited.");
            Assert.IsNull(adoption.MinimumGroupSize);
            Assert.AreEqual(0, adoption.OtherGroups);
        }

        private static TeamsAdoption Adoption(params TeamsDemographicRow[] rows)
        {
            var adoption = new TeamsAdoption();
            adoption.Breakdown.AddRange(rows);
            return adoption;
        }

        [TestMethod]
        public void PeopleWithoutIndividualData_FoldsDepartmentsWithFewerThanFivePowerUsers_AndNeverLeavesAHandfulInTheFoldedRow()
        {
            var people = new TeamsPeople();
            people.ChampionsByDepartment.Add(new TeamsNamedCountRow { Name = "Contoso Nine", Count = 9, SharePct = 9 * 100d / 11 });
            people.ChampionsByDepartment.Add(new TeamsNamedCountRow { Name = "Contoso Two", Count = 2, SharePct = 2 * 100d / 11 });

            var trimmed = people.WithoutIndividualData(5);

            // Two power users on their own would be the folded row, and also everyone less the named rows.
            Assert.AreEqual(1, trimmed.ChampionsByDepartment.Count);
            Assert.IsTrue(trimmed.ChampionsByDepartment[0].Other);
            Assert.IsNull(trimmed.ChampionsByDepartment[0].Name);
            Assert.AreEqual(11, trimmed.ChampionsByDepartment[0].Count);
            Assert.AreEqual(100d, trimmed.ChampionsByDepartment[0].SharePct.Value, 1e-9);
            Assert.AreEqual(2, trimmed.OtherChampionDepartments);
            Assert.AreEqual(2, people.ChampionsByDepartment.Count, "The cached section is never edited.");

            var alone = new TeamsPeople();
            alone.ChampionsByDepartment.Add(new TeamsNamedCountRow { Name = "Contoso Two", Count = 2 });
            var hidden = alone.WithoutIndividualData(5);
            Assert.AreEqual(0, hidden.ChampionsByDepartment.Count, "Too few even together: left out.");
            Assert.AreEqual(1, hidden.OtherChampionDepartments);
        }

        private static TeamsDemographicRow Group(string name, int known, int active, double messagesPerActiveUser = 0) =>
            new TeamsDemographicRow
            {
                Name = name,
                KnownUsers = known,
                ActiveUsers = active,
                ReachPct = TeamsExplorerScoring.Percentage(active, known),
                MessagesPerActiveUser = messagesPerActiveUser,
            };

        #endregion

        #region Window

        [TestMethod]
        public void WindowSnapsToAnAllowedLength()
        {
            Assert.AreEqual(7, TeamsExplorerQuery.SnapDays(5));
            Assert.AreEqual(7, TeamsExplorerQuery.SnapDays(7));
            Assert.AreEqual(28, TeamsExplorerQuery.SnapDays(30));
            Assert.AreEqual(90, TeamsExplorerQuery.SnapDays(100));
            Assert.AreEqual(365, TeamsExplorerQuery.SnapDays(100000));
            Assert.AreEqual(
                TeamsExplorerQuery.DefaultWindowDays,
                TeamsExplorerQuery.SnapDays(0),
                "A zero or negative window must fall back to the default, not produce an empty range.");
        }

        [TestMethod]
        public void AnAmbiguousWindowSnapsToTheCheaperOption()
        {
            // Exactly between 7 and 28. Ties go SHORT: an ambiguous request should cost less, not more.
            Assert.AreEqual(7, TeamsExplorerQuery.SnapDays(17));

            // ... and between 90 and 180.
            Assert.AreEqual(90, TeamsExplorerQuery.SnapDays(135));
        }

        [TestMethod]
        public void UsageWindowIsShiftedByTheReportLagNotTruncated()
        {
            var now = new DateTime(2026, 3, 20, 11, 0, 0, DateTimeKind.Utc);
            var query = TeamsExplorerQuery.Create(28, now);

            Assert.AreEqual(new DateTime(2026, 3, 21), query.ToExclusiveUtc);
            Assert.AreEqual(new DateTime(2026, 2, 21), query.FromUtc);

            // The usage window ends UsageReportLagDays earlier...
            Assert.AreEqual(new DateTime(2026, 3, 18), query.UsageToExclusiveUtc);

            // ...but still covers a FULL 28 days. Truncating instead would silently compare a 25-day
            // window against a 28-day one whenever the caller asked for 28.
            Assert.AreEqual(28, (query.UsageToExclusiveUtc - query.UsageFromUtc).Days);
            Assert.AreEqual(28, (query.ToExclusiveUtc - query.FromUtc).Days);
        }

        [TestMethod]
        public void TrailingActiveWindowsNeverReachOutsideTheReportingWindow()
        {
            var now = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);
            var week = TeamsExplorerQuery.Create(7, now);

            // With a 7-day window the "monthly" active window must not silently query 28 days.
            Assert.AreEqual(week.UsageFromUtc, TeamsExplorerSql.MonthlyActiveFrom(week));
            Assert.AreEqual(week.UsageFromUtc, TeamsExplorerSql.WeeklyActiveFrom(week));

            var quarter = TeamsExplorerQuery.Create(90, now);
            Assert.AreEqual(quarter.UsageToExclusiveUtc.AddDays(-7), TeamsExplorerSql.WeeklyActiveFrom(quarter));
            Assert.AreEqual(quarter.UsageToExclusiveUtc.AddDays(-28), TeamsExplorerSql.MonthlyActiveFrom(quarter));
        }

        [TestMethod]
        public void GroupingAndTopAreNormalisedNotRejected()
        {
            Assert.AreEqual("country", TeamsExplorerQuery.NormaliseGrouping("Country"));
            Assert.AreEqual("jobTitle", TeamsExplorerQuery.NormaliseGrouping("JOBTITLE"));
            Assert.AreEqual(TeamsExplorerQuery.DefaultGrouping, TeamsExplorerQuery.NormaliseGrouping("'; DROP TABLE teams--"));
            Assert.AreEqual(TeamsExplorerQuery.DefaultGrouping, TeamsExplorerQuery.NormaliseGrouping(null));

            Assert.AreEqual(TeamsExplorerQuery.MaximumTop, TeamsExplorerQuery.NormaliseTop(int.MaxValue));
            Assert.AreEqual(TeamsExplorerQuery.DefaultTop, TeamsExplorerQuery.NormaliseTop(-1));
            Assert.AreEqual(50, TeamsExplorerQuery.NormaliseTop(50));
        }

        [TestMethod]
        public void CacheKeyChangesWithEveryInputThatChangesTheAnswer()
        {
            var now = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);
            var baseline = TeamsExplorerQuery.Create(28, now).CacheKey("overview");

            Assert.AreNotEqual(baseline, TeamsExplorerQuery.Create(90, now).CacheKey("overview"));
            Assert.AreNotEqual(baseline, TeamsExplorerQuery.Create(28, now, "country").CacheKey("overview"));
            Assert.AreNotEqual(baseline, TeamsExplorerQuery.Create(28, now, top: 50).CacheKey("overview"));
            Assert.AreNotEqual(baseline, TeamsExplorerQuery.Create(28, now).CacheKey("people"));

            // Crossing midnight moves the window, so a cached entry must not survive it.
            Assert.AreNotEqual(baseline, TeamsExplorerQuery.Create(28, now.AddDays(1)).CacheKey("overview"));

            // The same day at a different time is the same window, so it MUST hit the cache.
            Assert.AreEqual(baseline, TeamsExplorerQuery.Create(28, now.AddHours(9)).CacheKey("overview"));
        }

        #endregion

        #region Segments

        [TestMethod]
        public void WorkingDaysExcludeWeekends()
        {
            // Mon 2 Mar 2026 to Sun 8 Mar inclusive = one full week.
            var from = new DateTime(2026, 3, 2);
            Assert.AreEqual(5, TeamsExplorerScoring.WorkingDaysBetween(from, from.AddDays(7)));
            Assert.AreEqual(20, TeamsExplorerScoring.WorkingDaysBetween(from, from.AddDays(28)));
            Assert.AreEqual(0, TeamsExplorerScoring.WorkingDaysBetween(from, from));
        }

        [TestMethod]
        public void SegmentsFollowTheStatedThresholds()
        {
            const int workingDays = 20;

            Assert.AreEqual(TeamsUserSegment.Dormant, TeamsExplorerScoring.Segment(0, workingDays));

            // < 20% of working days.
            Assert.AreEqual(TeamsUserSegment.Light, TeamsExplorerScoring.Segment(1, workingDays));
            Assert.AreEqual(TeamsUserSegment.Light, TeamsExplorerScoring.Segment(3, workingDays));

            // 20% - 60% inclusive.
            Assert.AreEqual(TeamsUserSegment.Regular, TeamsExplorerScoring.Segment(4, workingDays));
            Assert.AreEqual(TeamsUserSegment.Regular, TeamsExplorerScoring.Segment(12, workingDays));

            // > 60%.
            Assert.AreEqual(TeamsUserSegment.Power, TeamsExplorerScoring.Segment(13, workingDays));
            Assert.AreEqual(TeamsUserSegment.Power, TeamsExplorerScoring.Segment(20, workingDays));
        }

        [TestMethod]
        public void WeekendActivityIsCountedNotDiscarded()
        {
            // Seven active days against a five-working-day week: real activity, clamped rather than
            // thrown away, so it lands in Power rather than overflowing the scale.
            Assert.AreEqual(TeamsUserSegment.Power, TeamsExplorerScoring.Segment(7, 5));
        }

        [TestMethod]
        public void MinimumPowerDaysAgreesWithTheSegmentFunction()
        {
            // The closed form floor(workingDays * 0.6) + 1 is WRONG at five working days, because 0.6
            // has no exact binary representation. Assert agreement across every plausible window
            // length rather than trusting the arithmetic.
            for (var workingDays = 1; workingDays <= 261; workingDays++)
            {
                var threshold = TeamsExplorerScoring.MinimumPowerDays(workingDays);

                Assert.AreEqual(
                    TeamsUserSegment.Power,
                    TeamsExplorerScoring.Segment(threshold, workingDays),
                    $"{threshold} of {workingDays} working days should be a power user.");

                if (threshold > 1)
                {
                    Assert.AreNotEqual(
                        TeamsUserSegment.Power,
                        TeamsExplorerScoring.Segment(threshold - 1, workingDays),
                        $"{threshold - 1} of {workingDays} working days should NOT be a power user.");
                }
            }
        }

        [TestMethod]
        public void FiveWorkingDaysIsTheCaseTheClosedFormGetsWrong()
        {
            // Pinned separately because it is the specific value that motivated the search-based
            // implementation: floor(5 * 0.6) + 1 computes 3, but 3 of 5 days is exactly 60% and
            // therefore Regular.
            Assert.AreEqual(TeamsUserSegment.Regular, TeamsExplorerScoring.Segment(3, 5));
            Assert.AreEqual(4, TeamsExplorerScoring.MinimumPowerDays(5));
        }

        #endregion

        #region Bands

        [TestMethod]
        public void BandsMatchTheGaugeScaleUsedByTheUi()
        {
            // These boundaries are duplicated in the portal's GaugeRing ADOPTION_BANDS and in
            // teamsShared.ts. If they drift, the page states a figure is "healthy" beside a red card.
            Assert.AreEqual(40, TeamsExplorerScoring.NeedsAttentionUpperPct);
            Assert.AreEqual(70, TeamsExplorerScoring.ProgressingUpperPct);

            Assert.AreEqual("critical", TeamsExplorerScoring.BandTone(0));
            Assert.AreEqual("critical", TeamsExplorerScoring.BandTone(39.9));
            Assert.AreEqual("warning", TeamsExplorerScoring.BandTone(40));
            Assert.AreEqual("warning", TeamsExplorerScoring.BandTone(69.9));
            Assert.AreEqual("good", TeamsExplorerScoring.BandTone(70));
            Assert.AreEqual("good", TeamsExplorerScoring.BandTone(100));
        }

        [TestMethod]
        public void BandLabelAndToneNeverDisagree()
        {
            foreach (var percent in new[] { 0d, 10, 39.99, 40, 55, 69.99, 70, 85, 100 })
            {
                var label = TeamsExplorerScoring.BandLabel(percent);
                var tone = TeamsExplorerScoring.BandTone(percent);

                var expectedTone =
                    label == "Needs attention" ? "critical" :
                    label == "Progressing" ? "warning" : "good";

                Assert.AreEqual(expectedTone, tone, $"Label and tone disagree at {percent}%.");
            }
        }

        #endregion

        #region Percentages and concentration

        [TestMethod]
        public void PercentageIsSafeAgainstAZeroDenominator()
        {
            Assert.AreEqual(0, TeamsExplorerScoring.Percentage(5, 0));
            Assert.AreEqual(0, TeamsExplorerScoring.Percentage(0, 0));
            Assert.AreEqual(50, TeamsExplorerScoring.Percentage(1, 2));
        }

        [TestMethod]
        public void TopDecileShareIsTenPercentWhenContributionIsEven()
        {
            var even = Enumerable.Repeat(10L, 100).ToList();
            Assert.AreEqual(10, TeamsExplorerScoring.TopDecileShare(even), 0.0001);
        }

        [TestMethod]
        public void TopDecileShareRisesAsContributionConcentrates()
        {
            // One person runs 900 of 1,000 meetings; the other ninety-nine share the rest.
            var skewed = new List<long> { 900 };
            skewed.AddRange(Enumerable.Repeat(1L, 99));

            var share = TeamsExplorerScoring.TopDecileShare(skewed);

            Assert.IsTrue(share > 90, $"Expected a heavily concentrated share, got {share}.");
        }

        [TestMethod]
        public void TopDecileShareIsDefinedForTinyAndEmptyInputs()
        {
            Assert.AreEqual(0, TeamsExplorerScoring.TopDecileShare(null));
            Assert.AreEqual(0, TeamsExplorerScoring.TopDecileShare(new List<long>()));
            Assert.AreEqual(0, TeamsExplorerScoring.TopDecileShare(new List<long> { 0, 0 }));

            // A three-person tenant still has a "busiest tenth" - rounded up to one person.
            Assert.AreEqual(50, TeamsExplorerScoring.TopDecileShare(new List<long> { 5, 3, 2 }), 0.0001);
        }

        #endregion

        #region Judgements

        private static TeamsJudgementInputs FullyAvailable()
        {
            return new TeamsJudgementInputs
            {
                ActiveUsers = 800,
                KnownUsers = 1000,
                OpenCollaborationPct = 35,
                MeetingsPerActiveUser = 12,
                AfterHoursPct = 5,
                OrganiserConcentrationPct = 20,
                ActiveTeams = 40,
                TotalTeams = 50,
                OwnerlessTeams = 0,
                UsageReportsAvailable = true,
                CallsAvailable = true,
                TeamsAnalyticsAvailable = true,
            };
        }

        [TestMethod]
        public void JudgementsOnlyClaimWhatTheAvailableDataSupports()
        {
            var noSources = FullyAvailable();
            noSources.UsageReportsAvailable = false;
            noSources.CallsAvailable = false;
            noSources.TeamsAnalyticsAvailable = false;

            Assert.AreEqual(0, TeamsExplorerScoring.Judgements(noSources).Count,
                "With no sources switched on the page must not assert anything about the tenant.");

            var callsOnly = FullyAvailable();
            callsOnly.UsageReportsAvailable = false;
            callsOnly.TeamsAnalyticsAvailable = false;

            var keys = TeamsExplorerScoring.Judgements(callsOnly).Select(j => j.Key).ToList();
            CollectionAssert.DoesNotContain(keys, "reach");
            Assert.IsTrue(keys.Count > 0, "Calls data alone should still produce a meeting finding.");
        }

        [TestMethod]
        public void ReachFindingUsesTheSameBandAsTheGauge()
        {
            var inputs = FullyAvailable();
            inputs.ActiveUsers = 200;
            inputs.KnownUsers = 1000;

            var reach = TeamsExplorerScoring.Judgements(inputs).Single(j => j.Key == "reach");

            Assert.AreEqual("critical", reach.Tone);
            StringAssert.Contains(reach.Headline, "20%");
            StringAssert.Contains(reach.Detail, "needs attention");
        }

        [TestMethod]
        public void OwnerlessTeamsOutrankSprawlAsAGovernanceFinding()
        {
            var inputs = FullyAvailable();
            inputs.OwnerlessTeams = 3;

            var governance = TeamsExplorerScoring.Judgements(inputs).Single(
                j => j.Key == "ownerless-teams" || j.Key == "team-sprawl");

            Assert.AreEqual("ownerless-teams", governance.Key);
            Assert.AreEqual("critical", governance.Tone);
        }

        [TestMethod]
        public void SustainedOutOfHoursMeetingsAreSurfacedAheadOfAverages()
        {
            var inputs = FullyAvailable();
            inputs.AfterHoursPct = 31;

            var finding = TeamsExplorerScoring.Judgements(inputs).Single(
                j => j.Key == "after-hours" || j.Key == "organiser-concentration" || j.Key == "meeting-load");

            Assert.AreEqual("after-hours", finding.Key);
            Assert.AreEqual("warning", finding.Tone);
            StringAssert.Contains(finding.Detail, "UTC");
        }

        [TestMethod]
        public void PrivateChatDominanceIsFlaggedAsAKnowledgeProblemNotAnAdoptionOne()
        {
            var inputs = FullyAvailable();
            inputs.OpenCollaborationPct = 4;

            var finding = TeamsExplorerScoring.Judgements(inputs).Single(j => j.Key == "open-collaboration");

            Assert.AreEqual("warning", finding.Tone);
            StringAssert.Contains(finding.Detail, "private chats");
        }

        [TestMethod]
        public void EveryJudgementCarriesBothAHeadlineAndAnExplanation()
        {
            foreach (var judgement in TeamsExplorerScoring.Judgements(FullyAvailable()))
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(judgement.Headline), judgement.Key);
                Assert.IsFalse(string.IsNullOrWhiteSpace(judgement.Detail), judgement.Key);
                CollectionAssert.Contains(
                    new[] { "good", "warning", "critical", "neutral" },
                    judgement.Tone,
                    $"'{judgement.Tone}' is not a tone the UI can render.");
            }
        }

        #endregion

        #region Exports

        [TestMethod]
        public void OnlyKnownExportSectionsAreAccepted()
        {
            foreach (var section in TeamsExplorerExports.Sections)
            {
                Assert.IsTrue(TeamsExplorerExports.IsKnownSection(section));
                Assert.IsTrue(TeamsExplorerExports.IsKnownSection(section.ToUpperInvariant()));
            }

            Assert.IsFalse(TeamsExplorerExports.IsKnownSection("../../web.config"));
            Assert.IsFalse(TeamsExplorerExports.IsKnownSection(null));
            Assert.IsFalse(TeamsExplorerExports.IsKnownSection(string.Empty));
        }

        [TestMethod]
        public void SentimentColumnHeadingStatesItsScale()
        {
            // A bare "Sentiment" column of 0.5s reads as "50% positive", which is wrong: 0.5 is
            // exactly neutral on this scale.
            var heading = TeamsExplorerExports.TeamColumns().Single(c => c.Header.StartsWith("Sentiment")).Header;

            StringAssert.Contains(heading, "0.5 neutral");
        }

        [TestMethod]
        public void TeamExportSaysWhetherATeamWasMeasurable()
        {
            // Without this column a zero in "Channel messages" is ambiguous - an unauthorised team is
            // unmeasured, not quiet, and archiving one on that basis would be a mistake.
            var headers = TeamsExplorerExports.TeamColumns().Select(c => c.Header).ToList();
            CollectionAssert.Contains(headers, "Authorised for deep analytics");
        }

        [TestMethod]
        public void DemographicExportHeadingFollowsTheSelectedGrouping()
        {
            Assert.AreEqual("Country", TeamsExplorerExports.DemographicColumns("country").First().Header);
            Assert.AreEqual("Job title", TeamsExplorerExports.DemographicColumns("jobTitle").First().Header);
            Assert.AreEqual("Department", TeamsExplorerExports.DemographicColumns("nonsense").First().Header);
        }

        #endregion
    }
}
