extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using UnitTests.FakeLoaderClasses;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using CopilotAdoptionAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotAdoptionAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the Copilot licence-adoption engine.
    ///
    /// All the rules that decide what an executive sees - which SKUs count as a Copilot seat, how a
    /// user is scored and banded, how a licence candidate is ranked - live in pure functions in
    /// Common.Entities precisely so they can be pinned down here. These outputs are used to justify
    /// licence spend and to pick who gets training, so a silent change in any of them is a real cost
    /// to a customer, not a cosmetic regression.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionTests
    {
        private static readonly DateTime WindowStart = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Now = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

        #region Licence classification

        [TestMethod]
        public void CopilotSeatSkus_AreRecognised()
        {
            // The two SKU part numbers Microsoft has shipped the Microsoft 365 Copilot seat under, plus
            // the education variant - all three appear in the licensing CSV this product ships with.
            Assert.IsTrue(CopilotLicenceClassifier.IsCopilotSeat("M365_Copilot", "Microsoft Copilot for Microsoft 365"));
            Assert.IsTrue(CopilotLicenceClassifier.IsCopilotSeat("Microsoft_365_Copilot", "Microsoft Copilot for Microsoft 365"));
            Assert.IsTrue(CopilotLicenceClassifier.IsCopilotSeat("Microsoft_365_Copilot_EDU", "Microsoft 365 Copilot (Education Faculty)"));
        }

        [TestMethod]
        public void FutureCopilotSeatSku_IsRecognisedByPrefix()
        {
            // A SKU newer than the licensing CSV shipped in this build has no display name, so the
            // importer stores the part number as the name too. Prefix matching is what stops a
            // customer's newest (and most expensive) seats from silently vanishing from the report.
            Assert.IsTrue(
                CopilotLicenceClassifier.IsCopilotSeat("Microsoft_365_Copilot_Business_Preview", "Microsoft_365_Copilot_Business_Preview"),
                "An unrecognised Microsoft_365_Copilot* SKU must still be counted as a seat.");
        }

        [TestMethod]
        public void CopilotBrandedProductsThatAreNotSeats_AreExcluded()
        {
            // These are separately-sold products. Counting them would inflate the licensed population
            // and make adoption look far worse than it is - the failure mode that matters most, because
            // this number is used to argue about spend.
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("Microsoft_Copilot_for_Sales", "Microsoft 365 Copilot for Sales"));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("Microsoft_Viva_Sales", "Microsoft Sales Copilot"));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("Power_Virtual_Agents", "Microsoft Copilot Studio"));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("VIRTUAL_AGENT_USL", "Microsoft Copilot Studio User License"));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("CCIBOTS_PRIVPREV_VIRAL", "Microsoft Copilot Studio Viral Trial"));
        }

        [TestMethod]
        public void NonCopilotProducts_AreNotSeats()
        {
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("ENTERPRISEPACK", "Office 365 E3"));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("SPE_E5", "Microsoft 365 E5"));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat(null, null));
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat(string.Empty, string.Empty));
        }

        [TestMethod]
        public void RenamedSeatSku_IsRecognisedByDisplayName()
        {
            // Fallback path: an unrecognised part number, but a display name that clearly identifies the
            // Microsoft 365 Copilot product.
            Assert.IsTrue(CopilotLicenceClassifier.IsCopilotSeat("SOME_NEW_STEM", "Microsoft 365 Copilot"));

            // ...and the fallback must not swallow the other Copilot-branded products.
            Assert.IsFalse(CopilotLicenceClassifier.IsCopilotSeat("SOME_NEW_STEM", "Microsoft 365 Copilot Studio"));
        }

        [TestMethod]
        public void ExplicitOverride_ReplacesTheAutomaticClassification()
        {
            var licenceTypes = new List<LicenceTypeRow>
            {
                new LicenceTypeRow { Id = 1, SkuPartNumber = "Microsoft_365_Copilot", Name = "Microsoft Copilot for Microsoft 365" },
                new LicenceTypeRow { Id = 2, SkuPartNumber = "ENTERPRISEPACK", Name = "Office 365 E3" },
                new LicenceTypeRow { Id = 3, SkuPartNumber = "WEIRD_PARTNER_SKU", Name = "Bundled Copilot Seat (reseller)" },
            };

            CollectionAssert.AreEqual(
                new[] { 1 },
                CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, null).ToArray(),
                "With no override, only the automatically classified seat should count.");

            CollectionAssert.AreEqual(
                new[] { 1, 3 },
                CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, new[] { 1, 3 }).ToArray(),
                "An admin override must be honoured for SKUs the classifier cannot know about.");

            CollectionAssert.AreEqual(
                new[] { 2 },
                CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, new[] { 2, 99 }).ToArray(),
                "Ids that do not exist must be dropped rather than reaching the query.");
        }

        #endregion

        #region Engagement scoring

        [TestMethod]
        public void DailyMultiAppUser_ScoresAsAChampion()
        {
            // 20 active days over a 28-day window (every working day), 6 interactions a day, 4 apps:
            // full marks on all three components.
            var row = UsageRow(interactions: 120, activeDays: 20, appsUsed: 4, lastUse: Now.AddDays(-1));

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(100, scored.AdoptionScore, "Maximum on every component should be 100.");
            Assert.AreEqual(AdoptionBand.Champion, scored.Band);
            Assert.IsTrue(CopilotAdoptionScoring.IsHabitual(scored.Band));
        }

        [TestMethod]
        public void ScoreIsGraded_NotBinary()
        {
            // The requirement this feature exists for: usage is "rarely a yes/no". Two users who both
            // count as "active" in Microsoft's reports must be clearly separated here.
            var occasional = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 3, activeDays: 2, appsUsed: 1, lastUse: Now.AddDays(-6)),
                WindowStart, Now, auditAvailable: true);

            var regular = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 60, activeDays: 15, appsUsed: 3, lastUse: Now.AddDays(-1)),
                WindowStart, Now, auditAvailable: true);

            Assert.IsTrue(occasional.AdoptionScore > 0, "Some use must never score zero - that is the dormant band.");
            Assert.IsTrue(
                regular.AdoptionScore > occasional.AdoptionScore + 40,
                $"A regular user ({regular.AdoptionScore}) must be clearly separated from an occasional one ({occasional.AdoptionScore}).");
            Assert.AreEqual(AdoptionBand.Trialling, occasional.Band);
            Assert.AreEqual(AdoptionBand.Champion, regular.Band);
        }

        [TestMethod]
        public void NeverUsed_AndDormant_AreDifferentBands()
        {
            // The distinction that pays for the feature: one needs onboarding, the other needs a
            // conversation or the seat back. Averaging them into "not using it" hides both.
            var neverUsed = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 0, activeDays: 0, appsUsed: 0, lastUse: null),
                WindowStart, Now, auditAvailable: true);

            var dormantRow = UsageRow(interactions: 0, activeDays: 0, appsUsed: 0, lastUse: WindowStart.AddDays(-30));
            dormantRow.PriorInteractions = 42;
            var dormant = CopilotAdoptionScoring.Score(dormantRow, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(AdoptionBand.NeverUsed, neverUsed.Band);
            Assert.AreEqual(AdoptionBand.Dormant, dormant.Band);
            Assert.AreEqual(0, neverUsed.AdoptionScore);
            Assert.AreEqual(0, dormant.AdoptionScore);
            StringAssert.Contains(neverUsed.RecommendedAction, "Reclaim");
            StringAssert.Contains(dormant.RecommendedAction, "Win back");
        }

        [TestMethod]
        public void BreadthIsScoredSeparatelyFromFrequency()
        {
            // Someone using Copilot every day but only in one app is a different (and much cheaper to
            // fix) problem than someone using it rarely across many apps. The components must not be
            // collapsed into a single number that treats them the same.
            var deepButNarrow = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 100, activeDays: 20, appsUsed: 1, lastUse: Now),
                WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(100, deepButNarrow.FrequencyScore);
            Assert.AreEqual(100, deepButNarrow.DepthScore);
            Assert.IsTrue(deepButNarrow.BreadthScore < 40, "One app out of a target of three is a low breadth score.");
            Assert.IsTrue(deepButNarrow.AdoptionScore < 100, "A narrow user must not score full marks overall.");
            StringAssert.Contains(deepButNarrow.RecommendedAction, "broaden",
                "Even a deep user working in a single surface should be told the cheapest available gain.");
        }

        [TestMethod]
        public void WhenAuditIsUnavailable_MicrosoftsReportIsUsedInstead()
        {
            // Without this fallback, a deployment that only imports Microsoft's Copilot usage report
            // would report its entire licensed population as "never used" - both wrong and expensive.
            var row = UsageRow(interactions: 0, activeDays: 0, appsUsed: 0, lastUse: null);
            row.ReportPrompts = 90;
            row.ReportActiveDays = 18;
            row.ReportAppsUsed = 3;
            row.ReportLastActivityUtc = Now.AddDays(-2);

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: false);

            Assert.AreEqual(CopilotAdoptionScoring.SignalSourceUsageReport, scored.SignalSource,
                "The row must record which source it was scored from.");
            Assert.AreEqual(AdoptionBand.Champion, scored.Band);
            Assert.AreEqual(18, scored.ActiveDays);
        }

        [TestMethod]
        public void AGapInTheAuditDataForOneUser_DoesNotMarkThemNeverUsed()
        {
            // The dangerous case: the audit import is working overall, but has nothing for this user
            // while Microsoft says they were active. Reporting them as "never used" would put an
            // actively working person on a list of seats to take away.
            var row = UsageRow(interactions: 0, activeDays: 0, appsUsed: 0, lastUse: null);
            row.ReportPrompts = 90;
            row.ReportActiveDays = 18;
            row.ReportAppsUsed = 3;
            row.ReportLastActivityUtc = Now.AddDays(-2);

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(CopilotAdoptionScoring.SignalSourceUsageReport, scored.SignalSource);
            Assert.AreNotEqual(AdoptionBand.NeverUsed, scored.Band,
                "Microsoft's report says this user is active; they must not be listed as never having used Copilot.");
            Assert.AreEqual(AdoptionBand.Champion, scored.Band);
        }

        [TestMethod]
        public void AuditDataWinsWhenItHasSomethingToSay()
        {
            // When both sources have data for a user, the audit log wins: it is bucketed to exactly
            // the window that was requested, whereas Microsoft's report uses its own.
            var row = UsageRow(interactions: 4, activeDays: 2, appsUsed: 1, lastUse: Now.AddDays(-3));
            row.ReportPrompts = 500;
            row.ReportActiveDays = 20;
            row.ReportAppsUsed = 6;
            row.ReportLastActivityUtc = Now.AddDays(-1);

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(CopilotAdoptionScoring.SignalSourceAudit, scored.SignalSource);
            Assert.AreEqual(2, scored.ActiveDays, "The audit figures, not Microsoft's.");
            Assert.AreEqual(4, scored.Interactions);
            Assert.AreNotEqual(AdoptionBand.Champion, scored.Band,
                "Microsoft's 500 prompts across 20 days and 6 apps would band this user Champion. That is "
                + "the leak this test exists to catch.");
            Assert.AreEqual(AdoptionBand.Trialling, scored.Band,
                "Four interactions across two days of a 28-day window is occasional use. (This read "
                + "Developing until the depth component was made confidence-weighted: two active days "
                + "used to earn 40% of the depth marks outright, which flattered a two-day user into a "
                + "band whose advice is 'a habit is forming'.)");
        }

        [TestMethod]
        public void SqlScoreExpression_CollapsesUnavailableSourcesToZero()
        {
            // When a data source is unavailable its CTE and join are omitted from the query, so the
            // ranking expression must not still reference them - that is an unbound-identifier error at
            // run time, not a compile-time one, so only a real database (or this test) catches it.
            var auditOnly = CopilotAdoptionSql.LicenceOpportunitiesSql(
                new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: true, includeM365Usage: false);

            Assert.IsFalse(auditOnly.Contains("teams."), "The Teams CTE is not in this query, so nothing may reference it.");
            Assert.IsFalse(auditOnly.Contains("mail."), "The Outlook CTE is not in this query.");
            Assert.IsFalse(auditOnly.Contains("files."), "The file CTE is not in this query.");

            var usageOnly = CopilotAdoptionSql.LicenceOpportunitiesSql(
                new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: false, includeM365Usage: true);

            Assert.IsFalse(usageOnly.Contains("copilot."), "The Copilot CTE is not in this query.");
            StringAssert.Contains(usageOnly, "teams.Messages");
        }

        [TestMethod]
        public void TargetActiveDays_UsesWorkingDaysNotCalendarDays()
        {
            // Scoring against calendar days would cap a genuinely daily user at ~71%, which is not a
            // number anyone should have to explain to a CFO.
            var options = new CopilotAdoptionOptions { WindowDays = 28 };

            Assert.AreEqual(20d, CopilotAdoptionScoring.AvailableWorkingDays(options), 0.001);
            Assert.AreEqual(12d, CopilotAdoptionScoring.TargetActiveDays(options), 0.001);
        }

        [TestMethod]
        public void BrandNewInactiveUser_IsTooNewToJudgeNotReclaimable()
        {
            // New starters are the highest-risk false positive: without a tenure grace period a person
            // licensed five days ago is scored against a full month and lands in the reclaim list before
            // onboarding has had a fair chance to work.
            var row = UsageRow(interactions: 0, activeDays: 0, appsUsed: 0, lastUse: null);
            row.AccountEnabled = true;
            row.AccountCreatedUtc = Now.AddDays(-5);

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(AdoptionBand.NeverUsed, scored.Band, "The raw usage state is still true.");
            Assert.IsTrue(scored.TooNewToJudge);
            Assert.AreEqual(CopilotAdoptionScoring.ReclaimEligibilityTiers.Review, scored.ReclaimEligibility,
                "Too-new users need a later review, not an automatic reclaim recommendation.");
            Assert.AreEqual(CopilotAdoptionScoring.AdoptionActionCodes.Review, scored.RecommendedActionCode);
            StringAssert.Contains(scored.RecommendedAction, "Too new to judge");
            Assert.AreEqual(CopilotAdoptionScoring.TenureBasisAccountAge, scored.TenureBasis,
                "Until seat-tenure history exists, the row must say it used account age.");
        }

        [TestMethod]
        public void BrandNewHighlyActiveUser_GetsProratedFrequencyTarget()
        {
            // The grace period protects in both directions. A daily user who joined last week is a
            // champion-in-progress, not a Trialling user, because they could not possibly have been
            // active on the full-window target days.
            var row = UsageRow(interactions: 30, activeDays: 5, appsUsed: 3, lastUse: Now.AddDays(-1));
            row.AccountEnabled = true;
            row.AccountCreatedUtc = Now.AddDays(-5);

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: true);

            Assert.IsTrue(scored.ExpectedActiveDays < CopilotAdoptionScoring.TargetActiveDays(CopilotAdoptionOptions.Default),
                "Frequency must be prorated while the account-age tenure proxy is inside the grace period.");
            Assert.AreEqual(100, scored.FrequencyScore);
            Assert.AreEqual(AdoptionBand.Champion, scored.Band);
            Assert.IsFalse(scored.TooNewToJudge && scored.ReclaimEligibility == CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable,
                "Active new users must never leak into reclaim tiers.");
        }

        [TestMethod]
        public void LongTenuredInactiveUser_IsProbableReclaim()
        {
            // This is the low-risk reclaim case: no observed use, account enabled, and the available
            // tenure signal is comfortably beyond the grace period.
            var row = UsageRow(interactions: 0, activeDays: 0, appsUsed: 0, lastUse: null);
            row.AccountEnabled = true;
            row.AccountCreatedUtc = Now.AddDays(-120);

            var scored = CopilotAdoptionScoring.Score(row, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(AdoptionBand.NeverUsed, scored.Band);
            Assert.AreEqual(CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable, scored.ReclaimEligibility);
            Assert.AreEqual(CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, scored.RecommendedActionCode);
            StringAssert.Contains(scored.ReclaimEligibilityReason, "beyond the 30-day grace period");
        }

        [TestMethod]
        public void WindowStart_SpansExactlyTheRequestedNumberOfDates()
        {
            // "Last 28 days" has to contain 28 distinct calendar dates. Starting at midnight 28 days
            // ago spans 29 of them, so somebody active every single day recorded 29 active days while
            // the frequency component divided by a target derived from 28 - inflating the score of
            // precisely the most engaged users, and making the dates shown in the UI a day wider than
            // the window they are labelled with.
            var nowUtc = new DateTime(2026, 8, 24, 14, 30, 0, DateTimeKind.Utc);

            foreach (var windowDays in new[] { 7, 28, 90, 180 })
            {
                var start = CopilotAdoptionScoring.WindowStartUtc(nowUtc, windowDays);

                var distinctDates = 0;
                for (var day = start; day.Date <= nowUtc.Date; day = day.AddDays(1))
                {
                    distinctDates++;
                }

                Assert.AreEqual(windowDays, distinctDates,
                    $"a {windowDays}-day window must contain exactly {windowDays} dates");
                Assert.AreEqual(start.TimeOfDay, TimeSpan.Zero, "the window must start at midnight");
            }
        }

        [TestMethod]
        public void WindowStart_NeverProducesMoreActiveDaysThanTheFrequencyTargetAssumes()
        {
            // The end-to-end version of the bug above: a user active on every date of the window must
            // not be able to beat the "available working days" the target is derived from.
            var options = new CopilotAdoptionOptions { WindowDays = 28 };
            var nowUtc = new DateTime(2026, 8, 24, 23, 59, 0, DateTimeKind.Utc);
            var start = CopilotAdoptionScoring.WindowStartUtc(nowUtc, options.WindowDays);

            var datesInWindow = (int)(nowUtc.Date - start.Date).TotalDays + 1;

            Assert.AreEqual(options.WindowDays, datesInWindow);
            Assert.IsTrue(datesInWindow <= options.WindowDays,
                "the numerator's window cannot be wider than the denominator's");
        }

        [TestMethod]
        public void Diagnostics_RecordTimingsAndIdentifyTheSlowestStep()
        {
            // The slowest step is reported as a plain string property so an operator can triage from the
            // event list without unpacking customMeasurements.
            var diagnostics = new CopilotAdoptionDiagnostics();
            Assert.IsNull(diagnostics.SlowestStep, "With nothing recorded there is no slowest step.");

            diagnostics.Record(CopilotAdoptionSteps.LicensedUsers, 4200);
            diagnostics.Record(CopilotAdoptionSteps.WeeklyTrend, 90000, failed: true);
            diagnostics.Record(CopilotAdoptionSteps.AgentEstate, 300);

            Assert.AreEqual(3, diagnostics.Steps.Count);
            Assert.AreEqual(CopilotAdoptionSteps.WeeklyTrend, diagnostics.SlowestStep.Step);
            Assert.AreEqual(90000, diagnostics.SlowestStep.DurationMs);

            // A step that failed still has to carry its duration: a query abandoned at the command
            // timeout is the single most useful thing in this telemetry, and dropping it would hide
            // exactly the tenants whose report is quietly degrading.
            Assert.IsTrue(diagnostics.Steps.Single(s => s.Step == CopilotAdoptionSteps.WeeklyTrend).Failed);
            Assert.IsFalse(diagnostics.Steps.Single(s => s.Step == CopilotAdoptionSteps.AgentEstate).Failed);
        }

        [TestMethod]
        public void Diagnostics_IgnoreAnUnnamedStep()
        {
            // Step names become App Insights measurement names, so a blank one would produce an unusable
            // metric rather than a useful one.
            var diagnostics = new CopilotAdoptionDiagnostics();

            diagnostics.Record(null, 10);
            diagnostics.Record(string.Empty, 10);
            diagnostics.Record("   ", 10);

            Assert.AreEqual(0, diagnostics.Steps.Count);
        }

        [TestMethod]
        public void Diagnostics_AreSerialisedInCamelCaseLikeEveryOtherModel()
        {
            // This solution has no global camelCase contract resolver, so a model without explicit
            // [JsonProperty] names silently serialises as PascalCase and the client reads undefined.
            // That has already happened once in this feature, to CopilotAdoptionOptions.
            var diagnostics = new CopilotAdoptionDiagnostics { TotalMs = 1234 };
            diagnostics.Record(CopilotAdoptionSteps.LicensedUsers, 42);

            var json = JsonConvert.SerializeObject(diagnostics);

            StringAssert.Contains(json, "\"totalMs\"");
            StringAssert.Contains(json, "\"steps\"");
            StringAssert.Contains(json, "\"durationMs\"");
            Assert.IsFalse(json.Contains("\"TotalMs\""), "Serialised names must be camelCase.");
        }

        [TestMethod]
        public void AgentUsageQuery_DoesNotCountUnattributedEventsAsAUser()
        {
            // dbo.audit_events.user_id is nullable, and the agent query's driving CTE does not exclude
            // NULL users. The original COUNT(DISTINCT au.user_id) skipped them implicitly; the rewritten
            // form groups by (agent_id, user_id), which puts unattributed events into their own NULL
            // group. COUNT(*) would count that group as a person - COUNT(user_id) does not.
            //
            // This is not cosmetic. Users is the adoption threshold: AgentHealthFor returns Review below
            // AgentMinUsers (default 3) and Keep at or above it, so one unattributed interaction against
            // an agent genuinely used by two people would flip its verdict and print "used by 3 people.
            // Genuinely adopted - keep supporting it."
            //
            // A row-for-row comparison against a synthetic tenant does not catch this, because generated
            // data has no unattributed events. Hence a test on the query text.
            var sql = CopilotAdoptionSql.AgentUsageSql(new[] { 1 });

            StringAssert.Contains(sql, "COUNT(user_id) AS Users");
            Assert.AreEqual(0, CountOccurrences(sql, "COUNT(*) AS Users"),
                "COUNT(*) over a (agent, user) grouping counts the NULL-user group as a person.");
        }

        [TestMethod]
        public void CustomWeights_StillProduceAScoreOutOfOneHundred()
        {
            // The weights are tunable, so a set that does not sum to 1 must still normalise rather than
            // silently producing scores above 100 or capping everyone at a fraction of the range.
            var options = new CopilotAdoptionOptions
            {
                FrequencyWeight = 2,
                DepthWeight = 2,
                BreadthWeight = 2,
            };

            var scored = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 200, activeDays: 20, appsUsed: 5, lastUse: Now),
                WindowStart, Now, auditAvailable: true, options: options);

            Assert.AreEqual(100, scored.AdoptionScore);
        }

        [TestMethod]
        public void ScoreIsCapped_ByExtremeUsage()
        {
            // A runaway automation account must not produce a score of 4,000 and distort every average
            // and department breakdown on the page.
            var scored = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 100000, activeDays: 28, appsUsed: 25, lastUse: Now),
                WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(100, scored.AdoptionScore);
            Assert.AreEqual(100, scored.FrequencyScore);
            Assert.AreEqual(100, scored.BreadthScore);
        }

        #endregion

        #region Licence-opportunity scoring

        [TestMethod]
        public void ExistingUnlicensedCopilotUse_IsTheStrongestSignal()
        {
            // Evidence must beat inference: someone already using Copilot Chat has demonstrated demand
            // for Copilot, which is a better argument than "sends a lot of email".
            var provenDemand = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UnlicensedCopilotInteractions = 40,
                UnlicensedCopilotActiveDays = 12,
            });

            var busyButUntried = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                TeamsMessages = 30,
                TeamsMeetings = 10,
                EmailsSent = 20,
                EmailsRead = 20,
            });

            Assert.AreEqual(35, provenDemand.OpportunityScore,
                "Maxing the Copilot-demand component alone should award its full weight.");
            Assert.IsTrue(
                provenDemand.OpportunityScore > busyButUntried.OpportunityScore,
                "Proven Copilot demand must outrank general Microsoft 365 busyness.");
            StringAssert.Contains(provenDemand.Rationale, "already using Copilot Chat without a licence");
        }

        [TestMethod]
        public void HeavyUserAcrossEveryWorkload_IsRecommended()
        {
            var heavy = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "someone@contoso.com",
                UnlicensedCopilotInteractions = 25,
                UnlicensedCopilotActiveDays = 10,
                TeamsMessages = 50,
                TeamsMeetings = 20,
                EmailsSent = 40,
                EmailsRead = 60,
                FilesViewedOrEdited = 55,
            });

            Assert.AreEqual(100, heavy.OpportunityScore);
            Assert.IsTrue(heavy.Recommended);
            Assert.AreEqual(CopilotAdoptionScoring.OpportunityTiers.ProvenDemand, heavy.QualificationTier,
                "Ten days of unlicensed Copilot use is evidence, not inference.");
            StringAssert.StartsWith(heavy.Rationale, "Recommended");
        }

        [TestMethod]
        public void UserWithNoActivity_ScoresZeroAndIsNotRecommended()
        {
            var idle = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow());

            Assert.AreEqual(0, idle.OpportunityScore);
            Assert.IsFalse(idle.Recommended);
            StringAssert.Contains(idle.Rationale, "No qualifying");
        }

        [TestMethod]
        public void OneDayTrialist_IsNotReportedAsFormingAHabit()
        {
            // The defect this guards: depth is interactions per *active* day, so a single active day
            // made full depth marks trivial to reach. Five prompts crammed into one afternoon scored
            // 40.8 and banded Developing - whose advice is "Deepen to daily use - a habit is forming" -
            // for somebody who tried Copilot once and never came back. Worse, being active on FEWER
            // days could outscore being active on more: this user beat the two-day user below.
            var triedOnce = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 5, activeDays: 1, appsUsed: 1, lastUse: Now.AddDays(-20)),
                WindowStart, Now, auditAvailable: true);

            var twoDays = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 3, activeDays: 2, appsUsed: 1, lastUse: Now.AddDays(-2)),
                WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(AdoptionBand.Trialling, triedOnce.Band,
                "One active day in a 28-day window is a trial, not a forming habit.");
            Assert.IsTrue(triedOnce.AdoptionScore < twoDays.AdoptionScore,
                $"More active days must never score worse: one day scored {triedOnce.AdoptionScore}, "
                + $"two days scored {twoDays.AdoptionScore}.");
            Assert.IsFalse(CopilotAdoptionScoring.IsHabitual(triedOnce.Band));
        }

        [TestMethod]
        public void SingleDayOfUse_CannotReachFullDepthMarks()
        {
            // The mechanism, pinned separately from the banding so a threshold change cannot quietly
            // reintroduce the defect: depth is scaled by how much of the minimum sample was observed.
            var options = CopilotAdoptionOptions.Default;

            var oneDay = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 50, activeDays: 1, appsUsed: 1, lastUse: Now),
                WindowStart, Now, auditAvailable: true, options: options);

            Assert.IsTrue(oneDay.DepthScore < 100,
                "A one-day sample must not earn full depth marks however many interactions it contains.");
            Assert.AreEqual(
                Math.Round(100d / options.DepthMinActiveDays, 1), oneDay.DepthScore, 0.05,
                "One of the three minimum days observed should earn a third of the depth marks.");
        }

        [TestMethod]
        public void DepthConfidence_DoesNotPenaliseAGenuinelyDeepUser()
        {
            // The other side of the fix: at or above the minimum sample nothing changes at all. If this
            // fails, the confidence factor is taxing the users it was never meant to touch.
            var deep = CopilotAdoptionScoring.Score(
                UsageRow(interactions: 100, activeDays: 20, appsUsed: 1, lastUse: Now),
                WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(100, deep.FrequencyScore);
            Assert.AreEqual(100, deep.DepthScore,
                "Twenty active days is far above the minimum sample, so depth is untouched.");
        }

        [TestMethod]
        public void DisabledAccountHoldingASeat_IsAlwaysAReclaim()
        {
            // A disabled account cannot be won back or coached - the person has gone. Branching on the
            // band alone told a previously-active disabled account to "Win back" (write to someone who
            // has left) and a recently-active one that no action was needed, while the page itself
            // calls this "the clearest reclaim there is".
            var wasActive = UsageRow(interactions: 80, activeDays: 18, appsUsed: 3, lastUse: Now.AddDays(-1));
            wasActive.AccountEnabled = false;

            var scored = CopilotAdoptionScoring.Score(wasActive, WindowStart, Now, auditAvailable: true);

            Assert.AreEqual(CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, scored.RecommendedActionCode,
                "A disabled account is a reclaim whatever its usage looked like beforehand.");
            StringAssert.Contains(scored.RecommendedAction, "disabled");
            Assert.AreNotEqual(CopilotAdoptionScoring.AdoptionActionCodes.Sustain, scored.RecommendedActionCode);
            Assert.AreNotEqual(CopilotAdoptionScoring.AdoptionActionCodes.Reengage, scored.RecommendedActionCode);
        }

        [TestMethod]
        public void ProvenCopilotDemand_QualifiesOnItsOwn()
        {
            // The defect this guards: the Copilot-demand weight (35) sits below the recommendation bar
            // (50), so the one signal that PROVES demand for Copilot could never clear it alone - while
            // general Microsoft 365 busyness (25 + 20 + 20 = 65) could. Somebody using Copilot Chat
            // every day without a licence was not recommended for one; somebody who had never opened
            // Copilot was. Microsoft's own readiness guidance ranks these the other way round.
            var provenDemand = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "daily@contoso.com",
                UnlicensedCopilotInteractions = 1000,
                UnlicensedCopilotActiveDays = 20,
            });

            var busyButNeverTriedCopilot = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "busy@contoso.com",
                TeamsMessages = 60,
                TeamsMeetings = 10,
                EmailsSent = 40,
                EmailsRead = 40,
                FilesViewedOrEdited = 40,
            });

            Assert.IsTrue(provenDemand.Recommended,
                $"A thousand unlicensed Copilot interactions scored {provenDemand.OpportunityScore} and "
                + "must be recommended regardless - the composite score cannot express proven demand.");
            Assert.AreEqual(CopilotAdoptionScoring.OpportunityTiers.ProvenDemand, provenDemand.QualificationTier);
            StringAssert.Contains(provenDemand.Rationale, "proven demand");

            Assert.AreEqual(
                CopilotAdoptionScoring.OpportunityTiers.WorkloadInferred,
                busyButNeverTriedCopilot.QualificationTier,
                "Busyness is inference, not evidence, and must be labelled as such even when it clears the bar.");
            StringAssert.StartsWith(busyButNeverTriedCopilot.Rationale, "Candidate for assessment");
        }

        [TestMethod]
        public void TryingCopilotOnce_IsNotProvenDemand()
        {
            // "Recurrent" is the point. A single day of unlicensed use is curiosity, not demand, and
            // must not short-circuit the score - otherwise the tier would recommend a seat for anyone
            // who ever opened Copilot once.
            var curious = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "curious@contoso.com",
                UnlicensedCopilotInteractions = 2,
                UnlicensedCopilotActiveDays = 1,
            });

            Assert.AreNotEqual(CopilotAdoptionScoring.OpportunityTiers.ProvenDemand, curious.QualificationTier);
            Assert.IsFalse(curious.Recommended);
            Assert.AreEqual(CopilotAdoptionScoring.OpportunityTiers.None, curious.QualificationTier);
        }

        [TestMethod]
        public void ProvenDemandCandidates_SurviveTheRowCap()
        {
            // The C# tier is not enough on its own. The candidate list is TOP (@maxRows) ORDER BY the
            // composite score, and that score cannot express proven demand - so on a large tenant a
            // daily unlicensed Copilot user could be ranked below thousands of merely busy users and
            // truncated away before the C# scorer ever saw them, silently reinstating the defect.
            var sql = CopilotAdoptionSql.LicenceOpportunitiesSql(
                new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: true, includeM365Usage: true);

            var orderByIndex = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
            Assert.IsTrue(orderByIndex >= 0, "The candidate query must be ordered.");

            var orderBy = sql.Substring(orderByIndex);
            StringAssert.Contains(orderBy, "copilot.ActiveDays",
                "Proven demand must be part of the ranking, or the row cap can discard it.");
            Assert.IsTrue(
                orderBy.IndexOf("copilot.ActiveDays", StringComparison.Ordinal)
                    < orderBy.IndexOf("RankScore", StringComparison.Ordinal),
                "Proven demand must sort ahead of the composite score, not after it.");
        }

        [TestMethod]
        public void WithoutTheAuditImport_TheCandidateQueryStillOrdersValidly()
        {
            // Proven demand is unobservable without the audit import, and a bare constant in an ORDER BY
            // is a SQL Server error ("a constant expression was encountered in the ORDER BY list"), so
            // the clause has to be omitted rather than collapsed to a literal.
            var sql = CopilotAdoptionSql.LicenceOpportunitiesSql(
                new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: false, includeM365Usage: true);

            StringAssert.Contains(sql, "ORDER BY RankScore DESC");
            Assert.IsFalse(sql.Contains("copilot.ActiveDays"),
                "Without the audit CTE its columns must not be referenced anywhere, including the ORDER BY.");
        }

        [TestMethod]
        public void CopilotDemandTarget_ScalesWithTheReportingWindow()
        {
            // The other three opportunity components are per-active-day averages, so they mean the same
            // thing at any window length. The Copilot component is a raw total, so without scaling the
            // same person clears the bar over 180 days and misses it over 7 - their recommendation
            // flipping because the reader changed the period drop-down, in a list used to decide who
            // gets a paid seat.
            var week = new CopilotAdoptionOptions { WindowDays = 7 };
            var month = new CopilotAdoptionOptions { WindowDays = 28 };
            var halfYear = new CopilotAdoptionOptions { WindowDays = 180 };

            Assert.AreEqual(5, CopilotAdoptionScoring.OpportunityCopilotTargetForWindow(week), 0.01,
                "A quarter of the 28-day basis is a quarter of the target.");
            Assert.AreEqual(20, CopilotAdoptionScoring.OpportunityCopilotTargetForWindow(month), 0.01,
                "The default window must leave the shipped target exactly as documented.");
            Assert.IsTrue(
                CopilotAdoptionScoring.OpportunityCopilotTargetForWindow(halfYear) > 100,
                "Six months of use has to clear a proportionally higher bar.");

            // The behaviour that matters: identical raw usage, scored under two windows.
            var signals = new Func<UnlicensedUserSignalRow>(() => new UnlicensedUserSignalRow
            {
                UserPrincipalName = "same@contoso.com",
                UnlicensedCopilotInteractions = 20,
                UnlicensedCopilotActiveDays = 1,
            });

            var overAWeek = CopilotAdoptionScoring.ScoreOpportunity(signals(), week);
            var overHalfAYear = CopilotAdoptionScoring.ScoreOpportunity(signals(), halfYear);

            Assert.AreEqual(100, overAWeek.CopilotDemandScore,
                "Twenty interactions in a week is heavy unlicensed use.");
            Assert.IsTrue(overHalfAYear.CopilotDemandScore < 20,
                $"The same twenty interactions spread over six months is not, but scored "
                + $"{overHalfAYear.CopilotDemandScore}.");
        }

        [TestMethod]
        public void CopilotDemandTarget_IsNeverFreeOnAShortWindow()
        {
            // A pathologically short window must not scale the target to zero and hand every user who
            // has ever touched Copilot a full-marks demand score.
            var tiny = new CopilotAdoptionOptions { WindowDays = 1, OpportunityCopilotTarget = 2 };

            Assert.IsTrue(CopilotAdoptionScoring.OpportunityCopilotTargetForWindow(tiny) >= 1,
                "The scaled target is floored at one interaction.");
        }

        [TestMethod]
        public void OpportunitySqlExpression_UsesTheWindowScaledCopilotTarget()
        {
            // The database ranks candidates, so if the SQL used the unscaled target while C# used the
            // scaled one, the query would return a different set of people from the one the displayed
            // scores describe - and the drill-through would disagree with its own headline.
            var halfYear = new CopilotAdoptionOptions { WindowDays = 180 };
            var expected = CopilotAdoptionScoring.OpportunityCopilotTargetForWindow(halfYear);

            var sql = CopilotAdoptionScoring.BuildOpportunityScoreSql(
                halfYear, "cop", "teams", "meetings", "sent", "read", "files");

            StringAssert.Contains(sql, expected.ToString(CultureInfo.InvariantCulture),
                "The ranking expression must use the same window-scaled target as the C# scorer.");
        }

        [TestMethod]
        public void OpportunitySqlExpression_UsesTheSameWeightsAsTheCsharpScorer()
        {
            // The database ranks candidates so a 200k-user tenant does not have to be pulled into
            // memory. The expression is generated from the same options object, so if the weights ever
            // drift apart the SQL would return the wrong people - even though the displayed score
            // (always recomputed in C#) would still look right.
            var options = CopilotAdoptionOptions.Default;

            var sql = CopilotAdoptionScoring.BuildOpportunityScoreSql(
                options, "cop", "teams", "meetings", "sent", "read", "files");

            foreach (var weight in new[]
            {
                options.OpportunityUnlicensedCopilotWeight,
                options.OpportunityCollaborationWeight,
                options.OpportunityEmailWeight,
                options.OpportunityDocumentWeight,
            })
            {
                StringAssert.Contains(sql, weight.ToString(CultureInfo.InvariantCulture),
                    "Every C# weight must appear in the generated ranking expression.");
            }

            foreach (var target in new[]
            {
                options.OpportunityCopilotTarget,
                options.OpportunityCollaborationTarget,
                options.OpportunityEmailTarget,
                options.OpportunityDocumentTarget,
            })
            {
                StringAssert.Contains(sql, target.ToString(CultureInfo.InvariantCulture));
            }

            StringAssert.Contains(sql, "CAST(cop AS float)", "Integer division would floor every ratio to 0 or 1.");
            StringAssert.Contains(sql, "(teams + meetings)");
            StringAssert.Contains(sql, "(sent + read)");
        }

        [TestMethod]
        public void OpportunitySqlExpression_IsCultureInvariant()
        {
            // A server running a European culture would otherwise emit "0,35" and produce SQL that is
            // either a syntax error or - far worse - silently parsed as two arguments.
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                var sql = CopilotAdoptionScoring.BuildOpportunityScoreSql(
                    new CopilotAdoptionOptions { OpportunityCopilotTarget = 2.5, OpportunityUnlicensedCopilotWeight = 12.5 },
                    "cop", "teams", "meetings", "sent", "read", "files");

                StringAssert.Contains(sql, "2.5");
                StringAssert.Contains(sql, "12.5");
                Assert.IsFalse(sql.Contains("2,5"), "A comma decimal separator would break the generated SQL.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        #endregion

        #region SQL construction

        [TestMethod]
        public void EmptyLicenceTypeList_ProducesAValidAlwaysFalsePredicate()
        {
            // A tenant that has bought no Copilot licences must get an empty report, not a 500 from
            // "IN ()".
            Assert.AreEqual("-1", CopilotAdoptionSql.IdList(new int[0]));
            Assert.AreEqual("-1", CopilotAdoptionSql.IdList(null));
            Assert.AreEqual("3, 7", CopilotAdoptionSql.IdList(new[] { 3, 7, 3 }));
        }

        [TestMethod]
        public void CoworkPredicate_MatchesTheAppHostAndAnyKnownCoworkAgent()
        {
            var withoutAgents = CopilotAdoptionSql.CoworkPredicate(new int[0]);
            Assert.AreEqual("c.app_host = 'cowork'", withoutAgents);
            Assert.IsFalse(withoutAgents.Contains("LOWER("),
                "Wrapping an indexed column in LOWER() makes the predicate non-SARGable for no benefit - the collation is already case-insensitive.");

            var withAgents = CopilotAdoptionSql.CoworkPredicate(new[] { 4, 9 });
            StringAssert.Contains(withAgents, "c.app_host = 'cowork'");
            StringAssert.Contains(withAgents, "c.agent_id IN (4, 9)");
        }

        [TestMethod]
        public void WeekBucket_UsesDayArithmeticNotDatediffWeek()
        {
            // DATEDIFF(WEEK, ...) splits weeks on Sunday, which would push every Sunday's rows into the
            // following week and quietly disagree with the Reports area's charts.
            var bucket = CopilotAdoptionSql.WeekBucket("au.time_stamp");

            StringAssert.Contains(bucket, "DATEDIFF(DAY, 0, au.time_stamp) % 7");
            Assert.IsFalse(bucket.ToUpperInvariant().Contains("DATEDIFF(WEEK"));
        }

        [TestMethod]
        public void LicensedUsersQuery_SplitsTheWindowFromTheHistoryInOnePass()
        {
            var sql = CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: true);

            // One bounded scan of the audit history: the "in the window" and "before the window"
            // aggregates are CASE expressions over the same pass, not two joins.
            Assert.AreEqual(1, CountOccurrences(sql, "dbo.copilot_chats"),
                "The Copilot audit history must be read exactly once - it is the most expensive read in the report.");

            // Performance contract, not a style rule: this query must NOT touch dbo.audit_events.
            // copilot_chats carries denormalised user_id / time_stamp precisely so it does not have to
            // (migration DenormaliseCopilotChatUserAndTime). Re-introducing the join took this query from
            // 22.5s to 91.0s at a 28-day window and from 41.4s to 117.8s at 90 days on the synthetic
            // 200k-user bench - past the 90s command timeout, which is the failure in issue #360.
            Assert.AreEqual(0, CountOccurrences(sql, "dbo.audit_events"),
                "Copilot queries must read the denormalised columns on copilot_chats, never join audit_events.");

            StringAssert.Contains(sql, "c.time_stamp >= @historyFrom");
            StringAssert.Contains(sql, "SUM(CASE WHEN c.time_stamp >= @from THEN 1 ELSE 0 END) AS Interactions");
            StringAssert.Contains(sql, "SUM(CASE WHEN c.time_stamp <  @from THEN 1 ELSE 0 END) AS PriorInteractions");

            // Deterministic truncation, so a capped report is reproducible rather than randomly
            // different between runs.
            StringAssert.Contains(sql, "TOP (@maxRows)");
            StringAssert.Contains(sql, "u.created_utc AS AccountCreatedUtc",
                "The user detail rows must carry the account-age tenure proxy used by the grace-period rule.");
            StringAssert.Contains(sql, "dbo.copilot_adoption_reclaim_exclusions",
                "Reclaim exclusions must be read with the same user-id key the detail list drills through on.");
            StringAssert.Contains(sql, "ORDER BY u.id");
            StringAssert.Contains(sql, "OPTION (RECOMPILE)");
        }

        [TestMethod]
        public void NoCopilotQueryJoinsAuditEvents()
        {
            // The whole point of migration DenormaliseCopilotChatUserAndTime: copilot_chats carries its own
            // user_id and time_stamp, so nothing here needs dbo.audit_events - the largest table in the
            // product, clustered on a random uniqueidentifier. Re-introducing that join anywhere brings back
            // the timeouts in issue #360, and it is an easy mistake to make when adding a query because the
            // old shape is all over the git history.
            var seats = new[] { 1 };
            var queries = new Dictionary<string, string>
            {
                { "HasCopilotAuditData", CopilotAdoptionSql.HasCopilotAuditDataSql },
                { "LicensedUsers", CopilotAdoptionSql.LicensedUsersSql(seats, new int[0], includeCopilotReport: true) },
                { "LicenceOpportunities", CopilotAdoptionSql.LicenceOpportunitiesSql(
                    seats, CopilotAdoptionOptions.Default, includeCopilotAudit: true, includeM365Usage: true) },
                { "UnlicensedActiveUsers", CopilotAdoptionSql.UnlicensedActiveUsersSql(seats) },
                { "AgentUsage", CopilotAdoptionSql.AgentUsageSql(seats) },
                { "AgentUsageByDepartment", CopilotAdoptionSql.AgentUsageByDepartmentSql() },
                { "UnlicensedUsageRows", CopilotAdoptionSql.UnlicensedUsageRowsSql(seats) },
                { "TopResourceTypes", CopilotAdoptionSql.TopResourceTypesSql() },
                { "UsageByApp", CopilotAdoptionSql.UsageByAppSql(seats) },
                { "UnlicensedUsageByApp", CopilotAdoptionSql.UnlicensedUsageByAppSql(seats) },
                { "WeeklyAdoptionTrend", CopilotAdoptionSql.WeeklyAdoptionTrendSql(seats, new int[0]) },
            };

            foreach (var query in queries)
            {
                Assert.AreEqual(0, CountOccurrences(query.Value, "dbo.audit_events"),
                    $"{query.Key} must read copilot_chats.user_id / .time_stamp, not join dbo.audit_events.");
                Assert.AreEqual(0, CountOccurrences(query.Value, "AS au"),
                    $"{query.Key} still aliases the audit-events table.");
            }
        }

        [TestMethod]
        public void LicensedUsersQuery_NeverAsksForSeveralDistinctCountsInOneGrouping()
        {
            // This is a performance contract, not a style rule.
            //
            // SQL Server streams a single distinct aggregate cheaply, but two or more in the same
            // GROUP BY force it to fan the input out through a spool and process each distinct
            // separately. Measured on a synthetic 200k-user / 12M-interaction tenant, writing the
            // three per-user distinct counts as COUNT(DISTINCT ...) in one grouping cost 115M logical
            // reads and 281s on the default 28-day window - past the 90s command timeout, so the
            // report silently degraded to a warning. Computing each from its own pre-projected
            // DISTINCT set is the same answer in 772k reads and 73s.
            //
            // Re-introducing COUNT(DISTINCT ...) here would be an easy, innocent-looking edit, and the
            // damage only shows up on a tenant large enough that nobody is testing against it.
            var sql = CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: true);

            Assert.AreEqual(0, CountOccurrences(sql, "COUNT(DISTINCT"),
                "Per-user distinct counts must come from their own DISTINCT sub-selects, not from "
                + "COUNT(DISTINCT ...) aggregates sharing one GROUP BY.");

            // The shape that replaces them.
            StringAssert.Contains(sql, "SELECT DISTINCT user_id, CAST(time_stamp AS date)");
            StringAssert.Contains(sql, "SELECT DISTINCT user_id, app_host");
            StringAssert.Contains(sql, "SELECT DISTINCT user_id, agent_id");
        }

        [TestMethod]
        public void LicensedUsersQuery_OmitsTheReportJoinWhenThereIsNoSnapshot()
        {
            var sql = CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: false);

            Assert.IsFalse(sql.Contains("copilot_usage_user_activity_log"),
                "With no settled snapshot the report table must not be joined at all.");
            StringAssert.Contains(sql, "CAST(NULL AS int) AS ReportPrompts",
                "The result shape must stay the same so the row still materialises.");
        }

        [TestMethod]
        public void OpportunitiesQuery_ExcludesGuestsInEveryDataSourceCombination()
        {
            // The exclusion lives in the shared WHERE clause, but the query is assembled differently
            // depending on which imports have data - so assert it survives every shape.
            var combinations = new[]
            {
                new { Audit = true, M365 = true },
                new { Audit = true, M365 = false },
                new { Audit = false, M365 = true },
            };

            foreach (var c in combinations)
            {
                var sql = CopilotAdoptionSql.LicenceOpportunitiesSql(
                    new[] { 1 }, CopilotAdoptionOptions.Default, c.Audit, c.M365);

                StringAssert.Contains(sql, "u.user_name NOT LIKE '%#EXT#@%'",
                    $"Guests must be excluded with includeCopilotAudit={c.Audit}, includeM365Usage={c.M365}.");
            }
        }

        [TestMethod]
        public void UnlicensedQueries_ExcludeGuestsConsistentlyWithTheCandidateList()
        {
            // These all describe the SAME population: users with Copilot activity and no seat. The
            // headline count is what the page calls proven unmet demand, and the candidate list is what
            // it tells you to act on. Excluding guests from one and not the others would let the page
            // report demand that can never appear in the list it points at.
            var seatIds = new[] { 1 };
            var queries = new Dictionary<string, string>
            {
                { "unlicensed active users (headline)", CopilotAdoptionSql.UnlicensedActiveUsersSql(seatIds) },
                { "unlicensed usage rows (detail)", CopilotAdoptionSql.UnlicensedUsageRowsSql(seatIds) },
                { "unlicensed usage by app", CopilotAdoptionSql.UnlicensedUsageByAppSql(seatIds) },
            };

            foreach (var q in queries)
            {
                StringAssert.Contains(q.Value, "guest_check.user_name LIKE '%#EXT#@%'",
                    $"The {q.Key} query must exclude guests, like the candidate list does.");
            }
        }

        [TestMethod]
        public void UnlicensedGuestExclusion_KeepsUsersMissingFromTheDirectory()
        {
            // Written as NOT EXISTS rather than a join on purpose: a user id the directory import has
            // not caught up with has no dbo.users row, and an inner join would silently drop it from the
            // figures - re-creating the very class of invisible loss this change exists to remove.
            var sql = CopilotAdoptionSql.UnlicensedActiveUsersSql(new[] { 1 });

            StringAssert.Contains(sql, "AND NOT EXISTS (");
            StringAssert.Contains(sql, "FROM dbo.users AS guest_check");
            Assert.IsFalse(sql.Contains("JOIN dbo.users AS guest_check"),
                "A join would drop users with no directory row instead of keeping them.");
        }

        [TestMethod]
        public void WeeklyTrend_ExcludesGuestsFromTheUnlicensedSeriesOnly()
        {
            var sql = CopilotAdoptionSql.WeeklyAdoptionTrendSql(new[] { 1 }, new int[0]);

            // Unlicensed series: guests removed, so the trend agrees with the headline count.
            StringAssert.Contains(sql, "SUM(CASE WHEN IsLicensed = 0 AND IsGuest = 0 THEN 1 ELSE 0 END) AS UnlicensedUsers");
            StringAssert.Contains(sql, "SUM(CASE WHEN IsGuest = 0 THEN UnlicensedInteractions ELSE 0 END)");

            // Licensed series: untouched. A guest that somehow holds a seat is a real licence being
            // spent, so hiding it would understate what the tenant is paying for.
            StringAssert.Contains(sql, "SUM(IsLicensed) AS ActiveUsers");
            StringAssert.Contains(sql, "SUM(LicensedInteractions) AS LicensedInteractions");
        }

        [TestMethod]
        public void AgentEstate_DoesNotExcludeGuests()
        {
            // The agent inventory is deliberately tenant-wide - an agent's value does not depend on the
            // licence or account type of the people using it. Filtering guests here would understate
            // real usage of an agent rather than correct a seat decision.
            var sql = CopilotAdoptionSql.AgentUsageSql(new[] { 1 });

            Assert.IsFalse(sql.Contains("#EXT#"),
                "The agent estate counts all users on purpose; see the query's own remarks.");
        }

        #region Incomplete figures (issue #360)

        [TestMethod]
        public void Summary_IsNotIncompleteByDefault()
        {
            var summary = new CopilotAdoptionSummary();

            Assert.IsFalse(summary.FiguresIncomplete);
            Assert.AreEqual(0, summary.IncompleteReasons.Count);
        }

        [TestMethod]
        public void MarkFiguresIncomplete_RecordsTheDatasetThatFailed()
        {
            var summary = new CopilotAdoptionSummary();

            summary.MarkFiguresIncomplete("licensed user detail");

            Assert.IsTrue(summary.FiguresIncomplete);
            CollectionAssert.AreEqual(new[] { "licensed user detail" }, summary.IncompleteReasons.ToArray());
        }

        [TestMethod]
        public void MarkFiguresIncomplete_DoesNotRepeatTheSameDataset()
        {
            // Both licensed-user queries can fail in the same run; the message must not say it twice.
            var summary = new CopilotAdoptionSummary();

            summary.MarkFiguresIncomplete("licensed user detail");
            summary.MarkFiguresIncomplete("licensed user detail");
            summary.MarkFiguresIncomplete("Copilot licence assignments");

            Assert.AreEqual(2, summary.IncompleteReasons.Count);
        }

        [TestMethod]
        public void MarkFiguresIncomplete_IgnoresABlankDatasetButStillFlags()
        {
            var summary = new CopilotAdoptionSummary();

            summary.MarkFiguresIncomplete(null);
            summary.MarkFiguresIncomplete("   ");

            Assert.IsTrue(summary.FiguresIncomplete,
                "The flag is what suppresses the figures; it must be set even without a description.");
            Assert.AreEqual(0, summary.IncompleteReasons.Count);
        }

        [TestMethod]
        public void IncompleteIsDistinctFromAWarning()
        {
            // A warning means "one chart is missing". Incomplete means "the population these numbers are
            // derived from could not be loaded". Conflating them is what let a timed-out licence query
            // render as a normal dashboard reporting almost no adoption.
            var summary = new CopilotAdoptionSummary();
            summary.Warnings.Add("Could not load Copilot agent usage: timeout");

            Assert.IsFalse(summary.FiguresIncomplete,
                "A degraded chart must not suppress the headline figures.");
        }

        [TestMethod]
        public async Task AnalyseAsync_WhenEveryQueryFails_FlagsIncompleteRatherThanReportingZeroAdoption()
        {
            // The exact customer symptom behind issue #360: the queries fail (there, by timing out), every
            // count degrades to zero, and the page then renders as though the tenant had never used
            // Copilot. A failing context factory reproduces the failure path without needing a database -
            // what matters is that a failure is DISTINGUISHABLE from a genuine zero.
            var service = new CopilotAdoptionService(
                CopilotAdoptionOptions.Default,
                new ThrowingAnalyticsDbContextFactory("simulated database failure"));

            var analysis = await service.AnalyseAsync();
            var summary = analysis.Summary;

            Assert.IsTrue(summary.FiguresIncomplete,
                "Every query failed, so the headline figures are not trustworthy and must say so.");
            Assert.IsTrue(summary.Warnings.Count > 0, "Each failed dataset should also warn.");
            Assert.AreEqual(0, summary.LicensedUsers,
                "The count still reads zero - which is exactly why the flag has to exist.");
        }

        [TestMethod]
        public async Task AnalyseAsync_WhenCancelled_FaultsInsteadOfReturningAnEmptyAnalysis()
        {
            // The analysis is shared between callers through a cached Task. If a cancellation were
            // degraded into warnings, the aborted run would complete "successfully" with empty figures and
            // be served to everyone else until the cache expired.
            var service = new CopilotAdoptionService(
                CopilotAdoptionOptions.Default,
                new ThrowingAnalyticsDbContextFactory("should not be reached"));

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                try
                {
                    await service.AnalyseAsync(null, cts.Token);
                    Assert.Fail("A cancelled analysis must not return a result.");
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }
        }

        #endregion

        [TestMethod]
        public void OpportunitiesQuery_IsDrivenFromActivityNotFromTheDirectory()
        {
            var sql = CopilotAdoptionSql.LicenceOpportunitiesSql(
                new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: true, includeM365Usage: true);

            // Driving from dbo.users would scan every account in a 200k-user tenant to discard the
            // overwhelming majority that have no activity at all.
            StringAssert.Contains(sql, "FROM Candidates AS cand");
            StringAssert.Contains(sql, "JOIN dbo.users AS u ON u.id = cand.user_id");
            StringAssert.Contains(sql, "NOT EXISTS (SELECT 1 FROM SeatUsers");

            // Disabled accounts cannot use a licence, so proposing one would discredit the whole list.
            StringAssert.Contains(sql, "u.account_enabled IS NULL OR u.account_enabled = 1");

            // Neither can an external guest, and there is no userType column to tell them apart - the
            // '#EXT#' UPN marker Entra writes is the only signal available. On a real tenant guests were a
            // meaningful share of the directory, every one of them ranked as an unactionable candidate.
            StringAssert.Contains(sql, "u.user_name NOT LIKE '%#EXT#@%'");

            // The Microsoft 365 tables are Graph's DAILY user-detail reports: one row per user per day
            // they did something. Seeking a single [date] made the candidate list "whoever happened to
            // be active last Tuesday" and emptied the tab whenever that day was a weekend. The window
            // has to be read whole, and each table read exactly once.
            Assert.AreEqual(1, CountOccurrences(sql, "FROM dbo.teams_user_activity_log AS t"));
            StringAssert.Contains(sql, "t.[date] >= @m365From AND t.[date] <= @m365ReportDate");
            StringAssert.Contains(sql, "o.[date] >= @m365From AND o.[date] <= @m365ReportDate");
            StringAssert.Contains(sql, "sp.[date] >= @m365From AND sp.[date] <= @m365ReportDate");
            StringAssert.Contains(sql, "od.[date] >= @m365From AND od.[date] <= @m365ReportDate");
            Assert.AreEqual(0, CountOccurrences(sql, "[date] = @m365ReportDate"),
                "A single-date equality seek is exactly the bug this query shape replaced.");

            // Averaged per active day, not summed, so the OpportunityXTarget values keep their units.
            StringAssert.Contains(sql, "NULLIF(COUNT(DISTINCT CAST(t.[date] AS date)), 0)");
        }

        [TestMethod]
        public void OpportunitiesQuery_RefusesToBuildWithNoDataSource()
        {
            // An empty candidate CTE would be a syntax error; failing loudly here is better than
            // producing SQL that cannot run.
            Assert.ThrowsException<ArgumentException>(() =>
                CopilotAdoptionSql.LicenceOpportunitiesSql(
                    new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: false, includeM365Usage: false));
        }

        [TestMethod]
        public void DisplaySql_DeclaresItsParametersSoItCanBePastedIntoSsms()
        {
            var display = CopilotAdoptionSql.ForDisplay(
                "SELECT 1 WHERE x >= @from;",
                new Dictionary<string, object>
                {
                    { "@from", new DateTime(2026, 8, 1) },
                    { "@maxRows", 500 },
                });

            StringAssert.Contains(display, "DECLARE @from datetime = '2026-08-01 00:00:00';");
            StringAssert.Contains(display, "DECLARE @maxRows int = 500;");
            StringAssert.Contains(display, "SELECT 1 WHERE x >= @from;");
        }

        #endregion

        #region CSV export

        [TestMethod]
        public void Csv_QuotesAndEscapesPerRfc4180()
        {
            Assert.AreEqual("plain", CsvSerialiser.Escape("plain"));
            Assert.AreEqual("\"has, comma\"", CsvSerialiser.Escape("has, comma"));
            Assert.AreEqual("\"has \"\"quotes\"\"\"", CsvSerialiser.Escape("has \"quotes\""));
            Assert.AreEqual("\"line\r\nbreak\"", CsvSerialiser.Escape("line\r\nbreak"));
            Assert.AreEqual("\" leading space\"", CsvSerialiser.Escape(" leading space"));
        }

        [TestMethod]
        public void Csv_NeutralisesSpreadsheetFormulaInjection()
        {
            // Department and job title come from the customer's own directory and are not trusted
            // input. A cell starting "=" is executed as a formula by Excel and Google Sheets.
            foreach (var dangerous in new[] { "=cmd|'/c calc'!A1", "+1+1", "-1+1", "@SUM(A1)" })
            {
                var escaped = CsvSerialiser.Escape(dangerous);
                Assert.IsTrue(
                    escaped.StartsWith("'") || escaped.StartsWith("\"'"),
                    $"'{dangerous}' must be prefixed so a spreadsheet treats it as text, but was '{escaped}'.");
            }
        }

        [TestMethod]
        public void Csv_IsUtf8WithABomSoExcelRendersNonLatinNames()
        {
            // Real tenants have departments in Greek, Cyrillic, Japanese and so on. Without a BOM, Excel
            // on Windows assumes the local ANSI code page and renders them as mojibake - in a document
            // that is about to be sent to an executive. The UPN is ASCII because Entra does not permit
            // anything else (#402/#414); Department carries the Unicode here.
            var rows = new[]
            {
                new LicensedUserAdoptionRow
                {
                    UserPrincipalName = "kalimera@contoso.com",
                    Department = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5",
                    BandName = "Champion",
                },
            };

            var bytes = CsvSerialiser.ToBytes(rows, CopilotAdoptionExports.LicensedUserColumns());

            CollectionAssert.AreEqual(
                new byte[] { 0xEF, 0xBB, 0xBF },
                bytes.Take(3).ToArray(),
                "The file must start with a UTF-8 BOM.");

            var text = new UTF8Encoding(false).GetString(bytes.Skip(3).ToArray());
            StringAssert.Contains(text, "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5",
                "Greek text must survive the round trip unchanged.");
        }

        [TestMethod]
        public void Csv_WritesNumbersAndDatesInvariantly()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                var csv = CsvSerialiser.ToCsv(
                    new[] { new LicensedUserAdoptionRow { AdoptionScore = 12.5, UsedCowork = true, LastInteractionUtc = new DateTime(2026, 8, 20, 9, 30, 0) } },
                    CopilotAdoptionExports.LicensedUserColumns());

                StringAssert.Contains(csv, "12.5", "A comma decimal separator would be read as a column break.");
                StringAssert.Contains(csv, "2026-08-20 09:30:00");
                StringAssert.Contains(csv, "Yes", "Booleans are written for people to read, not as True/False.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void Csv_HeaderMatchesTheColumnCountOnEveryRow()
        {
            var rows = new[]
            {
                ScoredUser("a@contoso.com", 10, AdoptionBand.Trialling),
                ScoredUser("b@contoso.com", 80, AdoptionBand.Champion),
            };

            var columns = CopilotAdoptionExports.LicensedUserColumns();
            var lines = CsvSerialiser.ToCsv(rows, columns)
                .Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            Assert.AreEqual(3, lines.Length, "One header plus one line per row.");
            foreach (var line in lines)
            {
                Assert.AreEqual(columns.Count - 1, line.Count(c => c == ','),
                    $"Every line must have the same number of fields: '{line}'");
            }
        }

        [TestMethod]
        public void ExportFileName_IsSafeAndDated()
        {
            Assert.AreEqual(
                "copilot-licensed-users-2026-08-20.csv",
                CsvSerialiser.FileName("copilot-licensed-users", new DateTime(2026, 8, 20)));

            Assert.AreEqual(
                "a-b-c-2026-08-20.csv",
                CsvSerialiser.FileName("a/b\\c", new DateTime(2026, 8, 20)),
                "Path separators must never reach a Content-Disposition file name.");
        }

        #endregion

        #region Filtering, sorting and paging

        [TestMethod]
        public void BandFilter_SelectsOnlyTheRequestedBands()
        {
            var rows = new[]
            {
                ScoredUser("never@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("dormant@contoso.com", 0, AdoptionBand.Dormant),
                ScoredUser("champ@contoso.com", 90, AdoptionBand.Champion),
            };

            var reclaimable = CopilotAdoptionExports.Apply(rows, new LicensedUserQuery
            {
                Bands = new List<AdoptionBand> { AdoptionBand.NeverUsed, AdoptionBand.Dormant },
            });

            Assert.AreEqual(2, reclaimable.Count);
            CollectionAssert.AreEquivalent(
                new[] { "never@contoso.com", "dormant@contoso.com" },
                reclaimable.Select(r => r.UserPrincipalName).ToArray());
        }

        [TestMethod]
        public void DisabledAccountFilter_TreatsUnknownAsNotDisabled()
        {
            // AccountEnabled is nullable: null means "we have not imported that flag", which is not the
            // same as "disabled" and must not be swept into a list of seats to reclaim.
            var enabled = ScoredUser("on@contoso.com", 50, AdoptionBand.Established);
            enabled.AccountEnabled = true;
            var disabled = ScoredUser("off@contoso.com", 0, AdoptionBand.NeverUsed);
            disabled.AccountEnabled = false;
            var unknown = ScoredUser("unknown@contoso.com", 0, AdoptionBand.NeverUsed);
            unknown.AccountEnabled = null;

            var result = CopilotAdoptionExports.Apply(
                new[] { enabled, disabled, unknown },
                new LicensedUserQuery { DisabledAccountsOnly = true });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("off@contoso.com", result[0].UserPrincipalName);
        }

        [TestMethod]
        public void Sorting_IsStableSoPagingCannotDuplicateOrDropUsers()
        {
            // Without a deterministic tie-break, two users on the same score can swap places between
            // page 1 and page 2 and appear twice - or not at all - in an export.
            var rows = new[]
            {
                ScoredUser("charlie@contoso.com", 40, AdoptionBand.Developing),
                ScoredUser("alice@contoso.com", 40, AdoptionBand.Developing),
                ScoredUser("bob@contoso.com", 40, AdoptionBand.Developing),
            };

            var first = CopilotAdoptionExports.Apply(rows, new LicensedUserQuery());
            var second = CopilotAdoptionExports.Apply(rows.Reverse().ToArray(), new LicensedUserQuery());

            CollectionAssert.AreEqual(
                first.Select(r => r.UserPrincipalName).ToArray(),
                second.Select(r => r.UserPrincipalName).ToArray(),
                "The same set in a different input order must sort identically.");
            Assert.AreEqual("alice@contoso.com", first[0].UserPrincipalName);
        }

        [TestMethod]
        public void DefaultSort_PutsTheLeastEngagedFirst()
        {
            // The entire purpose of the list is finding people who are not getting value from a licence
            // somebody is paying for, so the default must not bury them on the last page.
            var rows = new[]
            {
                ScoredUser("champ@contoso.com", 95, AdoptionBand.Champion),
                ScoredUser("never@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("mid@contoso.com", 45, AdoptionBand.Developing),
            };

            var sorted = CopilotAdoptionExports.Apply(rows, new LicensedUserQuery());

            Assert.AreEqual("never@contoso.com", sorted[0].UserPrincipalName);
            Assert.AreEqual("champ@contoso.com", sorted[2].UserPrincipalName);
        }

        [TestMethod]
        public void Search_MatchesAcrossMetadataAndIgnoresCase()
        {
            var row = ScoredUser("someone@contoso.com", 20, AdoptionBand.Trialling);
            row.Department = "Finance";
            row.JobTitle = "Financial Controller";
            row.ManagerUserPrincipalName = "cfo@contoso.com";

            Assert.AreEqual(1, CopilotAdoptionExports.Apply(new[] { row }, new LicensedUserQuery { Search = "finance" }).Count);
            Assert.AreEqual(1, CopilotAdoptionExports.Apply(new[] { row }, new LicensedUserQuery { Search = "CFO@" }).Count);
            Assert.AreEqual(0, CopilotAdoptionExports.Apply(new[] { row }, new LicensedUserQuery { Search = "marketing" }).Count);
        }

        [TestMethod]
        public void Paging_ClampsAnOversizedPageRequest()
        {
            var rows = Enumerable.Range(0, 200).Select(i => ScoredUser($"u{i:D3}@contoso.com", i % 100, AdoptionBand.Developing)).ToList();

            Assert.AreEqual(50, CopilotAdoptionExports.Page(rows, 0, 50).Count);
            Assert.AreEqual(100, CopilotAdoptionExports.Page(rows, 0, 100000, maxTake: 100).Count,
                "A stray query string must not be able to ask for an unbounded page.");
            Assert.AreEqual(0, CopilotAdoptionExports.Page(rows, 500, 50).Count);
        }

        [TestMethod]
        public void OpportunityFilters_IsolateTheProvenDemandGroup()
        {
            var alreadyUsing = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "using@contoso.com",
                UnlicensedCopilotInteractions = 5,
                UnlicensedCopilotActiveDays = 3,
            });
            var neverTried = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "untried@contoso.com",
                TeamsMessages = 100,
            });

            var result = CopilotAdoptionExports.Apply(
                new[] { alreadyUsing, neverTried },
                new LicenceOpportunityQuery { ExistingCopilotUsersOnly = true });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("using@contoso.com", result[0].UserPrincipalName);
        }

        #endregion

        #region Summary assembly

        [TestMethod]
        public void Funnel_NarrowsMonotonically()
        {
            var analysis = SampleAnalysis();
            new CopilotAdoptionService().FinaliseSummary(analysis);

            var funnel = analysis.Summary.Funnel;
            Assert.AreEqual(5, funnel.Count);
            for (var i = 1; i < funnel.Count; i++)
            {
                Assert.IsTrue(
                    funnel[i].Value <= funnel[i - 1].Value,
                    $"'{funnel[i].Label}' ({funnel[i].Value}) cannot exceed '{funnel[i - 1].Label}' ({funnel[i - 1].Value}) - each stage is a subset of the last.");
            }
        }

        [TestMethod]
        public void HeadlineFigures_AreConsistentWithEachOther()
        {
            var analysis = SampleAnalysis();
            var summary = analysis.Summary;
            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(6, summary.LicensedUsers);
            Assert.AreEqual(3, summary.ActiveUsers, "Trialling, Established and Champion are active.");
            Assert.AreEqual(2, summary.NeverUsedUsers);
            Assert.AreEqual(1, summary.DormantUsers);
            Assert.AreEqual(2, summary.ReclaimableSeats, "Only certain/probable seats are reclaimable; dormant seats are review-only.");
            Assert.AreEqual(0, summary.ReclaimCertainSeats);
            Assert.AreEqual(2, summary.ReclaimProbableSeats);
            Assert.AreEqual(1, summary.ReclaimReviewSeats, "Dormant users need a human review, not automatic reclaim.");
            Assert.AreEqual(2, summary.HabitualUsers, "Established and Champion only.");
            Assert.AreEqual(50, summary.AdoptionRatePct);
            Assert.AreEqual(
                summary.LicensedUsers,
                summary.ActiveUsers + summary.NeverUsedUsers + summary.DormantUsers,
                "Every licensed user must land in exactly one of active / dormant / never used.");
        }

        [TestMethod]
        public void Summary_SplitsReclaimByConfidenceAndShowsExclusions()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(new[]
            {
                ScoredUser("disabled@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("probable@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("dormant@contoso.com", 0, AdoptionBand.Dormant),
                ScoredUser("excluded@contoso.com", 0, AdoptionBand.NeverUsed),
            });
            analysis.LicensedUsers[0].AccountEnabled = false;
            analysis.LicensedUsers[3].ReclaimExclusionReason = "service account";
            analysis.LicensedUsers[3].ReclaimExcludedBy = "admin@contoso.com";
            foreach (var user in analysis.LicensedUsers)
            {
                CopilotAdoptionScoring.ApplyReclaimEligibility(user);
                user.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(user);
            }

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(1, analysis.Summary.DisabledLicensedUsers);
            Assert.AreEqual(1, analysis.Summary.ReclaimCertainSeats);
            Assert.AreEqual(1, analysis.Summary.ReclaimProbableSeats);
            Assert.AreEqual(1, analysis.Summary.ReclaimReviewSeats);
            Assert.AreEqual(1, analysis.Summary.ReclaimExcludedUsers);
            Assert.AreEqual(2, analysis.Summary.ReclaimableSeats,
                "Excluded and review-only rows remain licensed seats but must not inflate the actionable reclaim KPI.");
        }

        [TestMethod]
        public void BandBreakdown_AlwaysShowsEveryBand()
        {
            // An empty "Champions" bar is itself the finding, so the chart must not silently drop it.
            var analysis = SampleAnalysis();
            new CopilotAdoptionService().FinaliseSummary(analysis);

            CollectionAssert.AreEqual(
                CopilotAdoptionScoring.AllBands.Select(CopilotAdoptionScoring.BandDisplayName).ToArray(),
                analysis.Summary.BandBreakdown.Select(b => b.Label).ToArray());
        }

        [TestMethod]
        public void CoworkIsNotReportedAsZeroPercentWhenItWasNeverSeen()
        {
            // On a tenant that has not been enabled for Cowork, "0% Cowork adoption" reads as a failure
            // rather than as "not available here" - and would be quoted as one.
            var analysis = SampleAnalysis();
            new CopilotAdoptionService().FinaliseSummary(analysis);
            Assert.IsFalse(analysis.Summary.CoworkDetected);

            analysis.LicensedUsers[0].UsedCowork = true;
            analysis.LicensedUsers[0].CoworkInteractions = 12;
            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.CoworkDetected);
            Assert.AreEqual(1, analysis.Summary.CoworkUsers);
            Assert.AreEqual(12, analysis.Summary.CoworkInteractions);
        }

        [TestMethod]
        public void SmallDepartmentsAreExcludedFromTheBreakdown()
        {
            // "0% adopted" across two seats at the top of an executive chart is noise that invites the
            // wrong decision.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(
                Enumerable.Range(0, 6).Select(i => Departmental($"big{i}@contoso.com", "Engineering", i * 20)));
            analysis.LicensedUsers.AddRange(
                Enumerable.Range(0, 2).Select(i => Departmental($"small{i}@contoso.com", "Legal", 0)));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var departments = analysis.Summary.AdoptionByDepartment.Select(d => d.Segment).ToList();
            CollectionAssert.Contains(departments, "Engineering");
            CollectionAssert.DoesNotContain(departments, "Legal");
        }

        [TestMethod]
        public void DepartmentBreakdown_IsOrderedWorstFirst()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(
                Enumerable.Range(0, 6).Select(i => Departmental($"good{i}@contoso.com", "Sales", 90)));
            analysis.LicensedUsers.AddRange(
                Enumerable.Range(0, 6).Select(i => Departmental($"bad{i}@contoso.com", "Operations", 0)));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual("Operations", analysis.Summary.AdoptionByDepartment.First().Segment,
                "The department needing the most help must be first - it is the running order for an enablement plan.");
        }

        [TestMethod]
        public void MedianIsReportedAlongsideTheMean()
        {
            // A handful of Champions pulls the mean up and makes adoption look healthier than it is.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(new[]
            {
                ScoredUser("a@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("b@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("c@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("d@contoso.com", 100, AdoptionBand.Champion),
                ScoredUser("e@contoso.com", 100, AdoptionBand.Champion),
            });

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(40, analysis.Summary.AverageAdoptionScore);
            Assert.AreEqual(0, analysis.Summary.MedianAdoptionScore,
                "The median exposes what the mean hides: most of this population is not using Copilot at all.");
        }

        [TestMethod]
        public void Median_HandlesEvenAndEmptyPopulations()
        {
            Assert.AreEqual(0, CopilotAdoptionScoring.Median(new double[0]));
            Assert.AreEqual(30, CopilotAdoptionScoring.Median(new double[] { 10, 50 }));
            Assert.AreEqual(50, CopilotAdoptionScoring.Median(new double[] { 10, 50, 90 }));
        }

        [TestMethod]
        public void PercentagesNeverDivideByZero()
        {
            var analysis = new CopilotAdoptionAnalysis();
            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(0, analysis.Summary.AdoptionRatePct);
            Assert.AreEqual(0, analysis.Summary.HabitRatePct);
            Assert.AreEqual(0, analysis.Summary.CoworkAdoptionPct);
            Assert.AreEqual(0, CopilotAdoptionScoring.Percentage(5, 0));
        }

        [TestMethod]
        public void RatesDivideByTheUsersActuallyAnalysed_NotTheSeatCount()
        {
            // The failure this guards against is arithmetic, not cosmetic. If the detail query hits its
            // row cap and the rates still divide by the seat count, a 200,000-seat tenant scored 50,000
            // deep can never report adoption above 25% however healthy it really is - and the funnel
            // opens with a 75% drop that is purely an artefact of how many rows were read.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(
                Enumerable.Range(0, 10).Select(i => ScoredUser($"u{i}@contoso.com", 80, AdoptionBand.Champion)));

            // Ten users analysed, but the tenant holds forty seats.
            analysis.Summary.LicensedUsers = 40;

            new CopilotAdoptionService().FinaliseSummary(analysis);
            var summary = analysis.Summary;

            Assert.AreEqual(40, summary.LicensedUsers, "The true seat count must be preserved.");
            Assert.AreEqual(10, summary.ScoredUsers);
            Assert.AreEqual(100, summary.AdoptionRatePct,
                "All ten analysed users are active, so adoption is 100% of what was measured - not 25%.");
            Assert.AreEqual(100, summary.HabitRatePct);

            Assert.AreEqual(10, summary.Funnel.First().Value,
                "The funnel must open at the analysed population, or stage one to stage two shows a fake collapse.");

            Assert.IsTrue(
                summary.Warnings.Any(w => w.Contains("40") && w.Contains("10")),
                "A capped analysis must say so, naming both the seat count and the analysed count.");
        }

        [TestMethod]
        public void CappedAnalysisWarning_NamesTheOldestRecordBias()
        {
            // The denominator warning was true but incomplete: ORDER BY u.id makes the capped set
            // reproducible, not representative. New joiners and recently-onboarded subsidiaries are the
            // first people excluded, and they are exactly the population most likely to be never-used.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(
                Enumerable.Range(0, 10).Select(i => ScoredUser($"u{i}@contoso.com", 80, AdoptionBand.Champion)));
            analysis.Summary.LicensedUsers = 40;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(
                analysis.Summary.Warnings.Any(w =>
                    w.Contains("oldest user records") && w.Contains("newest joiners")),
                "A capped analysis must say the subset is biased towards older directory records, not merely smaller.");
        }

        [TestMethod]
        public void WhenEveryLicensedUserIsAnalysed_TheDenominatorsAreIdentical()
        {
            // The normal case must be untouched by the fix above.
            var analysis = SampleAnalysis();
            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(analysis.Summary.LicensedUsers, analysis.Summary.ScoredUsers);
            Assert.AreEqual(50, analysis.Summary.AdoptionRatePct);
            Assert.IsFalse(
                analysis.Summary.Warnings.Any(w => w.Contains("could be analysed")),
                "An uncapped analysis must not carry a truncation warning.");
        }

        [TestMethod]
        public void AgentInteractionsPerUser_UsesTheSameWindowForBothSidesOfTheRatio()
        {
            // The inventory reads a long history so a dormant agent is still visible, but the agent-user
            // count is scoped to the reporting period. Summing the history-wide interaction total and
            // dividing it by that user count inflated a headline KPI by the ratio of the two windows -
            // roughly four times at the default settings.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.FromUtc = Now.AddDays(-28);

            var user = ScoredUser("power@contoso.com", 80, AdoptionBand.Champion);
            user.AgentsUsed = 1;
            analysis.LicensedUsers.Add(user);
            analysis.Summary.LicensedUsers = 1;

            var agent = Agent("Contoso Expenses Agent", users: 1, interactions: 1000, appsUsed: 1);
            agent.WindowInteractions = 40;
            analysis.Agents.Add(agent);

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(40, analysis.Summary.Agents.AgentInteractions,
                "The headline interaction count must be the in-period figure, not the whole history.");
            Assert.AreEqual(40, analysis.Summary.Agents.InteractionsPerAgentUser, 0.01,
                "Numerator and denominator must share the reporting window.");
        }

        [TestMethod]
        public void ConcentrationAndSegments_CountAUserWithPromptsButNoDayBreakdown()
        {
            // Microsoft's usage report can supply a prompt count with no per-day breakdown. Scoring
            // treats that as active; several downstream views tested active days alone and silently
            // dropped those users, so the headline and the breakdowns disagreed about the population.
            var analysis = new CopilotAdoptionAnalysis();
            var reportOnly = ScoredUser("reportonly@contoso.com", 40, AdoptionBand.Developing);
            reportOnly.Interactions = 30;
            reportOnly.ActiveDays = 0;
            reportOnly.Department = "Finance";

            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 5)
                .Select(i => Departmentalise(ActiveUser($"u{i}@contoso.com", 10, 50), "Finance")));
            analysis.LicensedUsers.Add(reportOnly);
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(6, analysis.Summary.Concentration.Sum(b => b.Users),
                "A user with prompts but no day breakdown is active, and must be ranked.");

            var finance = analysis.Summary.IntensityByDepartment.Single();
            Assert.AreEqual(6, finance.ActiveUsers,
                "The intensity view must count the same active population as the headline figures.");
        }

        [TestMethod]
        public void MixedSignalSources_DoNotAddMicrosoftPromptsToAuditInteractionTotals()
        {
            // A report-sourced row stores Microsoft's prompt count in Interactions because that is what
            // the per-user score used. Adding it to audit interaction counts publishes one number with
            // two units, and the same unit mix used to leak into concentration, intensity, score profiles
            // and the licensed/unlicensed segment comparison.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.LicensedUsers = 12;

            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i =>
                {
                    var row = Departmentalise(ActiveUser($"audit{i}@contoso.com", activeDays: 5, interactions: 10), "Finance");
                    row.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;
                    return row;
                }));

            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i =>
                {
                    var row = Departmentalise(ActiveUser($"report{i}@contoso.com", activeDays: 18, interactions: 1000), "Finance");
                    row.SignalSource = CopilotAdoptionScoring.SignalSourceUsageReport;
                    row.ReportPrompts = 1000;
                    row.ReportActiveDays = 18;
                    return row;
                }));

            analysis.UnlicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i => Unlicensed($"chat{i}@contoso.com", "Finance", activeDays: 10, interactions: 100)));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(6, analysis.Summary.UsageReportSourcedUsers);
            Assert.AreEqual(50, analysis.Summary.UsageReportSourcedUserPct);
            Assert.AreEqual(60, analysis.Summary.TotalInteractions,
                "Only audit interactions may appear in the licensed interaction total.");
            Assert.AreEqual(6, analysis.Summary.Concentration.Sum(b => b.Users),
                "Usage concentration is an audit-interaction distribution, not a prompt distribution.");
            Assert.AreEqual(6, analysis.Summary.ScoreProfiles.Single(p => p.Label == "Typical active user").Users,
                "The depth profile is interaction-derived, so report-prompt rows must not dilute it.");

            var financeIntensity = analysis.Summary.IntensityByDepartment.Single(r => r.Segment == "Finance");
            Assert.AreEqual(6, financeIntensity.ActiveUsers,
                "The intensity plot is interactions per audit active day; report-prompt rows must be disclosed, not mixed.");
            Assert.AreEqual(2, financeIntensity.ActionsPerActiveDay);

            var financeCombined = analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Finance");
            Assert.AreEqual(12, financeCombined.LicensedUsers);
            Assert.AreEqual(5, financeCombined.InteractionsPerLicensedUser,
                "The comparison keeps all seats in the denominator but counts only audit interactions in the numerator.");

            Assert.IsTrue(
                analysis.Summary.Warnings.Any(w => w.Contains("Microsoft prompt counts are not added")),
                "The summary must disclose that a visible share of rows came from a different source.");
        }

        [TestMethod]
        public void ReportPeriodMismatch_ExcludesReportSourcedRowsFromReclaimableSeats()
        {
            // We choose exclusion rather than linear normalisation for reclaim decisions. Normalising a
            // D7/D90/D180 Microsoft prompt window into the selected window would still be inferring an
            // absence of use for the exact users whose audit signal is already suspect; excluding them
            // is conservative and avoids taking a licence from someone the fallback says may be active.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.LicensedUsers = 2;
            analysis.Summary.DataSources.CopilotUsageReportPeriodDays = 90;

            var auditNever = ScoredUser("audit-never@contoso.com", 0, AdoptionBand.NeverUsed);
            auditNever.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;

            var reportNever = ScoredUser("report-never@contoso.com", 0, AdoptionBand.NeverUsed);
            reportNever.SignalSource = CopilotAdoptionScoring.SignalSourceUsageReport;

            analysis.LicensedUsers.Add(auditNever);
            analysis.LicensedUsers.Add(reportNever);

            new CopilotAdoptionService(new CopilotAdoptionOptions { WindowDays = 28 }).FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.UsageReportWindowMismatch);
            Assert.AreEqual(2, analysis.Summary.NeverUsedUsers,
                "The band breakdown still describes the scored rows and carries the source warning.");
            Assert.AreEqual(1, analysis.Summary.ReclaimableSeats,
                "A report-sourced row from a mismatched Microsoft period must not be counted as a licence to reclaim.");
            Assert.IsTrue(
                analysis.Summary.Warnings.Any(w => w.Contains("pinned Copilot usage-report period is D90")),
                "The conservative exclusion must be visible in the summary warnings.");

            // The figures must still tie out. Excluding rows from the reclaim headline while the band
            // breakdown keeps counting them leaves a gap the reader cannot account for, and a number
            // that does not reconcile loses the argument however defensible the reason behind it.
            Assert.AreEqual(1, analysis.Summary.ReclaimSeatsHeldBackForWindowMismatch,
                "The held-back seats must be published, not silently dropped.");
            AssertReclaimArithmeticTiesOut(analysis.Summary);
            Assert.IsTrue(
                analysis.Summary.Warnings.Any(w => w.Contains("held back for window mismatch")),
                "The warning must name the reconciling figure so the gap is explainable on screen.");
        }

        [TestMethod]
        public void ReclaimArithmeticTiesOut_WithConfidenceTiersAndAWindowMismatchAtTheSameTime()
        {
            // The two hold-back mechanisms were built on separate branches and had never met: confidence
            // tiering parks review/excluded seats, and a Microsoft report-period mismatch parks
            // report-sourced seats. They compose, and the composition is what a reader actually sees, so
            // this asserts the published figures still reconcile with BOTH active at once.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.LicensedUsers = 7;
            analysis.Summary.DataSources.CopilotUsageReportPeriodDays = 90;

            // Probable, audit-sourced: the only unambiguously reclaimable idle seat here.
            var auditProbable = ScoredUser("audit-probable@contoso.com", 0, AdoptionBand.NeverUsed);
            auditProbable.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;

            // Probable on the tier, but scored from Microsoft's report over a period that is not the
            // selected window - held back by the second mechanism.
            var reportProbable = ScoredUser("report-probable@contoso.com", 0, AdoptionBand.NeverUsed);
            reportProbable.SignalSource = CopilotAdoptionScoring.SignalSourceUsageReport;

            // Dormant: review-only, held back by the first mechanism.
            var dormant = ScoredUser("dormant@contoso.com", 0, AdoptionBand.Dormant);
            dormant.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;

            // Inside the grace period: review-only, held back by the first mechanism.
            var tooNew = ScoredUser("too-new@contoso.com", 0, AdoptionBand.NeverUsed);
            tooNew.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;
            tooNew.AccountCreatedUtc = Now.AddDays(-5);
            tooNew.TenureStartUtc = tooNew.AccountCreatedUtc;
            tooNew.DaysSinceTenureStart = 5;
            tooNew.TooNewToJudge = true;

            // Admin exclusion: held back by the first mechanism, still a licensed seat.
            var excluded = ScoredUser("excluded@contoso.com", 0, AdoptionBand.NeverUsed);
            excluded.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;
            excluded.ReclaimExclusionReason = "shared mailbox";
            excluded.ReclaimExcludedBy = "admin@contoso.com";

            // Disabled but was active right up to the day it was disabled. Certain reclaim, and the
            // reason the identity needs a term for reclaimable seats that are NOT idle.
            var disabledButActive = ScoredUser("disabled-active@contoso.com", 82, AdoptionBand.Champion);
            disabledButActive.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;
            disabledButActive.AccountEnabled = false;

            // A healthy user, to prove nothing here depends on the population being all-idle.
            var healthy = ScoredUser("healthy@contoso.com", 70, AdoptionBand.Established);
            healthy.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;

            analysis.LicensedUsers.AddRange(new[]
            {
                auditProbable, reportProbable, dormant, tooNew, excluded, disabledButActive, healthy,
            });

            foreach (var user in analysis.LicensedUsers)
            {
                CopilotAdoptionScoring.ApplyReclaimEligibility(user);
                user.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(user);
            }

            new CopilotAdoptionService(new CopilotAdoptionOptions { WindowDays = 28 }).FinaliseSummary(analysis);

            var summary = analysis.Summary;

            Assert.IsTrue(summary.UsageReportWindowMismatch, "Both mechanisms must be live for this test to mean anything.");
            Assert.AreEqual(4, summary.NeverUsedUsers);
            Assert.AreEqual(1, summary.DormantUsers);

            Assert.AreEqual(1, summary.ReclaimCertainSeats, "The disabled account, even though it was active.");
            Assert.AreEqual(2, summary.ReclaimProbableSeats, "The two established-tenure never-used accounts.");
            Assert.AreEqual(2, summary.ReclaimReviewSeats, "Dormant and too-new.");
            Assert.AreEqual(1, summary.ReclaimExcludedUsers);

            Assert.AreEqual(1, summary.ReclaimSeatsHeldBackForWindowMismatch,
                "Only the report-sourced probable seat is held back for the window mismatch.");
            Assert.AreEqual(3, summary.ReclaimSeatsHeldBackForReview,
                "Dormant, too-new and admin-excluded idle seats are held back by the tiering.");
            Assert.AreEqual(1, summary.ReclaimSeatsFromActiveBands,
                "The disabled-but-active seat is reclaimable without being never-used or dormant.");

            Assert.AreEqual(2, summary.ReclaimableSeats,
                "Certain + probable, minus the report-sourced row on a mismatched window.");

            AssertReclaimArithmeticTiesOut(summary);
        }

        /// <summary>
        /// The one identity the Copilot Adoption reclaim figures must always satisfy, whichever hold-back
        /// mechanisms happen to be active. A reclaim headline that cannot be reconciled against the band
        /// breakdown on screen loses a licence argument however defensible the reason behind it.
        /// </summary>
        private static void AssertReclaimArithmeticTiesOut(CopilotAdoptionSummary summary)
        {
            Assert.AreEqual(
                summary.NeverUsedUsers + summary.DormantUsers + summary.ReclaimSeatsFromActiveBands,
                summary.ReclaimableSeats
                    + summary.ReclaimSeatsHeldBackForWindowMismatch
                    + summary.ReclaimSeatsHeldBackForReview,
                "NeverUsed + Dormant + ReclaimSeatsFromActiveBands must equal "
                + "ReclaimableSeats + ReclaimSeatsHeldBackForWindowMismatch + ReclaimSeatsHeldBackForReview.");
        }

        [TestMethod]
        public void WithoutAWindowMismatch_NoSeatsAreHeldBack()
        {
            // The reconciling figure must be zero in the normal case, not merely ignored - otherwise a
            // non-zero value would be indistinguishable from an unset one.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.LicensedUsers = 2;
            analysis.Summary.DataSources.CopilotUsageReportPeriodDays = 28;

            var auditNever = ScoredUser("audit-never@contoso.com", 0, AdoptionBand.NeverUsed);
            auditNever.SignalSource = CopilotAdoptionScoring.SignalSourceAudit;
            var reportNever = ScoredUser("report-never@contoso.com", 0, AdoptionBand.NeverUsed);
            reportNever.SignalSource = CopilotAdoptionScoring.SignalSourceUsageReport;
            analysis.LicensedUsers.Add(auditNever);
            analysis.LicensedUsers.Add(reportNever);

            new CopilotAdoptionService(new CopilotAdoptionOptions { WindowDays = 28 }).FinaliseSummary(analysis);

            Assert.IsFalse(analysis.Summary.UsageReportWindowMismatch);
            Assert.AreEqual(2, analysis.Summary.ReclaimableSeats,
                "With matching windows a report-sourced idle seat is still an idle seat.");
            Assert.AreEqual(0, analysis.Summary.ReclaimSeatsHeldBackForWindowMismatch);
        }

        #endregion

        #region Explainability: the options must survive serialisation

        [TestMethod]
        public void EveryTuningValue_IsSerialisedInCamelCase()
        {
            // The UI states the formula behind every figure using the options the API returns. Without
            // an explicit camelCase JsonProperty the client reads `undefined` for every threshold and
            // the entire "How this is calculated" tab renders as "NaN% of the score" - which is exactly
            // the regression this test exists to stop happening again.
            var json = Newtonsoft.Json.Linq.JObject.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(CopilotAdoptionOptions.Default));

            var propertyNames = json.Properties().Select(p => p.Name).ToList();

            Assert.AreEqual(
                typeof(CopilotAdoptionOptions).GetProperties().Length - 1,
                propertyNames.Count,
                "Every tuning property except the static Default must be serialised.");

            foreach (var name in propertyNames)
            {
                Assert.IsTrue(
                    char.IsLower(name[0]),
                    $"'{name}' is serialised in PascalCase; the client reads camelCase and would show NaN.");
            }

            // Spot-check the values the methodology tab quotes directly.
            Assert.AreEqual(0.5, (double)json["frequencyWeight"]);
            Assert.AreEqual(75, (double)json["championScore"]);
            Assert.AreEqual(50, (double)json["opportunityRecommendScore"]);
            Assert.AreEqual(35, (double)json["opportunityUnlicensedCopilotWeight"]);
            Assert.AreEqual(28, (int)json["habitBucketNormalisationDays"]);
        }

        #endregion

        #region Habit buckets

        [TestMethod]
        public void HabitBuckets_MeanTheSameThingWhicheverPeriodIsSelected()
        {
            // 12 active days is a near-daily habit over 28 days and an occasional one over 90. Without
            // normalisation the same tile would silently change meaning when the reader changed the
            // period drop-down - the sort of thing nobody notices until a decision has been made on it.
            var over28 = CopilotAdoptionScoring.NormalisedActiveDaysPerMonth(12, 28);
            var over90 = CopilotAdoptionScoring.NormalisedActiveDaysPerMonth(12, 90);

            Assert.AreEqual(12, over28, 0.01);
            Assert.AreEqual(3.73, over90, 0.01);
            Assert.AreEqual("Frequent", CopilotAdoptionScoring.HabitBucketFor(over28));
            Assert.AreEqual("Infrequent", CopilotAdoptionScoring.HabitBucketFor(over90));
        }

        [TestMethod]
        public void AUserWithNoActivity_IsNotCalledInfrequent()
        {
            // They are a reclaimable seat, which is a much more expensive problem. Folding them into
            // "infrequent" would hide it inside a bucket that looks like light-but-real usage.
            Assert.IsNull(CopilotAdoptionScoring.HabitBucketFor(0));
            Assert.AreEqual("Infrequent", CopilotAdoptionScoring.HabitBucketFor(1));

            // One interaction in a 180-day window normalises to well under a day. It must still land in
            // a bucket rather than vanishing, because it is real activity.
            Assert.AreEqual(
                "Infrequent",
                CopilotAdoptionScoring.HabitBucketFor(CopilotAdoptionScoring.NormalisedActiveDaysPerMonth(1, 180)));
        }

        [TestMethod]
        public void BucketBoundaries_MatchTheirPrintedCaptions()
        {
            // The tiles are captioned "1-5 / 6-10 / 11-19 / 20+ active days a month". Because the
            // normalised figure is fractional, it is rounded to whole days first - otherwise a user on
            // 5.6 days would be filed under a caption that visibly excludes them.
            Assert.AreEqual("Infrequent", CopilotAdoptionScoring.HabitBucketFor(5.4));
            Assert.AreEqual("Moderate", CopilotAdoptionScoring.HabitBucketFor(5.6));
            Assert.AreEqual("Moderate", CopilotAdoptionScoring.HabitBucketFor(10.4));
            Assert.AreEqual("Frequent", CopilotAdoptionScoring.HabitBucketFor(10.6));
            Assert.AreEqual("Frequent", CopilotAdoptionScoring.HabitBucketFor(19.4));
            Assert.AreEqual("Daily", CopilotAdoptionScoring.HabitBucketFor(19.6));

            StringAssert.StartsWith(CopilotAdoptionScoring.HabitBucketRangeLabel("Infrequent"), "1-5");
            StringAssert.StartsWith(CopilotAdoptionScoring.HabitBucketRangeLabel("Moderate"), "6-10");
            StringAssert.StartsWith(CopilotAdoptionScoring.HabitBucketRangeLabel("Frequent"), "11-19");
            StringAssert.StartsWith(CopilotAdoptionScoring.HabitBucketRangeLabel("Daily"), "20+");
        }

        [TestMethod]
        public void HabitBucketShares_AreOfActiveUsersAndSumTo100()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(new[]
            {
                ActiveUser("daily@contoso.com", activeDays: 20, interactions: 200),
                ActiveUser("frequent@contoso.com", activeDays: 12, interactions: 60),
                ActiveUser("moderate@contoso.com", activeDays: 7, interactions: 21),
                ActiveUser("rare@contoso.com", activeDays: 2, interactions: 3),
                ScoredUser("never@contoso.com", 0, AdoptionBand.NeverUsed),
            });
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var buckets = analysis.Summary.HabitBuckets;
            CollectionAssert.AreEqual(
                CopilotAdoptionScoring.AllHabitBuckets.ToArray(),
                buckets.Select(b => b.Label).ToArray(),
                "Every bucket must appear, including the empty ones - an empty 'Daily' tile is the finding.");

            Assert.AreEqual(4, buckets.Sum(b => b.Users), "The never-used seat must not be bucketed.");
            Assert.AreEqual(100, buckets.Sum(b => b.SharePct), 0.11);
            Assert.AreEqual(1, buckets.Single(b => b.Label == "Daily").Users);
            Assert.AreEqual(25, buckets.Single(b => b.Label == "Daily").SharePct, 0.01);
        }

        #endregion

        #region Enablement plan

        [TestMethod]
        public void EveryLicensedUser_GetsExactlyOneAction()
        {
            // The plan is only usable as a plan if the counts add up to the whole population - an admin
            // reads it as "these are all the jobs, and this is how big each one is".
            var analysis = SampleAnalysis();
            foreach (var user in analysis.LicensedUsers)
            {
                user.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(user);
            }

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(
                analysis.Summary.LicensedUsers,
                analysis.Summary.ActionPlan.Sum(a => a.Users),
                "Every licensed user must be counted in exactly one action group.");
            Assert.IsTrue(analysis.Summary.ActionPlan.All(a => a.Users > 0), "Empty action groups are padding.");
            Assert.IsTrue(
                analysis.Summary.ActionPlan.All(a => !string.IsNullOrWhiteSpace(a.Description)),
                "Each action must explain itself once, since the per-row prose was removed.");
        }

        [TestMethod]
        public void TheActionTagAndTheExportedProse_NeverDisagree()
        {
            // The screen shows a two-word tag and the CSV carries the full sentence. If those two are
            // derived independently they will drift, and a department lead will act on prose that
            // contradicts the tag someone else filtered on.
            var cases = new[]
            {
                CopilotAdoptionScoring.Score(UsageRow(0, 0, 0, null), WindowStart, Now, auditAvailable: true),
                CopilotAdoptionScoring.Score(UsageRow(2, 1, 1, Now.AddDays(-1)), WindowStart, Now, auditAvailable: true),
                CopilotAdoptionScoring.Score(UsageRow(30, 6, 1, Now), WindowStart, Now, auditAvailable: true),
                CopilotAdoptionScoring.Score(UsageRow(100, 20, 1, Now), WindowStart, Now, auditAvailable: true),
                CopilotAdoptionScoring.Score(UsageRow(100, 20, 4, Now), WindowStart, Now, auditAvailable: true),
            };

            // The middle case must be the one that used to disagree: an Established user whose habit is
            // confined to a single surface is tagged "Broaden", so the sentence has to say Broaden too.
            Assert.IsTrue(
                cases.Any(c => c.RecommendedActionCode == CopilotAdoptionScoring.AdoptionActionCodes.Broaden),
                "This test is only meaningful if it covers the broaden case.");

            foreach (var scored in cases)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(scored.RecommendedActionCode), $"{scored.Band} has no action code.");
                Assert.AreEqual(
                    CopilotAdoptionScoring.ActionLabel(scored.RecommendedActionCode),
                    scored.RecommendedActionLabel);
                StringAssert.StartsWith(
                    scored.RecommendedAction,
                    FirstWord(scored.RecommendedActionLabel),
                    $"The '{scored.RecommendedActionLabel}' tag must match the sentence exported for band {scored.Band}.");
            }
        }

        [TestMethod]
        public void EveryActionLabel_IsDistinctAndNamesTheStepToTake()
        {
            // The action column exists so an admin can group by it and say "these 76 need X". That
            // only works if two different interventions do not read as the same instruction. The
            // original "Coach" / "Grow" pair failed this: both are vague encouragement verbs, and a
            // reader had to know the band thresholds to tell them apart.
            var labels = CopilotAdoptionScoring.AllActionCodes
                .Select(CopilotAdoptionScoring.ActionLabel)
                .ToList();

            CollectionAssert.AllItemsAreUnique(labels.ToList(), "Two actions share a label.");

            foreach (var code in CopilotAdoptionScoring.AllActionCodes)
            {
                var label = CopilotAdoptionScoring.ActionLabel(code);
                Assert.IsFalse(string.IsNullOrWhiteSpace(label), $"'{code}' has no label.");
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(CopilotAdoptionScoring.ActionDescription(code)),
                    $"'{code}' has a label but no explanation of why anyone qualifies for it.");

                // A label that is a bare single word is almost always a state ("Coach", "Sustain")
                // rather than an instruction. The one exception would have to be argued for.
                Assert.IsTrue(
                    label.Split(' ').Length > 1,
                    $"'{label}' is a single word - say what to do, not what the user is.");
            }

            // No label may be wholly contained in another: that is the shape of a near-synonym pair.
            foreach (var a in labels)
            {
                foreach (var b in labels.Where(x => x != a))
                {
                    Assert.IsFalse(
                        b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0,
                        $"'{a}' is contained in '{b}' - these will read as the same action.");
                }
            }
        }

        #endregion

        #region Frequency vs intensity

        [TestMethod]
        public void IntensityPlot_SeparatesFrequentButShallowFromDeepButOccasional()
        {
            // The whole point of the chart: these two departments have identical adoption rates and
            // identical average scores would still hide which intervention each of them needs.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i => Departmentalise(ActiveUser($"shallow{i}@contoso.com", activeDays: 20, interactions: 20), "Support")));
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i => Departmentalise(ActiveUser($"deep{i}@contoso.com", activeDays: 4, interactions: 80), "Legal")));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var support = analysis.Summary.IntensityByDepartment.Single(p => p.Segment == "Support");
            var legal = analysis.Summary.IntensityByDepartment.Single(p => p.Segment == "Legal");

            Assert.IsTrue(support.ActiveDaysPerUser > legal.ActiveDaysPerUser, "Support opens Copilot far more often.");
            Assert.IsTrue(legal.ActionsPerActiveDay > support.ActionsPerActiveDay, "Legal does far more each time.");
            Assert.AreEqual(1, support.ActionsPerActiveDay, 0.01);
            Assert.AreEqual(20, legal.ActionsPerActiveDay, 0.01);
        }

        [TestMethod]
        public void IntensityBubbleColour_AveragesTheSamePopulationAsItsAxes()
        {
            // The bubble is coloured by engagement, and both its axes describe active users only. If
            // the colour averaged the whole department it would contradict the chart's own caption and
            // double-count the unused seats that the reclaim figures already report.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 3)
                .Select(i => Departmentalise(ActiveUser($"active{i}@contoso.com", activeDays: 10, interactions: 50), "Finance")));
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 3)
                .Select(i => Departmentalise(ScoredUser($"idle{i}@contoso.com", 0, AdoptionBand.NeverUsed), "Finance")));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var finance = analysis.Summary.IntensityByDepartment.Single();
            Assert.AreEqual(50, finance.ActiveUserAverageScore, 0.01,
                "Averaged over the three active users, not diluted to 25 by the three unused seats.");

            // The department table deliberately keeps the whole-population average - the two answer
            // different questions and both are labelled as such.
            Assert.AreEqual(25, analysis.Summary.AdoptionByDepartment.Single().AverageAdoptionScore, 0.01);
        }

        [TestMethod]
        public void IntensityPlot_IgnoresUnusedSeatsSoDepartmentsAreNotDraggedToTheOrigin()
        {
            // Unused seats are already counted (and acted on) as reclaimable. Averaging them in here
            // would make every department look mediocre and hide the real spread between them.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 3)
                .Select(i => Departmentalise(ActiveUser($"active{i}@contoso.com", activeDays: 10, interactions: 50), "Finance")));
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 3)
                .Select(i => Departmentalise(ScoredUser($"idle{i}@contoso.com", 0, AdoptionBand.NeverUsed), "Finance")));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var finance = analysis.Summary.IntensityByDepartment.Single();
            Assert.AreEqual(6, finance.LicensedUsers, "The bubble is still sized by all the seats held.");
            Assert.AreEqual(3, finance.ActiveUsers);
            Assert.AreEqual(10, finance.ActiveDaysPerUser, 0.01, "Averaged over the active users only.");
            Assert.AreEqual(5, finance.ActionsPerActiveDay, 0.01);
        }

        #endregion

        #region Agent inventory

        [TestMethod]
        public void ANewAgentIsNotRetired_HoweverFewPeopleUseIt()
        {
            // The exemption that stops an agent programme being strangled in its first month. A
            // brand-new agent with one user has not failed, it has not started.
            Assert.AreEqual(
                AgentHealth.New,
                CopilotAdoptionScoring.AgentHealthFor(daysSinceFirstUse: 5, daysSinceLastUse: 1, users: 1));

            // The same agent a few months later, still with one user, is a genuine review candidate.
            Assert.AreEqual(
                AgentHealth.Review,
                CopilotAdoptionScoring.AgentHealthFor(daysSinceFirstUse: 120, daysSinceLastUse: 1, users: 1));
        }

        [TestMethod]
        public void AgentVerdicts_FollowInactivityThenAdoption()
        {
            var o = CopilotAdoptionOptions.Default;

            Assert.AreEqual(
                AgentHealth.Retire,
                CopilotAdoptionScoring.AgentHealthFor(200, o.AgentRetireInactiveDays, 50),
                "Long-dormant agents are retire candidates however popular they once were.");

            Assert.AreEqual(
                AgentHealth.Review,
                CopilotAdoptionScoring.AgentHealthFor(200, o.AgentReviewInactiveDays, 50),
                "Going quiet is a review, not yet a retirement.");

            Assert.AreEqual(
                AgentHealth.Keep,
                CopilotAdoptionScoring.AgentHealthFor(200, 1, o.AgentMinUsers),
                "Current and genuinely adopted.");

            Assert.AreEqual(
                AgentHealth.Review,
                CopilotAdoptionScoring.AgentHealthFor(200, 1, o.AgentMinUsers - 1),
                "Current but below the adoption floor - usually the author testing it.");
        }

        [TestMethod]
        public void AnAgentWithNoRecordedUse_IsRetiredRatherThanCrashing()
        {
            Assert.AreEqual(AgentHealth.Retire, CopilotAdoptionScoring.AgentHealthFor(null, null, 0));
            Assert.AreEqual(AgentHealth.New, CopilotAdoptionScoring.AgentHealthFor(3, null, 0));
        }

        [TestMethod]
        public void EveryAgentVerdict_ExplainsItself()
        {
            // Same rule as the rest of the tool: an assertion the reader cannot check is not evidence.
            var scored = CopilotAdoptionScoring.ScoreAgent(
                new AgentUsageQueryRow
                {
                    AgentId = 1,
                    Name = "Contoso Expenses Agent",
                    Interactions = 40,
                    Users = 8,
                    AppsUsed = 2,
                    FirstUsedUtc = Now.AddDays(-200),
                    LastUsedUtc = Now.AddDays(-1),
                },
                Now);

            Assert.AreEqual(AgentHealth.Keep, scored.Health);
            Assert.AreEqual("Keep", scored.HealthName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(scored.HealthReason));
            Assert.AreEqual(5, scored.InteractionsPerUser, 0.01);
            Assert.AreEqual(1, scored.DaysSinceLastUse);
        }

        [TestMethod]
        public void MostPopularAndMostVersatile_AnswerDifferentQuestions()
        {
            // An agent one person runs constantly is not the most widely useful one, and an agent used
            // everywhere by a few people is doing a broader job than a high-volume single-surface one.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.FromUtc = Now.AddDays(-28);
            analysis.Agents.AddRange(new[]
            {
                Agent("Heavy single-surface agent", users: 2, interactions: 5000, appsUsed: 1),
                Agent("Widely used agent", users: 40, interactions: 400, appsUsed: 2),
                Agent("Everywhere agent", users: 6, interactions: 120, appsUsed: 5),
            });

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual("Widely used agent", analysis.Summary.Agents.MostPopularAgent);
            Assert.AreEqual("Everywhere agent", analysis.Summary.Agents.MostVersatileAgent);
        }

        [TestMethod]
        public void AgentUsers_AreNotDoubleCountedAcrossAgents()
        {
            // Summing users across agents would count anyone using two agents twice, which is how an
            // agent programme ends up reporting more users than the tenant has people.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.FromUtc = Now.AddDays(-28);

            var multiAgentUser = ScoredUser("power@contoso.com", 80, AdoptionBand.Champion);
            multiAgentUser.AgentsUsed = 3;
            analysis.LicensedUsers.Add(multiAgentUser);
            analysis.LicensedUsers.Add(ScoredUser("none@contoso.com", 10, AdoptionBand.Trialling));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;

            analysis.Agents.AddRange(new[]
            {
                Agent("A", users: 1, interactions: 10, appsUsed: 1),
                Agent("B", users: 1, interactions: 10, appsUsed: 1),
                Agent("C", users: 1, interactions: 10, appsUsed: 1),
            });

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(1, analysis.Summary.Agents.AgentUsers,
                "One person using three agents is one agent user, not three.");
            Assert.AreEqual(1, analysis.Summary.Agents.LicensedAgentUsers);
        }

        #endregion

        #region Usage concentration

        [TestMethod]
        public void Concentration_ExposesUsageCarriedByAFewPeople()
        {
            // Two tenants with identical adoption rates and identical totals; only the distribution
            // differs. The adoption percentage cannot tell them apart, which is the whole point.
            var even = CopilotAdoptionScoring.Concentration(Enumerable.Repeat(10L, 100));
            var skewed = CopilotAdoptionScoring.Concentration(
                Enumerable.Repeat(91L, 10).Concat(Enumerable.Repeat(1L, 90)));

            var evenTop = even.Single(b => b.Label == "Top 10%");
            var skewedTop = skewed.Single(b => b.Label == "Top 10%");

            Assert.AreEqual(10, evenTop.SharePct, 0.5, "Evenly spread usage puts ~10% of activity in the top 10%.");
            Assert.IsTrue(skewedTop.SharePct > 85,
                $"A tenant carried by its top decile must show it - got {skewedTop.SharePct}%.");
        }

        [TestMethod]
        public void Concentration_AccountsForEveryActiveUserExactlyOnce()
        {
            // Rounding percentile boundaries is the obvious way to lose or duplicate a user.
            foreach (var count in new[] { 1, 2, 3, 7, 13, 99, 100, 101, 1000 })
            {
                var bands = CopilotAdoptionScoring.Concentration(
                    Enumerable.Range(1, count).Select(i => (long)i));

                Assert.AreEqual(count, bands.Sum(b => b.Users),
                    $"Every one of the {count} active users must land in exactly one cohort.");
                Assert.AreEqual(100, bands.Sum(b => b.SharePct), 0.2);
            }
        }

        [TestMethod]
        public void Concentration_IgnoresIdleSeats()
        {
            // Including them would put every idle seat in the bottom cohort at zero and give every
            // tenant on earth the same chart.
            var bands = CopilotAdoptionScoring.Concentration(new long[] { 100, 50, 0, 0, 0, 0 });
            Assert.AreEqual(2, bands.Sum(b => b.Users));
        }

        [TestMethod]
        public void Concentration_HandlesAnEmptyPopulation()
        {
            Assert.AreEqual(0, CopilotAdoptionScoring.Concentration(new long[0]).Count);
            Assert.AreEqual(0, CopilotAdoptionScoring.Concentration(null).Count);
        }

        #endregion

        #region Unlicensed population and the combined view

        [TestMethod]
        public void UnlicensedUsers_AreBucketedByTheSameHabitRulesAsLicensedOnes()
        {
            // The two strips are meant to be read against each other, which only works if they mean
            // the same thing.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.Add(ActiveUser("seat@contoso.com", activeDays: 20, interactions: 100));
            analysis.Summary.LicensedUsers = 1;
            analysis.UnlicensedUsers.AddRange(new[]
            {
                Unlicensed("chat1@contoso.com", department: "Legal", activeDays: 20, interactions: 200),
                Unlicensed("chat2@contoso.com", department: "Legal", activeDays: 2, interactions: 4),
            });

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var unlicensed = analysis.Summary.Unlicensed;
            Assert.AreEqual(2, unlicensed.ActiveUsers);
            Assert.AreEqual(204, unlicensed.Interactions);
            CollectionAssert.AreEqual(
                CopilotAdoptionScoring.AllHabitBuckets.ToArray(),
                unlicensed.HabitBuckets.Select(b => b.Label).ToArray());
            Assert.AreEqual(1, unlicensed.HabitBuckets.Single(b => b.Label == "Daily").Users);
            Assert.AreEqual(1, unlicensed.HabitBuckets.Single(b => b.Label == "Infrequent").Users);
        }

        [TestMethod]
        public void CombinedView_ShowsIdleSeatsNextToHeavyUnlicensedUse()
        {
            // The seat-allocation case: a department whose seats sit idle while its unlicensed people
            // use Copilot heavily. Neither single-population view can show this.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i => Departmentalise(ScoredUser($"idle{i}@contoso.com", 0, AdoptionBand.NeverUsed), "Legal")));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;
            analysis.UnlicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i => Unlicensed($"chat{i}@contoso.com", "Legal", activeDays: 15, interactions: 280)));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var legal = analysis.Summary.CombinedByDepartment.Single(r => r.Segment == "Legal");
            Assert.AreEqual(6, legal.LicensedUsers);
            Assert.AreEqual(0, legal.LicensedActiveUsers);
            Assert.AreEqual(0, legal.InteractionsPerLicensedUser);
            Assert.AreEqual(6, legal.UnlicensedActiveUsers);
            Assert.IsTrue(legal.InteractionsPerUnlicensedUser > 0,
                "The unlicensed side of a department with entirely idle seats is the finding.");
        }

        [TestMethod]
        public void CombinedView_DropsDepartmentsTooSmallToRead()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 6)
                .Select(i => Departmentalise(ActiveUser($"big{i}@contoso.com", 10, 50), "Engineering")));
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;
            analysis.UnlicensedUsers.Add(Unlicensed("lone@contoso.com", "Facilities", activeDays: 20, interactions: 500));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var segments = analysis.Summary.CombinedByDepartment.Select(r => r.Segment).ToList();
            CollectionAssert.Contains(segments, "Engineering");
            CollectionAssert.DoesNotContain(segments, "Facilities",
                "One unlicensed user is not a department-level finding.");
        }

        #endregion

        #region Controller parameter handling

        [TestMethod]
        public void RequestedWindow_IsSnappedToASupportedValue()
        {
            // A free-form window would let a hand-edited URL ask for an arbitrarily long scan of the
            // Copilot audit history.
            Assert.AreEqual(28, CopilotAdoptionAPIController.NormaliseWindowDays(28));
            Assert.AreEqual(28, CopilotAdoptionAPIController.NormaliseWindowDays(30));
            Assert.AreEqual(7, CopilotAdoptionAPIController.NormaliseWindowDays(1));
            Assert.AreEqual(180, CopilotAdoptionAPIController.NormaliseWindowDays(9999));
            Assert.AreEqual(7, CopilotAdoptionAPIController.NormaliseWindowDays(-5));
        }

        [TestMethod]
        public void LicenceIdParameter_DiscardsAnythingThatIsNotAnInteger()
        {
            // These ids are interpolated into SQL after being checked against the licence types that
            // actually exist. Discarding non-numeric input here is the first of the two gates.
            CollectionAssert.AreEqual(
                new[] { 1, 4 },
                CopilotAdoptionAPIController.ParseIds("1, 4, 1, abc, 1; DROP TABLE users").ToArray());

            Assert.AreEqual(0, CopilotAdoptionAPIController.ParseIds(null).Count);
            Assert.AreEqual(0, CopilotAdoptionAPIController.ParseIds("   ").Count);
        }

        [TestMethod]
        public void BandParameter_AcceptsNumbersAndNames()
        {
            CollectionAssert.AreEquivalent(
                new[] { AdoptionBand.NeverUsed, AdoptionBand.Dormant },
                CopilotAdoptionAPIController.ParseBands("0,1").ToArray());

            CollectionAssert.AreEquivalent(
                new[] { AdoptionBand.Champion },
                CopilotAdoptionAPIController.ParseBands("champion").ToArray());

            Assert.AreEqual(0, CopilotAdoptionAPIController.ParseBands("99,not-a-band").Count);
        }

        [TestMethod]
        public void ActionFilter_DrillsThroughToExactlyTheGroupThePlanCounted()
        {
            // The whole value of the drill-through is that the list it lands on has the same members
            // as the aggregate that was clicked. If it filtered by band instead, "Add a second app" -
            // which spans Developing and Established - would land on a different set of people than
            // the number the reader just clicked, which is worse than having no link at all.
            var rows = new[]
            {
                ActionRow("a@contoso.com", CopilotAdoptionScoring.AdoptionActionCodes.Broaden),
                ActionRow("b@contoso.com", CopilotAdoptionScoring.AdoptionActionCodes.Broaden),
                ActionRow("c@contoso.com", CopilotAdoptionScoring.AdoptionActionCodes.Reclaim),
            };

            var broaden = CopilotAdoptionExports.Apply(rows, new LicensedUserQuery
            {
                Actions = new List<string> { CopilotAdoptionScoring.AdoptionActionCodes.Broaden },
            });

            CollectionAssert.AreEquivalent(
                new[] { "a@contoso.com", "b@contoso.com" },
                broaden.Select(r => r.UserPrincipalName).ToArray());

            // No filter must mean no filtering, not "match nothing".
            Assert.AreEqual(3, CopilotAdoptionExports.Apply(rows, new LicensedUserQuery()).Count);

            // Codes arrive from a query string, so casing and whitespace are not guaranteed.
            CollectionAssert.AreEqual(
                new[] { CopilotAdoptionScoring.AdoptionActionCodes.Reclaim },
                CopilotAdoptionAPIController.ParseActions(" RECLAIM , reclaim ").ToArray());

            // An unknown code must not silently filter the list down to nothing - that reads as
            // "there is nobody in this group", which is the most misleading possible failure here.
            Assert.AreEqual(0, CopilotAdoptionAPIController.ParseActions("not-an-action").Count);
            Assert.AreEqual(
                3,
                CopilotAdoptionExports.Apply(rows, new LicensedUserQuery
                {
                    Actions = CopilotAdoptionAPIController.ParseActions("not-an-action"),
                }).Count);
        }

        [TestMethod]
        public async Task IndividualEndpoints_FailClosedWithoutTheConfiguredRole()
        {
            // Issue #538: the aggregate dashboard can remain [Authorize], but every route that exposes
            // the per-user population or exports it must refuse a normal signed-in user. This must be in
            // the controller rather than only in the SPA, because the CSV/XLSX downloads are plain
            // <a href> navigations and can be opened directly.
            using (var controller = GovernanceController(
                new CopilotAdoptionGovernanceSettings { IndividualDataRole = "CopilotAdoption.IndividualData" },
                SignedIn("reader@contoso.com")))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, await Status(controller.Filters()));
                Assert.AreEqual(HttpStatusCode.Forbidden, await Status(controller.LicensedUsers()));
                Assert.AreEqual(HttpStatusCode.Forbidden, await Status(controller.Opportunities()));
                Assert.AreEqual(HttpStatusCode.Forbidden, (await controller.ExportLicensedUsers()).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await controller.ExportOpportunities()).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await controller.ExportWorkbook()).StatusCode);
            }
        }

        [TestMethod]
        public void IndividualDataRole_MatchesAppRoleOrGroupClaimOnlyWhenConfigured()
        {
            var noRoleConfigured = new CopilotAdoptionGovernanceSettings();
            Assert.IsFalse(noRoleConfigured.HasIndividualDataAccess(SignedIn("admin@contoso.com", "CopilotAdoption.IndividualData")),
                "No setting means aggregate-only; an upgrade must not silently widen access.");

            var appRole = new CopilotAdoptionGovernanceSettings { IndividualDataRole = "CopilotAdoption.IndividualData" };
            Assert.IsTrue(appRole.HasIndividualDataAccess(SignedIn("admin@contoso.com", "CopilotAdoption.IndividualData")));
            Assert.IsFalse(appRole.HasIndividualDataAccess(SignedIn("admin@contoso.com")));

            var groupId = "00000000-0000-0000-0000-000000000538";
            var group = new CopilotAdoptionGovernanceSettings { IndividualDataRole = groupId };
            Assert.IsTrue(group.HasIndividualDataAccess(SignedInWithGroup("admin@contoso.com", groupId)));
        }

        [TestMethod]
        public void Pseudonymisation_RemovesDirectIdentifiersButKeepsCohortAndProvenance()
        {
            var row = ScoredUser("Καλημέρα.user@contoso.com", 20, AdoptionBand.Developing);
            row.Mail = "person@contoso.com";
            row.Department = "Finance";
            row.JobTitle = "Director";
            row.ManagerUserPrincipalName = "manager@contoso.com";
            row.SignalSource = CopilotAdoptionScoring.SignalSourceUsageReport;

            var redacted = CopilotAdoptionPseudonymiser.Pseudonymise(
                row,
                new CopilotAdoptionGovernanceSettings { TenantId = Guid.Parse("00000000-0000-0000-0000-000000000000") });

            StringAssert.StartsWith(redacted.UserPrincipalName, "User ");
            Assert.IsNull(redacted.Mail);
            Assert.IsNull(redacted.JobTitle);
            Assert.IsNull(redacted.ManagerUserPrincipalName);
            Assert.AreEqual("Finance", redacted.Department);
            Assert.AreEqual(AdoptionBand.Developing, redacted.Band);
            Assert.AreEqual(CopilotAdoptionScoring.SignalSourceUsageReport, redacted.SignalSource,
                "Redaction must not strip the provenance that tells a forwarded export how the row was scored.");
        }

        [TestMethod]
        public void Pseudonymisation_CarriesEveryNonIdentifyingColumnThrough()
        {
            // Pseudonymisation is ON by default, so a column it silently drops is blank for almost every
            // deployment with nothing failing to say so. The redaction used to rebuild the row from a
            // hand-written list of properties, which made that the DEFAULT outcome for any column added
            // afterwards - and merging the reclaim-eligibility and licence-qualification work into this
            // branch added fifteen of them at once.
            //
            // This walks every property on both row types, gives it a distinctive non-default value, and
            // asserts it either survives or is one of the identifiers redaction is meant to remove. A new
            // column therefore forces a deliberate decision instead of disappearing.
            AssertPseudonymisationIsExhaustive<LicensedUserAdoptionRow>(
                r => CopilotAdoptionPseudonymiser.Pseudonymise(r, new CopilotAdoptionGovernanceSettings()));
            AssertPseudonymisationIsExhaustive<LicenceOpportunityRow>(
                r => CopilotAdoptionPseudonymiser.Pseudonymise(r, new CopilotAdoptionGovernanceSettings()));
        }

        private static void AssertPseudonymisationIsExhaustive<T>(Func<T, T> pseudonymise) where T : new()
        {
            var source = new T();
            var properties = typeof(T)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .ToList();

            foreach (var property in properties)
            {
                property.SetValue(source, DistinctiveValue(property.PropertyType));
            }

            var redacted = pseudonymise(source);

            foreach (var property in properties)
            {
                var expected = property.GetValue(source);
                var actual = property.GetValue(redacted);

                if (CopilotAdoptionPseudonymiser.RedactedPropertyNames.Contains(property.Name))
                {
                    Assert.AreNotEqual(expected, actual,
                        $"{typeof(T).Name}.{property.Name} is listed as a direct identifier but survived redaction.");
                    continue;
                }

                Assert.AreEqual(expected, actual,
                    $"{typeof(T).Name}.{property.Name} was dropped by pseudonymisation. Either carry it through, "
                    + "or add it to CopilotAdoptionPseudonymiser.RedactedPropertyNames if it identifies someone.");
            }
        }

        /// <summary>A non-default value for any property type the adoption rows use.</summary>
        private static object DistinctiveValue(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string)) return "Καλημέρα κόσμε";
            if (underlying == typeof(bool)) return true;
            if (underlying == typeof(int)) return 7;
            if (underlying == typeof(long)) return 7L;
            if (underlying == typeof(double)) return 7.5d;
            if (underlying == typeof(DateTime)) return new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            if (underlying.IsEnum) return Enum.GetValues(underlying).Cast<object>().Last();

            Assert.Fail($"DistinctiveValue does not know how to populate a {underlying.Name}; extend it.");
            return null;
        }

        private static LicensedUserAdoptionRow ActionRow(string upn, string actionCode)
        {
            return new LicensedUserAdoptionRow
            {
                UserPrincipalName = upn,
                RecommendedActionCode = actionCode,
                RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(actionCode),
            };
        }

        private static CopilotAdoptionAPIController GovernanceController(
            CopilotAdoptionGovernanceSettings settings,
            IPrincipal principal)
        {
            var controller = new CopilotAdoptionAPIController(
                AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator.Default,
                () => settings,
                new RecordingAuditSink());
            controller.Request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/CopilotAdoption/test?windowDays=28");
            controller.Configuration = new HttpConfiguration();
            controller.RequestContext = new HttpRequestContext { Principal = principal };
            return controller;
        }

        private static async Task<HttpStatusCode> Status(Task<IHttpActionResult> action)
        {
            var result = await action;
            var response = await result.ExecuteAsync(CancellationToken.None);
            return response.StatusCode;
        }

        private static IPrincipal SignedIn(string upn, params string[] roles)
        {
            var identity = new ClaimsIdentity("test");
            identity.AddClaim(new Claim(ClaimTypes.Name, upn));
            identity.AddClaim(new Claim(ClaimTypes.Upn, upn));
            foreach (var role in roles)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
                identity.AddClaim(new Claim("roles", role));
            }

            return new ClaimsPrincipal(identity);
        }

        private static IPrincipal SignedInWithGroup(string upn, string groupId)
        {
            var identity = new ClaimsIdentity("test");
            identity.AddClaim(new Claim(ClaimTypes.Name, upn));
            identity.AddClaim(new Claim("groups", groupId));
            return new ClaimsPrincipal(identity);
        }

        private sealed class RecordingAuditSink : ICopilotAdoptionExportAuditSink
        {
            public bool Write(CopilotAdoptionExportAuditRecord record)
            {
                return true;
            }
        }

        #endregion

        #region Test data helpers

        private static LicensedUserUsageRow UsageRow(long interactions, int activeDays, int appsUsed, DateTime? lastUse)
        {
            return new LicensedUserUsageRow
            {
                UserId = 1,
                UserPrincipalName = "user@contoso.com",
                AccountEnabled = true,
                AccountCreatedUtc = Now.AddDays(-120),
                Interactions = interactions,
                ActiveDays = activeDays,
                AppsUsed = appsUsed,
                LastInteractionUtc = lastUse,
                FirstInteractionUtc = lastUse,
            };
        }

        private static LicensedUserAdoptionRow ScoredUser(string upn, double score, AdoptionBand band)
        {
            var row = new LicensedUserAdoptionRow
            {
                UserId = upn.GetHashCode(),
                UserPrincipalName = upn,
                AccountEnabled = true,
                AccountCreatedUtc = Now.AddDays(-120),
                TenureStartUtc = Now.AddDays(-120),
                TenureBasis = CopilotAdoptionScoring.TenureBasisAccountAge,
                DaysSinceTenureStart = 120,
                AdoptionScore = score,
                Band = band,
                BandName = CopilotAdoptionScoring.BandDisplayName(band),
            };
            CopilotAdoptionScoring.ApplyReclaimEligibility(row);
            row.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(row);
            row.RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(row.RecommendedActionCode);
            return row;
        }

        private static LicensedUserAdoptionRow Departmental(string upn, string department, double score)
        {
            var row = ScoredUser(upn, score, score >= 50 ? AdoptionBand.Established : score > 0 ? AdoptionBand.Trialling : AdoptionBand.NeverUsed);
            row.Department = department;
            return row;
        }

        /// <summary>A user with real activity, for the habit-bucket and intensity tests.</summary>
        private static LicensedUserAdoptionRow ActiveUser(string upn, int activeDays, long interactions)
        {
            var row = ScoredUser(upn, 50, AdoptionBand.Established);
            row.ActiveDays = activeDays;
            row.Interactions = interactions;
            row.AppsUsed = 2;
            return row;
        }

        private static LicensedUserAdoptionRow Departmentalise(LicensedUserAdoptionRow row, string department)
        {
            row.Department = department;
            return row;
        }

        /// <summary>An agent for the estate roll-up tests, active as of "now".</summary>
        private static AgentUsageRow Agent(string name, int users, long interactions, int appsUsed)
        {
            return new AgentUsageRow
            {
                AgentId = Math.Abs(name.GetHashCode()),
                Name = name,
                Users = users,
                LicensedUsers = users,
                Interactions = interactions,
                WindowInteractions = interactions,
                AppsUsed = appsUsed,
                FirstUsedUtc = Now.AddDays(-200),
                LastUsedUtc = Now.AddDays(-1),
                Health = AgentHealth.Keep,
                HealthName = "Keep",
            };
        }

        /// <summary>One unlicensed Copilot Chat user.</summary>
        private static UnlicensedUsageQueryRow Unlicensed(string upn, string department, int activeDays, long interactions)
        {
            return new UnlicensedUsageQueryRow
            {
                UserId = upn.GetHashCode(),
                Department = department,
                ActiveDays = activeDays,
                Interactions = interactions,
                AppsUsed = 1,
                LastInteractionUtc = Now.AddDays(-1),
            };
        }

        /// <summary>First word of an action label, which is how the exported sentence always opens.</summary>
        private static string FirstWord(string label)
        {
            return string.IsNullOrEmpty(label) ? label : label.Split(' ')[0];
        }

        /// <summary>Six licensed users spread across the bands, for the summary assembly tests.</summary>
        private static CopilotAdoptionAnalysis SampleAnalysis()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(new[]
            {
                ScoredUser("champ@contoso.com", 90, AdoptionBand.Champion),
                ScoredUser("established@contoso.com", 60, AdoptionBand.Established),
                ScoredUser("trialling@contoso.com", 10, AdoptionBand.Trialling),
                ScoredUser("dormant@contoso.com", 0, AdoptionBand.Dormant),
                ScoredUser("never1@contoso.com", 0, AdoptionBand.NeverUsed),
                ScoredUser("never2@contoso.com", 0, AdoptionBand.NeverUsed),
            });
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;
            return analysis;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        #endregion
    }
}
