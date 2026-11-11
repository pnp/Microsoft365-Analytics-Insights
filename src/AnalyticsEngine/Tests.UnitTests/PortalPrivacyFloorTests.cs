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
        private const string AdoptionRowsAndTotals =
            "#844: the email-domain table floors a row's combined population rather than each population on it, and totals "
            + "published beside rows (the whole-tenant figures beside the manager rows, the league table's candidates beside "
            + "the tenant's) are not complementarily withheld.";

        private const string CreditWindows =
            "#844 (S3-3): the withholding holds within one response, not across the 7-, 28-, 90- and 180-day windows a reader "
            + "can request, which all end today.";

        private const string InvestmentSmallViews =
            "#844: a view of one to four analysed seat holders still shows evidence and value figures, and the reclaim and "
            + "review-idle actions show the seat cost of one to four people.";

        private const string SpendFewConsumers =
            "#844: the tenant-wide pool balances and the per-agent ledger in a tenant with only a few consumers; #866: the Studio "
            + "note's unmatchedCredits (charges no user matched) is not withheld.";

        private const string EveryoneHoldingALicence =
            "#844: 'Everyone holding a licence' is published beside the per-licence figures without complementary withholding "
            + "across licences, so a withheld small licence can be worked back.";

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
                    + "it back.")
                .By(typeof(CopilotAdoptionService), nameof(CopilotAdoptionService.FinaliseSummary))
                .By(typeof(CopilotAdoptionSummary), nameof(CopilotAdoptionSummary.WithoutIndividualData))
                .By(typeof(LicenceTypeClassification), nameof(LicenceTypeClassification.WithoutSmallSeatIdleCounts))
                .ProvenBy(
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.DepartmentBreakdowns_LeaveOutDepartmentsWithFewerPeopleThanTheMinimum)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.AccountabilityRollup_SuppressesSmallLeaderGroupsLikeSegments)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSummary_WithoutSeePii_LosesTheManagerRollup_AndTheCachedAnalysisKeepsIt)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.SeatIdleCounts_OfACopilotSeatHeldByOneToFourPeople_AreWithheldWithoutSeePii_AndShownWithIt)),
                    Test<CopilotAdoptionTests>(nameof(CopilotAdoptionTests.SeatIdleWithholding_WithholdsTheSmallestSeatThatWouldGiveASmallOneBack_AndNothingWhenNoSeatIsSmall)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSeatIdle_OfACopilotSeatHeldByOneToFourPeople_IsWithheldWithoutSeePii_OnEveryEndpoint)))
                .Refusing("emailDomain, userFilter, seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_NarrowedAggregates_RequireSeePii)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii)))
                .WithKnownGap(AdoptionRowsAndTotals),
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
                    + "classification says why a withheld idle count is not shown, never 0.")
                .By(typeof(CopilotAdoptionService), nameof(CopilotAdoptionService.FinaliseSummary))
                .By(typeof(CopilotAdoptionWorkbook), nameof(CopilotAdoptionWorkbook.Build))
                .By(typeof(LicenceTypeClassification), nameof(LicenceTypeClassification.WithoutSmallSeatIdleCounts))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionWorkbook_WithoutSeePii_NamesNobody_AndWithItStillDoes)),
                    Test<CopilotAdoptionWorkbookTests>(nameof(CopilotAdoptionWorkbookTests.SnapshotFacts_WriteTheSectionsTheGlobalFilterWithholdsBlank_NeverZero)),
                    Test<CopilotAdoptionWorkbookTests>(nameof(CopilotAdoptionWorkbookTests.LicenceClassification_WithoutSeePii_SaysWhyASmallSeatsIdleCountIsNotShown_NeverZero)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoptionSeatIdle_OfACopilotSeatHeldByOneToFourPeople_IsWithheldWithoutSeePii_OnEveryEndpoint)))
                .Refusing("emailDomain, userFilter, seatLicenceTypeIds",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_NarrowedAggregates_RequireSeePii)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.CopilotAdoption_SeatLicenceOverride_NarrowsThePopulation_SoNeedsSeePii)))
                .WithKnownGap(AdoptionRowsAndTotals),

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
                    "Built with a minimum group of 5: a licence of 1-4 holders keeps its count but withholds its activity, "
                    + "departments and countries of fewer than 5 are folded, and when 1-4 people are in view every figure but the "
                    + "counts is withheld. Cached apart from a See PII reader's.")
                .By(typeof(LicenceActivityQuery), nameof(LicenceActivityQuery.WithMinimumGroupSize))
                .By(typeof(LicenceActivityReadModel), nameof(LicenceActivityReadModel.BuildOverview))
                .ProvenBy(
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_DepartmentAndCountryNarrowingIsRefused_AndTheOverviewWithholdsSmallGroups)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_GroupsOfOneToFourAreFoldedOrWithheld_AndAGroupOfFiveIsShown)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_FewerThanFivePeopleWithALicence_WithholdsEveryFigure_ButFiveDoNot)))
                .Refusing("departmentId, countryId",
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_DepartmentAndCountryNarrowingIsRefused_AndTheOverviewWithholdsSmallGroups)))
                .WithKnownGap(EveryoneHoldingALicence),
            ["LicenceActivityAPIController.Export"] = Floored(
                    "The overview the reader was served, refused if it was built with a smaller minimum group, so the workbook "
                    + "folds and withholds what the screen does.")
                .By(typeof(LicenceActivityWorkbook), nameof(LicenceActivityWorkbook.Build))
                .ProvenBy(
                    Test<LicenceActivityApiTests>(nameof(LicenceActivityApiTests.WithoutSeePii_TheScreenAndTheWorkbookFoldTheSameSmallGroups_AndSeePiiSeesThemAll)),
                    Test<LicenceActivityReadModelTests>(nameof(LicenceActivityReadModelTests.WithoutSeePii_TheWorkbookIsTheSameFoldedFigures)))
                .Refusing("usersId",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission)))
                .WithKnownGap(EveryoneHoldingALicence),

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
                    "Teams of fewer than 5 members, and their channels, are left out; reactions of 1-4 people withheld; rare "
                    + "reactions folded; the tab list withheld.")
                .By(typeof(TeamsCollaboration), nameof(TeamsCollaboration.WithoutSmallGroups))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsCollaboration_WithoutSeePii_LeavesOutTeamsOfFewerThanFive_WithholdsFewReactors_OnScreenAndInTheExports)),
                    Test<TeamsExplorerMembershipHorizonSqlIntegrationTests>(nameof(TeamsExplorerMembershipHorizonSqlIntegrationTests.ATeamWhoseOnlyMembershipSnapshotIsOlderThanTheHorizon_IsWithheldFromAReaderWithoutSeePii)))
                .WithKnownGap("#866: the tenant-wide channel-message and reaction KPIs less the team rows shown give the left-out and "
                    + "withheld teams together, which is one team when the tenant has no more teams than the leaderboard shows."),
            ["TeamsExplorerAPIController.Conversations"] = Floored(
                    "Key phrases and languages withheld; the sentiment of teams of fewer than 5 members, and of their channels, left out.")
                .By(typeof(TeamsConversations), nameof(TeamsConversations.WithoutSmallGroups))
                .ProvenBy(Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsConversations_WithoutSeePii_WithholdKeyPhrasesAndLanguages_AndTheSentimentOfTeamsOfFewerThanFive)))
                .WithKnownGap("#866: channel messages and the tenant-wide weekly sentiment are floored by team membership or not at "
                    + "all, because the posters per channel are not stored."),
            ["TeamsExplorerAPIController.People"] = Floored(
                    "A copy without the champion and dormant lists; departments of fewer than 5 power users folded before the cut to top.")
                .By(typeof(TeamsPeople), nameof(TeamsPeople.WithoutIndividualData))
                .By(typeof(TeamsPeople), nameof(TeamsPeople.WithFirstDepartments))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsPeople_WithoutSeePii_KeepTheDepartmentCounts_AndTheCachedSectionKeepsTheNames)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsAdoptionAndPeople_WithoutSeePii_FoldTheSameGroupsWhateverTopIsAskedFor)),
                    Test<TeamsExplorerScoringTests>(nameof(TeamsExplorerScoringTests.PeopleWithoutIndividualData_FoldsDepartmentsWithFewerThanFivePowerUsers_AndNeverLeavesAHandfulInTheFoldedRow)))
                .WithKnownGap("#844 (P4-1): departments beyond the first 500 rows are not folded."),
            ["TeamsExplorerAPIController.Export"] = Floored(
                    "The adoption, teams and channels files carry the same folded and floored rows as the page.")
                .By(typeof(TeamsAdoption), nameof(TeamsAdoption.WithoutSmallGroups))
                .By(typeof(TeamsCollaboration), nameof(TeamsCollaboration.WithoutSmallGroups))
                .ProvenBy(
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsAdoption_WithoutSeePii_FoldsGroupsOfFewerThanFive_OnScreenAndInTheExport_AndTheCachedSectionKeepsThem)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsCollaboration_WithoutSeePii_LeavesOutTeamsOfFewerThanFive_WithholdsFewReactors_OnScreenAndInTheExports)))
                .Refusing("section people, section dormant",
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.TeamsExplorerExport_OnlyThePeopleSectionsNeedSeePii)),
                    Test<PortalPermissionTests>(nameof(PortalPermissionTests.EveryRestrictedEndpoint_RefusesASignedInUserWithoutThePermission))),

            // WebActivityAPIController: the cache holds the complete section, and a reader without See PII is sent a trimmed copy
            // of it (#844, G2). Lists whose queries count no people are withheld rather than floored.
            ["WebActivityAPIController.Availability"] = NoGroupedFigures(
                "Whether the tracker is collecting, when it last saw a page view, and whether search and click tracking are in use."),
            ["WebActivityAPIController.Overview"] = Floored(
                    "Sites visited by fewer than 5 people are left out of the busiest sites, with the smallest site shown, so none can "
                    + "be worked out from the page-view total.")
                .By(typeof(WebActivityOverview), nameof(WebActivityOverview.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesJourneysVisitsAndOverview_WithoutSeePii_WithholdTheListsThatCountNoPeople_AndLeaveOutSmallSites)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut)))
                .WithKnownGap("#844 (S4-1), waiting on the same product decision as Teams Explorer: the engagement-segment mix (people "
                    + "per segment) is not floored. The headline figures, weekly trend, heatmap and visit depth are over the reader's "
                    + "whole scope with no people count (judgement call 1)."),
            ["WebActivityAPIController.Visits"] = Floored(
                    "Sites, devices and browsers of fewer than 5 visitors are left out with the smallest row shown, and a site the list "
                    + "leaves out is folded into '(other sites)' over time; the page list counts no people and is withheld.")
                .By(typeof(WebActivityVisits), nameof(WebActivityVisits.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFolded))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesJourneysVisitsAndOverview_WithoutSeePii_WithholdTheListsThatCountNoPeople_AndLeaveOutSmallSites)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut)))
                .WithKnownGap("The visit trend and the day, hour and period-of-day buckets are over the reader's whole scope with no "
                    + "people count (judgement call 1). A site over time is matched to the site list by display name, so a site "
                    + "outside the list's top N that shares a shown site's title keeps its series."),
            ["WebActivityAPIController.Pages"] = Floored(
                    "Top, slowest and quiet pages are withheld, because their queries count no people; sites of fewer than 5 visitors "
                    + "are left out with the smallest site shown.")
                .By(typeof(WebActivityPages), nameof(WebActivityPages.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesJourneysVisitsAndOverview_WithoutSeePii_WithholdTheListsThatCountNoPeople_AndLeaveOutSmallSites)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallGroups_ReturnsACopy_AndLeavesTheCachedSectionAsItWasBuilt)))
                .WithKnownGap("The weekly period-of-day chart is over the reader's whole scope with no people count (judgement call 1). "
                    + "The page lists could be floored instead of withheld once their queries count distinct visitors."),
            ["WebActivityAPIController.Journeys"] = Floored(
                    "Entry, exit and bounce pages, transitions, flows and clicked elements are withheld: none of their queries counts "
                    + "the people behind a row.")
                .By(typeof(WebActivityJourneys), nameof(WebActivityJourneys.WithoutSmallGroups))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.PagesJourneysVisitsAndOverview_WithoutSeePii_WithholdTheListsThatCountNoPeople_AndLeaveOutSmallSites)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallGroups_ReturnsACopy_AndLeavesTheCachedSectionAsItWasBuilt)))
                .WithKnownGap("The visit-depth buckets and the click total are over the reader's whole scope with no people count "
                    + "(judgement call 1)."),
            ["WebActivityAPIController.Geography"] = Floored(
                    "Countries, provinces and cities fewer than 5 people visited from are left out, each list with its smallest place "
                    + "shown, so no place can be worked out from the located page views; a country the list leaves out is folded into "
                    + "'(other countries)' over time.")
                .By(typeof(WebActivityGeography), nameof(WebActivityGeography.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFolded))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Geography_WithoutSeePii_LeavesOutPlacesOfFewerThanFiveVisitors_AndWhatIsLeftOutIsNeverOneSmallPlace)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut))),
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
                    + "5 visitors, each list with its smallest row shown; a device the list leaves out is folded into '(other devices)' "
                    + "over time.")
                .By(typeof(WebActivityTechnology), nameof(WebActivityTechnology.WithoutSmallGroups))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithoutSmallRows))
                .By(typeof(WebActivitySmallGroups), nameof(WebActivitySmallGroups.WithSeriesFolded))
                .ProvenBy(
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Technology_WithoutSeePii_LeavesOutDetailRowsAndPlatformsOfFewerThanFiveVisitors)),
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.WithoutSmallRows_LeavesOutTheSmallestShownRowWithTheSmallOnes_ChosenByPeopleThenName_SoNoRowCanBeWorkedOut))),
            ["WebActivityAPIController.Export"] = Floored(
                    "The technology file carries the screen's floored detail rows, cached apart from a See PII reader's file.")
                .By(typeof(WebActivityTechnology), nameof(WebActivityTechnology.WithoutSmallGroups))
                .ProvenBy(Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Export_WithoutSeePii_RefusesEveryFileItsScreenWithholds_AndTheTechnologyFileCarriesTheScreensRows)))
                .Refusing("section pages, quiet-pages, slow-pages, entry-pages, exit-pages, transitions, flows, search-terms",
                    Test<WebActivityPrivacyFloorTests>(nameof(WebActivityPrivacyFloorTests.Export_WithoutSeePii_RefusesEveryFileItsScreenWithholds_AndTheTechnologyFileCarriesTheScreensRows))),
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
