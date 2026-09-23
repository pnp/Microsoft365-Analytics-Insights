extern alias AnalyticsWeb;

using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;
using CopilotAdoptionAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotAdoptionAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the Cowork readiness model.
    ///
    /// This feature tells an executive who to enable Microsoft 365 Copilot Cowork for, and Cowork is
    /// billed by consumption rather than by seat - so a wrong answer here spends real money on the wrong
    /// people, or withholds a capability from the people who would benefit. The rules are pure functions
    /// in <see cref="CopilotAdoptionScoring"/> precisely so they can be pinned down here.
    ///
    /// Particular attention is paid to the evidence/inference boundary. The single most damaging thing
    /// this tab could do is present a prediction as an observation.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionCoworkTests
    {
        private static CopilotAdoptionOptions Options()
        {
            return CopilotAdoptionOptions.Default;
        }

        /// <summary>
        /// A seat holder with no activity of any kind. Every test starts from this and sets only the
        /// signals it is actually about, so a failure names the rule that broke.
        /// </summary>
        private static CoworkReadinessSignalRow Idle()
        {
            return new CoworkReadinessSignalRow
            {
                UserId = 1,
                UserPrincipalName = "aisha.rahman@contoso.com",
                Department = "Operations",
            };
        }

        /// <summary>Somebody who carries a heavy coordination load: meetings, mail and documents all day.</summary>
        private static CoworkReadinessSignalRow HeavyCoordinator()
        {
            var row = Idle();
            row.TeamsMeetings = 6;
            row.TeamsMessages = 60;
            row.EmailsSent = 40;
            row.EmailsRead = 60;
            row.FilesViewedOrEdited = 35;
            return row;
        }

        #region Coordination load

        [TestMethod]
        public void CoordinationLoad_IsZero_WhenThereIsNoActivity()
        {
            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(Idle(), Options());

            Assert.AreEqual(0d, scored.CoordinationLoadScore,
                "A user with no recorded Microsoft 365 activity has nothing for Cowork to take on.");
        }

        [TestMethod]
        public void CoordinationLoad_ReachesFullMarks_WhenEveryComponentIsAtTarget()
        {
            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(HeavyCoordinator(), Options());

            Assert.AreEqual(100d, scored.CoordinationLoadScore, 0.01,
                "The four component weights must sum to 100 so the score reads as a percentage.");
        }

        [TestMethod]
        public void CoordinationLoadComponents_AreCapped_SoOneOutlierCannotCarryTheScore()
        {
            var row = Idle();
            // An automated mailbox: a huge amount of one signal, none of the others. Without per-component
            // capping this would swamp the weighted sum and present a robot as a prime Cowork candidate.
            row.EmailsRead = 100000;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(Options().CoworkEmailWeight, scored.CoordinationLoadScore, 0.01,
                "A single saturated component must contribute only its own weight, never more.");
            Assert.IsTrue(scored.CoordinationLoadScore < Options().CoworkLoadMinScore,
                "One signal alone must not clear the coordination-load bar.");
        }

        [TestMethod]
        public void Meetings_AreWeightedAboveRawMessageVolume()
        {
            var meetingHeavy = Idle();
            meetingHeavy.TeamsMeetings = 5;

            var chatHeavy = Idle();
            chatHeavy.TeamsMessages = 50;

            var meetingScore = CopilotAdoptionScoring.ScoreCoworkReadiness(meetingHeavy, Options()).CoordinationLoadScore;
            var chatScore = CopilotAdoptionScoring.ScoreCoworkReadiness(chatHeavy, Options()).CoordinationLoadScore;

            Assert.IsTrue(meetingScore > chatScore,
                "A meeting implies preparation, notes and follow-ups - a chain of delegable tasks - so it must "
                + "outweigh the same-proportion volume of chat messages.");
        }

        #endregion

        #region Fluency

        [TestMethod]
        public void AgentFamiliarity_LiftsFluency_ButOnlyByTheConfiguredAmount()
        {
            var row = Idle();
            row.AdoptionScore = 40;
            row.AgentsUsed = 2;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(40 + Options().CoworkAgentFamiliarityUplift, scored.FluencyScore, 0.01);
            Assert.AreEqual(40d, scored.AdoptionScore, 0.01,
                "The underlying engagement score must be preserved so the uplift is auditable.");
        }

        [TestMethod]
        public void AgentFamiliarity_CannotCarryAnInactiveUserOverTheFluencyBar()
        {
            var row = HeavyCoordinator();
            row.AdoptionScore = 0;
            row.AgentsUsed = 5;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.IsTrue(scored.FluencyScore < Options().CoworkFluencyMinScore,
                "The uplift must promote a borderline user, never manufacture fluency from nothing.");
            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.BuildFluencyFirst, scored.Tier);
        }

        [TestMethod]
        public void Fluency_IsClampedTo100()
        {
            var row = Idle();
            row.AdoptionScore = 98;
            row.AgentsUsed = 1;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(100d, scored.FluencyScore, 0.01, "A 0-100 score must never exceed 100.");
        }

        #endregion

        #region Tiers

        [TestMethod]
        public void RegularCoworkUse_IsEstablished_AndRestsOnEvidence()
        {
            var row = Idle();
            row.CoworkInteractions = 25;
            row.CoworkActiveDays = Options().CoworkRegularMinActiveDays;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established, scored.Tier);
            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Evidence, scored.Basis);
            Assert.IsTrue(scored.RegularCoworkUser);
            Assert.IsTrue(scored.RecommendForPolicy,
                "An existing user must stay in the spending policy or the rollout revokes their access.");
        }

        [TestMethod]
        public void DisabledAccount_KeepsItsEvidenceTier_ButIsNotRecommendedForThePolicy()
        {
            var row = Idle();
            row.CoworkInteractions = 25;
            row.CoworkActiveDays = Options().CoworkRegularMinActiveDays;
            row.AccountEnabled = false;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            // The tier is a statement about observed behaviour, and the behaviour did happen - somebody who
            // used Cowork right up to the day they were disabled really is Established. Suppressing that
            // would stop this tab reconciling with the reclaim figures, so the row stays and stays honest.
            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established, scored.Tier,
                "A disabled account keeps the tier its observed usage earned; only the recommendation changes.");
            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Evidence, scored.Basis);

            // ...but the same row is ReclaimEligibility = Certain ("Reclaim immediately"). Recommending it
            // would put "grant this person Cowork" and "take this person's licence away" side by side in
            // one report, and scope a spending policy to an account that can never consume from it.
            Assert.IsFalse(scored.RecommendForPolicy,
                "A disabled account is a certain reclaim; it must never also be recommended for the Cowork "
                + "spending policy.");
        }

        [TestMethod]
        public void UnknownAccountEnabled_IsNotTreatedAsDisabled()
        {
            var row = Idle();
            row.CoworkInteractions = 25;
            row.CoworkActiveDays = Options().CoworkRegularMinActiveDays;
            row.AccountEnabled = null;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            // The positive direction of the guard above. NULL means the directory import did not state the
            // flag - it is not evidence of being disabled. Treating it as disabled would silently drop
            // every existing Cowork user on any tenant whose user import cannot populate the column, which
            // is precisely the "revoke access from the people already proving it works" failure the
            // recommendation rule exists to prevent.
            Assert.IsTrue(scored.RecommendForPolicy,
                "An unknown account state must not be read as disabled.");
        }

        [TestMethod]
        public void DisabledAccount_IsAlsoNotRecommended_WhenItIsOnlyAnInferredCandidate()
        {
            var row = HeavyCoordinator();
            row.AdoptionScore = 70;
            row.AccountEnabled = false;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.PrimeCandidate, scored.Tier);
            Assert.IsFalse(scored.RecommendForPolicy,
                "The exclusion is about the account being disabled, not about which tier it reached.");
        }

        [TestMethod]
        public void OneDayBelowTheBar_IsTriallingNotEstablished()
        {
            var row = Idle();
            row.CoworkInteractions = 40;
            row.CoworkActiveDays = Options().CoworkRegularMinActiveDays - 1;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Trialling, scored.Tier,
                "Regularity is counted in days, not interactions - one long afternoon is not a habit.");
            Assert.IsFalse(scored.RegularCoworkUser);
            Assert.IsTrue(scored.RecommendForPolicy, "A trialling user must not be dropped from the policy.");
        }

        [TestMethod]
        public void ManyInteractionsOnASingleDay_DoNotCountAsEstablished()
        {
            var row = Idle();
            row.CoworkInteractions = 500;
            row.CoworkActiveDays = 1;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Trialling, scored.Tier,
                "A large interaction count on one day is experimentation, not adoption. This is the whole "
                + "reason CoworkActiveDays exists as a separate signal.");
        }

        [TestMethod]
        public void FluentAndLoaded_IsAPrimeCandidate_AndIsLabelledAsInference()
        {
            var row = HeavyCoordinator();
            row.AdoptionScore = 70;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.PrimeCandidate, scored.Tier);
            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Inference, scored.Basis,
                "Nobody has observed this person using Cowork. Presenting the verdict as evidence would be "
                + "the single most damaging thing this tab could do.");
            Assert.IsTrue(scored.RecommendForPolicy);
        }

        [TestMethod]
        public void FluentButUnloaded_IsLowCoordinationLoad_AndNotRecommended()
        {
            var row = Idle();
            row.AdoptionScore = 90;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.LowCoordinationLoad, scored.Tier);
            Assert.IsFalse(scored.RecommendForPolicy,
                "Enabling Cowork for someone with nothing to delegate spends credits for no return.");
        }

        [TestMethod]
        public void NeitherFluentNorLoaded_IsNotIndicated()
        {
            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(Idle(), Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.NotIndicated, scored.Tier);
            Assert.IsFalse(scored.RecommendForPolicy);
        }

        [TestMethod]
        public void ObservedCoworkUse_BeatsALowMeasuredWorkload()
        {
            // Somebody who genuinely uses Cowork but whose usage reports look quiet - on leave for part of
            // the period, say. The report must not demote a real user to a prediction.
            var row = Idle();
            row.CoworkInteractions = 12;
            row.CoworkActiveDays = 4;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established, scored.Tier,
                "Evidence outranks inference. A measured workload cannot overrule observed usage.");
        }

        [TestMethod]
        public void EveryTier_HasALabelABasisAndADescription()
        {
            foreach (var tier in CopilotAdoptionScoring.AllCoworkTiers)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(CopilotAdoptionScoring.CoworkTierLabel(tier)),
                    $"Tier '{tier}' has no display label.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(CopilotAdoptionScoring.CoworkTierDescription(tier)),
                    $"Tier '{tier}' has no description, so the UI would show a count with no explanation.");

                var basis = CopilotAdoptionScoring.CoworkTierBasis(tier);
                Assert.IsTrue(
                    basis == CopilotAdoptionScoring.CoworkBasis.Evidence
                    || basis == CopilotAdoptionScoring.CoworkBasis.Inference,
                    $"Tier '{tier}' must declare whether it rests on evidence or inference.");
            }
        }

        [TestMethod]
        public void OnlyTheObservedTiers_AreClassedAsEvidence()
        {
            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Evidence,
                CopilotAdoptionScoring.CoworkTierBasis(CopilotAdoptionScoring.CoworkTiers.Established));
            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Evidence,
                CopilotAdoptionScoring.CoworkTierBasis(CopilotAdoptionScoring.CoworkTiers.Trialling));

            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Inference,
                CopilotAdoptionScoring.CoworkTierBasis(CopilotAdoptionScoring.CoworkTiers.PrimeCandidate));
            Assert.AreEqual(CopilotAdoptionScoring.CoworkBasis.Inference,
                CopilotAdoptionScoring.CoworkTierBasis(CopilotAdoptionScoring.CoworkTiers.BuildFluencyFirst));
        }

        #endregion


        [TestMethod]
        public void ReportTasks_BeatAuditInteractions_ForCoworkUsage()
        {
            var row = Idle();
            row.CoworkInteractions = 50;
            row.CoworkActiveDays = 1;
            row.CoworkReportTotalTasks = 3;
            row.CoworkReportScheduledTasks = 2;
            row.CoworkReportUserInitiatedTasks = 1;
            row.CoworkReportActiveDays = Options().CoworkRegularMinActiveDays;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.IsTrue(scored.UsedCowork);
            Assert.IsTrue(scored.RegularCoworkUser,
                "Regularity must come from Microsoft's Cowork usage report when task data is present.");
            Assert.AreEqual(3, scored.CoworkReportTotalTasks);
            Assert.AreEqual(50, scored.CoworkInteractions,
                "Audit interactions are retained separately for reconciliation, not relabelled as tasks.");
            Assert.AreEqual(CopilotAdoptionScoring.Percentage(2, 3), scored.CoworkAutomationRatioPct.Value);
            StringAssert.Contains(scored.Rationale, "Cowork task");
        }

        [TestMethod]
        public void CoworkAdoptionPct_IsSuppressed_WhenEligibilityIsUnknown()
        {
            var analysis = new CopilotAdoptionAnalysis
            {
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow { UserId = 1, UserPrincipalName = "aisha.rahman@contoso.com", CoworkReportTotalTasks = 4, UsedCowork = true },
                },
            };

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(1, analysis.Summary.CoworkUsers);
            Assert.IsNull(analysis.Summary.CoworkAdoptionPct,
                "Eligibility is spending-policy scope. Until that denominator is imported, the percentage must be unknown rather than licensed-user based.");
            Assert.IsTrue(analysis.Summary.Warnings.Any(w => w.IndexOf("deprecated Cowork agent", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void CoworkUsageReportParser_ReadsDocumentedTaskColumns()
        {
            var row = CoworkUsageUserDetailParser.Parse(new[]
            {
                JObject.Parse(@"{
                    'reportRefreshDate': '2026-09-01',
                    'userId': 'aisha.rahman@contoso.com',
                    'totalTasks': 10,
                    'scheduledTasks': 4,
                    'userInitiatedTasks': 6,
                    'activeDays': 5,
                    'lastActivityDate': '2026-08-31'
                }")
            }).Single();

            Assert.AreEqual("aisha.rahman@contoso.com", row.UserPrincipalName);
            Assert.AreEqual(10, row.TotalTasks);
            Assert.AreEqual(4, row.ScheduledTasks);
            Assert.AreEqual(6, row.UserInitiatedTasks);
            Assert.AreEqual(5, row.ActiveDays);
            Assert.AreEqual(new DateTime(2026, 8, 31), row.LastActivityDate.Value.Date);
        }

        [TestMethod]
        public void CoworkAgentLookup_IsNotAnEligibilityQuery()
        {
            var sql = CopilotAdoptionSql.CoworkAgentIdsSql;

            StringAssert.Contains(sql, "Copilot.M365Copilot.Cowork");
            Assert.IsFalse(sql.IndexOf("name LIKE", StringComparison.OrdinalIgnoreCase) >= 0,
                "Agent display names are customer-controlled. This lookup feeds observed Cowork usage, so a "
                + "tenant-owned agent name must not outrank inferred Cowork tiers.");
            Assert.IsFalse(sql.IndexOf("spending", StringComparison.OrdinalIgnoreCase) >= 0,
                "This query may only reconcile audit interactions; Cowork eligibility comes from spending policies, not the deprecated agent entry.");
        }

        #region Rationale wording

        [TestMethod]
        public void PredictedTiers_SayTheyArePredicted()
        {
            var row = HeavyCoordinator();
            row.AdoptionScore = 70;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            StringAssert.Contains(scored.Rationale, "Predicted",
                "The justification ends up in a CSV that gets forwarded to a budget holder. It must not let "
                + "a prediction read as a measurement.");
        }

        [TestMethod]
        public void ObservedTiers_QuoteTheActualUsage()
        {
            var row = Idle();
            row.CoworkInteractions = 25;
            row.CoworkActiveDays = 5;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            StringAssert.Contains(scored.Rationale, "25");
            StringAssert.Contains(scored.Rationale, "5");
            Assert.IsFalse(scored.Rationale.Contains("Predicted"),
                "An observed tier must not be hedged as a prediction.");
        }

        #endregion

        #region The SQL/C# dual

        [TestMethod]
        public void SqlLoadExpression_UsesTheSameTargetsAndWeightsAsTheCSharpScorer()
        {
            var options = Options();
            var sql = CopilotAdoptionScoring.BuildCoworkLoadScoreSql(
                options, "teams", "meetings", "sent", "read", "files");

            // The SQL ranks which rows come back; the C# decides what score is published. If they drift,
            // the database returns the wrong people and no test of the C# alone would notice.
            foreach (var expected in new[]
            {
                options.CoworkCollaborationTarget, options.CoworkMeetingTarget,
                options.CoworkEmailTarget, options.CoworkDocumentTarget,
                options.CoworkCollaborationWeight, options.CoworkMeetingWeight,
                options.CoworkEmailWeight, options.CoworkDocumentWeight,
            })
            {
                StringAssert.Contains(sql, expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "Every target and weight the C# scorer uses must appear in the generated SQL.");
            }

            foreach (var column in new[] { "teams", "meetings", "sent", "read", "files" })
            {
                StringAssert.Contains(sql, column);
            }
        }

        [TestMethod]
        public void SqlLoadExpression_IsInvariantCulture()
        {
            var options = Options();
            options.CoworkMeetingTarget = 2.5;

            var sql = CopilotAdoptionScoring.BuildCoworkLoadScoreSql(
                options, "a", "b", "c", "d", "e");

            StringAssert.Contains(sql, "2.5",
                "A comma decimal separator on a European server would produce syntactically broken SQL.");
        }

        #endregion

        #region The modelled estimate

        private static List<CoworkReadinessRow> Cohort(int users)
        {
            var rows = new List<CoworkReadinessRow>();
            for (var i = 0; i < users; i++)
            {
                rows.Add(new CoworkReadinessRow
                {
                    UserId = i,
                    TeamsMeetings = 4,
                    EmailsSent = 20,
                    EmailsRead = 40,
                    FilesViewedOrEdited = 10,
                });
            }
            return rows;
        }

        [TestMethod]
        public void Estimate_IsEmpty_ForAnEmptyCohort()
        {
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(new List<CoworkReadinessRow>(), Options());

            Assert.AreEqual(0, estimate.CohortUsers);
            Assert.AreEqual(0d, estimate.HoursPerMonthHigh);
        }

        [TestMethod]
        public void Estimate_AlwaysProducesARange_NeverAPointEstimate()
        {
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(10), Options());

            Assert.IsTrue(estimate.HoursPerMonthHigh > 0);
            Assert.IsTrue(estimate.HoursPerMonthLow < estimate.HoursPerMonthHigh,
                "A point estimate invites a precision this model does not have.");
        }

        [TestMethod]
        public void Estimate_NeverRendersWithoutItsAssumptions()
        {
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(5), Options());

            Assert.IsTrue(estimate.Assumptions.Count > 0,
                "The assumptions travel with the numbers so no surface can show one without the other.");
            Assert.IsTrue(estimate.IsModelled);
            Assert.IsTrue(
                estimate.Assumptions.Any(a => a.IndexOf("NOT measured", StringComparison.OrdinalIgnoreCase) >= 0),
                "The caveat that time saved is not measured must be stated explicitly.");
        }

        [TestMethod]
        public void Estimate_NeverProducesAMonetaryFigure()
        {
            // Epic #559 rejects an ROI / "hours saved" calculator, and #553 confines currency to idle
            // licence spend - a measured price for a seat we can prove is unused. This estimate is
            // modelled from assumed minutes per meeting/mail/document, so pricing it would put a
            // fabricated number in a board pack beside measured ones. The hours range survives because
            // it is labelled a rollout-sizing model; the money does not.
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(5), Options());

            // Assert on the serialised FIELD NAMES, not the whole payload: the assumption prose
            // deliberately explains why no money is shown, so a substring search over the entire JSON
            // would match its own disclaimer and fail for the wrong reason.
            var fields = JObject.FromObject(estimate).Properties().Select(p => p.Name).ToList();
            Assert.IsFalse(fields.Any(n => n.IndexOf("currency", StringComparison.OrdinalIgnoreCase) >= 0),
                "No monetary field may be serialised to any caller - UI, workbook or API. Found: "
                + string.Join(", ", fields));

            Assert.IsTrue(estimate.HoursPerMonthHigh > 0,
                "The modelled hours range is deliberately retained; only the monetary conversion is removed.");
            Assert.IsTrue(
                estimate.Assumptions.Any(a => a.IndexOf("No monetary value is shown", StringComparison.OrdinalIgnoreCase) >= 0),
                "The absence of a monetary figure must be explained rather than silently omitted.");
        }

        [TestMethod]
        public void Options_CarryNoLoadedCostOrCurrencyKnob()
        {
            // A configuration surface for an hourly rate is how a monetary figure would creep back in.
            var fields = JObject.FromObject(Options()).Properties().Select(p => p.Name).ToList();

            Assert.IsFalse(fields.Any(n => n.IndexOf("LoadedCost", StringComparison.OrdinalIgnoreCase) >= 0),
                "No loaded-hourly-cost option may exist on the Cowork estimate.");
            Assert.IsFalse(fields.Any(n => n.IndexOf("coworkCurrency", StringComparison.OrdinalIgnoreCase) >= 0),
                "No Cowork currency option may exist.");
        }

        [TestMethod]
        public void Estimate_KeepsTheRangeTheRightWayRound_WhenTheBoundRatioIsMisconfigured()
        {
            var options = Options();
            // A ratio above 1 would otherwise make the "low" bound exceed the "high" one and render a
            // backwards range on an executive report.
            options.CoworkEstimateLowerBoundRatio = 5;

            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(5), options);

            Assert.IsTrue(estimate.HoursPerMonthLow <= estimate.HoursPerMonthHigh);
        }

        [TestMethod]
        public void Estimate_ReportsObservedVolumesSeparatelyFromModelledHours()
        {
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(10), Options());

            Assert.IsTrue(estimate.AddressableMeetings > 0,
                "The volumes are observed from the usage reports and must be reportable on their own, so a "
                + "reader who distrusts the model can still see the addressable work.");
            Assert.IsTrue(estimate.AddressableMailThreads > 0);
            Assert.IsTrue(estimate.AddressableDocuments > 0);
        }

        /// <summary>
        /// The portal recomputes the hours in the browser, from the PUBLISHED volumes, whenever the reader
        /// enters their own assumptions - so the server has to use the same operands, or an uncustomised
        /// page and the Excel report downloaded from it can disagree by an hour.
        /// </summary>
        [TestMethod]
        public void Estimate_ComputesItsHoursFromThePublishedRoundedVolumes()
        {
            var options = Options().Clone();
            options.CoworkMinutesSavedPerMeeting = 120;
            options.CoworkMinutesSavedPerMailThread = 0;
            options.CoworkMinutesSavedPerDocument = 0;

            // 1.4 meetings publishes as 1. From the unrounded 1.4 the model would say 2.8 -> 3 hours; from
            // the published 1 it says 2. The page can only ever see the 1.
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(1, 1.4, 0, 0, options);

            Assert.AreEqual(1d, estimate.AddressableMeetings);
            Assert.AreEqual(2d, estimate.CopilotHoursPerMonthHigh,
                "Hours must be derived from the volumes the estimate publishes, which is all the portal has.");
        }

        /// <summary>
        /// The golden figure shared with the portal's <c>coworkTimeSaved.test.ts</c> ("matches the server
        /// to the hour"). The browser recomputes these hours from the same published inputs whenever a
        /// reader changes an assumption, so the two implementations are pinned to one answer - change
        /// either, and this or its TypeScript twin fails.
        /// </summary>
        [TestMethod]
        public void Estimate_MatchesThePortalsGoldenFigure()
        {
            // Copilot: 1,234 x 5 + 5,678 x 0.5 + 910 x 1 = 9,919 minutes = 165.3 hours; x 50% = 82.7.
            // Cowork: 45 observed + 7 projected x 12.5 (= 87.5, a deliberate midpoint: both languages
            // must round it up to 88) = 133 tasks x 6 minutes = 798 minutes = 13.3 hours; x 50% = 6.65.
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(10, 1234, 5678, 910, Options(), GoldenCoworkTasks());

            Assert.AreEqual(165d, estimate.CopilotHoursPerMonthHigh);
            Assert.AreEqual(83d, estimate.CopilotHoursPerMonthLow);
            CollectionAssert.AreEqual(
                new[] { 103d, 47d, 15d },
                CopilotAdoptionScoring.CopilotHoursByActivity(estimate, Options()));

            Assert.AreEqual(7, estimate.ProjectedCoworkUsers);
            Assert.AreEqual(133d, estimate.CoworkTasks);
            Assert.AreEqual(13d, estimate.CoworkHoursPerMonthHigh);
            Assert.AreEqual(7d, estimate.CoworkHoursPerMonthLow);

            Assert.AreEqual(178d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(90d, estimate.HoursPerMonthLow);
        }

        private static CoworkTaskInputs GoldenCoworkTasks()
        {
            return new CoworkTaskInputs
            {
                ObservedUsers = 3,
                ObservedTasksPerMonth = 45,
                Rate = new CoworkTaskRate { TasksPerPersonPerMonth = 12.5, Basis = CoworkTaskRateBases.Observed, Users = 3 },
            };
        }

        /// <summary>
        /// The shipped defaults are derived from Microsoft's published per-action credits and sit inside
        /// what published studies report. Changing one is a product decision that has to be made on
        /// purpose - with the evidence on the Cowork tab updated to match - never as a drive-by edit.
        /// </summary>
        [TestMethod]
        public void TimeSavedDefaults_AreTheEvidencedFigures()
        {
            var options = CopilotAdoptionOptions.Default;

            Assert.AreEqual(5d, options.CoworkMinutesSavedPerMeeting, "Microsoft's recap credit on one half-hour meeting in six.");
            Assert.AreEqual(0.5d, options.CoworkMinutesSavedPerMailThread, "Microsoft's 6-minute credit on one email in twelve.");
            Assert.AreEqual(1d, options.CoworkMinutesSavedPerDocument, "Microsoft's 6-minute credit on one document in six.");
            Assert.AreEqual(0.5d, options.CoworkEstimateLowerBoundRatio, "Forrester's standard 50% productivity recapture.");
            Assert.AreEqual(6d, options.CoworkMinutesSavedPerTask,
                "The smallest credit Microsoft's Agent Assisted Hours method gives a resolved agent session.");
            Assert.AreEqual(20d, options.CoworkAssumedTasksPerPersonPerMonth, "About one delegated task a working day.");
        }

        [TestMethod]
        public void Estimate_ByActivity_AddsUpToTheCopilotHighFigure()
        {
            // Rounding each activity on its own lets the parts miss the total by an hour, which a reader
            // checking the sum takes as an arithmetic error in the model.
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(3, 7, 7, 7, Options());
            var parts = CopilotAdoptionScoring.CopilotHoursByActivity(estimate, Options());

            Assert.AreEqual(3, parts.Length);
            Assert.AreEqual(estimate.CopilotHoursPerMonthHigh, parts.Sum(), 1e-9,
                "Meetings, email and documents must add up to exactly the Copilot layer's total.");
            Assert.IsTrue(parts.All(p => p >= 0 && p == Math.Floor(p)), "Each part is a whole number of hours.");
        }

        /// <summary>
        /// The defect this guards: the model used to be one blended layer - "Copilot and Cowork together
        /// save..." - resting entirely on Microsoft 365 Copilot evidence, so the figure used to justify
        /// Copilot Credits was mostly Copilot's. Each layer is now published on its own and the total is
        /// exactly their sum.
        /// </summary>
        [TestMethod]
        public void Estimate_PublishesCopilotAndCoworkAsSeparateLayers_ThatAddUpToTheTotal()
        {
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(10, 1234, 5678, 910, Options(), GoldenCoworkTasks());

            Assert.IsTrue(estimate.CopilotHoursPerMonthHigh > 0 && estimate.CoworkHoursPerMonthHigh > 0);
            Assert.AreEqual(estimate.CopilotHoursPerMonthHigh + estimate.CoworkHoursPerMonthHigh, estimate.HoursPerMonthHigh);
            Assert.AreEqual(estimate.CopilotHoursPerMonthLow + estimate.CoworkHoursPerMonthLow, estimate.HoursPerMonthLow);

            var noCowork = Options().Clone();
            noCowork.CoworkMinutesSavedPerTask = 0;
            var copilotOnly = CopilotAdoptionScoring.ModelCoworkValue(10, 1234, 5678, 910, noCowork, GoldenCoworkTasks());
            Assert.AreEqual(estimate.CopilotHoursPerMonthHigh, copilotOnly.HoursPerMonthHigh,
                "Cowork's minutes must never leak into the Copilot layer, or vice versa.");
        }

        [TestMethod]
        public void Estimate_StatesThatCoworkIsUnmeasured_AndTheCopilotMinutesAreCopilots()
        {
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(10, 1234, 5678, 910, Options(), GoldenCoworkTasks());

            Assert.IsTrue(estimate.Assumptions.Any(a => a.StartsWith("Assumes Microsoft 365 Copilot saves", StringComparison.Ordinal)),
                "The per-item minutes are Copilot's evidence and must be attributed to Copilot alone.");
            Assert.IsFalse(estimate.Assumptions.Any(a => a.IndexOf("Copilot and Cowork together", StringComparison.Ordinal) >= 0),
                "The blended attribution credited Cowork with evidence gathered on Copilot.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("No study has yet measured Cowork's time savings", StringComparison.Ordinal) >= 0),
                "The Cowork layer rests on an assumption, and must say so wherever it travels.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("the average of the 3 people already running them", StringComparison.Ordinal) >= 0),
                "An observed rate names how many people it averages.");
        }

        [TestMethod]
        public void CoworkTaskRate_IsTheObservedAverage_RestatedAsAMonth()
        {
            var rows = new List<CoworkReadinessRow>
            {
                new CoworkReadinessRow { UserId = 1, CoworkReportTotalTasks = 10 },
                new CoworkReadinessRow { UserId = 2, CoworkReportTotalTasks = 20 },
                // No tasks: neither in the average's numerator nor its denominator.
                new CoworkReadinessRow { UserId = 3, CoworkReportTotalTasks = null },
                new CoworkReadinessRow { UserId = 4, CoworkReportTotalTasks = 0 },
            };

            // 30 tasks over a 7-day report = 120 over the 28-day month, across 2 people = 60 each.
            var rate = CopilotAdoptionScoring.CoworkTaskRateFor(rows, 7, Options());

            Assert.AreEqual(CoworkTaskRateBases.Observed, rate.Basis);
            Assert.AreEqual(2, rate.Users);
            Assert.AreEqual(60d, rate.TasksPerPersonPerMonth);
        }

        [TestMethod]
        public void CoworkTaskRate_FallsBackToTheLabelledPlaceholder_WhenNothingIsObserved()
        {
            var nobody = CopilotAdoptionScoring.CoworkTaskRateFor(Cohort(3), 28, Options());
            Assert.AreEqual(CoworkTaskRateBases.Assumed, nobody.Basis);
            Assert.AreEqual(20d, nobody.TasksPerPersonPerMonth);
            Assert.AreEqual(0, nobody.Users);

            // Tasks over an unknown period cannot be restated as a month - 180 days of tasks read as one
            // month would multiply the Cowork layer six-fold - so they do not count as observed.
            var rows = new List<CoworkReadinessRow> { new CoworkReadinessRow { UserId = 1, CoworkReportTotalTasks = 50 } };
            Assert.AreEqual(CoworkTaskRateBases.Assumed, CopilotAdoptionScoring.CoworkTaskRateFor(rows, 0, Options()).Basis);
        }

        [TestMethod]
        public void Estimate_CountsObservedTasksAsReported_AndProjectsOnlyEveryoneElse()
        {
            var cohort = Cohort(4);
            cohort[0].CoworkReportTotalTasks = 14;

            var rate = new CoworkTaskRate { TasksPerPersonPerMonth = 10, Basis = CoworkTaskRateBases.Observed, Users = 1 };
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(cohort, Options(), 28, rate);

            Assert.AreEqual(1, estimate.CoworkTaskUsers);
            Assert.AreEqual(14d, estimate.ObservedCoworkTasks, "A 28-day report is already a 28-day month.");
            Assert.AreEqual(3, estimate.ProjectedCoworkUsers);
            Assert.AreEqual(14d + 3 * 10, estimate.CoworkTasks);
            Assert.AreEqual(Math.Round((14d + 30) * 6 / 60, MidpointRounding.AwayFromZero), estimate.CoworkHoursPerMonthHigh);
        }

        [TestMethod]
        public void FullRolloutAndReadyEstimates_ProjectTheSameTenantWideCoworkRate()
        {
            var established = HeavyCoordinator();
            established.UserId = 1;
            established.UserPrincipalName = "regular@contoso.com";
            established.CoworkReportTotalTasks = 12;
            established.CoworkReportActiveDays = 10;

            var notReady = HeavyCoordinator();
            notReady.UserId = 2;
            notReady.UserPrincipalName = "novice@contoso.com";

            var analysis = new CopilotAdoptionAnalysis
            {
                CoworkSignals = new List<CoworkReadinessSignalRow> { established, notReady },
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow { UserId = 1, UserPrincipalName = "regular@contoso.com", AdoptionScore = 80 },
                    new LicensedUserAdoptionRow { UserId = 2, UserPrincipalName = "novice@contoso.com", AdoptionScore = 5 },
                },
            };
            analysis.Summary.DataSources.CoworkUsageReportPeriodDays = 28;

            new CopilotAdoptionService().FinaliseSummary(analysis);
            var ready = analysis.Summary.CoworkValueEstimate;
            var full = analysis.Summary.CoworkFullRolloutEstimate;

            Assert.AreEqual(CoworkTaskRateBases.Observed, full.CoworkTaskRateBasis);
            Assert.AreEqual(12d, full.CoworkTasksPerPersonPerMonth, "One person's 12 tasks over a 28-day report.");
            Assert.AreEqual(full.CoworkTasksPerPersonPerMonth, ready.CoworkTasksPerPersonPerMonth,
                "Both cohorts must project the same rate, or the ready cohort could out-model the ceiling.");
            Assert.AreEqual(12d, full.ObservedCoworkTasks);
            Assert.AreEqual(1, full.ProjectedCoworkUsers, "The novice is projected; the regular is observed.");
            Assert.AreEqual(24d, full.CoworkTasks);
        }

        [TestMethod]
        public void FullRolloutEstimate_CoversEverySeatHolder_AndBoundsTheReadyCohort()
        {
            var ready = HeavyCoordinator();
            ready.UserId = 1;
            ready.UserPrincipalName = "prime@contoso.com";

            var notReady = HeavyCoordinator();
            notReady.UserId = 2;
            notReady.UserPrincipalName = "novice@contoso.com";

            var analysis = new CopilotAdoptionAnalysis
            {
                CoworkSignals = new List<CoworkReadinessSignalRow> { ready, notReady },
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow { UserId = 1, UserPrincipalName = "prime@contoso.com", AdoptionScore = 80 },
                    // Loaded but not yet fluent: "build fluency first", so outside the ready cohort.
                    new LicensedUserAdoptionRow { UserId = 2, UserPrincipalName = "novice@contoso.com", AdoptionScore = 5 },
                },
            };

            new CopilotAdoptionService().FinaliseSummary(analysis);
            var summary = analysis.Summary;

            Assert.AreEqual(1, summary.CoworkValueEstimate.CohortUsers, "Ready now is the recommended policy cohort.");
            Assert.AreEqual(2, summary.CoworkFullRolloutEstimate.CohortUsers,
                "Full adoption covers every scored Copilot seat holder.");
            Assert.IsTrue(summary.CoworkFullRolloutEstimate.HoursPerMonthHigh > summary.CoworkValueEstimate.HoursPerMonthHigh,
                "The ceiling must include the time the not-yet-ready seat holders could get back.");
            Assert.IsTrue(summary.CoworkFullRolloutEstimate.IsModelled);
            Assert.IsTrue(summary.CoworkFullRolloutEstimate.Assumptions.Any(
                a => a.IndexOf("NOT measured", StringComparison.OrdinalIgnoreCase) >= 0),
                "The ceiling is every bit as modelled as the ready cohort and must say so.");
        }

        [TestMethod]
        public void FullRolloutEstimate_IsEmpty_WhenCoworkCouldNotBeAssessed()
        {
            var analysis = new CopilotAdoptionAnalysis();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsFalse(analysis.Summary.CoworkReadinessAvailable);
            Assert.AreEqual(0, analysis.Summary.CoworkFullRolloutEstimate.CohortUsers,
                "No analysis means nothing to model - never a modelled zero presented as a finding.");
        }

        [TestMethod]
        public void TimeSavedOverrides_ApplyToACopy_AndNeverTheCachedOptions()
        {
            var cached = Options();
            var overrides = new CoworkTimeSavedOverrides { MinutesSavedPerMeeting = 12, LowerBoundRatio = 0.25 };

            var applied = overrides.ApplyTo(cached);

            Assert.AreNotSame(cached, applied);
            Assert.AreEqual(12d, applied.CoworkMinutesSavedPerMeeting);
            Assert.AreEqual(0.25d, applied.CoworkEstimateLowerBoundRatio);
            Assert.AreEqual(cached.CoworkMinutesSavedPerMailThread, applied.CoworkMinutesSavedPerMailThread,
                "A figure the reader did not supply keeps the configured default.");
            Assert.AreEqual(5d, cached.CoworkMinutesSavedPerMeeting,
                "The cached analysis's options are shared by every caller and must never be written to.");
        }

        [TestMethod]
        public void TimeSavedOverrides_ClampToTheSameBoundsAsThePortal()
        {
            var applied = new CoworkTimeSavedOverrides
            {
                MinutesSavedPerMeeting = 100000,
                MinutesSavedPerMailThread = -3,
                MinutesSavedPerDocument = double.NaN,
                LowerBoundRatio = 7,
            }.ApplyTo(Options());

            Assert.AreEqual(CoworkTimeSavedOverrides.MaxMinutesPerItem, applied.CoworkMinutesSavedPerMeeting,
                "A hand-edited URL must not turn a sizing model into a headline of millions of hours.");
            Assert.AreEqual(0d, applied.CoworkMinutesSavedPerMailThread, "A negative saving is not a saving.");
            Assert.AreEqual(Options().CoworkMinutesSavedPerDocument, applied.CoworkMinutesSavedPerDocument,
                "A figure that is not a number keeps the default rather than poisoning the estimate.");
            Assert.AreEqual(1d, applied.CoworkEstimateLowerBoundRatio);
            Assert.IsFalse(new CoworkTimeSavedOverrides { MinutesSavedPerMeeting = double.NaN }.Any);
        }

        [TestMethod]
        public void TimeSavedOverrides_ReplaceTheCoworkFigures_WithinTheirOwnBounds()
        {
            var published = new CoworkValueEstimate
            {
                CohortUsers = 5,
                CoworkTasksPerPersonPerMonth = 8,
                CoworkTaskRateBasis = CoworkTaskRateBases.Observed,
                CoworkTaskRateUsers = 2,
            };

            var overrides = new CoworkTimeSavedOverrides { MinutesSavedPerTask = 1000, TasksPerPersonPerMonth = 15 };
            Assert.IsTrue(overrides.Any);
            Assert.AreEqual(CoworkTimeSavedOverrides.MaxMinutesPerTask, overrides.ApplyTo(Options()).CoworkMinutesSavedPerTask);

            var custom = overrides.TaskRateFor(published);
            Assert.AreEqual(15d, custom.TasksPerPersonPerMonth);
            Assert.AreEqual(CoworkTaskRateBases.Custom, custom.Basis, "A reader's rate must never be labelled as observed.");

            Assert.AreEqual(CoworkTimeSavedOverrides.MaxTasksPerPersonPerMonth,
                new CoworkTimeSavedOverrides { TasksPerPersonPerMonth = 1e9 }.TaskRateFor(published).TasksPerPersonPerMonth);

            var untouched = new CoworkTimeSavedOverrides { MinutesSavedPerMeeting = 9 }.TaskRateFor(published);
            Assert.AreEqual(8d, untouched.TasksPerPersonPerMonth, "No rate from the reader keeps the published one.");
            Assert.AreEqual(CoworkTaskRateBases.Observed, untouched.Basis);
            Assert.AreEqual(2, untouched.Users);
        }

        /// <summary>
        /// The portal writes JavaScript numbers into the export URL. A culture-sensitive parse on a
        /// server running a European culture reads the full stop as a thousands separator, so "0.5"
        /// minutes per email would become 5 - ten times the saving the reader entered.
        /// </summary>
        [TestMethod]
        public void ExportTimeSavedFigures_ParseInvariantly_WhateverTheServerCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var parsed = CopilotAdoptionAPIController.ParseTimeSavedOverrides("12", "0.5", "not a number", "0.3", "7.5", "12.5");

                Assert.AreEqual(12d, parsed.MinutesSavedPerMeeting);
                Assert.AreEqual(0.5d, parsed.MinutesSavedPerMailThread, "A German server must not read '0.5' as 5.");
                Assert.IsNull(parsed.MinutesSavedPerDocument, "An unparseable figure keeps the product default.");
                Assert.AreEqual(0.3d, parsed.LowerBoundRatio);
                Assert.AreEqual(7.5d, parsed.MinutesSavedPerTask);
                Assert.AreEqual(12.5d, parsed.TasksPerPersonPerMonth, "A German server must not read '12.5' as 125.");
                Assert.IsFalse(CopilotAdoptionAPIController.ParseTimeSavedOverrides(null, " ", null, null, null, null).Any,
                    "An export with no figures of the reader's own must use the product defaults untouched.");
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        #endregion

        #region Filtering, sorting and export

        private static List<CoworkReadinessRow> ScoredMix()
        {
            var options = Options();

            var established = Idle();
            established.UserId = 1;
            established.UserPrincipalName = "established@contoso.com";
            established.CoworkInteractions = 30;
            established.CoworkActiveDays = 10;

            var prime = HeavyCoordinator();
            prime.UserId = 2;
            prime.UserPrincipalName = "prime@contoso.com";
            prime.AdoptionScore = 80;
            // A genuinely non-ASCII department. Unicode has to survive scoring, filtering and export -
            // this is free text from a customer tenant, not a UPN.
            prime.Department = "Πωλήσεις";

            var novice = HeavyCoordinator();
            novice.UserId = 3;
            novice.UserPrincipalName = "novice@contoso.com";
            novice.AdoptionScore = 5;

            var quiet = Idle();
            quiet.UserId = 4;
            quiet.UserPrincipalName = "quiet@contoso.com";

            return new[] { established, prime, novice, quiet }
                .Select(r => CopilotAdoptionScoring.ScoreCoworkReadiness(r, options))
                .ToList();
        }

        [TestMethod]
        public void RecommendedOnlyFilter_KeepsCandidatesAndExistingUsers()
        {
            var matched = CopilotAdoptionExports.Apply(
                ScoredMix(), new CoworkReadinessQuery { RecommendedOnly = true });

            CollectionAssert.AreEquivalent(
                new[] { "established@contoso.com", "prime@contoso.com" },
                matched.Select(r => r.UserPrincipalName).ToArray(),
                "The policy list is candidates PLUS current users - dropping current users would revoke "
                + "access from the people already proving Cowork works.");
        }

        [TestMethod]
        public void CoworkUsersOnlyFilter_KeepsOnlyObservedUsage()
        {
            var matched = CopilotAdoptionExports.Apply(
                ScoredMix(), new CoworkReadinessQuery { CoworkUsersOnly = true });

            Assert.AreEqual(1, matched.Count);
            Assert.AreEqual("established@contoso.com", matched[0].UserPrincipalName);
        }

        [TestMethod]
        public void TierFilter_MatchesCaseInsensitively()
        {
            var matched = CopilotAdoptionExports.Apply(ScoredMix(), new CoworkReadinessQuery
            {
                Tiers = new List<string> { "PRIMECANDIDATE" },
            });

            Assert.AreEqual(1, matched.Count);
            Assert.AreEqual("prime@contoso.com", matched[0].UserPrincipalName);
        }

        [TestMethod]
        public void UnicodeDepartment_SurvivesScoringAndFiltering()
        {
            var matched = CopilotAdoptionExports.Apply(
                ScoredMix(), new CoworkReadinessQuery { Department = "Πωλήσεις" });

            Assert.AreEqual(1, matched.Count);
            Assert.AreEqual("Πωλήσεις", matched[0].Department,
                "Department is free text from a customer tenant and must survive the full round trip.");
        }

        [TestMethod]
        public void UnknownTierTokens_AreDropped_RatherThanEmptyingTheList()
        {
            // An unrecognised code must not filter the list to nothing - that reads as "nobody is a
            // candidate for Cowork", the most misleading possible failure for this page.
            var parsed = CopilotAdoptionAPIController.ParseCoworkTiers("primeCandidate,notARealTier");

            CollectionAssert.AreEqual(
                new[] { CopilotAdoptionScoring.CoworkTiers.PrimeCandidate }, parsed.ToArray());
        }

        [TestMethod]
        public void CsvSchema_PutsUpnFirst_AndPairsTheVerdictWithItsBasis()
        {
            var columns = CopilotAdoptionExports.CoworkReadinessColumns();
            var headers = columns.Select(c => c.Header).ToList();

            Assert.AreEqual("User principal name", headers[0],
                "This file is pasted into a Cowork spending policy, so the UPN leads.");

            var tierIndex = headers.IndexOf("Cowork tier");
            var basisIndex = headers.IndexOf("Verdict based on");
            Assert.IsTrue(tierIndex >= 0 && basisIndex == tierIndex + 1,
                "The basis must sit immediately beside the tier so a prediction cannot be read as an "
                + "observation by someone scanning columns.");
        }

        [TestMethod]
        public void CsvSchema_NeverLabelsCreditsAsCoworkSpend()
        {
            var creditHeader = CopilotAdoptionExports.CoworkReadinessColumns()
                .Select(c => c.Header)
                .Single(h => h.IndexOf("not attributable", StringComparison.OrdinalIgnoreCase) >= 0);

            // Microsoft meters Cowork against the shared Copilot Credits pool with no per-row workload
            // discriminator, so a "Cowork credits" column would be a fabrication.
            StringAssert.Contains(creditHeader, "not Cowork-only");
            StringAssert.Contains(creditHeader, "not attributable");
        }

        [TestMethod]
        public void CsvSchema_OmitsThePerUserModelledEstimate()
        {
            var headers = CopilotAdoptionExports.CoworkReadinessColumns().Select(c => c.Header);

            Assert.IsFalse(
                headers.Any(h => h.IndexOf("hours", StringComparison.OrdinalIgnoreCase) >= 0),
                "The estimate is a cohort-level model. A per-user hours column would be read as a "
                + "measurement of that individual, which this product cannot make.");
        }

        [TestMethod]
        public void DefaultSort_PutsTheStrongestCasesFirst()
        {
            var sorted = CopilotAdoptionExports.Apply(ScoredMix(), new CoworkReadinessQuery());

            Assert.IsTrue(
                sorted.First().CoordinationLoadScore >= sorted.Last().CoordinationLoadScore,
                "The default view must lead with the people worth acting on.");
        }

        #endregion

        #region JSON contract

        /// <summary>
        /// The property names the SPA reads, per model. If a C# property is renamed or loses its
        /// <c>[JsonProperty]</c>, the client silently reads <c>undefined</c> and the tab renders blank -
        /// with no compiler or runtime error anywhere. This has already happened once in this feature,
        /// to <c>CopilotAdoptionOptions</c>, which is why the equivalent guard exists there too.
        /// </summary>
        [TestMethod]
        public void CoworkModels_AreSerialisedInCamelCaseForTheClient()
        {
            var row = Newtonsoft.Json.Linq.JObject.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    CopilotAdoptionScoring.ScoreCoworkReadiness(HeavyCoordinator(), Options())));

            foreach (var expected in new[]
            {
                "userPrincipalName", "department", "coworkInteractions", "coworkActiveDays",
                "lastCoworkInteractionUtc", "coworkReportTotalTasks", "coworkReportScheduledTasks",
                "coworkReportUserInitiatedTasks", "coworkReportActiveDays", "coworkReportLastActivityDate",
                "coworkReportRetainedUser", "coworkAutomationRatioPct", "coworkCreditsPerTask",
                "usedCowork", "regularCoworkUser",
                "coordinationLoadScore", "fluencyScore", "adoptionScore", "agentsUsed",
                "collaborationScore", "meetingScore", "emailScore", "documentScore",
                "teamsMessages", "teamsMeetings", "emailsSent", "emailsRead", "filesViewedOrEdited",
                "tier", "tierLabel", "basis", "recommendForPolicy", "rationale", "totalCopilotCredits",
            })
            {
                Assert.IsTrue(row.Property(expected) != null,
                    $"CoworkReadinessRow must serialise '{expected}' - the SPA reads it by that name.");
            }

            Assert.IsFalse(row.Properties().Any(p => char.IsUpper(p.Name[0])),
                "Every serialised name must be camelCase; a PascalCase slip renders the tab blank.");
        }

        [TestMethod]
        public void CoworkSummaryModels_AreSerialisedInCamelCaseForTheClient()
        {
            foreach (var model in new object[]
            {
                new CoworkTierSummary(),
                new CoworkQuadrantPoint(),
                new CoworkSegmentRow(),
                new CoworkCreditPosition(),
                new CoworkValueEstimate(),
                new CoworkReadinessPage(),
            })
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(
                    Newtonsoft.Json.JsonConvert.SerializeObject(model));

                Assert.IsTrue(json.Properties().Any(), $"{model.GetType().Name} serialised nothing.");
                Assert.IsFalse(
                    json.Properties().Any(p => char.IsUpper(p.Name[0])),
                    $"{model.GetType().Name} has a PascalCase property - the client would read undefined.");
            }
        }

        [TestMethod]
        public void CoworkSummaryFields_AreSerialisedOnTheSummary()
        {
            // These live on CopilotAdoptionSummary alongside dozens of existing fields, so a missing
            // attribute here would be easy to overlook and would blank the whole tab's headline figures.
            var json = Newtonsoft.Json.Linq.JObject.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(new CopilotAdoptionSummary()));

            foreach (var expected in new[]
            {
                "coworkReadinessAvailable", "coworkScoredUsers", "coworkEstablishedUsers",
                "coworkTriallingUsers", "coworkPrimeCandidates", "coworkBuildFluencyFirst",
                "coworkRecommendedForPolicy", "coworkAverageCoordinationLoad", "coworkAverageFluency",
                "coworkTiers", "coworkQuadrant", "coworkByDepartment", "coworkCreditPosition",
                "coworkValueEstimate", "coworkReportUsers", "coworkReportTotalTasks",
                "coworkReportScheduledTasks", "coworkReportUserInitiatedTasks", "coworkAutomationRatioPct",
                "coworkTasksPerActiveUser", "coworkReportRetainedUsers", "coworkReportRetentionPct",
                "coworkAuditUsers", "coworkEligibilityKnown", "coworkEligibleUsers",
            })
            {
                Assert.IsTrue(json.Property(expected) != null,
                    $"CopilotAdoptionSummary must serialise '{expected}' - the Cowork tab reads it by that name.");
            }
        }

        [TestMethod]
        public void CoworkCredits_SerialiseNullRatherThanZero_WhenNotAttributable()
        {
            // A zero would read as "this person costs nothing", which is a different claim from "we
            // could not attribute any credits to this person". The distinction has to survive the wire.
            var row = CopilotAdoptionScoring.ScoreCoworkReadiness(Idle(), Options());
            Assert.IsNull(row.TotalCopilotCredits);

            var json = Newtonsoft.Json.Linq.JObject.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(row));

            Assert.AreEqual(
                Newtonsoft.Json.Linq.JTokenType.Null,
                json.Property("totalCopilotCredits").Value.Type,
                "Unattributable credits must serialise as null, never as 0.");
        }

        #endregion

        #region Publication guards

        private static CopilotAdoptionAnalysis AnalysisWithOneCoworkSignal()
        {
            var signal = HeavyCoordinator();
            signal.CoworkInteractions = 12;
            signal.CoworkActiveDays = 5;

            return new CopilotAdoptionAnalysis
            {
                CoworkSignals = new List<CoworkReadinessSignalRow> { signal },
            };
        }

        [TestMethod]
        public void Cowork_IsPublished_WhenTheLicensedUserAnalysisIsPresent()
        {
            // The control for the test below: with the fluency input available, the tab publishes.
            var analysis = AnalysisWithOneCoworkSignal();
            analysis.LicensedUsers = new List<LicensedUserAdoptionRow>
            {
                new LicensedUserAdoptionRow
                {
                    UserId = 1,
                    UserPrincipalName = "aisha.rahman@contoso.com",
                    AdoptionScore = 70,
                    AgentsUsed = 2,
                },
            };

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable);
            Assert.AreEqual(1, analysis.CoworkReadiness.Count);
        }

        [TestMethod]
        public void Cowork_IsNotPublished_WhenTheLicensedUserAnalysisIsMissing()
        {
            // Fluency is JOINED IN from the licensed-user analysis, never recalculated. If that analysis
            // produced nothing while Cowork signals exist, the input is unavailable - it is not a measured
            // zero. CoworkReadinessSql semi-joins to seat holders, so signals imply seat holders exist,
            // which means the licensed-user step failed and SafeAsync degraded it to a warning.
            //
            // Publishing anyway scored everyone at fluency 0 and banded them "build fluency first": a
            // missing input rendered as a verdict of "not fluent enough", on the tab that decides who gets
            // access. This is the same class of mistake as rendering unattributable credits as 0, which the
            // test above forbids - and the same answer applies.
            var analysis = AnalysisWithOneCoworkSignal();
            analysis.LicensedUsers = new List<LicensedUserAdoptionRow>();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsFalse(analysis.Summary.CoworkReadinessAvailable,
                "An unavailable fluency input must leave the tab unavailable, not published as zeros.");
            Assert.AreEqual(0, analysis.CoworkReadiness.Count,
                "Nothing may be published from an analysis that could not be scored.");

            // The tab's unavailable card otherwise tells the admin this is a missing usage-report import,
            // which is exactly wrong here: the Cowork signals imported fine and the fault is upstream. The
            // panel filters warnings on the word "Cowork", so the warning has to name it to be surfaced.
            var warning = analysis.Summary.Warnings.SingleOrDefault(
                w => w.IndexOf("Cowork", StringComparison.OrdinalIgnoreCase) >= 0);

            Assert.IsNotNull(warning,
                "The unavailable path must raise a Cowork-named warning, or the tab silently blames a "
                + "missing import for an upstream query failure.");
            StringAssert.Contains(warning, "licensed-user analysis");
        }

        #endregion
    }
}
