using Common.Entities.CopilotAdoption;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot adoption report narrowed by a user filter: the same in-memory re-scoring the email
    /// domain uses, driven by Entra ID attributes and custom organisations instead.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionUserFilterScopeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        private const int ProgrammeType = 3;

        [TestMethod]
        public void Filtering_RecomputesEveryFigureForTheMatchedPeopleAlone()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "[{\"d\":\"department\",\"v\":[\"Sales\"]}]", service);

            Assert.AreEqual(4, scoped.Summary.LicensedUsers);
            Assert.AreEqual(4, scoped.LicensedUsers.Count);
            Assert.IsTrue(scoped.LicensedUsers.All(u => u.Department == "Sales"));
            Assert.AreEqual(1, scoped.Summary.ActiveUsers, "Only one Sales seat holder used Copilot.");
            Assert.AreEqual(1, scoped.Summary.Unlicensed.ActiveUsers, "One unlicensed Sales person used Copilot Chat.");
            Assert.AreEqual(1, scoped.Opportunities.Count);
            Assert.IsTrue(scoped.CoworkSignals.All(s => s.Department == "Sales"));
        }

        [TestMethod]
        public void Filtering_EchoesTheFilterAndTheTenantSeatCount()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "[{\"d\":\"department\",\"v\":[\"Sales\"]}]", service);

            Assert.IsNotNull(scoped.Summary.UserFilter, "The page must be told which filter was actually applied.");
            Assert.AreEqual(UserFilterDimensions.Department, scoped.Summary.UserFilter.Clauses.Single().Dimension);
            Assert.AreEqual(5, scoped.Summary.UserFilter.MatchedPeople, "Four seat holders and one unlicensed person.");
            Assert.AreEqual(8, scoped.Summary.UnscopedLicensedUsers, "So the page can say '4 of 8 licence holders'.");
            Assert.AreEqual("Department is Sales", scoped.Summary.UserFilterDescription);
            Assert.IsNull(scoped.Summary.ScopedEmailDomain);
            CollectionAssert.Contains(scoped.Summary.UnscopedSections, CopilotAdoptionUnscopedSections.UsageByApp,
                "Sections with no per-person detail are named, exactly as for an email domain.");
        }

        [TestMethod]
        public void GlobalFilter_ListsOnlyTheCopilotLicenceTypes_WhoseCountsItCanNarrow()
        {
            var analysis = Analysis();
            analysis.Summary.SeatLicenceTypes.Add(new LicenceTypeClassification
            {
                Id = 99,
                Name = "Microsoft 365 E3",
                SkuPartNumber = "SPE_E3",
                IsCopilotSeat = false,
                AssignedUsers = 4000,
                PurchasedUnits = 4500,
                UnassignedUnits = 500,
            });
            var service = Service();
            service.FinaliseSummary(analysis);
            var sales = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}]"), Directory());

            // Under the administrator's filter a whole-tenant E3 count - or its purchased and unassigned
            // seats, which give the same number - would tell a reader limited to Sales about everyone else.
            var global = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.Create(null, null, sales, null, null), service.FinaliseSummary);
            Assert.IsTrue(global.Summary.SeatLicenceTypes.Count > 0);
            Assert.IsTrue(global.Summary.SeatLicenceTypes.All(l => l.IsCopilotSeat), "Only the types whose counts were narrowed.");

            var rows = CopilotAdoptionScopeFilter.FilterRows(analysis, CopilotAdoptionScope.Create(null, null, sales, null, null));
            Assert.IsTrue(rows.Summary.SeatLicenceTypes.All(l => l.IsCopilotSeat));

            // The reader's own filter lists every type, the others tenant-wide, as it always has.
            var own = Narrow(analysis, "[{\"d\":\"department\",\"v\":[\"Sales\"]}]", service);
            Assert.AreEqual(4000, own.Summary.SeatLicenceTypes.Single(l => !l.IsCopilotSeat).AssignedUsers);

            Assert.IsTrue(analysis.Summary.SeatLicenceTypes.Any(l => !l.IsCopilotSeat), "The cached tenant analysis is untouched.");
        }

        [TestMethod]
        public void Filtering_LeavesTheCachedTenantAnalysisUntouched()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var licensedBefore = analysis.Summary.LicensedUsers;
            var activeBefore = analysis.Summary.ActiveUsers;
            var rowsBefore = analysis.LicensedUsers.Count;
            var coworkBefore = analysis.CoworkReadiness.Count;

            Narrow(analysis, "[{\"d\":\"department\",\"v\":[\"Sales\"]}]", service);

            Assert.AreEqual(licensedBefore, analysis.Summary.LicensedUsers);
            Assert.AreEqual(activeBefore, analysis.Summary.ActiveUsers);
            Assert.AreEqual(rowsBefore, analysis.LicensedUsers.Count);
            Assert.AreEqual(coworkBefore, analysis.CoworkReadiness.Count);
            Assert.IsNull(analysis.Summary.UserFilter);
            Assert.IsNull(analysis.Summary.UnscopedLicensedUsers);
        }

        [TestMethod]
        public void Filtering_ByACustomOrganisation_NarrowsLikeAnEntraAttribute()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "[{\"d\":\"org:" + ProgrammeType + "\",\"v\":[\"Pilot wave 1\"]}]", service);

            CollectionAssert.AreEquivalent(new[] { 1, 5 }, scoped.LicensedUsers.Select(u => u.UserId).ToArray());
            Assert.AreEqual("Programme is Pilot wave 1", scoped.Summary.UserFilterDescription);
        }

        [TestMethod]
        public void Filtering_WithOrGroups_TakesEitherPopulation()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            // (Sales and United Kingdom) or Pilot wave 1
            var scoped = Narrow(
                analysis,
                "[{\"d\":\"department\",\"v\":[\"Sales\"]},{\"d\":\"country\",\"v\":[\"United Kingdom\"]},"
                + "{\"j\":\"or\",\"d\":\"org:" + ProgrammeType + "\",\"v\":[\"Pilot wave 1\"]}]",
                service);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 5 }, scoped.LicensedUsers.Select(u => u.UserId).ToArray());
        }

        [TestMethod]
        public void Filtering_CombinesWithTheEmailDomainByAnd()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var filter = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}]"), Directory());
            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.Create("fabrikam.com", filter), service.FinaliseSummary);

            CollectionAssert.AreEquivalent(new[] { 4 }, scoped.LicensedUsers.Select(u => u.UserId).ToArray(),
                "Only the Sales seat holder on fabrikam.com.");
            Assert.AreEqual("fabrikam.com", scoped.Summary.ScopedEmailDomain);
            Assert.IsNotNull(scoped.Summary.UserFilter);
        }

        [TestMethod]
        public void Filtering_ToAnEmptyCoworkSlice_StaysUnavailable_WhenTheAssessmentWasCapped()
        {
            // The Cowork query stops at MaxCoworkUsersScored. If it did, the people a filter selects may
            // just have been cut off - so an empty slice is unknown, not zero.
            var analysis = Analysis();
            analysis.Summary.Options = new CopilotAdoptionOptions { WindowDays = 28, MaxCoworkUsersScored = 8 };
            var service = new CopilotAdoptionService(analysis.Summary.Options);
            service.FinaliseSummary(analysis);

            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis,
                CopilotAdoptionScope.Create(null, UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Legal\"]}]"), Directory())),
                service.FinaliseSummary);

            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable);
            Assert.IsFalse(scoped.Summary.CoworkReadinessAvailable);

            // ...and says why, so the Cowork tab explains the cap instead of blaming an import that is
            // working. The tenant view is not a slice, so it never carries this warning.
            var detail = scoped.Summary.WarningDetails.SingleOrDefault(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceNotAssessed);
            Assert.IsNotNull(detail, "An empty slice of a capped assessment must be explained.");
            Assert.AreEqual(8, Convert.ToInt32(detail.Values["maxUsers"]), "The cap quoted is the one the tenant assessment ran with.");
            Assert.AreEqual(scoped.Summary.Warnings.Count, scoped.Summary.WarningDetails.Count, "Warnings and details stay index-aligned.");
            Assert.IsFalse(analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceNotAssessed));
        }

        [TestMethod]
        public void Workbook_SaysWhenTheFilterNamesAnAttributeThatNoLongerExists()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "[{\"d\":\"org:99\",\"op\":\"isNot\",\"v\":[\"CC-100\"]}]", service);
            var text = CopilotAdoptionWorkbookText.Of(CopilotAdoptionWorkbook.Build(scoped));

            StringAssert.Contains(text, "THE FILTER NAMES ATTRIBUTES THAT NO LONGER EXIST: org:99");
            Assert.AreEqual(0, scoped.Summary.LicensedUsers, "A condition on a type that has gone matches nobody.");
        }

        [TestMethod]
        public void Filtering_ThatMatchesNobody_ReportsAnEmptyPopulationRatherThanTheTenant()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "[{\"d\":\"department\",\"v\":[\"Legal\"]}]", service);

            Assert.AreEqual(0, scoped.Summary.LicensedUsers);
            Assert.AreEqual(0, scoped.LicensedUsers.Count);
            Assert.AreEqual(0, scoped.Summary.UserFilter.MatchedPeople);

            // Cowork ran for the tenant; nobody in this slice is a finding, not a missing import.
            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable, "The fixture must exercise the Cowork path.");
            Assert.IsTrue(scoped.Summary.CoworkReadinessAvailable,
                "An empty slice must not tell the reader to check an import that is working.");
            Assert.AreEqual(0, scoped.Summary.CoworkScoredUsers);
            Assert.AreEqual(CopilotAdoptionScoring.AllCoworkTiers.Count(), scoped.Summary.CoworkTiers.Count);
            Assert.IsTrue(scoped.Summary.CoworkTiers.All(t => t.Users == 0));
            Assert.IsFalse(scoped.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.CoworkSliceNotAssessed),
                "A complete assessment vouches for the zero; there is nothing to explain.");
        }

        [TestMethod]
        public void FilterRows_NarrowsEveryPopulationWithoutRescoring()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var filter = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[\"Sales\"]}]"), Directory());
            var rows = CopilotAdoptionScopeFilter.FilterRows(analysis, CopilotAdoptionScope.Create(null, filter));

            Assert.IsTrue(rows.LicensedUsers.All(u => u.Department == "Engineering"));
            Assert.AreEqual(4, rows.LicensedUsers.Count);
            Assert.IsTrue(rows.Opportunities.All(o => o.UserId == 10));
            Assert.IsTrue(rows.UnlicensedUsers.All(u => u.UserId == 10));
            Assert.IsTrue(rows.CoworkReadiness.All(c => c.Department == "Engineering"));
            Assert.AreEqual(analysis.Summary.GeneratedUtc, rows.Summary.GeneratedUtc);
            Assert.IsNotNull(rows.Summary.UserFilter);
        }

        [TestMethod]
        public void Scope_WithNeitherADomainNorAFilter_IsTheWholeTenant()
        {
            Assert.AreSame(CopilotAdoptionScope.WholeTenant, CopilotAdoptionScope.Create(null, null));
            Assert.AreSame(CopilotAdoptionScope.WholeTenant, CopilotAdoptionScope.Create("  ", null));
            Assert.IsTrue(CopilotAdoptionScope.Create(null, UserFilterCompiler.Compile(
                UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}]"), Directory())).IsNarrowed);
        }

        [TestMethod]
        public void Workbook_SaysWhenItHasBeenFiltered_OnTheCoverSheetAndInTheFileName()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "[{\"d\":\"department\",\"v\":[\"Sales\"]}]", service);
            var text = CopilotAdoptionWorkbookText.Of(CopilotAdoptionWorkbook.Build(scoped));

            StringAssert.Contains(text, "Microsoft 365 Copilot - adoption report (filtered)");
            StringAssert.Contains(text, "FILTERED TO PEOPLE WHERE: Department is Sales");
            StringAssert.Contains(text, "must not be quoted as a tenant-wide figure");
            StringAssert.Contains(text, "People where Department is Sales");
            StringAssert.Contains(text, "4 of the tenant's 8 Copilot licence holders");
            StringAssert.EndsWith(CopilotAdoptionWorkbook.FileName(scoped.Summary), ".xlsx");
            StringAssert.Contains(CopilotAdoptionWorkbook.FileName(scoped.Summary), "-filtered-");
            Assert.IsFalse(CopilotAdoptionWorkbook.FileName(analysis.Summary).Contains("filtered"));
        }

        [TestMethod]
        public void AFilteredViewPastTheLicensedUserCapSaysHowManyOfItsPeopleWereNotAnalysed()
        {
            // The detail query stopped at its row cap, and it cuts the newest user records first. Seat
            // holders 11 and 12 are in Sales; 13 is in Engineering. None of them has a row to narrow.
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);
            analysis.LicensedUsersNotAnalysed = new[] { 11, 12, 13 };

            var scoped = NarrowPastTheCap(analysis, null, Sales, service);

            var detail = scoped.Summary.WarningDetails.Single(d => d.Key == CopilotAdoptionWarningKeys.ScopedLicensedUsersNotAnalysed);
            Assert.AreEqual(2, detail.Values["count"]);
            Assert.AreEqual(analysis.Summary.Options.MaxLicensedUsersScored, detail.Values["maxUsers"]);
            StringAssert.Contains(scoped.Summary.Warnings.Last(), "(2 of them)");
            Assert.AreEqual(4, scoped.Summary.LicensedUsers, "The figures still describe the people analysed; the warning says who they leave out.");
            Assert.IsFalse(
                analysis.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.ScopedLicensedUsersNotAnalysed),
                "The tenant view has its own cap warning.");
        }

        [TestMethod]
        public void TheEmailDomainIsAppliedToThePeopleNotAnalysedToo()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);
            analysis.LicensedUsersNotAnalysed = new[] { 11, 12, 13 };

            var scoped = NarrowPastTheCap(analysis, "fabrikam.com", Sales, service);

            Assert.AreEqual(
                1,
                scoped.Summary.WarningDetails.Single(d => d.Key == CopilotAdoptionWarningKeys.ScopedLicensedUsersNotAnalysed).Values["count"],
                "Only lee@fabrikam.com is in Sales at fabrikam.com.");
        }

        [TestMethod]
        public void NothingIsSaidWhenNoneOfThePeopleNotAnalysedWouldBeInTheView()
        {
            var analysis = Analysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            analysis.LicensedUsersNotAnalysed = new[] { 13 };
            Assert.IsFalse(HasNotAnalysedWarning(NarrowPastTheCap(analysis, null, Sales, service)), "13 is not in Sales.");

            analysis.LicensedUsersNotAnalysed = null;
            Assert.IsFalse(HasNotAnalysedWarning(NarrowPastTheCap(analysis, null, Sales, service)), "The query was not capped.");

            // An email domain alone has no directory to look a person up in, so that stays with the
            // tenant-wide cap warning, as before.
            analysis.LicensedUsersNotAnalysed = new[] { 11, 12, 13 };
            var domainOnly = CopilotAdoptionScopeFilter.Apply(analysis, CopilotAdoptionScope.ForEmailDomain("contoso.com"), service.FinaliseSummary);
            Assert.IsFalse(HasNotAnalysedWarning(domainOnly));
        }

        [TestMethod]
        public void ThePeopleNotAnalysedAreTheSeatHoldersTheCappedQueryDidNotReturn()
        {
            CollectionAssert.AreEqual(
                new[] { 13, 11 },
                CopilotAdoptionService.NotAnalysed(new[] { 13, 4, 11, 1 }, new[] { 1, 4, 99 }));
        }

        #region Fixture

        private const string Sales = "[{\"d\":\"department\",\"v\":[\"Sales\"]}]";

        private static bool HasNotAnalysedWarning(CopilotAdoptionAnalysis scoped) =>
            scoped.Summary.WarningDetails.Any(d => d.Key == CopilotAdoptionWarningKeys.ScopedLicensedUsersNotAnalysed);

        /// <summary>Narrows against a directory that also holds three seat holders the analysis never covered.</summary>
        private static CopilotAdoptionAnalysis NarrowPastTheCap(
            CopilotAdoptionAnalysis analysis, string emailDomain, string encoded, CopilotAdoptionService service)
        {
            var builder = new UserDirectorySnapshotBuilder();
            foreach (var person in People)
            {
                builder.AddUser(new UserDirectoryEntry { UserId = person.Id, UserPrincipalName = person.Upn, Department = person.Department, AccountEnabled = true });
            }

            builder.AddUser(new UserDirectoryEntry { UserId = 11, UserPrincipalName = "kim@contoso.com", Department = "Sales", AccountEnabled = true });
            builder.AddUser(new UserDirectoryEntry { UserId = 12, UserPrincipalName = "lee@fabrikam.com", Department = "Sales", AccountEnabled = true });
            builder.AddUser(new UserDirectoryEntry { UserId = 13, UserPrincipalName = "max@contoso.com", Department = "Engineering", AccountEnabled = true });

            var filter = UserFilterCompiler.Compile(UserFilterCodec.Parse(encoded), builder.Build(Now));
            return CopilotAdoptionScopeFilter.Apply(analysis, CopilotAdoptionScope.Create(emailDomain, filter), service.FinaliseSummary);
        }

        private static CopilotAdoptionService Service()
        {
            return new CopilotAdoptionService(new CopilotAdoptionOptions { WindowDays = 28 });
        }

        private static CopilotAdoptionAnalysis Narrow(CopilotAdoptionAnalysis analysis, string encoded, CopilotAdoptionService service)
        {
            var filter = UserFilterCompiler.Compile(UserFilterCodec.Parse(encoded), Directory());
            return CopilotAdoptionScopeFilter.Apply(analysis, CopilotAdoptionScope.Create(null, filter), service.FinaliseSummary);
        }

        /// <summary>
        /// The directory the filter reads: the same people as the analysis, with the attributes the
        /// importer would have stored, plus one custom organisation type.
        /// </summary>
        private static UserDirectorySnapshot Directory()
        {
            var builder = new UserDirectorySnapshotBuilder();

            foreach (var person in People)
            {
                builder.AddUser(new UserDirectoryEntry
                {
                    UserId = person.Id,
                    UserPrincipalName = person.Upn,
                    Department = person.Department,
                    Country = person.Country,
                    AccountEnabled = true,
                });
            }

            builder.AddOrgType(ProgrammeType, "Programme");
            builder.AddOrgAssignment(1, ProgrammeType, "Pilot wave 1");
            builder.AddOrgAssignment(5, ProgrammeType, "Pilot wave 1");
            builder.AddOrgAssignment(2, ProgrammeType, "Pilot wave 2");

            return builder.Build(Now);
        }

        private sealed class Person
        {
            public int Id;
            public string Upn;
            public string Department;
            public string Country;
            public double Score;
            public AdoptionBand Band;
        }

        private static readonly Person[] People =
        {
            new Person { Id = 1, Upn = "ana@contoso.com", Department = "Sales", Country = "United Kingdom", Score = 72, Band = AdoptionBand.Established },
            new Person { Id = 2, Upn = "ben@contoso.com", Department = "Sales", Country = "United Kingdom", Score = 0, Band = AdoptionBand.NeverUsed },
            new Person { Id = 3, Upn = "cai@contoso.com", Department = "Sales", Country = "United States", Score = 0, Band = AdoptionBand.NeverUsed },
            new Person { Id = 4, Upn = "dee@fabrikam.com", Department = "Sales", Country = "United States", Score = 0, Band = AdoptionBand.NeverUsed },
            new Person { Id = 5, Upn = "eli@contoso.com", Department = "Engineering", Country = "United States", Score = 85, Band = AdoptionBand.Champion },
            new Person { Id = 6, Upn = "fay@contoso.com", Department = "Engineering", Country = "United States", Score = 64, Band = AdoptionBand.Established },
            new Person { Id = 7, Upn = "gus@contoso.com", Department = "Engineering", Country = "Ireland", Score = 30, Band = AdoptionBand.Developing },
            new Person { Id = 8, Upn = "hal@fabrikam.com", Department = "Engineering", Country = "Ireland", Score = 12, Band = AdoptionBand.Trialling },

            // Unlicensed: one Sales, one Engineering.
            new Person { Id = 9, Upn = "ivy@contoso.com", Department = "Sales", Country = "United Kingdom" },
            new Person { Id = 10, Upn = "jon@contoso.com", Department = "Engineering", Country = "Ireland" },
        };

        private static CopilotAdoptionAnalysis Analysis()
        {
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;

            summary.GeneratedUtc = Now;
            summary.WindowDays = 28;
            summary.FromUtc = Now.AddDays(-28);
            summary.ToUtc = Now;
            summary.Options = new CopilotAdoptionOptions { WindowDays = 28 };
            summary.SeatLicenceTypes.Add(new LicenceTypeClassification
            {
                Id = 1,
                Name = "Microsoft 365 Copilot",
                SkuPartNumber = "Microsoft_365_Copilot",
                IsCopilotSeat = true,
                AssignedUsers = 8,
                PurchasedUnits = 10,
                UnassignedUnits = 2,
            });
            summary.UsageByApp.Add(new AdoptionCategory { Label = "Teams", Value = 90 });

            foreach (var person in People.Where(p => p.Id <= 8))
            {
                var row = new LicensedUserAdoptionRow
                {
                    UserId = person.Id,
                    UserPrincipalName = person.Upn,
                    EmailDomain = CopilotAdoptionEmailDomain.From(person.Upn),
                    Department = person.Department,
                    Country = person.Country,
                    AccountEnabled = true,
                    AccountCreatedUtc = Now.AddDays(-200),
                    TenureStartUtc = Now.AddDays(-200),
                    TenureBasis = CopilotAdoptionScoring.TenureBasisAccountAge,
                    DaysSinceTenureStart = 200,
                    AdoptionScore = person.Score,
                    Band = person.Band,
                    BandName = CopilotAdoptionScoring.BandDisplayName(person.Band),
                    Interactions = person.Band > AdoptionBand.Dormant ? 40 : 0,
                    ActiveDays = person.Band > AdoptionBand.Dormant ? 8 : 0,
                    SeatLicenceTypeIds = new List<int> { 1 },
                };

                CopilotAdoptionScoring.ApplyReclaimEligibility(row);
                row.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(row);
                row.RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(row.RecommendedActionCode);
                analysis.LicensedUsers.Add(row);

                analysis.CoworkSignals.Add(new CoworkReadinessSignalRow
                {
                    UserId = person.Id,
                    UserPrincipalName = person.Upn,
                    EmailDomain = row.EmailDomain,
                    Department = person.Department,
                    Country = person.Country,
                    AccountEnabled = true,
                    CoworkInteractions = person.Score > 50 ? 12 : 0,
                    CoworkActiveDays = person.Score > 50 ? 5 : 0,
                    TeamsMessages = 300,
                    TeamsMeetings = 20,
                    EmailsSent = 120,
                    EmailsRead = 400,
                    FilesViewedOrEdited = 80,
                });
            }

            foreach (var person in People.Where(p => p.Id > 8))
            {
                analysis.UnlicensedUsers.Add(new UnlicensedUsageQueryRow
                {
                    UserId = person.Id,
                    UserPrincipalName = person.Upn,
                    EmailDomain = CopilotAdoptionEmailDomain.From(person.Upn),
                    Department = person.Department,
                    Interactions = 60,
                    ActiveDays = 6,
                    AppsUsed = 1,
                    LastInteractionUtc = Now.AddDays(-1),
                });

                analysis.Opportunities.Add(new LicenceOpportunityRow
                {
                    UserId = person.Id,
                    UserPrincipalName = person.Upn,
                    EmailDomain = CopilotAdoptionEmailDomain.From(person.Upn),
                    Department = person.Department,
                    Country = person.Country,
                    Recommended = true,
                    OpportunityScore = 80,
                });
            }

            return analysis;
        }

        #endregion
    }

    /// <summary>Every cell of every sheet of a workbook, one per line, for text assertions.</summary>
    internal static class CopilotAdoptionWorkbookText
    {
        public static string Of(byte[] bytes)
        {
            var builder = new System.Text.StringBuilder();

            using (var stream = new System.IO.MemoryStream(bytes))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                                                          && e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    using (var part = entry.Open())
                    {
                        var doc = System.Xml.Linq.XDocument.Load(part);
                        foreach (var text in doc.Descendants().Where(e => !e.HasElements))
                        {
                            builder.AppendLine(text.Value);
                        }
                    }
                }
            }

            return builder.ToString();
        }
    }
}
