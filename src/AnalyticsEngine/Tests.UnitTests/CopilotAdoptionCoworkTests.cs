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
            // The same work, unrounded, as the Cowork estimate reads it.
            row.MeetingsOrganisedPerActiveDay = 2;
            row.MeetingsAttendedPerActiveDay = 4;
            row.ChatAndChannelMessagesPerActiveDay = 60;
            row.EmailsSentPerActiveDay = 40;
            row.FilesPerActiveDay = 35;
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
        public void AuditInteractions_AreTheCoworkUsage_AndAreNeverRelabelledAsTasks()
        {
            // #692: Microsoft's Cowork usage report is not available through Graph, so the Copilot audit log
            // is the only evidence of Cowork use. Fifty interactions on one day are use, but not regular use.
            var row = Idle();
            row.CoworkInteractions = 50;
            row.CoworkActiveDays = 1;

            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(row, Options());

            Assert.IsTrue(scored.UsedCowork);
            Assert.IsFalse(scored.RegularCoworkUser,
                "Regular use is judged on the days with a Cowork interaction, not on how many there were.");
            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Trialling, scored.Tier);
            Assert.AreEqual(50, scored.CoworkInteractions);
            StringAssert.Contains(scored.Rationale, "50 Cowork interactions in the Copilot audit log");
            Assert.IsFalse(scored.Rationale.IndexOf("task", StringComparison.OrdinalIgnoreCase) >= 0,
                "The audit log counts interactions. Calling them tasks invites comparison with Microsoft's task counts.");
        }

        [TestMethod]
        public void CoworkAdoptionPct_IsSuppressed_WhenEligibilityIsUnknown()
        {
            var analysis = new CopilotAdoptionAnalysis
            {
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow { UserId = 1, UserPrincipalName = "aisha.rahman@contoso.com", CoworkInteractions = 4, UsedCowork = true },
                },
            };

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(1, analysis.Summary.CoworkUsers);
            Assert.IsNull(analysis.Summary.CoworkAdoptionPct,
                "Eligibility is spending-policy scope. Until that denominator is imported, the percentage must be unknown rather than licensed-user based.");
            Assert.IsTrue(analysis.Summary.WarningDetails.Any(w => w.Key == CopilotAdoptionWarningKeys.CoworkEligibilityUnknown));
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

        /// <summary>
        /// A cohort of people not yet running Cowork tasks, each doing the same work by hand every active
        /// day: 1 meeting organised, 4 attended, 20 emails sent, 50 Teams messages and 10 files.
        /// </summary>
        private static List<CoworkReadinessRow> Cohort(int users)
        {
            var rows = new List<CoworkReadinessRow>();
            for (var i = 0; i < users; i++)
            {
                rows.Add(new CoworkReadinessRow
                {
                    UserId = i,
                    TeamsMeetings = 5,
                    EmailsSent = 20,
                    EmailsRead = 40,
                    FilesViewedOrEdited = 10,
                    MeetingsOrganisedPerActiveDay = 1,
                    MeetingsAttendedPerActiveDay = 4,
                    EmailsSentPerActiveDay = 20,
                    ChatAndChannelMessagesPerActiveDay = 50,
                    FilesPerActiveDay = 10,
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
        public void WithoutLicenceEstimate_ModelsCandidatesFromTheirUnroundedWork_AndSaysTheyNeedALicence()
        {
            var options = Options();
            // Two candidates, each organising half a meeting a day - which a rounded figure would drop.
            var candidates = new List<LicenceOpportunityRow>
            {
                new LicenceOpportunityRow { UserId = 1, MeetingsOrganisedPerActiveDay = 0.5, EmailsSentPerActiveDay = 4 },
                new LicenceOpportunityRow { UserId = 2, MeetingsOrganisedPerActiveDay = 0.5, FilesPerActiveDay = 3 },
            };

            var estimate = CopilotAdoptionScoring.EstimateCoworkValueWithoutLicence(candidates, options);

            // The same arithmetic as the seat holders' estimate over the same work.
            var sameWork = CopilotAdoptionScoring.EstimateCoworkValue(
                candidates.Select(c => new CoworkReadinessRow
                {
                    MeetingsOrganisedPerActiveDay = c.MeetingsOrganisedPerActiveDay,
                    EmailsSentPerActiveDay = c.EmailsSentPerActiveDay,
                    FilesPerActiveDay = c.FilesPerActiveDay,
                }).ToList(),
                options);

            Assert.AreEqual(2, estimate.CohortUsers);
            Assert.AreEqual(sameWork.HoursPerMonthHigh, estimate.HoursPerMonthHigh);
            Assert.AreEqual(sameWork.HoursPerMonthLow, estimate.HoursPerMonthLow);
            var organised = estimate.Activities.Single(a => a.Activity == CoworkActivities.OrganiseMeetings);
            Assert.AreEqual(
                Math.Round(2 * 0.5 * CopilotAdoptionScoring.WorkingDaysPerMonth(options), 0, MidpointRounding.AwayFromZero),
                organised.VolumePerMonth,
                "Half a meeting a day each is real work, not zero.");

            Assert.IsTrue(estimate.Assumptions.Any(a => a.Contains("2 people without a Microsoft 365 Copilot licence")), string.Join(" | ", estimate.Assumptions));
            Assert.IsTrue(estimate.Assumptions.Any(a => a.Contains("never added together")), "The licence estimate is never summed with this one.");
            Assert.IsFalse(estimate.Assumptions.Any(a => a.Contains("seat holder")), "These people hold no seat.");
            Assert.AreEqual(0, CopilotAdoptionScoring.EstimateCoworkValueWithoutLicence(new List<LicenceOpportunityRow>(), options).CohortUsers);
        }

        [TestMethod]
        public void Estimate_NeverProducesAMonetaryFigure()
        {
            // Epic #559 rejects an ROI calculator, and the idle-licence-spend figure #553 once allowed has
            // since been withdrawn. This estimate is modelled from assumed shares and minutes, so pricing it
            // would put a fabricated number in a board pack beside measured ones. The hours range survives
            // because it is labelled a rollout-sizing model; the money does not.
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(5), Options());

            // Assert on the serialised FIELD NAMES, not the whole payload: the assumption prose
            // deliberately explains why no money is shown, so a substring search over the entire JSON
            // would match its own disclaimer and fail for the wrong reason.
            var json = JObject.FromObject(estimate);
            var fields = json.Properties().Select(p => p.Name)
                .Concat(json["activities"].Children<JObject>().SelectMany(a => a.Properties().Select(p => p.Name)))
                .ToList();
            Assert.IsFalse(fields.Any(n => n.IndexOf("currency", StringComparison.OrdinalIgnoreCase) >= 0
                                           || n.IndexOf("cost", StringComparison.OrdinalIgnoreCase) >= 0),
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

        /// <summary>
        /// The Cowork estimate is the value of enabling Cowork for people who already hold a Copilot
        /// licence, so it carries no Copilot layer at all.
        /// </summary>
        /// <remarks>
        /// The defect this guards: the headline used to add a Microsoft 365 Copilot layer for existing
        /// seat holders - meetings, email and documents - and that layer was most of the figure used to
        /// justify Copilot Credits. Enabling Cowork does not unlock it, the licence already did. The
        /// Cowork estimate now reads meetings, email and documents too, but only as the work Cowork could
        /// take on, at Cowork's own shares and minutes - never at Copilot's.
        /// </remarks>
        [TestMethod]
        public void Estimate_IsCoworkOnly_AndNeverMovesWithTheCopilotMinutes()
        {
            var fields = JObject.FromObject(CopilotAdoptionScoring.ModelCoworkValue(10, Options(), GoldenCoworkInputs()))
                .Properties().Select(p => p.Name).ToList();

            Assert.IsFalse(fields.Any(n => n.StartsWith("addressable", StringComparison.Ordinal)
                                           || n.StartsWith("copilot", StringComparison.Ordinal)),
                "The Cowork estimate must carry no Copilot volumes or hours. Found: " + string.Join(", ", fields));

            var heavierCopilot = Options().Clone();
            heavierCopilot.CopilotMinutesSavedPerMeeting = 120;
            heavierCopilot.CopilotMinutesSavedPerMailThread = 60;
            heavierCopilot.CopilotMinutesSavedPerDocument = 120;

            Assert.AreEqual(
                CopilotAdoptionScoring.ModelCoworkValue(10, Options(), GoldenCoworkInputs()).HoursPerMonthHigh,
                CopilotAdoptionScoring.ModelCoworkValue(10, heavierCopilot, GoldenCoworkInputs()).HoursPerMonthHigh,
                "Copilot's minutes must never leak into the figure that justifies Copilot Credits.");
        }

        /// <summary>
        /// The portal recomputes the hours in the browser, from the PUBLISHED counts, whenever the reader
        /// enters their own assumptions - so the server has to use the same operands, or an uncustomised
        /// page and the Excel report downloaded from it can disagree by an hour.
        /// </summary>
        [TestMethod]
        public void Estimate_ComputesItsHoursFromThePublishedRoundedCounts()
        {
            var options = Options().Clone();
            options.CoworkSendEmailShare = 1;
            options.CoworkSendEmailMinutes = 120;
            options.CoworkPostInTeamsShare = 1;
            options.CoworkPostInTeamsMinutes = 120;

            // 10.4 emails publish as 10, and 10.4 Teams messages as 10. From the unrounded figures the model
            // would say (10.4 + 10.4) x 2 = 41.6 -> 42 hours; from the published ones it says 40. The page can
            // only ever see the published ones.
            var inputs = new CoworkTaskInputs();
            inputs.ActivityVolumes[CoworkActivities.SendEmail] = 10.4;
            inputs.ActivityVolumes[CoworkActivities.PostInTeams] = 10.4;

            var estimate = CopilotAdoptionScoring.ModelCoworkValue(2, options, inputs);

            Assert.AreEqual(10d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.SendEmail));
            Assert.AreEqual(10d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.PostInTeams));
            Assert.AreEqual(40d, estimate.HoursPerMonthHigh,
                "Hours must be derived from the counts the estimate publishes, which is all the portal has.");
        }

        /// <summary>
        /// The golden figure shared with the portal's <c>coworkTimeSaved.test.ts</c> ("matches the server
        /// to the hour"). The browser recomputes these hours from the same published inputs whenever a
        /// reader changes an assumption, so the two implementations are pinned to one answer - change
        /// either, and this or its TypeScript twin fails. The licence estimate's twin is
        /// <see cref="CopilotAdoptionLicenceEstimateTests.Estimate_MatchesThePortalsGoldenFigure"/>.
        /// </summary>
        [TestMethod]
        public void Estimate_MatchesThePortalsGoldenFigure()
        {
            // At the product defaults: 142 meetings organised x 25% = 35.5, 560 attended x 10% = 56,
            // 1,300 emails x 5% = 65, 4,200 Teams messages x 1% = 42 and 1,100 files x 2% = 22 - 220.5
            // pieces of work, published as 221. 220.5 x 6 minutes = 1,323 minutes = 22.05 hours; x 50% = 11.
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(10, Options(), GoldenCoworkInputs());

            Assert.AreEqual(10, estimate.CohortUsers);
            Assert.AreEqual(221d, estimate.ProjectedCoworkTasks);
            Assert.AreEqual(22d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(11d, estimate.HoursPerMonthLow);

            // Where the time comes from, apportioned so the parts add up to the headline: the five kinds
            // of work in catalogue order. The portal splits its bar identically.
            CollectionAssert.AreEqual(
                new[] { 4d, 6d, 6d, 4d, 2d },
                CopilotAdoptionScoring.CoworkHoursByActivity(estimate, Options()));
            CollectionAssert.AreEqual(
                new[] { 36d, 56d, 65d, 42d, 22d },
                CopilotAdoptionScoring.CoworkTasksByActivity(estimate, Options()));
        }

        private static CoworkTaskInputs GoldenCoworkInputs()
        {
            var inputs = new CoworkTaskInputs();
            inputs.ActivityVolumes[CoworkActivities.OrganiseMeetings] = 142;
            inputs.ActivityVolumes[CoworkActivities.PrepareMeetings] = 560;
            inputs.ActivityVolumes[CoworkActivities.SendEmail] = 1300;
            inputs.ActivityVolumes[CoworkActivities.PostInTeams] = 4200;
            inputs.ActivityVolumes[CoworkActivities.CreateDocuments] = 1100;
            return inputs;
        }

        /// <summary>
        /// The point of the activity model: the time comes from what each person already does, so
        /// someone who organises ten meetings a week has more to hand Cowork than someone who organises
        /// none, and the estimate can say where the time is.
        /// </summary>
        [TestMethod]
        public void Estimate_ModelsEachPersonFromTheWorkTheyAlreadyDo()
        {
            var busy = Cohort(1)[0];
            var quiet = new CoworkReadinessRow { UserId = 99 };

            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(new List<CoworkReadinessRow> { busy, quiet }, Options());

            // 28 days x 5/7 = 20 working days a month. The quiet person does nothing, so adds nothing.
            Assert.AreEqual(2, estimate.CohortUsers);
            Assert.AreEqual(20d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.OrganiseMeetings));
            Assert.AreEqual(80d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.PrepareMeetings));
            Assert.AreEqual(400d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.SendEmail));
            Assert.AreEqual(1000d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.PostInTeams));
            Assert.AreEqual(200d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.CreateDocuments));

            // 20 x 25% + 80 x 10% + 400 x 5% + 1,000 x 1% + 200 x 2% = 5 + 8 + 20 + 10 + 4 = 47 pieces
            // x 6 minutes = 282 minutes = 4.7 hours.
            Assert.AreEqual(47d, estimate.ProjectedCoworkTasks);
            Assert.AreEqual(5d, estimate.HoursPerMonthHigh);

            var alone = CopilotAdoptionScoring.EstimateCoworkValue(new List<CoworkReadinessRow> { quiet }, Options());
            Assert.AreEqual(0d, alone.HoursPerMonthHigh,
                "No flat rate is projected onto someone who does none of the work Cowork takes on.");
        }

        /// <summary>
        /// Rounding a per-day volume to a whole number models two meetings a week as none. The signals
        /// stay unrounded from the query to the sum.
        /// </summary>
        [TestMethod]
        public void Estimate_KeepsTheLowVolumesARoundingWouldErase()
        {
            var rows = Enumerable.Range(1, 10)
                .Select(i => new CoworkReadinessRow { UserId = i, MeetingsOrganisedPerActiveDay = 0.4 })
                .ToList();

            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(rows, Options());

            // 10 people x 0.4 a day x 20 working days = 80 meetings a month, not 0.
            Assert.AreEqual(80d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.OrganiseMeetings));
            Assert.AreEqual(20d, estimate.ProjectedCoworkTasks);
        }

        [TestMethod]
        public void Estimate_AppliesEachKindOfWorksOwnShareAndMinutes()
        {
            var baseline = CopilotAdoptionScoring.ModelCoworkValue(10, Options(), GoldenCoworkInputs());

            var moreMeetings = Options().Clone();
            moreMeetings.CoworkOrganiseMeetingsShare = 0.5;
            moreMeetings.CoworkOrganiseMeetingsMinutes = 12;
            var changed = CopilotAdoptionScoring.ModelCoworkValue(10, moreMeetings, GoldenCoworkInputs());

            // 142 x 50% = 71 pieces x 12 minutes = 852 minutes, up from 35.5 x 6 = 213: 639 more.
            Assert.AreEqual(Math.Round((1323d + 639d) / 60d, MidpointRounding.AwayFromZero), changed.HoursPerMonthHigh);
            Assert.AreEqual(221d, baseline.ProjectedCoworkTasks);
            Assert.AreEqual(256d, changed.ProjectedCoworkTasks, "Only the meetings organised move: 220.5 + 35.5 = 256 pieces.");

            var noShare = Options().Clone();
            noShare.CoworkOrganiseMeetingsShare = 0;
            noShare.CoworkPrepareMeetingsShare = 0;
            noShare.CoworkSendEmailShare = 0;
            noShare.CoworkPostInTeamsShare = 0;
            noShare.CoworkCreateDocumentsShare = 0;
            var nothingHandedOver = CopilotAdoptionScoring.ModelCoworkValue(10, noShare, GoldenCoworkInputs());
            Assert.AreEqual(0d, nothingHandedOver.ProjectedCoworkTasks);
            Assert.AreEqual(0d, nothingHandedOver.HoursPerMonthHigh,
                "With no work handed over there is nothing to model: no task count is added on top (#692).");
        }

        [TestMethod]
        public void Estimate_PublishesEveryKindOfWork_InCatalogueOrder_EvenAtZero()
        {
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(new List<CoworkReadinessRow> { new CoworkReadinessRow() }, Options());

            CollectionAssert.AreEqual(
                new[] { "organiseMeetings", "prepareMeetings", "sendEmail", "postInTeams", "createDocuments" },
                estimate.Activities.Select(a => a.Activity).ToArray(),
                "The portal sums in this order; publishing another order would move the hours by a rounding.");
            Assert.IsTrue(estimate.Activities.All(a => a.VolumePerMonth == 0d));
        }

        [TestMethod]
        public void Estimate_ClampsAShareAboveOne_SoCoworkIsNeverHandedMoreWorkThanPeopleDo()
        {
            var misconfigured = Options().Clone();
            misconfigured.CoworkSendEmailShare = 3;
            misconfigured.CoworkPostInTeamsShare = -1;

            var inputs = new CoworkTaskInputs();
            inputs.ActivityVolumes[CoworkActivities.SendEmail] = 100;
            inputs.ActivityVolumes[CoworkActivities.PostInTeams] = 100;

            var estimate = CopilotAdoptionScoring.ModelCoworkValue(1, misconfigured, inputs);

            Assert.AreEqual(100d, estimate.ProjectedCoworkTasks, "300% of 100 emails is 100; a negative share is none.");
        }

        /// <summary>
        /// The shipped defaults are a product decision, made on purpose - with the evidence on the Cowork
        /// and Licence opportunities tabs updated to match - never as a drive-by edit.
        /// </summary>
        [TestMethod]
        public void TimeSavedDefaults_AreTheEvidencedFigures()
        {
            var options = CopilotAdoptionOptions.Default;

            Assert.AreEqual(5d, options.CopilotMinutesSavedPerMeeting, "Microsoft's recap credit on one half-hour meeting in six.");
            Assert.AreEqual(0.5d, options.CopilotMinutesSavedPerMailThread, "Microsoft's 6-minute credit on one email in twelve.");
            Assert.AreEqual(1d, options.CopilotMinutesSavedPerDocument, "Microsoft's 6-minute credit on one document in six.");
            Assert.AreEqual(0.5d, options.CoworkEstimateLowerBoundRatio, "Forrester's standard 50% productivity recapture.");

            // Every kind of work Cowork could take on starts from the smallest credit Microsoft's Agent
            // Assisted Hours method gives a resolved agent session - nothing published tells them apart - and
            // the shares carry the judgement, stated as "one in N" on the Cowork tab.
            foreach (var activity in CoworkActivities.All)
            {
                Assert.AreEqual(6d, activity.Minutes(options), $"{activity.Key}: the same six-minute agent credit.");
            }

            Assert.AreEqual(0.25d, options.CoworkOrganiseMeetingsShare, "One meeting organised in four.");
            Assert.AreEqual(0.1d, options.CoworkPrepareMeetingsShare, "One meeting attended in ten.");
            Assert.AreEqual(0.05d, options.CoworkSendEmailShare, "One email sent in twenty.");
            Assert.AreEqual(0.01d, options.CoworkPostInTeamsShare, "One Teams message in a hundred.");
            Assert.AreEqual(0.02d, options.CoworkCreateDocumentsShare, "One file worked on in fifty.");
        }

        /// <summary>
        /// Every option carries the name of the estimate it drives, and the Cowork activity options carry
        /// the name the export and the portal use for them. The Copilot minutes were
        /// <c>coworkMinutesSavedPer*</c> once; the old keys must not linger, or a Settings sheet would carry
        /// two keys for one assumption and a diff of two exports would never line up.
        /// </summary>
        [TestMethod]
        public void TimeSavedOptions_AreNamedForTheEstimateTheyDrive()
        {
            var keys = JObject.FromObject(Options()).Properties().Select(p => p.Name).ToList();

            CollectionAssert.IsSubsetOf(
                new[]
                {
                    "copilotMinutesSavedPerMeeting", "copilotMinutesSavedPerMailThread", "copilotMinutesSavedPerDocument",
                    "coworkEstimateLowerBoundRatio",
                },
                keys);
            CollectionAssert.IsSubsetOf(
                CoworkActivities.All.SelectMany(a => new[] { a.ShareOption, a.MinutesOption }).ToArray(),
                keys,
                "Each kind of work's share and minutes must be serialised under the names the export reads.");
            Assert.IsFalse(keys.Any(k => k.StartsWith("coworkMinutesSavedPerM", StringComparison.Ordinal)
                                         || k == "coworkMinutesSavedPerDocument"),
                "The Copilot minutes must not keep their historical Cowork names.");
            Assert.IsFalse(keys.Contains("coworkAssumedTasksPerPersonPerMonth"),
                "The flat tasks-a-person placeholder is gone: everyone is modelled from their own work.");
            Assert.IsFalse(keys.Contains("coworkMinutesSavedPerTask"),
                "There is no per-task figure: Cowork task counts are not available to this product (#692).");
        }

        [TestMethod]
        public void Estimate_StatesThatCoworkIsUnmeasured_AndIsItsIncrementOverCopilot()
        {
            var estimate = CopilotAdoptionScoring.ModelCoworkValue(10, Options(), GoldenCoworkInputs());

            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("No study has yet measured Cowork's time savings", StringComparison.Ordinal) >= 0),
                "The Cowork estimate rests on an assumption, and must say so wherever it travels.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("Cowork's increment over Copilot alone", StringComparison.Ordinal) >= 0),
                "The minutes are what Cowork adds for people who already use Copilot.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("No study has measured how much work people hand to Cowork", StringComparison.Ordinal) >= 0),
                "The shares are assumptions as well, and must say so.");
            Assert.IsFalse(estimate.Assumptions.Any(a => a.StartsWith("Assumes Microsoft 365 Copilot saves", StringComparison.Ordinal)),
                "Copilot's per-item minutes size the licence decision, never the Cowork one.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("Covers 10 Copilot seat holders", StringComparison.Ordinal) >= 0));
            Assert.IsTrue(estimate.Assumptions.Any(a => a.StartsWith("Assumes people hand Cowork 25% of the meetings they organise, 10% of the meetings they attend, 5% of the emails they send, 1% of their Teams messages and 2% of the files they work on.", StringComparison.Ordinal)));
        }

        /// <summary>
        /// #692: the product cannot read Microsoft's Cowork task counts, so nobody is counted "as reported".
        /// Everyone is modelled from the work they already do by hand - people the Copilot audit log shows
        /// using Cowork included - and the assumptions say so without ever citing the Cowork usage report.
        /// </summary>
        [TestMethod]
        public void Estimate_ModelsEveryoneFromTheirWork_AndNeverCitesTheCoworkUsageReport()
        {
            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(3), Options());

            Assert.IsTrue(estimate.Assumptions.Contains(
                "Everyone here is modelled from the work they already do by hand, including people already using Cowork."));
            Assert.IsFalse(estimate.Assumptions.Any(a => a.IndexOf("Cowork usage report", StringComparison.OrdinalIgnoreCase) >= 0),
                "The estimate must not cite a report this product never reads.");
            Assert.IsFalse(estimate.Assumptions.Any(a => a.IndexOf("task", StringComparison.OrdinalIgnoreCase) >= 0),
                "Nothing in the model is a task count: it hands Cowork pieces of work.");
        }

        /// <summary>
        /// The reviewer hotspot of #692: someone the Copilot audit log shows using Cowork is modelled from
        /// their own work like everyone else. Their interactions add nothing on top - they are not tasks,
        /// and adding them would count the same time twice.
        /// </summary>
        [TestMethod]
        public void Estimate_ModelsPeopleAlreadyUsingCowork_FromTheirWorkLikeEveryoneElse()
        {
            var withUsers = Cohort(4);
            withUsers[0].CoworkInteractions = 14;
            withUsers[0].CoworkActiveDays = 10;
            withUsers[0].UsedCowork = true;

            var estimate = CopilotAdoptionScoring.EstimateCoworkValue(withUsers, Options());
            var withoutUsers = CopilotAdoptionScoring.EstimateCoworkValue(Cohort(4), Options());

            Assert.AreEqual(4, estimate.CohortUsers);
            // All 4 people's own meetings: 4 x 1 a day x 20 working days.
            Assert.AreEqual(80d, CopilotAdoptionScoring.CoworkVolume(estimate, CoworkActivities.OrganiseMeetings));
            // Each hands Cowork the 47 pieces a month one Cohort row does (see above): 188, x 6 minutes = 18.8 hours.
            Assert.AreEqual(188d, estimate.ProjectedCoworkTasks);
            Assert.AreEqual(19d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(withoutUsers.HoursPerMonthHigh, estimate.HoursPerMonthHigh,
                "Cowork interactions in the audit log must not move the modelled hours.");
        }

        [TestMethod]
        public void FullRolloutAndReadyEstimates_ModelEveryoneTheyCover()
        {
            var established = HeavyCoordinator();
            established.UserId = 1;
            established.UserPrincipalName = "regular@contoso.com";
            established.CoworkInteractions = 30;
            established.CoworkActiveDays = 10;

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
            analysis.Summary.DataSources.M365UsageReportsAvailable = true;

            new CopilotAdoptionService().FinaliseSummary(analysis);
            var ready = analysis.Summary.CoworkValueEstimate;
            var full = analysis.Summary.CoworkFullRolloutEstimate;

            Assert.AreEqual(1, ready.CohortUsers, "Ready now holds the established user, who stays in scope.");
            Assert.AreEqual(2, full.CohortUsers);
            // Each heavy coordinator organises 2 meetings a day x 20 days = 40 a month, and both are modelled.
            Assert.AreEqual(40d, CopilotAdoptionScoring.CoworkVolume(ready, CoworkActivities.OrganiseMeetings),
                "The established user is modelled from their own work, not left out as 'observed'.");
            Assert.AreEqual(80d, CopilotAdoptionScoring.CoworkVolume(full, CoworkActivities.OrganiseMeetings));
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
            analysis.Summary.DataSources.M365UsageReportsAvailable = true;

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

        /// <summary>
        /// Without the Microsoft 365 usage reports everyone has zero meetings, emails, messages and files,
        /// so the estimate would publish "0 hours" - which reads as "Cowork has nothing to take on" rather
        /// than as a missing import. It is not published at all instead, exactly like the licence estimate.
        /// </summary>
        [TestMethod]
        public void Estimates_AreEmpty_WhenTheUsageReportsHaveNotBeenImported()
        {
            var signal = HeavyCoordinator();
            var analysis = new CopilotAdoptionAnalysis
            {
                CoworkSignals = new List<CoworkReadinessSignalRow> { signal },
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow { UserId = 1, UserPrincipalName = "aisha.rahman@contoso.com", AdoptionScore = 80 },
                },
            };

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable, "The readiness tab itself still publishes.");
            Assert.AreEqual(0, analysis.Summary.CoworkValueEstimate.CohortUsers);
            Assert.AreEqual(0, analysis.Summary.CoworkFullRolloutEstimate.CohortUsers);
        }

        [TestMethod]
        public void TimeSavedOverrides_ApplyToACopy_AndNeverTheCachedOptions()
        {
            var cached = Options();
            var overrides = new TimeSavedOverrides { MinutesSavedPerMeeting = 12, LowerBoundRatio = 0.25 };
            overrides.CoworkShares[CoworkActivities.SendEmail] = 0.2;

            var applied = overrides.ApplyTo(cached);

            Assert.AreNotSame(cached, applied);
            Assert.AreEqual(12d, applied.CopilotMinutesSavedPerMeeting);
            Assert.AreEqual(0.25d, applied.CoworkEstimateLowerBoundRatio);
            Assert.AreEqual(0.2d, applied.CoworkSendEmailShare);
            Assert.AreEqual(cached.CopilotMinutesSavedPerMailThread, applied.CopilotMinutesSavedPerMailThread,
                "A figure the reader did not supply keeps the configured default.");
            Assert.AreEqual(5d, cached.CopilotMinutesSavedPerMeeting,
                "The cached analysis's options are shared by every caller and must never be written to.");
            Assert.AreEqual(0.05d, cached.CoworkSendEmailShare);
        }

        [TestMethod]
        public void TimeSavedOverrides_ClampToTheSameBoundsAsThePortal()
        {
            var applied = new TimeSavedOverrides
            {
                MinutesSavedPerMeeting = 100000,
                MinutesSavedPerMailThread = -3,
                MinutesSavedPerDocument = double.NaN,
                LowerBoundRatio = 7,
            }.ApplyTo(Options());

            Assert.AreEqual(TimeSavedOverrides.MaxMinutesPerItem, applied.CopilotMinutesSavedPerMeeting,
                "A hand-edited URL must not turn a sizing model into a headline of millions of hours.");
            Assert.AreEqual(0d, applied.CopilotMinutesSavedPerMailThread, "A negative saving is not a saving.");
            Assert.AreEqual(Options().CopilotMinutesSavedPerDocument, applied.CopilotMinutesSavedPerDocument,
                "A figure that is not a number keeps the default rather than poisoning the estimate.");
            Assert.AreEqual(1d, applied.CoworkEstimateLowerBoundRatio);
            Assert.IsFalse(new TimeSavedOverrides { MinutesSavedPerMeeting = double.NaN }.Any);
        }

        [TestMethod]
        public void TimeSavedOverrides_ReplaceTheCoworkFigures_WithinTheirOwnBounds()
        {
            var overrides = new TimeSavedOverrides();
            overrides.CoworkShares[CoworkActivities.OrganiseMeetings] = 5;
            overrides.CoworkShares[CoworkActivities.PostInTeams] = -1;
            overrides.CoworkMinutes[CoworkActivities.CreateDocuments] = 1e9;
            overrides.CoworkMinutes[CoworkActivities.SendEmail] = double.NaN;
            overrides.CoworkShares["notAKindOfWork"] = 0.9;

            Assert.IsTrue(overrides.Any);
            Assert.IsTrue(overrides.AnyCowork);

            var applied = overrides.ApplyTo(Options());
            Assert.AreEqual(1d, applied.CoworkOrganiseMeetingsShare, "A share above 100% would hand Cowork more work than exists.");
            Assert.AreEqual(0d, applied.CoworkPostInTeamsShare);
            Assert.AreEqual(TimeSavedOverrides.MaxMinutesPerTask, applied.CoworkCreateDocumentsMinutes);
            Assert.AreEqual(6d, applied.CoworkSendEmailMinutes, "A figure that is not a number keeps the default.");

            var copilotOnly = new TimeSavedOverrides { MinutesSavedPerMeeting = 9 };
            Assert.IsTrue(copilotOnly.Any);
            Assert.IsFalse(copilotOnly.AnyCowork, "A reader who retuned Copilot's minutes has not changed the Cowork estimate.");
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

                var query = new[]
                {
                    new KeyValuePair<string, string>("windowDays", "28"),
                    new KeyValuePair<string, string>("coworkSendEmailShare", "0.15"),
                    // Matched case-insensitively, as Web API binds named parameters.
                    new KeyValuePair<string, string>("COWORKORGANISEMEETINGSMINUTES", "7.5"),
                    new KeyValuePair<string, string>("coworkPostInTeamsShare", "lots"),
                    // The first value wins for a repeated key, as for a bound parameter.
                    new KeyValuePair<string, string>("coworkCreateDocumentsShare", "0.1"),
                    new KeyValuePair<string, string>("coworkCreateDocumentsShare", "0.9"),
                };

                var parsed = CopilotAdoptionAPIController.ParseTimeSavedOverrides("12", "0.5", "not a number", "0.3", query);

                Assert.AreEqual(12d, parsed.MinutesSavedPerMeeting);
                Assert.AreEqual(0.5d, parsed.MinutesSavedPerMailThread, "A German server must not read '0.5' as 5.");
                Assert.IsNull(parsed.MinutesSavedPerDocument, "An unparseable figure keeps the product default.");
                Assert.AreEqual(0.3d, parsed.LowerBoundRatio);
                Assert.AreEqual(0.15d, parsed.CoworkShares[CoworkActivities.SendEmail], "A German server must not read '0.15' as 15.");
                Assert.AreEqual(7.5d, parsed.CoworkMinutes[CoworkActivities.OrganiseMeetings]);
                Assert.IsNull(parsed.CoworkShares[CoworkActivities.PostInTeams], "An unparseable figure keeps the product default.");
                Assert.AreEqual(0.1d, parsed.CoworkShares[CoworkActivities.CreateDocuments]);
                Assert.IsFalse(parsed.CoworkShares.ContainsKey(CoworkActivities.PrepareMeetings));

                Assert.IsFalse(CopilotAdoptionAPIController.ParseTimeSavedOverrides(null, " ", null, null).Any,
                    "An export with no figures of the reader's own must use the product defaults untouched.");

                // #692 removed the per-task minutes. A link saved before then still carries them; they are
                // ignored rather than failing the export.
                var staleLink = new[] { new KeyValuePair<string, string>("coworkMinutesSavedPerTask", "7.5") };
                Assert.IsFalse(CopilotAdoptionAPIController.ParseTimeSavedOverrides(null, null, null, null, staleLink).Any,
                    "A retired per-task figure must not count as a figure of the reader's own.");
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

            // This is the separate Copilot Studio credit import, not Cowork billing.
            StringAssert.Contains(creditHeader, "Copilot Studio Credits");
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
                "lastCoworkInteractionUtc",
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

            // The unrounded per-day volumes are the Cowork estimate's inputs only. On the wire they would
            // be a second "emails sent" disagreeing with the first in the decimals, on every row.
            foreach (var hidden in new[]
            {
                "meetingsOrganisedPerActiveDay", "meetingsAttendedPerActiveDay", "chatAndChannelMessagesPerActiveDay",
                "emailsSentPerActiveDay", "filesPerActiveDay",
            })
            {
                Assert.IsNull(row.Property(hidden, StringComparison.OrdinalIgnoreCase),
                    $"CoworkReadinessRow must not serialise '{hidden}'.");
            }
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
                new CoworkActivityVolume(),
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
                "coworkValueEstimate",
                "coworkAuditUsers", "coworkEligibilityKnown", "coworkEligibleUsers",
            })
            {
                Assert.IsTrue(json.Property(expected) != null,
                    $"CopilotAdoptionSummary must serialise '{expected}' - the Cowork tab reads it by that name.");
            }
        }

        /// <summary>
        /// #692: the product never reads Microsoft's Cowork usage report, so no Cowork figure that only that
        /// report could supply - task counts, the scheduled/user-initiated split, automation ratio, retention,
        /// the report's active days or last activity, credits per task - may be serialised to any caller.
        /// </summary>
        [TestMethod]
        public void NoReportSourcedCoworkField_IsSerialisedAnywhere()
        {
            var options = Options();
            var row = CopilotAdoptionScoring.ScoreCoworkReadiness(HeavyCoordinator(), options);
            var payloads = new Dictionary<string, object>
            {
                { nameof(CopilotAdoptionSummary), new CopilotAdoptionSummary() },
                { nameof(CopilotAdoptionSummary.DataSources), new CopilotAdoptionSummary().DataSources },
                { nameof(CoworkReadinessRow), row },
                { nameof(CoworkReadinessSignalRow), new CoworkReadinessSignalRow() },
                { nameof(CoworkSegmentRow), new CoworkSegmentRow() },
                { nameof(CoworkValueEstimate), CopilotAdoptionScoring.ModelCoworkValue(10, options, GoldenCoworkInputs()) },
                { nameof(LicensedUserAdoptionRow), new LicensedUserAdoptionRow() },
                { nameof(LicensedUserUsageRow), new LicensedUserUsageRow() },
                { nameof(CopilotAdoptionOptions), options },
            };

            var retiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "coworkTasks", "coworkTaskUsers", "observedCoworkTasks", "projectedCoworkUsers",
                "observedTasksPerPersonPerMonth", "observedTaskRateUsers", "coworkMinutesSavedPerTask",
            };

            foreach (var payload in payloads)
            {
                var names = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(payload.Value))
                    .Properties().Select(p => p.Name).ToList();
                var reportSourced = names.Where(n =>
                        retiredNames.Contains(n)
                        || n.IndexOf("coworkReport", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("coworkUsageReport", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("automationRatio", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("creditsPerTask", StringComparison.OrdinalIgnoreCase) >= 0
                        || (n.IndexOf("cowork", StringComparison.OrdinalIgnoreCase) >= 0
                            && (n.IndexOf("retention", StringComparison.OrdinalIgnoreCase) >= 0
                                || n.IndexOf("retained", StringComparison.OrdinalIgnoreCase) >= 0
                                || n.IndexOf("tasksPer", StringComparison.OrdinalIgnoreCase) >= 0)))
                    .ToList();

                Assert.AreEqual(0, reportSourced.Count,
                    $"{payload.Key} serialises report-sourced Cowork fields: {string.Join(", ", reportSourced)}");
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
        public void Cowork_SaysItsCountsCoverOnlyWhoItReached_WhenTheAssessmentStoppedAtItsCap()
        {
            // The query ranks by coordination load and stops at MaxCoworkUsersScored, so the seat holders
            // with the least load were never assessed. On dev nothing said so, anywhere.
            var analysis = PublishableCoworkAnalysis();

            new CopilotAdoptionService(new CopilotAdoptionOptions { MaxCoworkUsersScored = 1 }).FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable, "What it did assess is still published.");
            var detail = analysis.Summary.WarningDetails.Single(d => d.Key == CopilotAdoptionWarningKeys.CoworkReadinessCapped);
            Assert.AreEqual(1, detail.Values["maxUsers"]);
        }

        [TestMethod]
        public void Cowork_SaysSoInASliceOfACappedAssessment_EvenWithPeopleInIt()
        {
            // A slice holds far fewer signals than the cap however many of its people the cap left out, so
            // its own count cannot tell - only the tenant assessment it was cut from can.
            var analysis = PublishableCoworkAnalysis();
            analysis.CoworkAssessmentCapped = true;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable);
            Assert.IsTrue(analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkReadinessCapped));
        }

        [TestMethod]
        public void Cowork_SaysNothingAboutACapTheAssessmentDidNotReach()
        {
            var analysis = PublishableCoworkAnalysis();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsFalse(analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkReadinessCapped));
        }

        /// <summary>One Cowork signal and the licensed-user row its fluency is taken from: a tab that publishes.</summary>
        private static CopilotAdoptionAnalysis PublishableCoworkAnalysis()
        {
            var analysis = AnalysisWithOneCoworkSignal();
            analysis.LicensedUsers = new List<LicensedUserAdoptionRow>
            {
                new LicensedUserAdoptionRow
                {
                    UserId = analysis.CoworkSignals[0].UserId,
                    UserPrincipalName = analysis.CoworkSignals[0].UserPrincipalName,
                    AdoptionScore = 70,
                    AgentsUsed = 2,
                },
            };
            return analysis;
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

        [TestMethod]
        public void Cowork_InASliceBeyondTheLicensedUserCap_BlamesTheCapNotAFailure()
        {
            // The licensed-user query keeps the lowest user ids past its cap and the Cowork query the most
            // loaded people, so a view made up of the newest user records can hold Cowork signals and no
            // licensed-user row at all, with both queries complete. Telling that admin to check Health and
            // re-run sends them looking for a failure that does not exist.
            var analysis = AnalysisWithOneCoworkSignal();
            analysis.LicensedUsers = new List<LicensedUserAdoptionRow>();
            analysis.LicensedUsersCapped = true;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsFalse(analysis.Summary.CoworkReadinessAvailable,
                "Still unavailable: nobody in the view has a fluency figure to score them with.");
            Assert.AreEqual(0, analysis.CoworkReadiness.Count);
            Assert.IsFalse(analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkFluencyMissingAll));
            var detail = analysis.Summary.WarningDetails.Single(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceBeyondLicensedCap);
            Assert.AreEqual(1, detail.Values["total"]);
            Assert.AreEqual(CopilotAdoptionOptions.Default.MaxLicensedUsersScored, detail.Values["maxLicensed"]);
        }

        [TestMethod]
        public void Cowork_StillBlamesAFailure_WhenTheLicensedUserQueryWasNotCapped()
        {
            var analysis = AnalysisWithOneCoworkSignal();
            analysis.LicensedUsers = new List<LicensedUserAdoptionRow>();

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkFluencyMissingAll));
            Assert.IsFalse(analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceBeyondLicensedCap));
        }

        [TestMethod]
        public void Cowork_AFilteredViewOfACappedAnalysisKnowsTheCapWasReached()
        {
            // End to end through the scope filter: the slice is rebuilt from narrowed rows, so the cap has
            // to travel with it - the view itself holds no licensed-user row that could say so.
            var analysis = CappedAnalysisWithANewJoinerPastTheCap();

            var service = new CopilotAdoptionService();
            service.FinaliseSummary(analysis);
            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable, "The tenant view has fluency for most people.");

            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"), service.FinaliseSummary);

            Assert.IsFalse(scoped.Summary.CoworkReadinessAvailable);
            Assert.IsTrue(scoped.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceBeyondLicensedCap));
            Assert.IsFalse(scoped.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkFluencyMissingAll));
        }

        [TestMethod]
        public void Cowork_RowsOfASliceTheSummaryCannotScoreAreWithheldToo()
        {
            // The Cowork list and its export read the narrowed rows directly, not the summary. The tenant
            // view scored the new joiner against a default fluency of 0 (with its partial warning), so a
            // slice of only them handed that verdict out as a spending-policy list while its own summary
            // said the tab could not be scored.
            var analysis = CappedAnalysisWithANewJoinerPastTheCap();
            var service = new CopilotAdoptionService();
            service.FinaliseSummary(analysis);
            Assert.AreEqual(2, analysis.CoworkReadiness.Count, "The tenant view lists both, as before.");

            var listed = CopilotAdoptionScopeFilter.FilterRows(analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"));
            Assert.AreEqual(0, listed.CoworkReadiness.Count, "The list and export agree with the summary.");

            var summarised = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"), service.FinaliseSummary);
            Assert.AreEqual(0, summarised.CoworkReadiness.Count);

            var scorable = CopilotAdoptionScopeFilter.FilterRows(analysis, CopilotAdoptionScope.ForEmailDomain("contoso.com"));
            Assert.AreEqual(1, scorable.CoworkReadiness.Count, "A slice with fluency to score keeps its rows.");
        }

        [TestMethod]
        public void Cowork_AnUnscorableAnalysisPublishesNoRows()
        {
            // A slice reaches FinaliseCowork carrying the tenant's rows for its people. When it cannot be
            // scored, those rows must not outlive the verdict.
            var analysis = AnalysisWithOneCoworkSignal();
            analysis.LicensedUsers = new List<LicensedUserAdoptionRow>();
            analysis.CoworkReadiness = new List<CoworkReadinessRow>
            {
                CopilotAdoptionScoring.ScoreCoworkReadiness(analysis.CoworkSignals[0], Options()),
            };

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsFalse(analysis.Summary.CoworkReadinessAvailable);
            Assert.AreEqual(0, analysis.CoworkReadiness.Count);
        }

        [TestMethod]
        public void Cowork_ASliceWhoseLicensedAndCoworkPeopleNeverOverlapCannotBeScored()
        {
            // Both lists hold somebody - an older seat holder the licensed-user query reached, and a newer
            // one only the Cowork assessment reached - but nobody is in both. "Any licensed-user row" called
            // that scorable, and every Cowork row went out at a fluency of 0 as "build fluency first", in
            // the summary and in the list and export alike.
            var analysis = CappedAnalysisWithANewJoinerPastTheCap(withAColleagueCoworkNeverReached: true);
            var service = new CopilotAdoptionService();
            service.FinaliseSummary(analysis);
            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable, "The tenant view still has someone in both lists.");

            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"), service.FinaliseSummary);

            Assert.AreEqual(1, scoped.LicensedUsers.Count, "The slice has a licensed-user row - just not for anyone Cowork reached.");
            Assert.IsFalse(scoped.Summary.CoworkReadinessAvailable);
            Assert.AreEqual(1, scoped.Summary.WarningDetails.Single(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceBeyondLicensedCap).Values["total"]);
            Assert.IsFalse(scoped.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkFluencyPartial));
            Assert.AreEqual(0, scoped.CoworkReadiness.Count);

            var listed = CopilotAdoptionScopeFilter.FilterRows(analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"));
            Assert.AreEqual(0, listed.CoworkReadiness.Count, "The list and export agree with the summary.");
        }

        [TestMethod]
        public void Cowork_CanBeScoredOnlyWhenSomeoneItReachedHasALicensedUserRow()
        {
            var reached = new List<CoworkReadinessSignalRow> { new CoworkReadinessSignalRow { UserId = 7 } };

            Assert.IsTrue(CopilotAdoptionScoring.CoworkCanBeScored(new List<CoworkReadinessSignalRow>(), null), "Nothing to score is not a fault.");
            Assert.IsFalse(CopilotAdoptionScoring.CoworkCanBeScored(reached, null));
            Assert.IsFalse(CopilotAdoptionScoring.CoworkCanBeScored(reached, new[] { new LicensedUserAdoptionRow { UserId = 8 } }));
            Assert.IsTrue(CopilotAdoptionScoring.CoworkCanBeScored(
                reached, new[] { new LicensedUserAdoptionRow { UserId = 8 }, new LicensedUserAdoptionRow { UserId = 7 } }));
        }

        /// <summary>
        /// A capped tenant analysis: one seat holder the licensed-user query reached, and one newer one past
        /// its cap whom the Cowork assessment still reached - each in their own email domain. Optionally an
        /// older colleague of the newer one, in the same domain, whom only the licensed-user query reached.
        /// </summary>
        private static CopilotAdoptionAnalysis CappedAnalysisWithANewJoinerPastTheCap(bool withAColleagueCoworkNeverReached = false)
        {
            var assessed = HeavyCoordinator();
            assessed.UserId = 1;
            assessed.UserPrincipalName = "aisha.rahman@contoso.com";
            assessed.EmailDomain = "contoso.com";
            var newJoiner = HeavyCoordinator();
            newJoiner.UserId = 60001;
            newJoiner.UserPrincipalName = "noor.haddad@fabrikam.com";
            newJoiner.EmailDomain = "fabrikam.com";

            var analysis = new CopilotAdoptionAnalysis
            {
                CoworkSignals = new List<CoworkReadinessSignalRow> { assessed, newJoiner },
                LicensedUsers = new List<LicensedUserAdoptionRow>
                {
                    new LicensedUserAdoptionRow
                    {
                        UserId = 1,
                        UserPrincipalName = "aisha.rahman@contoso.com",
                        EmailDomain = "contoso.com",
                        AdoptionScore = 70,
                    },
                },
                LicensedUsersCapped = true,
            };

            if (withAColleagueCoworkNeverReached)
            {
                analysis.LicensedUsers.Add(new LicensedUserAdoptionRow
                {
                    UserId = 2,
                    UserPrincipalName = "grace.okafor@fabrikam.com",
                    EmailDomain = "fabrikam.com",
                    AdoptionScore = 40,
                });
            }

            return analysis;
        }

        #endregion
    }
}
