using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the licence estimate: the time Microsoft 365 Copilot could give back to the people
    /// recommended for a licence.
    ///
    /// This is the figure a licence purchase is justified with, so it has to be the right people - the
    /// recommended candidates, and only them - and the right arithmetic, which the portal recomputes in
    /// the browser from the same published inputs. The golden figure here has a TypeScript twin in
    /// <c>coworkTimeSaved.test.ts</c>; change either and one of them fails.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionLicenceEstimateTests
    {
        private static CopilotAdoptionOptions Options()
        {
            return CopilotAdoptionOptions.Default;
        }

        /// <summary>Recurrent Copilot Chat use without a licence: recommended on proven demand.</summary>
        private static LicenceOpportunityRow ProvenDemand(string upn = "chat.user@contoso.com")
        {
            return CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = upn,
                UnlicensedCopilotInteractions = 30,
                UnlicensedCopilotActiveDays = 6,
                TeamsMessages = 20,
                TeamsMeetings = 4,
                EmailsSent = 20,
                EmailsRead = 40,
                FilesViewedOrEdited = 10,
            }, Options());
        }

        /// <summary>No Copilot use, but a heavy workload: recommended on inference.</summary>
        private static LicenceOpportunityRow WorkloadInferred(string upn = "busy@contoso.com", long chatInteractions = 0)
        {
            return CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = upn,
                UnlicensedCopilotInteractions = chatInteractions,
                UnlicensedCopilotActiveDays = chatInteractions > 0 ? 1 : 0,
                TeamsMessages = 80,
                TeamsMeetings = 6,
                EmailsSent = 40,
                EmailsRead = 60,
                FilesViewedOrEdited = 40,
            }, Options());
        }

        /// <summary>Light activity and at most a single brush with Copilot Chat: not recommended.</summary>
        private static LicenceOpportunityRow NotRecommended(string upn = "quiet@contoso.com", long chatInteractions = 0)
        {
            return CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = upn,
                UnlicensedCopilotInteractions = chatInteractions,
                UnlicensedCopilotActiveDays = chatInteractions > 0 ? 1 : 0,
                TeamsMessages = 5,
                TeamsMeetings = 1,
                EmailsSent = 2,
                EmailsRead = 4,
                FilesViewedOrEdited = 1,
            }, Options());
        }

        private static CopilotAdoptionAnalysis AnalysisWith(params LicenceOpportunityRow[] opportunities)
        {
            var analysis = new CopilotAdoptionAnalysis { Opportunities = opportunities.ToList() };
            analysis.Summary.DataSources.M365UsageReportsAvailable = true;
            return analysis;
        }

        #region The arithmetic

        /// <summary>
        /// The golden figure shared with the portal's <c>coworkTimeSaved.test.ts</c> ("matches the server
        /// to the hour"). The browser recomputes these hours from the same published inputs whenever a
        /// reader changes an assumption, so the two implementations are pinned to one answer.
        /// </summary>
        [TestMethod]
        public void Estimate_MatchesThePortalsGoldenFigure()
        {
            // 1,234 x 5 + 5,678 x 0.5 + 910 x 1 = 9,919 minutes = 165.3 hours; x 50% = 82.7.
            var estimate = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, Options());

            Assert.AreEqual(165d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(83d, estimate.HoursPerMonthLow);
            CollectionAssert.AreEqual(
                new[] { 103d, 47d, 15d },
                CopilotAdoptionScoring.LicenceHoursByActivity(estimate, Options()));
        }

        [TestMethod]
        public void Estimate_RestatesPerActiveDayVolumesAsAWorkingMonth()
        {
            // Both the opportunity query and the Cowork query publish per-active-day averages. 4 meetings,
            // 60 emails and 10 documents a day over the 20 working days in a 28-day month:
            // 80 x 5 + 1,200 x 0.5 + 200 x 1 = 1,200 minutes = 20 hours.
            var candidate = CopilotAdoptionScoring.ScoreOpportunity(new UnlicensedUserSignalRow
            {
                UserPrincipalName = "a@contoso.com",
                TeamsMeetings = 4,
                EmailsSent = 20,
                EmailsRead = 40,
                FilesViewedOrEdited = 10,
            }, Options());

            var estimate = CopilotAdoptionScoring.EstimateLicenceValue(new[] { candidate }, Options());

            Assert.AreEqual(80d, estimate.AddressableMeetings);
            Assert.AreEqual(1200d, estimate.AddressableMailThreads);
            Assert.AreEqual(200d, estimate.AddressableDocuments);
            Assert.AreEqual(20d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(10d, estimate.HoursPerMonthLow);
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
            options.CopilotMinutesSavedPerMeeting = 120;
            options.CopilotMinutesSavedPerMailThread = 0;
            options.CopilotMinutesSavedPerDocument = 0;

            // 1.4 meetings publishes as 1. From the unrounded 1.4 the model would say 2.8 -> 3 hours; from
            // the published 1 it says 2. The page can only ever see the 1.
            var estimate = CopilotAdoptionScoring.ModelLicenceValue(1, 1.4, 0, 0, options);

            Assert.AreEqual(1d, estimate.AddressableMeetings);
            Assert.AreEqual(2d, estimate.HoursPerMonthHigh,
                "Hours must be derived from the volumes the estimate publishes, which is all the portal has.");
        }

        [TestMethod]
        public void Estimate_ByActivity_AddsUpToTheHighFigure()
        {
            // Rounding each activity on its own lets the parts miss the total by an hour, which a reader
            // checking the sum takes as an arithmetic error in the model.
            var estimate = CopilotAdoptionScoring.ModelLicenceValue(3, 7, 7, 7, Options());
            var parts = CopilotAdoptionScoring.LicenceHoursByActivity(estimate, Options());

            Assert.AreEqual(3, parts.Length);
            Assert.AreEqual(estimate.HoursPerMonthHigh, parts.Sum(), 1e-9,
                "Meetings, email and documents must add up to exactly the estimate's high end.");
            Assert.IsTrue(parts.All(p => p >= 0 && p == Math.Floor(p)), "Each part is a whole number of hours.");
        }

        [TestMethod]
        public void Estimate_IsEmpty_ForNoCandidates()
        {
            var estimate = CopilotAdoptionScoring.EstimateLicenceValue(new List<LicenceOpportunityRow>(), Options());

            Assert.AreEqual(0, estimate.CohortUsers);
            Assert.AreEqual(0d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(0, estimate.Assumptions.Count, "Nobody to model means nothing to caveat either.");
        }

        [TestMethod]
        public void Estimate_AlwaysProducesARange_AndKeepsItTheRightWayRound()
        {
            var estimate = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, Options());
            Assert.IsTrue(estimate.HoursPerMonthLow < estimate.HoursPerMonthHigh,
                "A point estimate invites a precision this model does not have.");

            var misconfigured = Options().Clone();
            misconfigured.CoworkEstimateLowerBoundRatio = 5;
            var backwards = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, misconfigured);
            Assert.IsTrue(backwards.HoursPerMonthLow <= backwards.HoursPerMonthHigh);
        }

        [TestMethod]
        public void Estimate_OwnsTheCopilotMinutes_AndNothingOfCoworks()
        {
            var options = Options().Clone();
            options.CoworkMinutesSavedPerTask = 240;
            options.CoworkAssumedTasksPerPersonPerMonth = 200;

            var estimate = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, options);

            Assert.AreEqual(165d, estimate.HoursPerMonthHigh, "Cowork's figures must never move the licence estimate.");
            Assert.IsFalse(estimate.Assumptions.Any(a => a.IndexOf("Cowork", StringComparison.Ordinal) >= 0),
                "Nothing about Cowork belongs in the case for a Copilot licence.");
        }

        #endregion

        #region Its caveats

        [TestMethod]
        public void Estimate_NeverRendersWithoutItsAssumptions()
        {
            var estimate = CopilotAdoptionScoring.ModelLicenceValue(4, 200, 1000, 400, Options());

            Assert.IsTrue(estimate.IsModelled);
            Assert.IsTrue(estimate.Assumptions.Any(a => a.StartsWith("Assumes Microsoft 365 Copilot saves 5 minutes per meeting, 0.5 per email and 1 per document", StringComparison.Ordinal)),
                "The per-item minutes are Copilot's evidence and must be stated with the figure.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("for 4 recommended licence candidates, restated over 20 working days a month", StringComparison.Ordinal) >= 0));
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("NOT measured", StringComparison.Ordinal) >= 0),
                "The caveat that time saved is not measured must be stated explicitly.");
            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("already using Copilot Chat", StringComparison.Ordinal) >= 0),
                "Candidates already using Copilot Chat may be realising part of it today, and the figure says so.");
        }

        [TestMethod]
        public void Estimate_NeverProducesAMonetaryFigure()
        {
            // Assert on the serialised FIELD NAMES, not the whole payload: the assumption prose explains
            // why no money is shown, so a substring search over the JSON would match its own disclaimer.
            var estimate = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, Options());
            var fields = JObject.FromObject(estimate).Properties().Select(p => p.Name).ToList();

            foreach (var banned in new[] { "currency", "cost", "price", "spend", "value" })
            {
                Assert.IsFalse(fields.Any(n => n.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0),
                    $"No monetary field may be serialised. Found: {string.Join(", ", fields)}");
            }

            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("No monetary value is shown", StringComparison.Ordinal) >= 0),
                "The absence of a monetary figure must be explained rather than silently omitted.");
        }

        [TestMethod]
        public void Estimate_SaysItIsAFloor_OnlyWhenTheCandidateListWasCapped()
        {
            var capped = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, Options(), candidatesCapped: true);
            var complete = CopilotAdoptionScoring.ModelLicenceValue(10, 1234, 5678, 910, Options());

            Assert.IsTrue(capped.CandidatesCapped);
            Assert.IsTrue(capped.Assumptions.Any(a => a.IndexOf($"{Options().MaxOpportunityCandidates:N0}-candidate limit", StringComparison.Ordinal) >= 0),
                "A capped list must say its figure is a floor, naming the limit.");
            Assert.IsFalse(complete.CandidatesCapped);
            Assert.IsFalse(complete.Assumptions.Any(a => a.IndexOf("candidate limit", StringComparison.Ordinal) >= 0));
        }

        #endregion

        #region Who it covers

        [TestMethod]
        public void Summary_ModelsTheRecommendedCandidates_AndOnlyThem()
        {
            var analysis = AnalysisWith(ProvenDemand(), WorkloadInferred(), NotRecommended());

            new CopilotAdoptionService().FinaliseSummary(analysis);
            var summary = analysis.Summary;

            Assert.AreEqual(2, summary.RecommendedForLicence);
            Assert.AreEqual(2, summary.LicenceOpportunityEstimate.CohortUsers,
                "The estimate sizes the purchase the list recommends - nobody the list does not recommend.");
            Assert.IsTrue(summary.LicenceOpportunityEstimate.HoursPerMonthHigh > 0);
            Assert.IsTrue(summary.LicenceOpportunityEstimate.IsModelled);
        }

        /// <summary>
        /// The figure beside the headline is the people the list's "Already using Copilot" filter shows
        /// among the recommended candidates - so a reader can bring up exactly the people it counts.
        /// </summary>
        [TestMethod]
        public void Summary_ChatUsersAreExactlyTheRecommendedPeopleTheAlreadyUsingFilterShows()
        {
            var opportunities = new[]
            {
                ProvenDemand("proven@contoso.com"),
                WorkloadInferred("busy@contoso.com"),
                // Recommended on workload, with a single day of Chat use: already using Copilot Chat.
                WorkloadInferred("trying@contoso.com", chatInteractions: 2),
                // Used Chat once but not recommended: in neither figure.
                NotRecommended("once@contoso.com", chatInteractions: 1),
            };
            var analysis = AnalysisWith(opportunities);

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var filtered = CopilotAdoptionExports.Apply(
                opportunities,
                new LicenceOpportunityQuery { RecommendedOnly = true, ExistingCopilotUsersOnly = true });

            Assert.AreEqual(2, filtered.Count);
            Assert.AreEqual(filtered.Count, analysis.Summary.LicenceChatUsersEstimate.CohortUsers);
            Assert.AreEqual(3, analysis.Summary.LicenceOpportunityEstimate.CohortUsers);
            Assert.IsTrue(analysis.Summary.LicenceChatUsersEstimate.HoursPerMonthHigh
                          < analysis.Summary.LicenceOpportunityEstimate.HoursPerMonthHigh,
                "A subset of the recommended candidates can never model more time than all of them.");
        }

        [TestMethod]
        public void Summary_ModelsNothing_WhenTheUsageReportsAreUnavailable()
        {
            // Without the Microsoft 365 usage reports every candidate has zero meetings, emails and
            // documents, and "0 hours" would read as a finding about them rather than a missing import.
            var analysis = AnalysisWith(ProvenDemand(), WorkloadInferred());
            analysis.Summary.DataSources.M365UsageReportsAvailable = false;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(2, analysis.Summary.RecommendedForLicence, "The candidates are still recommended.");
            Assert.AreEqual(0, analysis.Summary.LicenceOpportunityEstimate.CohortUsers);
            Assert.AreEqual(0, analysis.Summary.LicenceChatUsersEstimate.CohortUsers);
        }

        [TestMethod]
        public void Summary_CarriesTheCapIntoTheEstimate_AndIntoAnEmailDomainScope()
        {
            var analysis = AnalysisWith(
                ProvenDemand("proven@contoso.com"),
                WorkloadInferred("busy@fabrikam.com"),
                WorkloadInferred("heavy@fabrikam.com", chatInteractions: 4));
            analysis.OpportunitiesCapped = true;

            var service = new CopilotAdoptionService();
            service.FinaliseSummary(analysis);
            Assert.IsTrue(analysis.Summary.LicenceOpportunityEstimate.CandidatesCapped);

            // The cap was applied to the tenant-wide ranking, so a narrowed view is just as likely to be
            // missing candidates - even though it holds far fewer rows than the cap.
            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"), service.FinaliseSummary);

            Assert.AreEqual(2, scoped.Summary.LicenceOpportunityEstimate.CohortUsers,
                "A domain-scoped report must model that domain's candidates only.");
            Assert.AreEqual(1, scoped.Summary.LicenceChatUsersEstimate.CohortUsers);
            Assert.IsTrue(scoped.Summary.LicenceOpportunityEstimate.CandidatesCapped);
            Assert.AreEqual(3, analysis.Summary.LicenceOpportunityEstimate.CohortUsers,
                "Scoping must never write into the cached tenant-wide analysis.");
        }

        #endregion

        #region Serialisation

        [TestMethod]
        public void Estimate_IsSerialisedInCamelCaseForTheClient()
        {
            var json = JObject.FromObject(CopilotAdoptionScoring.ModelLicenceValue(2, 10, 10, 10, Options()));

            foreach (var expected in new[]
            {
                "isModelled", "cohortUsers", "addressableMeetings", "addressableMailThreads",
                "addressableDocuments", "hoursPerMonthLow", "hoursPerMonthHigh", "candidatesCapped", "assumptions",
            })
            {
                Assert.IsNotNull(json.Property(expected), $"LicenceValueEstimate must serialise '{expected}'.");
            }

            Assert.IsFalse(json.Properties().Any(p => char.IsUpper(p.Name[0])),
                "Every serialised name must be camelCase; a PascalCase slip renders the headline blank.");
        }

        [TestMethod]
        public void Summary_SerialisesBothLicenceEstimates()
        {
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new CopilotAdoptionSummary()));

            Assert.IsNotNull(json.Property("licenceOpportunityEstimate"));
            Assert.IsNotNull(json.Property("licenceChatUsersEstimate"));
        }

        #endregion
    }
}
