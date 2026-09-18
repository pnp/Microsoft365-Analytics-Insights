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
