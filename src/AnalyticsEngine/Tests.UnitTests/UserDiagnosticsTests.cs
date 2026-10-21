extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDiagnostics;
using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.Licences;
using Common.Entities.State;
using Common.Entities.UserDiagnostics;
using Common.Entities.UserScope;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// The per-user licence and coverage diagnostics behind Administration &gt; User lookup: Copilot classification
    /// explanations, the licensing catalogue, the stored-versus-live comparison, the report verdict trace and the scope
    /// gate on live directory reads. Everything runs against fakes - no SQL Server or Graph.
    /// </summary>
    [TestClass]
    public class UserDiagnosticsTests
    {
        private const string Upn = "jane.doe@contoso.com";
        private const string ObjectId = "00000000-0000-0000-0000-000000000001";
        private static readonly Guid CopilotSkuGuid = new Guid("00000000-0000-0000-0000-0000000000c1");
        private static readonly Guid E3SkuGuid = new Guid("00000000-0000-0000-0000-0000000000e3");
        private static readonly DateTime Now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        #region Classification explanations

        [DataTestMethod]
        [DataRow("Microsoft_365_Copilot", "Microsoft 365 Copilot", true, CopilotSeatClassificationReasons.SeatSkuPrefix, "MICROSOFT_365_COPILOT")]
        [DataRow("MICROSOFT_365_E7", "Microsoft 365 E7", true, CopilotSeatClassificationReasons.SeatSkuPrefix, "MICROSOFT_365_E7")]
        [DataRow("Microsoft_Copilot_for_Sales", "Microsoft 365 Copilot for Sales", false, CopilotSeatClassificationReasons.ExcludedSkuPrefix, "MICROSOFT_COPILOT_FOR_SALES")]
        [DataRow("CONTOSO_NEW_SKU", "Microsoft 365 Copilot (Contoso edition)", true, CopilotSeatClassificationReasons.SeatDisplayName, "copilot + microsoft 365")]
        [DataRow("CONTOSO_STUDIO", "Copilot Studio for Contoso", false, CopilotSeatClassificationReasons.ExcludedNameWord, "studio")]
        [DataRow("CONTOSO_GENERIC", "Contoso Copilot", false, CopilotSeatClassificationReasons.CopilotNameWithoutQualifier, null)]
        [DataRow("ENTERPRISEPACK", "Office 365 E3", false, CopilotSeatClassificationReasons.NoRuleMatched, null)]
        public void Explain_NamesTheRuleThatDecided(string sku, string name, bool seat, string reason, string rule)
        {
            var explanation = CopilotLicenceClassifier.Explain(sku, name);

            Assert.AreEqual(seat, explanation.IsCopilotSeat);
            Assert.AreEqual(reason, explanation.Reason);
            Assert.AreEqual(rule, explanation.MatchedRule);
            Assert.AreEqual(seat, CopilotLicenceClassifier.IsCopilotSeat(sku, name), "IsCopilotSeat must stay the same decision.");
        }

        [TestMethod]
        public void ApplyOverride_RecordsWhereTheReportDisagreedWithTheRules()
        {
            var classified = CopilotLicenceClassifier.Classify(new[]
            {
                new LicenceTypeRow { Id = 1, Name = "Microsoft 365 Copilot", SkuPartNumber = "Microsoft_365_Copilot" },
                new LicenceTypeRow { Id = 2, Name = "Microsoft 365 Copilot for Sales", SkuPartNumber = "Microsoft_Copilot_for_Sales" },
            });

            CopilotLicenceClassifier.ApplyOverride(classified, new[] { 2 });

            Assert.IsFalse(classified[0].IsCopilotSeat);
            Assert.AreEqual(CopilotSeatClassificationReasons.ReportOverrideExcluded, classified[0].ClassificationReason);
            Assert.AreEqual("MICROSOFT_365_COPILOT", classified[0].MatchedRule, "The rule the override replaced stays visible.");
            Assert.IsTrue(classified[1].IsCopilotSeat);
            Assert.AreEqual(CopilotSeatClassificationReasons.ReportOverrideIncluded, classified[1].ClassificationReason);
        }

        #endregion

        #region Licensing catalogue

        [TestMethod]
        public void Catalogue_ParsesQuotedFieldsGuidsAndServicePlans()
        {
            var csv = "Product_Display_Name,String_Id,GUID,Service_Plan_Name,Service_Plan_Id,Service_Plans_Included_Friendly_Names\r\n" +
                      $"\"Contoso, Copilot \"\"Plus\"\"\",CONTOSO_COPILOT,{CopilotSkuGuid},M365_COPILOT_APPS,00000000-0000-0000-0000-000000000000,\"Apps, in Office\"\r\n" +
                      $"\"Contoso, Copilot \"\"Plus\"\"\",CONTOSO_COPILOT,{CopilotSkuGuid},M365_COPILOT_BUSINESS_CHAT,00000000-0000-0000-0000-000000000000,Chat\r\n" +
                      $"Contoso E3,CONTOSO_E3\t,{E3SkuGuid},EXCHANGE_S_ENTERPRISE,00000000-0000-0000-0000-000000000000,Exchange\r\n";

            var catalogue = LicensingCatalogue.Parse(new StringReader(csv));

            var copilot = catalogue.FindByGuid(CopilotSkuGuid);
            Assert.AreEqual("CONTOSO_COPILOT", copilot.PartNumber);
            Assert.AreEqual("Contoso, Copilot \"Plus\"", copilot.DisplayName);
            Assert.IsTrue(copilot.CarriesCopilotSeatServicePlans);
            Assert.IsFalse(catalogue.FindByPartNumber("contoso_e3").CarriesCopilotSeatServicePlans, "Lookups ignore case and the stray tab.");
            Assert.AreEqual(2, catalogue.SkuCount);
            Assert.AreEqual(12, catalogue.ContentHash.Length);
        }

        /// <summary>
        /// The importer names licence types with OfficeLicenseNameResolver and the portal predicts those names with the
        /// catalogue. If the two parsers ever disagreed the comparison would report false catalogue drift.
        /// </summary>
        [TestMethod]
        public void Catalogue_NamesEverySkuExactlyAsTheImporterDoes()
        {
            var resolver = new OfficeLicenseNameResolver();
            var catalogue = LicensingCatalogue.Embedded;

            Assert.AreEqual(resolver.SkuPartNumbers.Count, catalogue.SkuCount);
            foreach (var sku in resolver.SkuPartNumbers)
            {
                Assert.AreEqual(resolver.GetDisplayNameFor(sku), catalogue.FindByPartNumber(sku)?.DisplayName, sku);
            }
        }

        [TestMethod]
        public void Catalogue_KnowsWhichShippedSkusCarryTheCopilotSeat()
        {
            var catalogue = LicensingCatalogue.Embedded;

            Assert.IsTrue(catalogue.FindByPartNumber("Microsoft_365_Copilot").CarriesCopilotSeatServicePlans);
            Assert.IsTrue(catalogue.FindByPartNumber("MICROSOFT_365_E7").CarriesCopilotSeatServicePlans);
            Assert.IsFalse(catalogue.FindByPartNumber("ENTERPRISEPACK").CarriesCopilotSeatServicePlans);
        }

        #endregion

        #region Live comparison

        private static List<StoredLicenceType> TenantTypes() => new List<StoredLicenceType>
        {
            new StoredLicenceType { Id = 1, Name = "Contoso Copilot", SkuPartNumber = "CONTOSO_COPILOT" },
            new StoredLicenceType { Id = 2, Name = "Contoso E3", SkuPartNumber = "CONTOSO_E3" },
        };

        private static LicensingCatalogue TestCatalogue(params string[] extraRows)
        {
            var csv = "Product_Display_Name,String_Id,GUID,Service_Plan_Name,Service_Plan_Id,Service_Plans_Included_Friendly_Names\r\n" +
                      $"Contoso Copilot,CONTOSO_COPILOT,{CopilotSkuGuid},M365_COPILOT_APPS,00000000-0000-0000-0000-000000000000,x\r\n" +
                      $"Contoso Copilot,CONTOSO_COPILOT,{CopilotSkuGuid},M365_COPILOT_BUSINESS_CHAT,00000000-0000-0000-0000-000000000000,x\r\n" +
                      $"Contoso E3,CONTOSO_E3,{E3SkuGuid},EXCHANGE_S_ENTERPRISE,00000000-0000-0000-0000-000000000000,x\r\n" +
                      string.Join("", extraRows.Select(r => r + "\r\n"));
            return LicensingCatalogue.Parse(new StringReader(csv));
        }

        private static LiveDirectoryObservation Live(params Guid[] skus) => new LiveDirectoryObservation
        {
            ObjectId = ObjectId,
            UserPrincipalName = Upn,
            AccountEnabled = true,
            AssignedLicencesPresent = true,
            AssignedSkuIds = skus.ToList(),
            ObservedAtUtc = Now,
        };

        [TestMethod]
        public void Compare_ClassifiesMatchesAndDifferences()
        {
            var result = LiveLicenceComparer.Compare(TenantTypes(), new[] { 2 }, Live(CopilotSkuGuid), TestCatalogue(), ObjectId, Upn);

            Assert.IsTrue(result.Complete);
            var copilot = result.Rows.Single(r => r.LicenceTypeId == 1);
            Assert.AreEqual(LiveComparisonOutcomes.LiveOnly, copilot.Comparison, "Graph assigns it; SQL does not hold it.");
            Assert.AreEqual(SkuMappingStates.Unique, copilot.Mapping);
            Assert.AreEqual(CopilotSeatClassificationReasons.CopilotNameWithoutQualifier, copilot.Classification.Reason,
                "Each row carries the same explanation the reports use.");
            var e3 = result.Rows.Single(r => r.LicenceTypeId == 2);
            Assert.AreEqual(LiveComparisonOutcomes.StoredOnly, e3.Comparison, "A complete live list proves SQL holds a stale licence.");
            Assert.IsTrue(result.Directory.ObjectIdMatchesStored.Value);
        }

        /// <summary>The authoritative-empty rule: without the assignedLicenses array nothing can be "stored only".</summary>
        [TestMethod]
        public void Compare_WithoutTheAssignmentList_ClaimsNothingIsStale()
        {
            var live = Live();
            live.AssignedLicencesPresent = false;

            var result = LiveLicenceComparer.Compare(TenantTypes(), new[] { 1, 2 }, live, TestCatalogue(), ObjectId, Upn);

            Assert.IsFalse(result.Complete);
            Assert.IsTrue(result.Rows.All(r => r.Comparison == LiveComparisonOutcomes.Indeterminate));
        }

        [TestMethod]
        public void Compare_AnUnidentifiedSku_MakesStoredOnlyIndeterminate_AndIsShownUnmapped()
        {
            var unknown = new Guid("00000000-0000-0000-0000-0000000000ff");

            var result = LiveLicenceComparer.Compare(TenantTypes(), new[] { 2 }, Live(unknown), TestCatalogue(), ObjectId, Upn);

            Assert.IsFalse(result.Complete, "An SKU nobody can name might be the stored one.");
            Assert.AreEqual(LiveComparisonOutcomes.Indeterminate, result.Rows.Single(r => r.LicenceTypeId == 2).Comparison);
            var unmapped = result.Rows.Single(r => r.LicenceTypeId == null);
            Assert.AreEqual(SkuMappingStates.Unmapped, unmapped.Mapping);
            Assert.AreEqual(LiveComparisonOutcomes.LiveOnly, unmapped.Comparison);
        }

        [TestMethod]
        public void Compare_TwoSkusWithOneProductName_AreCollapsedNotMismatched()
        {
            var otherGuid = new Guid("00000000-0000-0000-0000-0000000000c2");
            var catalogue = TestCatalogue($"Contoso Copilot,CONTOSO_COPILOT_EDU,{otherGuid},M365_COPILOT_APPS,00000000-0000-0000-0000-000000000000,x");

            var result = LiveLicenceComparer.Compare(TenantTypes(), new[] { 1 }, Live(CopilotSkuGuid, otherGuid), catalogue, ObjectId, Upn);

            var row = result.Rows.Single(r => r.LicenceTypeId == 1);
            Assert.AreEqual(SkuMappingStates.Collapsed, row.Mapping);
            Assert.AreEqual(LiveComparisonOutcomes.Match, row.Comparison);
            Assert.AreEqual(2, row.LiveSkus.Count);
        }

        [TestMethod]
        public void Compare_ARenamedProduct_IsCatalogueDriftNotAMissingLicence()
        {
            var types = new List<StoredLicenceType> { new StoredLicenceType { Id = 7, Name = "Contoso Copilot (old name)", SkuPartNumber = "CONTOSO_COPILOT" } };

            var result = LiveLicenceComparer.Compare(types, new[] { 7 }, Live(CopilotSkuGuid), TestCatalogue(), ObjectId, Upn);

            var row = result.Rows.Single();
            Assert.AreEqual(SkuMappingStates.CatalogueDrift, row.Mapping);
            Assert.AreEqual(LiveComparisonOutcomes.Match, row.Comparison);
        }

        [TestMethod]
        public void Compare_ReportsCopilotPlansSwitchedOffAndAssignmentErrors()
        {
            var live = Live(CopilotSkuGuid);
            live.AssignmentStates.Add(new LiveAssignmentState { SkuId = CopilotSkuGuid, AssignedByGroup = "00000000-0000-0000-0000-00000000000a", State = "Error", Error = "CountViolation" });
            live.LicenceDetails = new List<LiveLicenceDetail>
            {
                new LiveLicenceDetail
                {
                    SkuId = CopilotSkuGuid,
                    SkuPartNumber = "CONTOSO_COPILOT",
                    ServicePlanStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "M365_COPILOT_APPS", "Disabled" },
                        { "M365_COPILOT_BUSINESS_CHAT", "Success" },
                    },
                },
            };

            var result = LiveLicenceComparer.Compare(TenantTypes(), new[] { 1 }, live, TestCatalogue(), ObjectId, Upn);

            var sku = result.Rows.Single(r => r.LicenceTypeId == 1).LiveSkus.Single();
            Assert.AreEqual(CopilotPlanStates.Disabled, sku.CopilotSeatPlans);
            CollectionAssert.AreEqual(new[] { "group" }, sku.AssignmentPaths);
            Assert.AreEqual("Error", sku.State);
            Assert.AreEqual("CountViolation", sku.Error);
            Assert.AreEqual("licenseDetails", result.PartNumberSource);
        }

        [TestMethod]
        public void ParseUser_TreatsAMissingOrUnreadableAssignmentListAsIncomplete()
        {
            var missing = GraphLiveDirectoryReader.ParseUser(JObject.Parse($"{{\"id\":\"{ObjectId}\",\"userPrincipalName\":\"{Upn}\"}}"), Now);
            var empty = GraphLiveDirectoryReader.ParseUser(JObject.Parse($"{{\"id\":\"{ObjectId}\",\"assignedLicenses\":[]}}"), Now);
            var garbled = GraphLiveDirectoryReader.ParseUser(JObject.Parse($"{{\"id\":\"{ObjectId}\",\"assignedLicenses\":[{{\"skuId\":\"not-a-guid\"}}]}}"), Now);

            Assert.IsFalse(missing.AssignedLicencesPresent);
            Assert.IsTrue(empty.AssignedLicencesPresent, "An empty array is an authoritative 'no licences'.");
            Assert.IsFalse(garbled.AssignedLicencesPresent);
        }

        #endregion

        #region Scope gate

        private static UserImportScopeResolution Resolved(params (string id, string upn)[] members)
        {
            var set = new UserScopeMembers();
            foreach (var m in members) set.Add(m.id, m.upn, null);
            return UserImportScopeResolution.Resolved(set, new List<ResolvedGroup> { new ResolvedGroup("g", "Pilot", new[] { "Pilot" }, members.Length) }, null, Now);
        }

        [TestMethod]
        public void DecideScope_OnlyAllowsLiveReadsWhenScopeIsProven()
        {
            Assert.AreEqual(UserScopeDecisions.Unfiltered, UserDiagnosticsService.DecideScope(UserImportScopeResolution.Unfiltered(Now), Upn).Decision);

            var member = UserDiagnosticsService.DecideScope(Resolved((ObjectId, "other@contoso.com")), Upn, null, ObjectId);
            Assert.AreEqual(UserScopeDecisions.InScope, member.Decision, "Matched by object id even after a rename.");
            Assert.IsTrue(member.LiveComparisonAllowed);

            var excluded = UserDiagnosticsService.DecideScope(Resolved(("00000000-0000-0000-0000-000000000002", "other@contoso.com")), Upn);
            Assert.AreEqual(UserScopeDecisions.Excluded, excluded.Decision);
            Assert.IsFalse(excluded.LiveComparisonAllowed);

            var partial = new UserScopeMembers();
            partial.Add(ObjectId, Upn, null);
            var unknown = UserDiagnosticsService.DecideScope(
                UserImportScopeResolution.Unavailable(partial, null, "budget", Now, UserImportScopeFailureKind.BudgetExhausted), Upn, null, ObjectId);
            Assert.AreEqual(UserScopeDecisions.Unknown, unknown.Decision, "A partly-read member list proves nothing.");
            Assert.IsFalse(unknown.LiveComparisonAllowed);
            Assert.AreEqual("budgetExhausted", unknown.FailureKind);
        }

        [TestMethod]
        public async Task CompareLive_OutsideScope_IsRefusedBeforeAnyGraphCall()
        {
            var reader = new FakeLiveReader();
            var service = Service(new FakeDiagnosticsQuery(), Resolved(("00000000-0000-0000-0000-000000000002", "other@contoso.com")), reader);

            var result = await service.CompareLiveAsync(Upn, CancellationToken.None);

            Assert.AreEqual(LiveComparisonStatuses.NotAllowed, result.Value.Status);
            Assert.AreEqual(LiveComparisonReasons.ScopeExcluded, result.Value.Reason);
            Assert.AreEqual(0, reader.Calls);
        }

        [TestMethod]
        public async Task CompareLive_UsesTheStoredObjectId_AndReportsGraphFailures()
        {
            var query = new FakeDiagnosticsQuery { User = StoredUser() };
            var reader = new FakeLiveReader { Result = LiveDirectoryReadResult.Failed(LiveComparisonReasons.PermissionDenied, 403) };
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), reader);

            var result = await service.CompareLiveAsync(Upn, CancellationToken.None);

            Assert.AreEqual(ObjectId, reader.LastKey, "The object id survives a UPN rename.");
            Assert.AreEqual(LiveComparisonStatuses.Failed, result.Value.Status);
            Assert.AreEqual(LiveComparisonReasons.PermissionDenied, result.Value.Reason);
            Assert.AreEqual(403, result.Value.HttpStatus);
        }

        #endregion

        #region Diagnostics and findings

        private static StoredUserRow StoredUser() => new StoredUserRow
        {
            Id = 42,
            UserPrincipalName = Upn,
            AzureAdId = ObjectId,
            AccountEnabled = true,
            LastUpdated = Now.AddDays(-30),
        };

        [TestMethod]
        public async Task Diagnostics_ForSomeoneNeverImported_StillAnswers()
        {
            var service = Service(new FakeDiagnosticsQuery(), UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var result = await service.GetDiagnosticsAsync(Upn);

            Assert.AreEqual(UserDataLookupStatus.Ok, result.Status);
            Assert.IsNull(result.Value.StoredUser);
            Assert.IsTrue(result.Value.Scope.LiveComparisonAllowed);
            Assert.IsTrue(result.Value.Findings.Any(f => f.Key == DiagnosticFindingKeys.NoStoredUser));
        }

        [TestMethod]
        public async Task Diagnostics_ExplainLicencesHistoryAndStaleImports()
        {
            var query = new FakeDiagnosticsQuery
            {
                User = StoredUser(),
                Types = new List<LicenceTypeRow>
                {
                    new LicenceTypeRow { Id = 1, Name = "Microsoft 365 Copilot", SkuPartNumber = "Microsoft_365_Copilot", AssignedUsers = 10 },
                    new LicenceTypeRow { Id = 2, Name = "Microsoft 365 Copilot for Sales", SkuPartNumber = "Microsoft_Copilot_for_Sales", AssignedUsers = 3 },
                },
                Stored = new List<StoredLicenceType> { new StoredLicenceType { Id = 1, Name = "Microsoft 365 Copilot", SkuPartNumber = "Microsoft_365_Copilot" } },
                History = new LicenceHistoryRead
                {
                    Available = true,
                    TrackingBeganUtc = Now.AddDays(-90),
                    LastRefreshUtc = Now.AddDays(-5),
                    Rows = new List<LicenceHistoryRowModel> { new LicenceHistoryRowModel { LicenceTypeId = 1, ValidFromUtc = Now.AddDays(-90), HeldBeforeTrackingBegan = true } },
                },
            };
            var stamps = new FakeStamps { [ImportScheduleKeys.GraphUsersMetadata] = new ImportStamp(StampStates.Recorded, Now.AddDays(-5)) };
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader(), stamps, intervalHours: 24);

            var model = (await service.GetDiagnosticsAsync(Upn)).Value;

            var licence = model.Licences.Single();
            Assert.AreEqual(CopilotSeatClassificationReasons.SeatSkuPrefix, licence.Classification.Reason);
            Assert.AreEqual(HeldSinceKinds.BeforeTrackingBegan, licence.HeldSinceKind, "A seeded row is not an assignment date.");
            Assert.IsTrue(licence.CatalogueCarriesCopilotSeat);
            Assert.AreEqual(UserVerificationStates.NotRecorded, model.Freshness.UserVerification, "A tenant-wide run is not proof this person was checked.");
            Assert.IsTrue(model.Findings.Any(f => f.Key == DiagnosticFindingKeys.UserImportOverdue));

            // The catalogue says Copilot for Sales carries the seat plans; the rules exclude it. Surfaced, not changed.
            var sales = model.Catalogue.TenantLicenceTypes.Single(t => t.Id == 2);
            Assert.IsTrue(sales.PossibleRuleGap);
            Assert.IsFalse(sales.Classification.IsCopilotSeat);
            Assert.IsTrue(model.Findings.Any(f => f.Key == DiagnosticFindingKeys.PossibleCopilotRuleGap && (string)f.Values["sku"] == "Microsoft_Copilot_for_Sales"));
        }

        [TestMethod]
        public async Task Diagnostics_NoStoredLicences_IsNotCalledUnlicensed()
        {
            var query = new FakeDiagnosticsQuery { User = StoredUser() };
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var model = (await service.GetDiagnosticsAsync(Upn)).Value;

            Assert.IsTrue(model.Findings.Any(f => f.Key == DiagnosticFindingKeys.NoStoredLicences));
            Assert.IsTrue(model.Findings.Any(f => f.Key == DiagnosticFindingKeys.UserImportNotRecorded), "No stamp is not a stale import, nor a fresh one.");
        }

        #endregion

        #region Report verdict

        private static FakeDiagnosticsQuery VerdictQuery() => new FakeDiagnosticsQuery
        {
            User = StoredUser(),
            Types = new List<LicenceTypeRow>
            {
                new LicenceTypeRow { Id = 1, Name = "Microsoft 365 Copilot", SkuPartNumber = "Microsoft_365_Copilot" },
                new LicenceTypeRow { Id = 2, Name = "Office 365 E3", SkuPartNumber = "ENTERPRISEPACK" },
            },
            HistoryStart = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        [TestMethod]
        public async Task Verdict_RollingWindow_UsesCurrentAssignmentsWithTheReportsSeats()
        {
            var query = VerdictQuery();
            query.Evidence = new List<SeatEvidenceRow> { new SeatEvidenceRow { LicenceTypeId = 1 } };
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var verdict = (await service.GetReportVerdictAsync(Upn, "copilotAdoption", 28, null, null, new List<int>())).Value;

            Assert.AreEqual(ReportVerdicts.Licensed, verdict.Verdict);
            Assert.AreEqual(ReportVerdictBases.CurrentAssignments, verdict.Basis);
            CollectionAssert.AreEqual(new[] { 1 }, query.LastEvidenceIds.ToArray(), "Only the licence types the report counts as seats.");
            Assert.IsFalse(query.LastUseHistory);
            CollectionAssert.Contains(verdict.Notes, ReportVerdictNotes.RollingWindowUsesCurrentAssignments);
            StringAssert.Contains(verdict.Sql, "user_license_type_lookups");
        }

        [TestMethod]
        public async Task Verdict_PastPeriod_UsesHistory_AndSaysWhenItStartedLate()
        {
            var query = VerdictQuery();
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var verdict = (await service.GetReportVerdictAsync(Upn, "copilotAdoption", 28, "2026-08-15", "2026-09-14", new List<int>())).Value;

            Assert.AreEqual(ReportVerdicts.Unlicensed, verdict.Verdict);
            Assert.AreEqual(ReportVerdictBases.LicenceHistory, verdict.Basis);
            Assert.IsTrue(query.LastUseHistory);
            CollectionAssert.Contains(verdict.Notes, ReportVerdictNotes.HistoryPartialForPeriod);
            StringAssert.Contains(verdict.Sql, "user_license_history");
        }

        [TestMethod]
        public async Task Verdict_WithAWhatIfSelection_CountsWhatTheReportCounted()
        {
            var query = VerdictQuery();
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var verdict = (await service.GetReportVerdictAsync(Upn, "copilotAdoption", 28, null, null, new List<int> { 2 })).Value;

            Assert.AreEqual("reportOverride", verdict.SeatSelection);
            CollectionAssert.AreEqual(new[] { 2 }, query.LastEvidenceIds.ToArray());
            Assert.AreEqual(CopilotSeatClassificationReasons.ReportOverrideIncluded, verdict.CountedLicenceTypes.Single().Classification.Reason);
        }

        [TestMethod]
        public async Task Verdict_RejectsAnInvalidRangeWithTheReportsOwnKey()
        {
            var service = Service(VerdictQuery(), UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var result = await service.GetReportVerdictAsync(Upn, "copilotAdoption", 28, "2026-09-14", null, null);

            Assert.AreEqual(UserDataLookupStatus.BadRequest, result.Status);
            Assert.AreEqual("copilotAdoption.error.missingDate", result.ErrorMessage);
        }

        [TestMethod]
        public void UserSeatEvidenceSql_SharesTheReportsHistoryRule()
        {
            var condition = CopilotAdoptionSql.HistoryHoldingCondition("h", "  ");

            StringAssert.Contains(CopilotAdoptionSql.UserSeatEvidenceSql(new[] { 1 }, true), condition);
            StringAssert.Contains(CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], false, useLicenceHistory: true),
                CopilotAdoptionSql.HistoryHoldingCondition("h", "      "));
        }

        #endregion

        #region Coverage and stamps

        [TestMethod]
        public void CoverageRules_CoverEveryDatedLookupCategory()
        {
            foreach (var key in UserCoverageRules.CategoryKeys)
            {
                Assert.IsTrue(UserCoverageRules.IsKnown(key), $"No coverage rule for '{key}'.");
            }
            Assert.AreEqual(CoverageLatestKinds.ReportDate, UserCoverageRules.LatestKind(UserDataLookupRules.CatUsageTeams), "A usage-report row date is not an activity date.");
            Assert.AreEqual(ImportScheduleKeys.UsageReport("TeamsUserUsageLoader"), UserCoverageRules.StampKey(UserDataLookupRules.CatUsageTeams));
        }

        /// <summary>The coverage stamps name importer loader types; a rename must fail here, not read as "never completed".</summary>
        [TestMethod]
        public void CoverageRules_NameRealUsageReportLoaders()
        {
            var engine = typeof(GraphImporter).Assembly;
            foreach (var key in UserCoverageRules.CategoryKeys.Select(UserCoverageRules.StampKey).Where(k => k != null && k.StartsWith(ImportScheduleKeys.UsageReportPrefix)))
            {
                var loader = key.Substring(ImportScheduleKeys.UsageReportPrefix.Length);
                Assert.IsTrue(engine.GetTypes().Any(t => t.Name == loader), $"No importer type named '{loader}'.");
            }
        }

        [TestMethod]
        public void StampParse_ReturnsUtc_AndTreatsGarbageAsNotRecorded()
        {
            var stamp = TableImportStampReader.Parse("2026-10-21T14:00:00.0000000+02:00");
            Assert.AreEqual(StampStates.Recorded, stamp.State);
            Assert.AreEqual(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc), stamp.LastSuccessUtc);
            Assert.AreEqual(StampStates.NotRecorded, TableImportStampReader.Parse("yesterday").State);
        }

        [TestMethod]
        public async Task Coverage_OneFailingCategory_DoesNotLoseTheRest()
        {
            var query = new FakeDiagnosticsQuery { User = StoredUser(), FailLatestFor = UserDataLookupRules.CatAuditEvents };
            var service = Service(query, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader());

            var model = (await service.GetCoverageAsync(Upn)).Value;

            Assert.IsTrue(model.Categories.Single(c => c.Key == UserDataLookupRules.CatAuditEvents).Error);
            Assert.AreEqual(StampStates.NoStampForSource, model.Categories.Single(c => c.Key == UserDataLookupRules.CatAuditEvents).SourceStampState);
            Assert.AreEqual(UserCoverageRules.CategoryKeys.Count, model.Categories.Count);
            Assert.IsTrue(model.Categories.Where(c => c.Key != UserDataLookupRules.CatAuditEvents).All(c => !c.Error));
        }

        #endregion

        #region One-person refresh requests

        [TestMethod]
        public async Task SubmitRefresh_IsRefused_WithoutConfirmedScopeOrAStoredUser_OrStorage()
        {
            var store = new UserRefreshStore(new InMemoryKeyValueStore(() => Now), () => Now);
            var stored = new FakeDiagnosticsQuery { User = StoredUser() };
            var unavailable = UserImportScopeResolution.Unavailable(new UserScopeMembers(), null, "budget", Now, UserImportScopeFailureKind.BudgetExhausted);

            Assert.AreEqual(UserRefreshRejections.ScopeUnknown, (await Service(stored, unavailable, new FakeLiveReader(), refreshStore: store).SubmitRefreshAsync(Upn, "admin@contoso.com")).Value.Rejection);
            Assert.AreEqual(UserRefreshRejections.ScopeExcluded, (await Service(stored, Resolved(("00000000-0000-0000-0000-000000000002", "other@contoso.com")), new FakeLiveReader(), refreshStore: store).SubmitRefreshAsync(Upn, "admin@contoso.com")).Value.Rejection);
            Assert.AreEqual(UserRefreshRejections.NotStored, (await Service(new FakeDiagnosticsQuery(), UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader(), refreshStore: store).SubmitRefreshAsync(Upn, "admin@contoso.com")).Value.Rejection);
            Assert.AreEqual(UserRefreshRejections.StorageNotConfigured, (await Service(stored, UserImportScopeResolution.Unfiltered(Now), new FakeLiveReader()).SubmitRefreshAsync(Upn, "admin@contoso.com")).Value.Rejection);
            Assert.IsFalse(await store.HasQueuedAsync(), "Nothing refused may reach the importer's queue.");
        }

        [TestMethod]
        public async Task SubmitRefresh_QueuesOnce_AndDiagnosticsShowItAndTheLastVerification()
        {
            var store = new UserRefreshStore(new InMemoryKeyValueStore(() => Now), () => Now);
            var service = Service(new FakeDiagnosticsQuery { User = StoredUser() }, Resolved((ObjectId, Upn)), new FakeLiveReader(), refreshStore: store);

            var first = (await service.SubmitRefreshAsync(Upn, "admin@contoso.com")).Value;
            var second = (await service.SubmitRefreshAsync(Upn.ToUpperInvariant(), "other.admin@contoso.com")).Value;

            Assert.IsTrue(first.Accepted);
            Assert.AreEqual(first.Request.Id, second.Request.Id, "A second click joins the waiting request.");
            Assert.AreEqual(42, first.Request.UserId);
            Assert.AreEqual(UserRefreshStates.Queued, first.Request.State);

            await store.RecordVerificationAsync(42, new UserVerificationRecord { VerifiedUtc = Now.AddMinutes(-5), Outcome = UserRefreshOutcomes.Unchanged, RequestId = "earlier" });
            var diagnostics = (await service.GetDiagnosticsAsync(Upn)).Value;

            Assert.IsTrue(diagnostics.Refresh.Available);
            Assert.AreEqual(first.Request.Id, diagnostics.Refresh.Active?.Id);
            Assert.AreEqual(UserVerificationStates.Verified, diagnostics.Freshness.UserVerification);
            Assert.AreEqual(Now.AddMinutes(-5), diagnostics.Freshness.UserVerifiedUtc);
            Assert.AreEqual(first.Request.Id, (await service.GetRefreshAsync(first.Request.Id)).Id);
            Assert.IsNull(await service.GetRefreshAsync("no-such-request"));
        }

        #endregion

        #region Fakes

        private static UserDiagnosticsService Service(FakeDiagnosticsQuery query, UserImportScopeResolution scope, FakeLiveReader reader,
            FakeStamps stamps = null, int intervalHours = 24, UserRefreshStore refreshStore = null)
        {
            return new UserDiagnosticsService(query, () => Task.FromResult(scope), reader, stamps ?? new FakeStamps(),
                () => new ImportTaskSettings { GraphUsersMetadata = true, GraphUsageReports = true, ActivityLog = true, Copilot = true },
                intervalHours, LicensingCatalogue.Embedded, "test-build", () => Now, refreshStore);
        }

        private sealed class FakeStamps : Dictionary<string, ImportStamp>, IImportStampReader
        {
            public Task<IReadOnlyDictionary<string, ImportStamp>> ReadAsync(IEnumerable<string> keys)
            {
                IReadOnlyDictionary<string, ImportStamp> result = keys.Distinct().ToDictionary(k => k, k => TryGetValue(k, out var s) ? s : new ImportStamp(StampStates.NotRecorded, null));
                return Task.FromResult(result);
            }
        }

        private sealed class FakeLiveReader : ILiveDirectoryReader
        {
            public LiveDirectoryReadResult Result { get; set; } = LiveDirectoryReadResult.Ok(new LiveDirectoryObservation { ObjectId = ObjectId, UserPrincipalName = Upn, AssignedLicencesPresent = true, ObservedAtUtc = Now });
            public int Calls { get; private set; }
            public string LastKey { get; private set; }

            public Task<LiveDirectoryReadResult> ReadAsync(string objectIdOrUserPrincipalName, CancellationToken cancellationToken, string profileSelect = null)
            {
                Calls++;
                LastKey = objectIdOrUserPrincipalName;
                return Task.FromResult(Result);
            }
        }

        private sealed class FakeDiagnosticsQuery : IUserDiagnosticsQuery
        {
            public StoredUserRow User { get; set; }
            public List<LicenceTypeRow> Types { get; set; } = new List<LicenceTypeRow>();
            public List<StoredLicenceType> Stored { get; set; } = new List<StoredLicenceType>();
            public LicenceHistoryRead History { get; set; } = new LicenceHistoryRead();
            public DateTime? HistoryStart { get; set; }
            public List<SeatEvidenceRow> Evidence { get; set; } = new List<SeatEvidenceRow>();
            public string FailLatestFor { get; set; }
            public IReadOnlyList<int> LastEvidenceIds { get; private set; }
            public bool LastUseHistory { get; private set; }

            public Task<StoredUserRow> FindUserAsync(string upn) =>
                Task.FromResult(User != null && string.Equals(User.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase) ? User : null);

            public Task<List<StoredLicenceType>> StoredLicenceTypesForUserAsync(int userId) => Task.FromResult(Stored);

            public Task<LicenceHistoryRead> HistoryAsync(int userId, int take) => Task.FromResult(History);

            public Task<LatestStoredRow> LatestAsync(int userId, string categoryKey)
            {
                if (categoryKey == FailLatestFor) throw new InvalidOperationException("timeout");
                return Task.FromResult(new LatestStoredRow { LatestUtc = Now.AddDays(-1) });
            }

            public Task<List<LicenceTypeRow>> LicenceTypesAsync() => Task.FromResult(Types);

            public Task<DateTime?> LicenceHistoryStartAsync() => Task.FromResult(HistoryStart);

            public Task<List<SeatEvidenceRow>> SeatEvidenceAsync(int userId, IReadOnlyList<int> licenceTypeIds, bool useLicenceHistory,
                DateTime fromUtc, DateTime toExclusiveUtc, DateTime? historyStartUtc)
            {
                LastEvidenceIds = licenceTypeIds;
                LastUseHistory = useLicenceHistory;
                return Task.FromResult(Evidence.Where(e => licenceTypeIds.Contains(e.LicenceTypeId)).ToList());
            }
        }

        #endregion
    }
}
