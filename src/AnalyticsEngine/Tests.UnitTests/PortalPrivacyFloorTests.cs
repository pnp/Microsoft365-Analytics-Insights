extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models;
using Common.Entities.ActivityAnalysis;
using Common.Entities.CopilotAdoption;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.LicenceActivity;
using Common.Entities.PromptCategories;
using Common.Entities.SpoWebActivity;
using Common.Entities.TeamsExplorer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Tests.UnitTests
{
    /// <summary>
    /// The privacy floor (#844): a reader without See PII is never served a figure about a group small enough to single
    /// people out - fewer than five people. Every Web API action such a reader can call is classified here, as
    /// <see cref="PortalPermissionTests"/> records who may call each one.
    /// </summary>
    /// <remarks>
    /// <para>Each action is one of: <b>no grouped figures</b> (status, settings, names, or totals over the reader's whole
    /// scope, which the administrator's filter never lets be 1-4 people for this reader); <b>floored</b>, naming the code
    /// that applies the floor and the tests that prove it; or a <b>known gap</b>, grouped figures served without a floor.
    /// A narrowing parameter the action refuses without See PII is listed with the test that proves the refusal.</para>
    /// <para>A known gap is reported in this test's output and does not fail it: it is a decision for the maintainer, kept
    /// visible here until it is fixed and the entry is reclassified as floored.</para>
    /// <para>Actions that need Administration or See PII are out of scope: a reader without See PII cannot call them, and
    /// with roles switched off (<c>EnforcePortalRoles=false</c>) every reader holds See PII.</para>
    /// </remarks>
    [TestClass]
    public class PortalPrivacyFloorTests
    {
        private const string AdoptionFurtherFindings =
            "#844: found while closing the department breakdowns, and left for a decision. The side-by-side table's agent "
            + "figures are floored on a department's active users, not on its agent users, so a department where one to four "
            + "people used an agent publishes those people's agent interactions, distinct agents and interactions per agent; "
            + "and the tenant's count of departments with an agent user counts departments whose agent figures are withheld, "
            + "so with the rows shown it says whether a withheld one has any. The agent estate's interactions by department "
            + "(agents.usageByDepartment) leave out departments of fewer than MinSeatsPerSegment users beside the estate's "
            + "total interactions, which less them are the departments left out. Seat holders' time saved by engagement band "
            + "has no floor, and the usage-concentration bands of a tenant with fewer than 45 active users put one to four "
            + "people in a band: both then publish figures for those few.";

        private const string CreditWindows =
            "#844 (S3-3): the withholding holds within one response, not across the 7-, 28-, 90- and 180-day windows a reader "
            + "can request, which all end today.";

        private const string InvestmentSmallViews =
            "#844: a view of one to four analysed seat holders still shows evidence and value figures, and the reclaim and "
            + "review-idle actions show the seat cost of one to four people.";

        private const string SpendFewConsumers =
            "#844: the tenant-wide pool balances and the per-agent ledger in a tenant with only a few consumers; #866: the Studio "
            + "note's unmatchedCredits (charges no user matched) is not withheld.";

        private const string LicenceActivityAddedOrAcrossBreakdowns =
            "#844: the licence table is checked for every figure taken away from everyone or from a licence. Not covered: "
            + "combinations that add a figure back - for example two licences shown that between them hold everyone in view, "
            + "added together less everyone, are the people who hold both, which can be 1-4 - and rows of different breakdowns "
            + "set against each other: a department or country row and a licence row, or a department row and a country row, "
            + "when one's people all fall inside the other's, differ by the people in one but not the other.";

        /// <summary>
        /// Every Web API action a reader without See PII can call, and how it stands against the floor. Adding such an
        /// action without classifying it fails <see cref="EveryActionAReaderWithoutSeePiiCanCall_IsClassifiedForThePrivacyFloor"/>.
        /// </summary>
        private static readonly Dictionary<string, FloorDecision> Registry = new Dictionary<string, FloorDecision>(StringComparer.Ordinal)
        {
            ["ActivityAnalysisAPIController.Availability"] = NoGroupedFigures(
                "Whether the weekly profiling tables are installed, the weeks they cover and the metrics compiled."),
            ["ActivityAnalysisAPIController.Report"] = Floored(
                    "Companies and departments of fewer than 5 people are folded into one unnamed row, the smallest named groups "
                    + "joining it when the fold is itself 1-4 people; when 1-4 people match, every figure but the counts is "
                    + "suppressed; no range bounds, each being one person's total.")
                .By(typeof(ActivityAnalysisEvaluation), nameof(ActivityAnalysisEvaluation.BuildReport))
                .ProvenBy(
                    Test<ActivityAnalysisApiTests>(nameof(ActivityAnalysisApiTests.AReaderWithoutSeePii_GetsSmallGroupsFolded_AndAHandfulOfPeopleSuppressed)),
                    Test<ActivityAnalysisReadModelTests>(nameof(ActivityAnalysisReadModelTests.ReaderWithoutSeePii_NeverGetsAFoldedRowOfAHandfulOfPeople_TheSmallestNamedGroupsJoinIt)))
                .Refusing("userFilter, licences, ranges",
                    Test<ActivityAnalysisApiTests>(nameof(ActivityAnalysisApiTests.ThePeopleFilter_NeedsSeePii)),
                    Test<ActivityAnalysisApiTests>(nameof(ActivityAnalysisApiTests.LicenceAndActivityFilters_NeedSeePii))),

            ["AgentCostsAPIController.Availability"] = NoGroupedFigures(
                "Whether the agent-cost imports are switched on, when they last ran, their last errors and which dimensions hold data."),
            ["AgentCostsAPIController.Summary"] = Floored(
                    "Billed and non-billed credits, counts and the busiest slice's people add up only the slices Microsoft reports "
                    + "5 or more people on; a slice of fewer, or with no count, is in no figure, so no total less another gives one "
                    + "back. Narrowing (agentId, environmentId, harness, feature, search) to slices that are all small gets every "
                    + "figure withheld (null, withheld: true), never 0.")
                .By(typeof(AgentCostsAPIController), nameof(AgentCostsAPIController.ForReader))
                .By(typeof(Common.Entities.AgentCosts.SqlAgentCostReportStore), nameof(Common.Entities.AgentCosts.SqlAgentCostReportStore.Floored))
                .ProvenBy(
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_IsGivenOnlySlicesOfFiveOrMorePeople_WithSmallAgentsAndEnvironmentsCombinedIntoOneRowWithoutFigures)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_NarrowingOrSearchingToAGroupOfFewerThanFivePeople_GetsItsFiguresWithheld_NeverZero)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.ForAReaderWithoutSeePii_NoTotalLessTheFiguresShown_GivesBackAWithheldSlice)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithSeePii_IsGivenEverySlice_AsBefore)))
                .WithKnownGap("#844: the capacity tiles are the tenant's consumed credits, so in a period that matches theirs, they less the "
                    + "figures shown give the withheld slices together with any consumption Microsoft attributes to nobody: one to four "
                    + "people's credits in a tenant whose only small slices are theirs (few consumers)."),
            ["AgentCostsAPIController.Trend"] = Floored(
                    "Daily credits from slices of 5 or more people only; a day whose slices are all small is left out, never drawn "
                    + "as 0, and narrowing is floored as the summary is.")
                .By(typeof(AgentCostsAPIController), nameof(AgentCostsAPIController.ForReader))
                .By(typeof(Common.Entities.AgentCosts.SqlAgentCostReportStore), nameof(Common.Entities.AgentCosts.SqlAgentCostReportStore.Floored))
                .ProvenBy(
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_IsGivenOnlySlicesOfFiveOrMorePeople_WithSmallAgentsAndEnvironmentsCombinedIntoOneRowWithoutFigures)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_NarrowingOrSearchingToAGroupOfFewerThanFivePeople_GetsItsFiguresWithheld_NeverZero)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.ForAReaderWithoutSeePii_NoTotalLessTheFiguresShown_GivesBackAWithheldSlice)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithSeePii_IsGivenEverySlice_AsBefore))),
            ["AgentCostsAPIController.Breakdown"] = Floored(
                    "Rows per agent, environment, harness or feature from slices of 5 or more people only; the groups with no such "
                    + "slice - a personal agent, a developer's own environment - are one last row with their number and no key, "
                    + "name or figures, and are in no other figure.")
                .By(typeof(AgentCostsAPIController), nameof(AgentCostsAPIController.ForReader))
                .By(typeof(Common.Entities.AgentCosts.SqlAgentCostReportStore), nameof(Common.Entities.AgentCosts.SqlAgentCostReportStore.Floored))
                .ProvenBy(
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_IsGivenOnlySlicesOfFiveOrMorePeople_WithSmallAgentsAndEnvironmentsCombinedIntoOneRowWithoutFigures)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_NarrowingOrSearchingToAGroupOfFewerThanFivePeople_GetsItsFiguresWithheld_NeverZero)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.ForAReaderWithoutSeePii_NoTotalLessTheFiguresShown_GivesBackAWithheldSlice)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithSeePii_IsGivenEverySlice_AsBefore))),
            ["AgentCostsAPIController.Detail"] = Floored(
                    "Only the billed slices of 5 or more people are listed, counted and exported (the page's CSV is built from "
                    + "these rows); smallSlicesWithheld says others were left out, never how many.")
                .By(typeof(AgentCostsAPIController), nameof(AgentCostsAPIController.ForReader))
                .By(typeof(Common.Entities.AgentCosts.SqlAgentCostReportStore), nameof(Common.Entities.AgentCosts.SqlAgentCostReportStore.Floored))
                .ProvenBy(
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_IsGivenOnlySlicesOfFiveOrMorePeople_WithSmallAgentsAndEnvironmentsCombinedIntoOneRowWithoutFigures)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithoutSeePii_NarrowingOrSearchingToAGroupOfFewerThanFivePeople_GetsItsFiguresWithheld_NeverZero)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.ForAReaderWithoutSeePii_NoTotalLessTheFiguresShown_GivesBackAWithheldSlice)),
                    Test<AgentCostReportConsistencyTests>(nameof(AgentCostReportConsistencyTests.AReaderWithSeePii_IsGivenEverySlice_AsBefore))),
            ["AgentCostsAPIController.Azure"] = NoGroupedFigures(
                "Azure Cost Management spend by meter, service, category, resource, resource group, subscription or tag: "
                + "resource-scoped billing with no user identity on any row."),
            ["AgentCostsAPIController.Filters"] = NoGroupedFigures(
                "The agents, environments, harnesses and features that occur in the window, for the pickers: names, no figures. "
                + "Without See PII, only values with a slice of 5 or more people, so no personal agent's name is offered."),

            ["CallRecordWebhookController.Post"] = NoGroupedFigures(
                "Microsoft Graph's call-record change notifications: echoes the validation token or accepts a notification."),

            ["CopilotAdoptionAPIController.Availability"] = NoGroupedFigures(
                "Which of the imports the tool needs are switched on, and what is missing."),
            ["CopilotAdoptionAPIController.Summary"] = Floored(
                    "Every breakdown leaves out groups of fewer than MinSeatsPerSegment (5) people, for every reader; a reader "
                    + "without See PII gets a copy without the manager roll-up or the heaviest agent users, and without the idle "
                    + "count of a Copilot seat 1-4 people hold or of the smallest seat that, with the reclaimable total, would give "
                    + "it back. In the copy, the department, country and accountability breakdowns, the side-by-side table's "
                    + "unlicensed side and agent figures, and the agent builders (each count of 1-4 withheld first) leave out "
                    + "their smallest listed group from every view whenever they leave a group out; each population on an "
                    + "email-domain row - seat holders, unlicensed Chat users, licence candidates - is floored on its own; and "
                    + "rows published beside a total (the domains' columns, the departments' candidate counts in the league "
                    + "table and chart, the departments' manager figures) are withheld, smallest first, until the total less "
                    + "the rows shown can no longer give back 1-4 people.")
                .By(typeof(CopilotAdoptionService), nameof(CopilotAdoptionService.FinaliseSummary))
                .By(typeof(CopilotAdoptionSummary), nameof(CopilotAdoptionSummary.WithoutIndividualData))
                .By(typeof(LicenceTypeClassification), nameof(LicenceTypeClassification.WithoutSmallSeatIdleCounts))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.WithholdSmallestListedGroups))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.EmailDomainsWithoutSmallPopulations))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.CandidateCountsWithoutComplements))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.ManagerDepartmentsWithoutComplements))
                .ProvenBy(
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.DepartmentBreakdowns_LeaveOutDepartmentsWithFewerPeopleThanTheMinimum)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.AccountabilityRollup_SuppressesSmallLeaderGroupsLikeSegments)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSummary_WithoutSeePii_LosesTheManagerRollup_AndTheCachedAnalysisKeepsIt)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.SeatIdleCounts_OfACopilotSeatHeldByOneToFourPeople_AreWithheldWithoutSeePii_AndShownWithIt)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.SeatIdleWithholding_WithholdsTheSmallestSeatThatWouldGiveASmallOneBack_AndNothingWhenNoSeatIsSmall)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSeatIdle_OfACopilotSeatHeldByOneToFourPeople_IsWithheldWithoutSeePii_OnEveryEndpoint)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.DepartmentBreakdowns_WithoutSeePii_LeaveOutTheSmallestListedDepartmentFromEveryView_WhenTheyLeaveOneOut)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.CountryAndOfficeBreakdowns_WithoutSeePii_LeaveOutTheirSmallestListedGroup_WhenTheyLeaveOneOut)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.UnlicensedSide_WithoutSeePii_LeavesOutTheSmallestListedDepartment_InTheTableAndTheUseByDepartmentChart)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.Breakdowns_WithoutSeePii_NeverLetATotalLessTheGroupsShownIsolateASmallGroup_AndEveryViewAgrees)),
                    Test<CopilotAdoptionAgentBreadthReachTests>(nameof(CopilotAdoptionAgentBreadthReachTests.Builders_WithoutSeePii_AreWithheldForOneToFour_AndForTheSmallestShown_WhenTheTenantsWouldGiveOneBack)),
                    Test<CopilotAdoptionAgentBreadthReachTests>(nameof(CopilotAdoptionAgentBreadthReachTests.AgentFigures_WithoutSeePii_TheTenantsActiveUsersLessTheDepartmentsShown_NeverGiveBackAWithheldOne)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionDepartmentsLeftOut_AreComplementedWithoutSeePii_OnTheSummaryAndTheWorkbook_AndUnchangedWithIt)),
                    Test<CopilotAdoptionEmailDomainTests>(nameof(CopilotAdoptionEmailDomainTests.WithoutSeePii_ADomainOf20SeatHoldersAnd3ChatUsers_WithholdsTheChatCount_AndKeepsTheSeatHolders)),
                    Test<CopilotAdoptionEmailDomainTests>(nameof(CopilotAdoptionEmailDomainTests.WithoutSeePii_NoDomainTotalLessTheRowsShown_GivesBackAFewPeople_AndTheCachedRowsAreNeverEdited)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.DepartmentCandidateCounts_WithoutSeePii_NeverLetTheTotalLessTheCountsShownGiveBackAWithheldDepartment)),
                    Test<CopilotAdoptionManagerModellingTests>(nameof(CopilotAdoptionManagerModellingTests.WithoutSeePii_TheWholePopulationLessTheDepartmentsShown_NeverGivesBackAWithheldDepartment_AndWithSeePiiNothingChanges)),
                    Test<CopilotAdoptionManagerModellingTests>(nameof(CopilotAdoptionManagerModellingTests.WithoutSeePii_TheManagerOfADepartmentTooSmallToList_IsNotGivenBackByTheWholeLessTheDepartmentsShown)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionRowsBesideTotals_AreFlooredWithoutSeePii_OnTheSummaryAndTheWorkbook_AndUnchangedWithIt)))
                .Refusing("emailDomain, userFilter, seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_NarrowedAggregates_RequireSeePii)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii)))
                .WithKnownGap(AdoptionFurtherFindings),
            ["CopilotAdoptionAPIController.Rebuild"] = NoGroupedFigures(
                    "Starts or joins a rebuild of the analysis, and answers with its generation id and progress only.")
                .ProvenBy(Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionRebuild_RejectsInvalidTokens_AndStaleTokensReturnOnlyGenerationMetadata)))
                .Refusing("emailDomain, userFilter, seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionRebuild_NarrowedRequestsRequireSeePiiBeforeStartingWork))),
            ["CopilotAdoptionAPIController.LicenceTypes"] = Floored(
                    "Every licence type with its assigned count, inventory that Licence activity keeps too. A reader without See "
                    + "PII is given the summary's list: without the idle count of a Copilot seat 1-4 people hold, or of the "
                    + "smallest seat that, with the reclaimable total, would give it back.")
                .By(typeof(CopilotAdoptionSummary), nameof(CopilotAdoptionSummary.WithoutIndividualData))
                .By(typeof(LicenceTypeClassification), nameof(LicenceTypeClassification.WithoutSmallSeatIdleCounts))
                .ProvenBy(
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.SeatIdleCounts_OfACopilotSeatHeldByOneToFourPeople_AreWithheldWithoutSeePii_AndShownWithIt)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.SeatIdleWithholding_WithholdsTheSmallestSeatThatWouldGiveASmallOneBack_AndNothingWhenNoSeatIsSmall)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSeatIdle_OfACopilotSeatHeldByOneToFourPeople_IsWithheldWithoutSeePii_OnEveryEndpoint)))
                .Refusing("seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii))),
            ["CopilotAdoptionAPIController.Sql"] = NoGroupedFigures(
                    "The SQL text behind the figures, for the query popover.")
                .Refusing("seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii))),
            ["CopilotAdoptionAPIController.Filters"] = NoGroupedFigures(
                    "The email domains, departments and countries present, as value lists without counts, and the fixed band and "
                    + "Cowork tier lists. #844 asks whether to leave out values that fewer than five people hold.")
                .Refusing("seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii))),
            ["CopilotAdoptionAPIController.ExportWorkbook"] = Floored(
                    "The summary's figures, floored as on screen, without the per-person sheets or named lists; under the "
                    + "administrator's filter the tenant sections it withholds are written blank, never 0. The licence "
                    + "classification says why a withheld idle count is not shown, never 0. The department, country and "
                    + "accountability tables, the side-by-side table, the email domains, the habit table's candidate counts, the "
                    + "candidate chart and the department manager table are the summary's copy for this reader: a withheld "
                    + "group is left out of every sheet the screen leaves it out of, a withheld cell is blank, never 0, and each "
                    + "sheet says why.")
                .By(typeof(CopilotAdoptionService), nameof(CopilotAdoptionService.FinaliseSummary))
                .By(typeof(CopilotAdoptionWorkbook), nameof(CopilotAdoptionWorkbook.Build))
                .By(typeof(LicenceTypeClassification), nameof(LicenceTypeClassification.WithoutSmallSeatIdleCounts))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.WithholdSmallestListedGroups))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.EmailDomainsWithoutSmallPopulations))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.CandidateCountsWithoutComplements))
                .By(typeof(CopilotAdoptionSmallGroups), nameof(CopilotAdoptionSmallGroups.ManagerDepartmentsWithoutComplements))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionWorkbook_WithoutSeePii_NamesNobody_AndWithItStillDoes)),
                    Test<CopilotAdoptionWorkbookTests>(nameof(CopilotAdoptionWorkbookTests.SnapshotFacts_WriteTheSectionsTheGlobalFilterWithholdsBlank_NeverZero)),
                    Test<CopilotAdoptionWorkbookTests>(nameof(CopilotAdoptionWorkbookTests.LicenceClassification_WithoutSeePii_SaysWhyASmallSeatsIdleCountIsNotShown_NeverZero)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSeatIdle_OfACopilotSeatHeldByOneToFourPeople_IsWithheldWithoutSeePii_OnEveryEndpoint)),
                    Test<CopilotAdoptionWorkbookTests>(nameof(CopilotAdoptionWorkbookTests.EmailDomainSheet_WithoutSeePii_LeavesAFewPeoplesFiguresBlank_AsTheScreenDoes_AndWithItWritesThem)),
                    Test<CopilotAdoptionWorkbookTests>(nameof(CopilotAdoptionWorkbookTests.DepartmentSheet_WithoutSeePii_LeavesOutTheDepartmentTheScreenDoes_AndSaysWhy_AndWithItWritesEveryOne)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionRowsBesideTotals_AreFlooredWithoutSeePii_OnTheSummaryAndTheWorkbook_AndUnchangedWithIt)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionDepartmentsLeftOut_AreComplementedWithoutSeePii_OnTheSummaryAndTheWorkbook_AndUnchangedWithIt)))
                .Refusing("emailDomain, userFilter, seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_NarrowedAggregates_RequireSeePii)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii)))
                .WithKnownGap(AdoptionFurtherFindings),

            ["CopilotCostHealthAPIController.Get"] = NoGroupedFigures(
                "Connection outcomes and timestamps of the two Copilot cost sources; no account or tenant identifiers."),

            ["CopilotInvestmentAPIController.Availability"] = NoGroupedFigures(
                "Whether the adoption evidence, the Copilot Studio import and the credit collection are available."),
            ["CopilotInvestmentAPIController.Summary"] = Floored(
                    "A copy without the people, with every group, contributor population, quadrant, concentration curve, balance "
                    + "and Studio charge of fewer than 5 people withheld, together with enough others that a total less the parts "
                    + "shown cannot give one back.")
                .By(typeof(CopilotInvestmentSummary), nameof(CopilotInvestmentSummary.WithoutIndividualData))
                .By(typeof(CopilotInvestmentWithholding), nameof(CopilotInvestmentWithholding.Apply))
                .ProvenBy(
                    Test<CopilotInvestmentCalculatorTests>(nameof(CopilotInvestmentCalculatorTests.TheCombinedRowOfSmallGroups_ShowsNoFigures_WhenItIsMadeOfFewerPeopleThanTheMinimum)),
                    Test<CopilotInvestmentCalculatorTests>(nameof(CopilotInvestmentCalculatorTests.AGroupWithheldFromAReaderWithoutSeePii_CannotBeWorkedOutFromTheTotalLessTheGroupsShown)),
                    Test<CopilotInvestmentCalculatorTests>(nameof(CopilotInvestmentCalculatorTests.UnmatchedBalancesAndStudioCharges_OfOneToFourAccounts_AreWithheldFromAReaderWithoutSeePii)))
                .Refusing("userFilter, dimension=manager",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(InvestmentSmallViews)
                .WithKnownGap(CreditWindows),
            ["CopilotInvestmentAPIController.Trend"] = Floored(
                    "Balance readings on sample dates; a reading of fewer than 5 consuming people is withheld from a copy.")
                .By(typeof(CreditTrendPoint), nameof(CreditTrendPoint.WithoutSmallSamples))
                .ProvenBy(Test<CopilotSpendServiceTests>(nameof(CopilotSpendServiceTests.TheBalanceTrend_WithholdsAReadingOfTooFewPeople_FromAReaderWithoutSeePii_OnBothReports)))
                .Refusing("userFilter",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(CreditWindows),
            ["CopilotInvestmentAPIController.ExportWorkbook"] = Floored(
                    "The summary's withheld copy without the people sheet; a withheld figure is written as its reason, never as a number.")
                .By(typeof(CopilotInvestmentSummary), nameof(CopilotInvestmentSummary.WithoutIndividualData))
                .By(typeof(CopilotInvestmentWorkbook), nameof(CopilotInvestmentWorkbook.Build))
                .ProvenBy(
                    Test<CopilotInvestmentCalculatorTests>(nameof(CopilotInvestmentCalculatorTests.TheWorkbookWithoutSeePii_SaysWhyWhereThePageWithholds_AndCarriesNoWithheldFigure)),
                    Test<CopilotInvestmentWorkbookLanguageTests>(nameof(CopilotInvestmentWorkbookLanguageTests.ACombinedRowOfTooFewPeople_IsWrittenAsItsReasonAlone_InEitherLanguage)))
                .Refusing("userFilter, dimension=manager",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(InvestmentSmallViews)
                .WithKnownGap(CreditWindows),

            ["CopilotSpendAPIController.Summary"] = Floored(
                    "A copy without the people, with groups, environments, ledger days, source totals, balances and distributions "
                    + "of fewer than 5 people withheld or combined, together with enough others that a total cannot give one back.")
                .By(typeof(CopilotSpendSummary), nameof(CopilotSpendSummary.WithoutIndividualData))
                .ProvenBy(
                    Test<CopilotSpendTests>(nameof(CopilotSpendTests.AReaderWithoutSeePii_GetsAggregatesOnly_OnACopy)),
                    Test<CopilotSpendTests>(nameof(CopilotSpendTests.AGroupWithheldFromAReaderWithoutSeePii_CannotBeWorkedOutFromTheTotalLessTheGroupsShown)),
                    Test<CopilotSpendTests>(nameof(CopilotSpendTests.SmallEnvironments_AreCombinedForAReaderWithoutSeePii_OnACopy)),
                    Test<CopilotSpendTests>(nameof(CopilotSpendTests.ADayOfTooFewPeople_IsWithheldFromAReaderWithoutSeePii_WithADayThatKeepsItFromBeingWorkedOut)))
                .Refusing("userFilter, dimension=manager",
                    Test<CopilotSpendServiceTests>(nameof(CopilotSpendServiceTests.AReaderWithoutSeePii_IsRefusedAManagerGrouping_OnThePageAndInTheWorkbook)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(SpendFewConsumers)
                .WithKnownGap(CreditWindows),
            ["CopilotSpendAPIController.Trend"] = Floored(
                    "Balance readings on sample dates; a reading of fewer than 5 consuming people is withheld from a copy.")
                .By(typeof(CreditTrendPoint), nameof(CreditTrendPoint.WithoutSmallSamples))
                .ProvenBy(Test<CopilotSpendServiceTests>(nameof(CopilotSpendServiceTests.TheBalanceTrend_WithholdsAReadingOfTooFewPeople_FromAReaderWithoutSeePii_OnBothReports)))
                .Refusing("userFilter",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(CreditWindows),
            ["CopilotSpendAPIController.ExportWorkbook"] = Floored(
                    "The summary's withheld copy without the people, top-spender and lowest-spender sheets; a withheld figure is "
                    + "written as its reason.")
                .By(typeof(CopilotSpendSummary), nameof(CopilotSpendSummary.WithoutIndividualData))
                .By(typeof(CopilotSpendWorkbook), nameof(CopilotSpendWorkbook.Build))
                .ProvenBy(
                    Test<CopilotSpendTests>(nameof(CopilotSpendTests.TheWorkbook_IsWrittenInTheReadersLanguage_WithTheSameTypedCells_AndPeopleOnlyWithSeePii)),
                    Test<CopilotSpendTests>(nameof(CopilotSpendTests.TheDistributionSheet_GivesAReaderWithoutSeePiiTheAnonymisedFigures_AndLeavesAWithheldFigureEmptyWithItsReason)))
                .Refusing("userFilter, dimension=manager",
                    Test<CopilotSpendServiceTests>(nameof(CopilotSpendServiceTests.AReaderWithoutSeePii_IsRefusedAManagerGrouping_OnThePageAndInTheWorkbook)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(SpendFewConsumers)
                .WithKnownGap(CreditWindows),

            ["DlpAPIController.Availability"] = NoGroupedFigures(
                "Which DLP sources are switched on, and why not."),
            ["DlpAPIController.Summary"] = Floored(
                    "Agent, policy, sensitivity-label and tenant DLP policy rows, and the policies under a listed agent, that "
                    + "affected fewer than 5 people are left out of the anonymous build; the top-users query is not run.")
                .By(typeof(DlpAPIController), nameof(DlpAPIController.WithoutSmallGroups))
                .ProvenBy(
                    Test<DlpApiSqlIntegrationTests>(nameof(DlpApiSqlIntegrationTests.WithoutSmallGroups_LeavesOutRowsOfFewerThanFivePeople_AndKeepsFiveOrMore)),
                    Test<DlpApiSqlIntegrationTests>(nameof(DlpApiSqlIntegrationTests.SummaryExecutesEveryQueryAndCountsBlockedSeparatelyFromAudited)))
                .WithKnownGap("#866: a row left out is not protected against the KPI totals beside the tables (complementary withholding, #844 S4)."),
            ["DlpAPIController.Governance"] = NoGroupedFigures(
                "Jailbreak and XPIA rates and the labelled share of resources over the reader's whole scope, and interactions per "
                + "Microsoft AI model and system plugin: Microsoft's catalogue, not a group of people."),

            ["GlobalFilterAPIController.Effective"] = NoGroupedFigures(
                    "The administrator's filter as it applies to the reader, how many people it leaves them (a count, no activity) "
                    + "and whether that is too few; sign-in names are withheld without See PII.")
                .ProvenBy(Test<GlobalFilterWebTests>(nameof(GlobalFilterWebTests.Effective_WithholdsSignInNamesFromAReaderWithoutSeePii))),

            ["ImportConfigController.Post"] = NoGroupedFigures(
                "The SharePoint tracker's configuration for the instrumentation key it presents."),

            ["LicenceActivityAPIController.Availability"] = NoGroupedFigures(
                "Whether the user metadata import is switched on, and fixed guidance."),
            ["LicenceActivityAPIController.Overview"] = Floored(
                    "Built with a minimum group of 5: a licence of 1-4 holders keeps its count but withholds its activity, and so "
                    + "does any licence through which everyone holding a licence, or another licence, less licences shown would be "
                    + "1-4 people's activity, decided on the memberships themselves; departments and countries of fewer than 5 are "
                    + "folded, and when 1-4 people are in view every figure but the counts is withheld. Cached apart from a See PII "
                    + "reader's.")
                .By(typeof(LicenceActivityQuery), nameof(LicenceActivityQuery.WithMinimumGroupSize))
                .By(typeof(LicenceActivityReadModel), nameof(LicenceActivityReadModel.BuildOverview))
                .By(typeof(LicenceActivityComplements), nameof(LicenceActivityComplements.Withhold))
                .ProvenBy(
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_DepartmentAndCountryNarrowingIsRefused_AndTheOverviewWithholdsSmallGroups)),
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_ALicenceThatWouldGiveASmallOneBack_IsWithheldOnScreenAndInTheWorkbook_AndSeePiiSeesIt)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_GroupsOfOneToFourAreFoldedOrWithheld_AndAGroupOfFiveIsShown)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_FewerThanFivePeopleWithALicence_WithholdsEveryFigure_ButFiveDoNot)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_ASmallLicence_IsWithheldWithTheSmallestLicenceThatWouldOtherwiseGiveItBack)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_OverlappingLicences_TheLicenceWithheldIsOneThatActuallyHidesTheSmallOne)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_ALicenceNearlyEveryoneHolds_IsWithheldWhenEveryoneLessItIsAHandfulOfPeople)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_ALicenceLessTheLicencesInsideIt_NeverGivesBackASmallOne)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WhenTheSearchRunsOutOfBudget_TheLicencesInsideTheFigureAreWithheld_NeverShownUnchecked)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithSeePii_NoLicenceIsWithheld_AsBefore)))
                .Refusing("departmentId, countryId",
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_DepartmentAndCountryNarrowingIsRefused_AndTheOverviewWithholdsSmallGroups)))
                .WithKnownGap(LicenceActivityAddedOrAcrossBreakdowns),
            ["LicenceActivityAPIController.Export"] = Floored(
                    "The overview the reader was served, refused if it was built with a smaller minimum group, so the workbook "
                    + "folds and withholds what the screen does, and says why each licence is withheld.")
                .By(typeof(LicenceActivityWorkbook), nameof(LicenceActivityWorkbook.Build))
                .ProvenBy(
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_TheScreenAndTheWorkbookFoldTheSameSmallGroups_AndSeePiiSeesThemAll)),
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_ALicenceThatWouldGiveASmallOneBack_IsWithheldOnScreenAndInTheWorkbook_AndSeePiiSeesIt)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_TheWorkbookIsTheSameFoldedFigures)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_TheWorkbookSaysWhyEachLicenceIsWithheld_AsTheScreenDoes)))
                .Refusing("usersId",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(LicenceActivityAddedOrAcrossBreakdowns),

            ["PortalAccessAPIController.Get"] = NoGroupedFigures(
                    "Which permissions the signed-in user holds, and the app roles that grant them.")
                .ProvenBy(Test<PortalPermissionTests>(nameof(PortalPermissionTests.PortalAccess_TellsTheSpaWhatTheUserHolds))),

            ["PromptCategoriesAPIController.Report"] = Floored(
                    "Every taxonomy version, category and week needs at least 10 distinct people after the administrator's filter "
                    + "(HAVING COUNT(DISTINCT user_id) >= 10), for every reader.")
                .By(typeof(PromptCategoryReportSql), nameof(PromptCategoryReportSql.Versions))
                .By(typeof(PromptCategoryReportSql), nameof(PromptCategoryReportSql.Mix))
                .By(typeof(PromptCategoryReportSql), nameof(PromptCategoryReportSql.Trend))
                .ProvenBy(Test<PromptCategoryReportTests>(nameof(PromptCategoryReportTests.AggregateQueries_RequireTenDistinctPeople_AfterScope_AndNeverMixVersions))),

            ["ReportsAPIController.Areas"] = NoGroupedFigures(
                "Which report areas are switched on."),
            ["ReportsAPIController.Copilot"] = Floored(
                    "Prompt key phrases and languages used by fewer than 5 people are left out of a copy; the other charts are "
                    + "weekly totals over the reader's whole scope and interactions per Microsoft app.")
                .By(typeof(ReportAreaData), nameof(ReportAreaData.WithoutSmallGroups))
                .By(typeof(ReportChart), nameof(ReportChart.WithoutSmallGroups))
                .ProvenBy(
                    Test<ReportsAPIControllerTests>(nameof(ReportsAPIControllerTests.WithoutSmallGroups_HoldsPhrasesLanguagesAndDepartmentGridsToFivePeople_OnACopy)),
                    Test<ReportsAPIControllerTests>(nameof(ReportsAPIControllerTests.PromptPhrasesAndLanguages_CountTheDistinctPeopleBehindEach))),
            ["ReportsAPIController.CopilotAgents"] = Floored(
                    "Each agent's distinct people are counted in SQL. For this reader the top 1-20 agents are the most used of "
                    + "those five or more people used: an agent of fewer is left out of both charts, its total and its weekly "
                    + "line, and the next agent takes its place. How many were left out is never said, so neither changing the "
                    + "number of agents nor the agentName filter tells where a small agent ranks or whether one by that name exists.")
                .By(typeof(ReportsAPIController), nameof(ReportsAPIController.BuildCopilotAgentsQuery))
                .By(typeof(ReportAreaData), nameof(ReportAreaData.WithoutSmallGroups))
                .By(typeof(ReportChart), nameof(ReportChart.WithoutSmallGroups))
                .ProvenBy(
                    Test<CopilotAgentsReportSqlTests>(nameof(CopilotAgentsReportSqlTests.TheAgentsQuery_CountsTheDistinctPeopleBehindEachAgent_AndRanksAgentsOfFewerThanFiveApart)),
                    Test<CopilotAgentsReportSqlTests>(nameof(CopilotAgentsReportSqlTests.AReaderWithoutSeePii_GetsTheTopAgentsOfFiveOrMorePeople_AndASeePiiReaderTheTopAgents)),
                    Test<CopilotAgentsReportSqlTests>(nameof(CopilotAgentsReportSqlTests.AReaderWithoutSeePii_NarrowingToAnAgentOfFewerThanFive_GetsWhatANameMatchingNothingGets)))
                .WithKnownGap("#844 (S3-3): a week of an agent that five or more people used across the window can be one to four "
                    + "people's use, and the 1- to 6-month windows all end this week, so one window's total less another's is "
                    + "the weeks between (the few-consumers class of judgement call 1)."),
            ["ReportsAPIController.Usage"] = NoGroupedFigures(
                    "Weekly active users per Microsoft 365 workload over the reader's whole scope.")
                .ProvenBy(ScopeIsNeverAHandful()),
            ["ReportsAPIController.SpoAudit"] = NoGroupedFigures(
                    "Weekly SharePoint and OneDrive operations, and the ten most common operation types, over the reader's whole scope.")
                .ProvenBy(ScopeIsNeverAHandful()),
            ["ReportsAPIController.WebTraffic"] = NoGroupedFigures(
                    "Weekly page views and unique visitors over the reader's whole scope.")
                .ProvenBy(ScopeIsNeverAHandful()),
            ["ReportsAPIController.Calls"] = NoGroupedFigures(
                    "Weekly Teams calls and call minutes over the reader's whole scope.")
                .ProvenBy(ScopeIsNeverAHandful()),
            ["ReportsAPIController.Emails"] = NoGroupedFigures(
                    "Weekly sent emails over the reader's whole scope.")
                .ProvenBy(ScopeIsNeverAHandful()),
            ["ReportsAPIController.OfficeApps"] = Floored(
                    "Department and email-domain columns of fewer than 5 people are left out of a copy and cells of 1-4 people "
                    + "withheld; the department adoption rate leaves out departments of fewer than 5 people for every reader; the "
                    + "other charts count people per Microsoft app, platform or number of apps used.")
                .By(typeof(ReportAreaData), nameof(ReportAreaData.WithoutSmallGroups))
                .By(typeof(ReportChart), nameof(ReportChart.WithoutSmallGroups))
                .ProvenBy(Test<ReportsAPIControllerTests>(nameof(ReportsAPIControllerTests.WithoutSmallGroups_HoldsPhrasesLanguagesAndDepartmentGridsToFivePeople_OnACopy)))
                .WithKnownGap("#866: a withheld cell is not protected against the app's tenant total on the popularity chart "
                    + "(complementary withholding, #844 S4)."),

            ["SystemStatusAPIController.Get"] = NoGroupedFigures(
                    "Record counts of the main tables, the build label and the enabled imports; the administration fields are left out.")
                .ProvenBy(Test<PortalPermissionTests>(nameof(PortalPermissionTests.SystemStatus_ForAnInsightsReader_CarriesNoAdminFieldAtAll))),

            ["TeamsExplorerAPIController.Availability"] = NoGroupedFigures(
                "Which Teams sources are switched on, and how many teams are authorised for deep analytics."),
            ["TeamsExplorerAPIController.Overview"] = Floored(
                    "Read over the people the Adoption breakdown is made of - the enabled directory users reach is measured "
                    + "against - and cached apart from a See PII reader's section (#844 S4-1), so no total less the breakdown's rows "
                    + "leaves a since-disabled account's activity. A week of fewer than 5 active people is withheld with the week "
                    + "that keeps it from being worked out from the totals, and every week's calls; an engagement segment of fewer "
                    + "than 5 with the segment that keeps it from being worked out from the active people, and the shares; 1-4 "
                    + "licensed people left out of the mix; and, when 1-4 people were active, every usage KPI and the judgements "
                    + "worded from them.")
                .By(typeof(TeamsExplorerQuery), nameof(TeamsExplorerQuery.WithDirectoryPopulation))
                .By(typeof(TeamsExplorerSql), nameof(TeamsExplorerSql.UsagePopulation))
                .By(typeof(TeamsExplorerSql), nameof(TeamsExplorerSql.OverviewTrendOverDirectory))
                .By(typeof(TeamsOverview), nameof(TeamsOverview.WithoutSmallGroups))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.WithoutSmallWeeks))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.WithoutSmallSegments))
                .ProvenBy(
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.Overview_WithoutSeePii_IsReadOverTheBreakdownsPopulation_AndNoWeekOrSegmentOrTotalLessTheRowsShownIsUnderFive)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.Overview_WithoutSeePii_WhenOneToFourPeopleWereActive_WithholdsEveryUsageKpi_AndTheJudgementsWordedFromThem)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.Overview_ReadersWithAndWithoutSeePii_NeverShareACachedSection_InEitherOrder)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.WithoutSmallSegments_ADormantSegmentOfOneToFour_IsWithheldWithEveryShare_SoTheMeasuredTotalCannotGiveItBack)),
                    Test<TeamsExplorerPopulationSqlIntegrationTests>(nameof(TeamsExplorerPopulationSqlIntegrationTests.OverTheDirectoryPopulation_EveryUsageFigureLeavesItOut_AndTheOverviewLessTheBreakdownIsNothing)),
                    Test<TeamsExplorerPopulationSqlIntegrationTests>(nameof(TeamsExplorerPopulationSqlIntegrationTests.OverAWiderWindow_TheDirectoryTrends_AddEachPersonsWeekUp_AndLeaveTheDisabledAccountOut)))
                .WithKnownGap("#844 (G5), accepted under #872's judgement call 3 (counts of people without activity are kept): a "
                    + "Dormant segment of 1-4 is withheld from the mix, but known less active users gives its count back when "
                    + "everyone in view holds a Teams licence and every day was imported, and monthly less weekly active users "
                    + "(Adoption) can give back part or all of it on a 7-day window. A period's active people less a week's, and "
                    + "licensed less measured people (the unmeasured count), are differences of the same kind. Each counts people "
                    + "with no activity, never what anyone did; flooring it would blank reach and most weeks of any stable population.")
                .WithKnownGap("#844 (S3-3): the withholding holds within one response, not across the 7-, 28-, 90-, 180- and "
                    + "365-day windows a reader can request, which all end today: where one window withholds a small week with a "
                    + "week that another window shows, the small week can be worked out from the two.")
                .WithKnownGap("#866: the calls KPI and the call judgements are the Meetings tab's figures, with its residuals: a view "
                    + "of 1-4 attendees still shows them, and organiser concentration is the busiest tenth of organisers' share of "
                    + "meetings - fewer than 5 organisers whenever there are fewer than 41."),
            ["TeamsExplorerAPIController.Adoption"] = Floored(
                    "Breakdown groups of fewer than 5 people, whatever groupBy is asked for, are folded into one row over every "
                    + "group and only then cut to top, so none can be worked out from the totals. The rest of the tab is read over "
                    + "the breakdown's own population and cached apart from a See PII reader's (#844 S4-1): a weekly or monthly "
                    + "count of 1-4 active people is withheld, and the daily mean when 1-4 were active in the period; a lifecycle "
                    + "count, engagement segment or platform of 1-4 people with whatever would give it back; a week whose segments "
                    + "include 1-4 people whole.")
                .By(typeof(TeamsAdoption), nameof(TeamsAdoption.WithoutSmallGroups))
                .By(typeof(TeamsAdoption), nameof(TeamsAdoption.WithFirstGroups))
                .By(typeof(TeamsExplorerQuery), nameof(TeamsExplorerQuery.WithDirectoryPopulation))
                .By(typeof(TeamsExplorerSql), nameof(TeamsExplorerSql.UsagePopulation))
                .By(typeof(TeamsExplorerSql), nameof(TeamsExplorerSql.DevicePopulation))
                .By(typeof(TeamsExplorerSql), nameof(TeamsExplorerSql.PerPersonPopulation))
                .By(typeof(TeamsAdoptionRhythm), nameof(TeamsAdoptionRhythm.WithoutSmallGroups))
                .By(typeof(TeamsLifecycle), nameof(TeamsLifecycle.WithoutSmallGroups))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.WithoutSmallPlatforms))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsAdoption_WithoutSeePii_FoldsGroupsOfFewerThanFive_OnScreenAndInTheExport_AndTheCachedSectionKeepsThem)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsAdoption_WithoutSeePii_FoldsGroupsBeyondTheMaximumTop_SoNoneCanBeWorkedOutFromTheTotals)),
                    Test<TeamsExplorerScoringTests>(nameof(TeamsExplorerScoringTests.WithoutSmallGroups_AFoldedRowShortOfPeople_TakesTheSmallestShownGroup)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.Adoption_WithoutSeePii_IsReadOverTheBreakdownsPopulation_AndHoldsRhythmLifecycleSegmentsAndPlatformsToFivePeople)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.Adoption_ASeePiiReader_IsServedTheSectionAsItWas_AndTheTwoReadersNeverShareACachedSection_InEitherOrder)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.Rhythm_WithoutSmallGroups_WithholdsTheDailyMean_WhenThePeriodsActivePeopleAreFew_OrCannotBeRead)),
                    Test<TeamsExplorerOverviewFloorTests>(nameof(TeamsExplorerOverviewFloorTests.WithoutSmallPlatforms_WithholdsAnyMobile_WhenIosOrAndroidIs)),
                    Test<TeamsExplorerPopulationSqlIntegrationTests>(nameof(TeamsExplorerPopulationSqlIntegrationTests.OverTheDirectoryPopulation_EveryUsageFigureLeavesItOut_AndTheOverviewLessTheBreakdownIsNothing)),
                    Test<TeamsExplorerPopulationSqlIntegrationTests>(nameof(TeamsExplorerPopulationSqlIntegrationTests.OverAWiderWindow_TheDirectoryTrends_AddEachPersonsWeekUp_AndLeaveTheDisabledAccountOut)))
                .WithKnownGap("#844 (G5), accepted under #872's judgement call 3 (counts of people without activity are kept): "
                    + "counts of people that nest are each floored, but not their differences - the people in the larger count and "
                    + "not the smaller: active in the month and not the last week (on a 7-day window, part or all of the Dormant "
                    + "segment); in a breakdown group and not active, which summed over the groups is the Dormant segment's count "
                    + "when everyone in view holds a Teams licence and every day was imported; licensed and not in the engagement "
                    + "mix, the unmeasured count; and, while the platform shares are shown, seen on any device and not on one "
                    + "platform. Each counts people with no activity of some kind, never what anyone did, and flooring it would "
                    + "blank most weeks, and reach, of any stable population."),
            ["TeamsExplorerAPIController.Meetings"] = Floored(
                    "A copy without the named organiser and attendee lists; a week of fewer than 5 attendees withheld with the week "
                    + "of fewest attendees still shown, so the calls and call hours less the weeks shown are never one week's; every "
                    + "distribution withheld when fewer than 5 people attended in the view, and modalities of fewer than 5 people folded.")
                .By(typeof(TeamsMeetings), nameof(TeamsMeetings.WithoutIndividualData))
                .By(typeof(TeamsMeetings), nameof(TeamsMeetings.WithoutSmallGroups))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.WithoutSmallCallWeeks))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsMeetings_WithoutSeePii_LoseTheNamedLeaderboards_AndTheCachedSectionKeepsThem)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsMeetings_WithoutSeePii_WithholdAWeekOfFewerThanFiveAttendees_AndFoldRareModalities)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsMeetings_WithoutSmallGroups_WithholdsEveryDistribution_WhenFewerThanFivePeopleAttended)),
                    Test<TeamsExplorerMeetingsFloorTests>(nameof(TeamsExplorerMeetingsFloorTests.Meetings_WithoutSeePii_NoTotalLessTheWeeksShown_IsASingleWithheldWeek)),
                    Test<TeamsExplorerMeetingsFloorTests>(nameof(TeamsExplorerMeetingsFloorTests.Meetings_ASeePiiReader_IsServedEveryWeekAsItWas_AndTheCachedSectionIsNeverTrimmed_InEitherOrder)),
                    Test<TeamsExplorerMeetingsFloorTests>(nameof(TeamsExplorerMeetingsFloorTests.WithoutSmallCallWeeks_WithholdsTheQuietestWeekShown_WheneverAWeekIsWithheld)))
                .WithKnownGap("#866: the size, length, heatmap, time-of-day and call-experience buckets have no people count, so the "
                    + "floor applies to the view as a whole; the headline KPIs of a view of 1-4 attendees are shown.")
                .WithKnownGap("#844, accepted under #872's judgement call 3: the window's distinct attendees less a week's - the people "
                    + "who attended calls in other weeks and not that one - is not floored; it counts people without activity in that "
                    + "week, never what anyone did. Attendees are distinct people, so the weeks' do not add up to the window's.")
                .WithKnownGap("#844 (S3-3): the withholding holds within one response, not across the 7-, 28-, 90-, 180- and "
                    + "365-day windows a reader can request, which all end today: where one window withholds a small week with a "
                    + "week that another window shows, the small week can be worked out from the two."),
            ["TeamsExplorerAPIController.Collaboration"] = Floored(
                    "Teams of fewer than 5 members, and their channels, are left out, with the smallest team (channel) shown that has "
                    + "messages, so the channel messages less the rows shown are never one small team's; reactions of 1-4 people "
                    + "withheld, with the fewest-reactor row of 5 or more when nothing else hidden is 5 people's; rare reactions "
                    + "folded; the tab list withheld. Decided over the export-sized list - read with one row more, the rows beyond "
                    + "it counted as left out - and only then cut to top, so no two tops leave out different rows (#844, P4-1).")
                .By(typeof(TeamsCollaboration), nameof(TeamsCollaboration.WithoutSmallGroups))
                .By(typeof(TeamsCollaboration), nameof(TeamsCollaboration.WithFirstRows))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.WithoutSmallRowsOfList))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.ReactionsWithheld))
                .By(typeof(TeamsExplorerQuery), nameof(TeamsExplorerQuery.WithListProbe))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsCollaboration_WithoutSeePii_LeavesOutTeamsOfFewerThanFive_WithholdsFewReactors_OnScreenAndInTheExports)),
                    Test<TeamsExplorerMembershipHorizonSqlIntegrationTests>(nameof(TeamsExplorerMembershipHorizonSqlIntegrationTests.ATeamWhoseOnlyMembershipSnapshotIsOlderThanTheHorizon_IsWithheldFromAReaderWithoutSeePii)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.Collaboration_WithoutSeePii_LeavesOutTheSameTeamsWhateverTopIsAsked_AndNoTotalLessTheRowsShownIsASmallTeams)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.Collaboration_WithoutSeePii_ATeamBeyondTheExportSizedList_IsCountedAsLeftOut)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.Collaboration_WithoutSeePii_TheReactionsLessThoseShown_AreNeverAHandfulOfPeoples)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.CollaborationAndConversations_ASeePiiReader_IsServedTheSectionAsItWas_AndTheTwoReadersNeverShareACachedSection_InEitherOrder)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.WithoutSmallRowsOfList_LeavesOutWithTheSmallRows_TheSmallestRowShownThatAddsToTheTotals)))
                .WithKnownGap("#844 (P4-1 review): each list is floored on its own. The team rows count members from each team's "
                    + "latest snapshot ever, the channel rows from the last 7 days' (TeamsExplorerSql.LatestTeamMembers), so a team "
                    + "the two measures put on either side of 5 can be left out of one list and shown in the other; and a team "
                    + "left out to keep a small one from the totals can still be shown as its channels, or in the Conversations "
                    + "tab's sentiment. A team's messages less its channels shown, or the two tabs' figures together, can then "
                    + "single out a small team. When every team with messages is small, the tenant's channel messages are theirs.")
                .WithKnownGap("#844 (S3-3): the withholding holds within one response, not across the 7-, 28-, 90-, 180- and "
                    + "365-day windows a reader can request, which all end today."),
            ["TeamsExplorerAPIController.Conversations"] = Floored(
                    "Key phrases and languages withheld; the sentiment of teams of fewer than 5 members, and of their channels, "
                    + "left out with the smallest row shown, so the weekly messages less the rows shown are never one small team's. "
                    + "Decided over the export-sized list - read with one row more, the rows beyond it counted as left out - and "
                    + "only then cut to top (#844, P4-1).")
                .By(typeof(TeamsConversations), nameof(TeamsConversations.WithoutSmallGroups))
                .By(typeof(TeamsConversations), nameof(TeamsConversations.WithFirstRows))
                .By(typeof(TeamsSmallGroups), nameof(TeamsSmallGroups.WithoutSmallRowsOfList))
                .By(typeof(TeamsExplorerQuery), nameof(TeamsExplorerQuery.WithListProbe))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsConversations_WithoutSeePii_WithholdKeyPhrasesAndLanguages_AndTheSentimentOfTeamsOfFewerThanFive)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.Conversations_WithoutSeePii_LeavesOutTheSameSentimentWhateverTopIsAsked_AndTheWeeksMessagesLessThoseShownAreNeverOneSmallTeams)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.CollaborationAndConversations_ASeePiiReader_IsServedTheSectionAsItWas_AndTheTwoReadersNeverShareACachedSection_InEitherOrder)))
                .WithKnownGap("#866: channel messages and the tenant-wide weekly sentiment are floored by team membership or not at "
                    + "all, because the posters per channel are not stored.")
                .WithKnownGap("#844 (P4-1 review): the sentiment lists are floored apart from the Collaboration tab's team and "
                    + "channel rows, which show each team's sentiment too, so a team one tab leaves out to protect a small one can "
                    + "be shown by the other."),
            ["TeamsExplorerAPIController.People"] = Floored(
                    "A copy without the champion and dormant lists; departments of fewer than 5 power users folded over every "
                    + "department, read over the Power segment's own population (#844, P4-1), before the cut to top. When the "
                    + "Overview and Adoption tabs withhold that Power segment, or the list could be cut short, the departments' total "
                    + "is withheld too: no combined row, no shares, and the smallest named department left out when needed.")
                .By(typeof(TeamsPeople), nameof(TeamsPeople.WithoutIndividualData))
                .By(typeof(TeamsPeople), nameof(TeamsPeople.WithFirstDepartments))
                .By(typeof(TeamsPeople), nameof(TeamsPeople.PowerSegmentWithheld))
                .By(typeof(TeamsExplorerQuery), nameof(TeamsExplorerQuery.WithEveryGroup))
                .By(typeof(TeamsExplorerSql), nameof(TeamsExplorerSql.JoinedUserPopulation))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsPeople_WithoutSeePii_KeepTheDepartmentCounts_AndTheCachedSectionKeepsTheNames)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsAdoptionAndPeople_WithoutSeePii_FoldTheSameGroupsWhateverTopIsAskedFor)),
                    Test<TeamsExplorerScoringTests>(nameof(TeamsExplorerScoringTests.PeopleWithoutIndividualData_FoldsDepartmentsWithFewerThanFivePowerUsers_AndNeverLeavesAHandfulInTheFoldedRow)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.People_WithoutSeePii_FoldsADepartmentBeyondTheMaximumTop_SoThePowerUsersLessTheDepartmentsShownAreNeverItAlone)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.People_ASeePiiReader_IsServedTheDepartmentsAsTheyWere_AndTheTwoReadersNeverShareACachedSection_InEitherOrder)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.People_ADepartmentListCutShort_TreatsWhatLiesBeyondAsLeftOut)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.People_WhenTheOverviewWithholdsThePowerSegment_TheDepartmentsNeverAddUpToIt)),
                    Test<TeamsExplorerPopulationSqlIntegrationTests>(nameof(TeamsExplorerPopulationSqlIntegrationTests.People_OverTheDirectoryPopulation_EveryDepartmentsPowerUsersAddUpToThePowerSegment_AndLeaveTheDisabledAccountOut)))
                .WithKnownGap("#844 (S3-3): the withholding holds within one response, not across the 7-, 28-, 90-, 180- and "
                    + "365-day windows a reader can request, which all end today."),
            ["TeamsExplorerAPIController.Export"] = Floored(
                    "The adoption, teams and channels files carry the same folded and floored rows as the page: the teams and "
                    + "channels files are floored over the export-sized list read with one row more, then cut to top.")
                .By(typeof(TeamsAdoption), nameof(TeamsAdoption.WithoutSmallGroups))
                .By(typeof(TeamsCollaboration), nameof(TeamsCollaboration.WithoutSmallGroups))
                .By(typeof(TeamsCollaboration), nameof(TeamsCollaboration.WithFirstRows))
                .By(typeof(TeamsExplorerQuery), nameof(TeamsExplorerQuery.WithListProbe))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsAdoption_WithoutSeePii_FoldsGroupsOfFewerThanFive_OnScreenAndInTheExport_AndTheCachedSectionKeepsThem)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsCollaboration_WithoutSeePii_LeavesOutTeamsOfFewerThanFive_WithholdsFewReactors_OnScreenAndInTheExports)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.Collaboration_WithoutSeePii_LeavesOutTheSameTeamsWhateverTopIsAsked_AndNoTotalLessTheRowsShownIsASmallTeams)),
                    Test<TeamsExplorerListFloorTests>(nameof(TeamsExplorerListFloorTests.Collaboration_WithoutSeePii_ATeamBeyondTheExportSizedList_IsCountedAsLeftOut)))
                .Refusing("section people, section dormant",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsExplorerExport_OnlyThePeopleSectionsNeedSeePii)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission))),

            // WebActivityAPIController: the cache holds the complete section, and a reader without See PII is sent a trimmed copy
            // of it (#844, G2). Every list is floored on the distinct people behind each row, over the first rows it can hold
            // (WebActivityQuery.MaximumTop, fetched with one row more to tell a list cut at that cap) and then cut to the rows
            // asked for; only the search terms are withheld.
            ["WebActivityAPIController.Availability"] = NoGroupedFigures(
                "Whether the tracker is collecting, when it last saw a page view, and whether search and click tracking are in use."),
            ["WebActivityAPIController.Overview"] = Floored(
                    "Sites visited by fewer than 5 people are left out of the busiest sites, and engagement segments of fewer than 5 "
                    + "people out of the segment mix, each with the smallest one shown, so none can be worked out from the page-view "
                    + "or visitor total. The sites are floored over the first 500 sites the list can hold - any site past them "
                    + "counting as one left out - and then cut to the rows shown, so the rows left out never depend on how many were "
                    + "asked for.")
                .By(typeof(WebActivityOverview), nameof(WebActivityOverview.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsWithinCap))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.FirstRows))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesVisitsAndOverview_WithoutSeePii_LeaveOutPagesAndSitesOfFewerThanFivePeople_WithTheSmallestShown)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRowsWithinCap_TakesNoRowWhenNoneLiesPastTheCap_AndNoSecondRowWhenOneIsLeftOutAlready)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut)))
                .WithKnownGap("The headline figures, weekly trend, heatmap and visit depth are over the reader's whole scope with no "
                    + "people count (judgement call 1)."),
            ["WebActivityAPIController.Visits"] = Floored(
                    "Sites, pages, devices and browsers of fewer than 5 visitors are left out, each list with its smallest row shown, "
                    + "floored over the first 500 rows it can hold - any row past them counting as one left out - and then cut to the "
                    + "rows asked for; a site the list leaves out is folded into '(other sites)' over time - matched by the site's id, "
                    + "so another site with the same title is folded too. A page's visits are its unique page views, which the Pages "
                    + "tab does not send this reader, so they are shown only here.")
                .By(typeof(WebActivityVisits), nameof(WebActivityVisits.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsWithinCap))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.FirstRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFolded))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFoldedByKey))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.VisitsByPage))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.SiteOverTime))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesVisitsAndOverview_WithoutSeePii_LeaveOutPagesAndSitesOfFewerThanFivePeople_WithTheSmallestShown)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.UniquePageViews_WithoutSeePii_AreLeftOffThePagesTab_SoAPageTheVisitsTabLeavesOutCannotBeReadThere)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.SiteOverTime_WithoutSeePii_IsFoldedBySite_NotByATitleTwoSitesShare)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut)),
                    Test<WebActivitySqlIntegrationTests>(nameof(WebActivitySqlIntegrationTests.EveryPageAndPathList_CountsEachPersonOnce_AndAPageViewWithNoVisitAddsNoOne)))
                .WithKnownGap("The visit trend and the day, hour and period-of-day buckets are over the reader's whole scope with no "
                    + "people count (judgement call 1).")
                .WithKnownGap("#881 decision, not fixed: a site with one page carries that page's visits, so a page the visits by page "
                    + "leave out as their smallest shown can be read from its site's row, which the sites list floors on its own.")
                .WithKnownGap("#881 decision, not fixed: the Pages tab's sites carry each site's visits too, ranked by page views where "
                    + "these are ranked by visits. With more than 500 sites the two lists can hold different sites and pick their "
                    + "smallest shown apart, so a site one leaves out can be shown with its visits by the other."),
            ["WebActivityAPIController.Pages"] = Floored(
                    "Top, slowest and quiet pages and sites of fewer than 5 people are left out, each list with its smallest row shown, "
                    + "floored over the first 500 rows it can hold - any row past them counting as one left out - and then cut to the "
                    + "rows asked for, so the page views less the rows shown are never one small page's. A quiet page has at most three "
                    + "page views, so every quiet page is left out. The most viewed, slowest and quiet pages share each page's page "
                    + "views, so they leave out one combined set of pages. No page carries a figure counted in visits: its unique page "
                    + "views are the Visits tab's visits by page, and its entries, exits and bounces the Journeys tab's, each floored "
                    + "on its own tab.")
                .By(typeof(WebActivityPages), nameof(WebActivityPages.WithoutSmallGroups))
                .By(typeof(WebActivityPageRow), nameof(WebActivityPageRow.VisitFiguresWithheld))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsWithinCap))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.FirstRows))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.PageStats))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.SlowestPages))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.QuietPages))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesVisitsAndOverview_WithoutSeePii_LeaveOutPagesAndSitesOfFewerThanFivePeople_WithTheSmallestShown)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EntryExitAndBouncePages_WithoutSeePii_LeaveOutOneCombinedSetOfPages_SoNoPageIsShownWhereAnotherListHidesIt)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.MostViewedSlowestAndQuietPages_WithoutSeePii_LeaveOutOneCombinedSetOfPages_SoNoPageIsShownWhereAnotherListHidesIt)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.UniquePageViews_WithoutSeePii_AreLeftOffThePagesTab_SoAPageTheVisitsTabLeavesOutCannotBeReadThere)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallGroups_ReturnsACopy_AndLeavesTheCachedSectionAsItWasBuilt)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.SeePii_IsSentEveryPageAndPathListAsBuilt_AndNeverAPeopleCount)),
                    Test<WebActivitySqlIntegrationTests>(nameof(WebActivitySqlIntegrationTests.EveryPageAndPathList_CountsEachPersonOnce_AndAPageViewWithNoVisitAddsNoOne)))
                .WithKnownGap("The weekly period-of-day chart is over the reader's whole scope with no people count (judgement call 1).")
                .WithKnownGap("#881 decision, not fixed: a row is floored on its own people, but a figure inside a shown row can describe "
                    + "fewer than 5 of them - a page's average time on page and load time are over the page views that reported one. "
                    + "Flooring those needs a people count per figure, in SQL, and a product decision.")
                .WithKnownGap("#881 decision, not fixed: a site with one page carries that page's page views (here and on the Overview), "
                    + "so a page the page lists leave out as their smallest shown can be read from its site's row, which the sites "
                    + "list floors on its own.")
                .WithKnownGap("#881 decision, not fixed: the sites here carry each site's visits, which the Visits tab ranks by visits "
                    + "where this list ranks by page views. With more than 500 sites the two lists can hold different sites and pick "
                    + "their smallest shown apart, so a site one leaves out can be shown with its visits by the other."),
            ["WebActivityAPIController.Journeys"] = Floored(
                    "Entry, exit and bounce pages, flows and clicked elements of fewer than 5 people are left out, each list with its "
                    + "smallest row shown, floored over the first 500 rows it can hold - any row past them counting as one left out - "
                    + "and then cut to the rows asked for, so the visits, bounces and clicks less the rows shown are never one small "
                    + "row's. The entry, exit and bounce pages, which share each page's entries, exits and bounces, leave out one "
                    + "combined set of pages, and no flow that starts or ends on one is shown; a step of fewer than 5 people is left "
                    + "out with the smallest step out of the same page, whose share it is of, and that smallest step goes too when the "
                    + "page's steps go on past the list.")
                .By(typeof(WebActivityJourneys), nameof(WebActivityJourneys.WithoutSmallGroups))
                .By(typeof(WebActivityJourneys), nameof(WebActivityJourneys.StepsGoOnPastTheList))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsWithinCap))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsInEachGroup))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.FirstRows))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.Transitions))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.StartEndFlows))
                .By(typeof(WebActivitySql), nameof(WebActivitySql.ClickedElements))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Journeys_WithoutSeePii_LeaveOutSmallRows_WithTheSmallestShown_AndASmallStepWithTheSmallestStepOutOfItsPage)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EntryExitAndBouncePages_WithoutSeePii_LeaveOutOneCombinedSetOfPages_SoNoPageIsShownWhereAnotherListHidesIt)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.StepsOutOfAPage_LoseTheirSmallestShownOnlyWhenThatPagesStepsGoOnPastTheCap)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRowsInEachGroup_LeavesOutTheSmallestShownRowOfTheSmallRowsOwnGroup_AndKeepsTheOrder)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.SeePii_IsSentEveryPageAndPathListAsBuilt_AndNeverAPeopleCount)),
                    Test<WebActivitySqlIntegrationTests>(nameof(WebActivitySqlIntegrationTests.EveryPageAndPathList_CountsEachPersonOnce_AndAPageViewWithNoVisitAddsNoOne)))
                .WithKnownGap("The visit-depth buckets and the click total are over the reader's whole scope with no people count "
                    + "(judgement call 1). The flows are floored against all paired visits, not against each start or end page: "
                    + "an entry page's entries less its flows shown can be a small flow's visits, without naming where it ended.")
                .WithKnownGap("#881 decision, not fixed: a row is floored on its own people, but a figure inside a shown row can describe "
                    + "fewer than 5 of them - an entry or exit page's bounces (its single-page visits), a flow's single-page visits "
                    + "and average pages, and the average time on page (over the page views that reported one). Flooring those needs a "
                    + "people count per figure, in SQL, and a product decision."),
            ["WebActivityAPIController.Geography"] = Floored(
                    "Countries, provinces and cities fewer than 5 people visited from are left out, each list with its smallest place "
                    + "shown, floored over the first 500 places it can hold - any place past them counting as one left out - and then "
                    + "cut to the rows asked for, so no place can be worked out from the located page views; a country the list "
                    + "leaves out is folded into '(other countries)' over time.")
                .By(typeof(WebActivityGeography), nameof(WebActivityGeography.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsWithinCap))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.FirstRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFolded))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Geography_WithoutSeePii_LeavesOutPlacesOfFewerThanFiveVisitors_AndWhatIsLeftOutIsNeverOneSmallPlace)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut)))
                .WithKnownGap("#881 decision, not fixed: a country or province with one city carries that city's figures, so a city the "
                    + "cities list leaves out as its smallest shown can be read from its country's or province's row, which those lists "
                    + "floor on their own."),
            ["WebActivityAPIController.Search"] = Floored(
                    "Search terms and dead-end terms are withheld, as Teams key phrases are: they are text people typed. Searches by "
                    + "site count no people and are withheld too.")
                .By(typeof(WebActivitySearch), nameof(WebActivitySearch.WithoutSmallGroups))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Search_WithoutSeePii_WithholdsEveryTermAndTheSitesSearched_AndTheCachedSectionKeepsThemForSeePii)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallGroups_ReturnsACopy_AndLeavesTheCachedSectionAsItWasBuilt)))
                .WithKnownGap("The search trend and the day and period-of-day buckets are over the reader's whole scope with no "
                    + "people count (judgement call 1)."),
            ["WebActivityAPIController.Technology"] = Floored(
                    "Browsers, operating systems, devices and the browser x device x operating system x city detail are left out below "
                    + "5 visitors, each list with its smallest row shown, floored over the first 500 rows it can hold - any row past "
                    + "them counting as one left out - and then cut to the rows asked for; a device the list leaves out is folded into "
                    + "'(other devices)' over time. The detail names only the cities Geography shows for the same window and scope, so "
                    + "a city Geography leaves out cannot be taken back out of its remainder.")
                .By(typeof(WebActivityTechnology), nameof(WebActivityTechnology.WithoutSmallGroups))
                .By(typeof(WebActivityGeography), nameof(WebActivityGeography.CityNamesShownWithoutSeePii))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRowsWithinCap))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.FirstRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFolded))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Technology_WithoutSeePii_LeavesOutDetailRowsAndPlatformsOfFewerThanFiveVisitors)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Technology_WithoutSeePii_NamesOnlyCitiesGeographyShows_OnScreenAndInTheExport)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut)))
                .WithKnownGap("#881 decision, not fixed: a row is floored on its own people, but a figure inside a shown row can describe "
                    + "fewer than 5 of them - a platform's or detail row's average load time and time on page are over the page views "
                    + "that reported one. Flooring those needs a people count per figure, in SQL, and a product decision.")
                .WithKnownGap("#881 decision, not fixed: a browser, device or operating system seen in one detail combination carries "
                    + "that combination's figures, so a detail row the detail leaves out as its smallest shown can be read from the "
                    + "platform's row, which its own list floors apart."),
            ["WebActivityAPIController.Export"] = Floored(
                    "Every file carries its screen's floored rows - pages, quiet and slow pages, entry and exit pages, steps, flows "
                    + "and the technology detail, which names only the cities Geography shows - from the same section, floored the same "
                    + "way and cut to the rows asked for, and cached apart from a See PII reader's file. The page files carry no figure "
                    + "counted in visits: no unique page views, entries, exits or bounces. The search terms are refused.")
                .By(typeof(WebActivityPages), nameof(WebActivityPages.WithoutSmallGroups))
                .By(typeof(WebActivityJourneys), nameof(WebActivityJourneys.WithoutSmallGroups))
                .By(typeof(WebActivityTechnology), nameof(WebActivityTechnology.WithoutSmallGroups))
                .By(typeof(WebActivityGeography), nameof(WebActivityGeography.CityNamesShownWithoutSeePii))
                .By(typeof(WebActivityExports), nameof(WebActivityExports.PageColumns))
                .By(typeof(WebActivityExports), nameof(WebActivityExports.PageTrafficColumns))
                .By(typeof(WebActivityQuery), nameof(WebActivityQuery.ForFloor))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Export_WithoutSeePii_EveryFileCarriesItsScreensFlooredRows_AndOnlyTheSearchTermsAreRefused)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Export_ASeePiiReadersFile_IsNeverServedToTheNextReaderWithoutSeePii)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_IsTheFirstRowsOfOneFlooredList_SoAShortAnswerNeverShowsARowALongerOneLeavesOut)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.EveryFlooredList_WithoutSeePii_WithOneSmallRowPastTheCap_LeavesOutItsSmallestShownToo)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.MostViewedSlowestAndQuietPages_WithoutSeePii_LeaveOutOneCombinedSetOfPages_SoNoPageIsShownWhereAnotherListHidesIt)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.UniquePageViews_WithoutSeePii_AreLeftOffThePagesTab_SoAPageTheVisitsTabLeavesOutCannotBeReadThere)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Technology_WithoutSeePii_NamesOnlyCitiesGeographyShows_OnScreenAndInTheExport)))
                .Refusing("section search-terms",
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Export_WithoutSeePii_EveryFileCarriesItsScreensFlooredRows_AndOnlyTheSearchTermsAreRefused))),
        };

        #region The checks

        [TestMethod]
        public void EveryActionAReaderWithoutSeePiiCanCall_IsClassifiedForThePrivacyFloor()
        {
            var actions = PortalPermissionTests.WebApiActions()
                .ToDictionary(a => a.Key, a => PortalPermissionTests.Classify(a.Controller, a.Method), StringComparer.Ordinal);
            var callable = new HashSet<string>(actions.Where(a => ReaderWithoutSeePiiCanCall(a.Value)).Select(a => a.Key), StringComparer.Ordinal);
            var problems = new List<string>();

            var unclassified = callable.Except(Registry.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (unclassified.Count > 0)
            {
                problems.Add("Web API action(s) that a reader without See PII can call, with no privacy-floor classification. Read what "
                    + "each one serves, then add it to PortalPrivacyFloorTests.Registry as NoGroupedFigures, as Floored - naming the "
                    + "code that applies the floor and the test that proves it - or as a KnownGap for the maintainer: "
                    + string.Join(", ", unclassified));
            }

            var gone = Registry.Keys.Where(k => !actions.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (gone.Count > 0)
            {
                problems.Add("PortalPrivacyFloorTests.Registry lists action(s) that no longer exist: " + string.Join(", ", gone));
            }

            var restricted = Registry.Keys.Where(k => actions.ContainsKey(k) && !callable.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => k + " (" + actions[k] + ")")
                .ToList();
            if (restricted.Count > 0)
            {
                problems.Add("PortalPrivacyFloorTests.Registry lists action(s) a reader without See PII can no longer call. Remove them: "
                    + string.Join(", ", restricted));
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            Console.WriteLine(Summary());
        }

        [TestMethod]
        public void EveryFloorAndRefusal_NamesCodeThatExists_AndATestThatProvesIt()
        {
            var problems = new List<string>();
            foreach (var entry in Registry.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var key = entry.Key;
                var decision = entry.Value;

                if (string.IsNullOrWhiteSpace(decision.Reason)) problems.Add(key + ": says nothing about what it serves.");

                if (decision.Kind == FloorClass.Floored)
                {
                    if (decision.FlooredBy.Count == 0) problems.Add(key + ": floored, but names no code that applies the floor.");
                    if (decision.Proofs.Count == 0) problems.Add(key + ": floored, but names no test that proves it.");
                }
                else
                {
                    if (decision.FlooredBy.Count > 0) problems.Add(key + ": names code that applies a floor, so classify it as Floored.");
                    if (decision.KnownGaps.Count > 0) problems.Add(key + ": a gap beside a floor belongs on a Floored entry.");
                }

                foreach (var (type, member) in decision.FlooredBy)
                {
                    if (!HasMember(type, member)) problems.Add(key + ": " + type.Name + "." + member + ", named as applying the floor, does not exist.");
                }

                foreach (var refusal in decision.Refusals)
                {
                    if (string.IsNullOrWhiteSpace(refusal.Parameters)) problems.Add(key + ": a refusal names no parameter.");
                    if (refusal.ProvenBy.Count == 0) problems.Add(key + ": the refusal of " + refusal.Parameters + " names no test that proves it.");
                }

                foreach (var test in decision.Proofs.Concat(decision.Refusals.SelectMany(r => r.ProvenBy)))
                {
                    var problem = WhyItProvesNothing(test);
                    if (problem != null) problems.Add(key + ": " + test + " " + problem);
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static bool ReaderWithoutSeePiiCanCall(string whoMayCall) =>
            whoMayCall == PortalPermissionTests.Any || whoMayCall == PortalPermissionTests.Public;

        private static bool HasMember(Type type, string name) =>
            type.GetMember(name, MemberTypes.Method | MemberTypes.Field | MemberTypes.Property,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Length > 0;

        /// <summary>Null when the test can prove something: a <c>[TestMethod]</c> of a <c>[TestClass]</c>, neither ignored.</summary>
        private static string WhyItProvesNothing(TestRef test)
        {
            var methods = test.TestClass.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == test.Method).ToList();
            if (methods.Count == 0) return "does not exist.";
            if (test.TestClass.GetCustomAttribute<TestClassAttribute>() == null) return "is not in a [TestClass].";
            if (test.TestClass.GetCustomAttribute<IgnoreAttribute>() != null) return "is in an [Ignore]d class.";
            if (!methods.Any(m => m.GetCustomAttribute<TestMethodAttribute>() != null)) return "is not a [TestMethod].";
            if (methods.Any(m => m.GetCustomAttribute<IgnoreAttribute>() != null)) return "is [Ignore]d.";
            return null;
        }

        /// <summary>The classification and every known gap, for the test output: gaps are decisions, not failures.</summary>
        private static string Summary()
        {
            var text = new StringBuilder();
            text.AppendLine("Privacy floor: " + Registry.Count + " Web API actions a reader without See PII can call.");
            foreach (var kind in Enum.GetValues(typeof(FloorClass)).Cast<FloorClass>())
            {
                text.AppendLine("  " + kind + ": " + Registry.Count(e => e.Value.Kind == kind));
            }

            text.AppendLine("Known gaps: grouped figures served without a floor.");
            foreach (var entry in Registry.Where(e => e.Value.Kind == FloorClass.KnownGap).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                text.AppendLine("  " + entry.Key + ": " + entry.Value.Reason);
            }

            text.AppendLine("Known gaps beside a floor.");
            var besideAFloor = Registry
                .SelectMany(e => e.Value.KnownGaps.Select(gap => (Gap: gap, Action: e.Key)))
                .GroupBy(g => g.Gap, StringComparer.Ordinal)
                .OrderBy(g => g.Min(x => x.Action), StringComparer.Ordinal);
            foreach (var gap in besideAFloor)
            {
                text.AppendLine("  " + gap.Key);
                text.AppendLine("    " + string.Join(", ", gap.Select(x => x.Action).OrderBy(a => a, StringComparer.Ordinal)));
            }

            return text.ToString();
        }

        #endregion

        #region The registry's vocabulary

        private enum FloorClass
        {
            /// <summary>Status, settings, names, or totals over the reader's whole scope: no figure about a group of people.</summary>
            NoGroupedFigures,

            /// <summary>Grouped figures, floored for a reader without See PII by the named code, as the named tests prove.</summary>
            Floored,

            /// <summary>Grouped figures served without a floor: reported, not failed.</summary>
            KnownGap,
        }

        private static FloorDecision NoGroupedFigures(string why) => new FloorDecision(FloorClass.NoGroupedFigures, why);

        private static FloorDecision Floored(string how) => new FloorDecision(FloorClass.Floored, how);

        private static FloorDecision KnownGap(string exposed) => new FloorDecision(FloorClass.KnownGap, exposed);

        private static TestRef Test<TTestClass>(string method) => new TestRef(typeof(TTestClass), method);

        /// <summary>
        /// A reader without See PII is refused a scope of the administrator's filter that leaves them 1-4 people, so a total over
        /// their whole scope is never a handful of people's.
        /// </summary>
        private static TestRef ScopeIsNeverAHandful() =>
            Test<GlobalFilterWebTests>(nameof(GlobalFilterWebTests.ReaderWithoutSeePii_IsRefusedAScopeOfAHandfulOfPeople_ButNotOneOfNobody));

        private sealed class TestRef
        {
            internal TestRef(Type testClass, string method)
            {
                TestClass = testClass;
                Method = method;
            }

            internal Type TestClass { get; }

            internal string Method { get; }

            public override string ToString() => TestClass.Name + "." + Method;
        }

        private sealed class Refusal
        {
            internal Refusal(string parameters, IReadOnlyList<TestRef> provenBy)
            {
                Parameters = parameters;
                ProvenBy = provenBy;
            }

            /// <summary>The narrowing parameters refused without See PII.</summary>
            internal string Parameters { get; }

            internal IReadOnlyList<TestRef> ProvenBy { get; }
        }

        private sealed class FloorDecision
        {
            internal FloorDecision(FloorClass kind, string reason)
            {
                Kind = kind;
                Reason = reason;
            }

            internal FloorClass Kind { get; }

            /// <summary>What the action serves, and how the floor holds - or, for a known gap, what is exposed.</summary>
            internal string Reason { get; }

            internal List<(Type Type, string Member)> FlooredBy { get; } = new List<(Type Type, string Member)>();

            internal List<TestRef> Proofs { get; } = new List<TestRef>();

            internal List<Refusal> Refusals { get; } = new List<Refusal>();

            /// <summary>What a floored action still serves without a floor.</summary>
            internal List<string> KnownGaps { get; } = new List<string>();

            /// <summary>The code that applies the floor: a method, or the field holding the SQL that does.</summary>
            internal FloorDecision By(Type type, string member)
            {
                FlooredBy.Add((type, member));
                return this;
            }

            internal FloorDecision ProvenBy(params TestRef[] tests)
            {
                Proofs.AddRange(tests);
                return this;
            }

            internal FloorDecision Refusing(string parameters, params TestRef[] provenBy)
            {
                Refusals.Add(new Refusal(parameters, provenBy));
                return this;
            }

            internal FloorDecision WithKnownGap(string gap)
            {
                KnownGaps.Add(gap);
                return this;
            }
        }

        #endregion
    }
}
