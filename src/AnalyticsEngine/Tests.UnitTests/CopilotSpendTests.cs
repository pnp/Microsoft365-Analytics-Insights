extern alias AnalyticsWeb;

using Common.Entities.CopilotCosts;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.Localisation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using AdoptionResourceTypeRow = Common.Entities.CopilotAdoption.AdoptionResourceTypeRow;
using CopilotAgentOriginKeys = Common.Entities.Copilot.CopilotAgentOriginKeys;
using CopilotResourceTypeKind = Common.Entities.Copilot.CopilotResourceTypeKind;
using CopilotSpendAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotSpendAPIController;
using CopilotTurnSql = Common.Entities.Copilot.CopilotTurnSql;
using PromptCategoryDefinition = Common.Entities.PromptCategories.PromptCategoryDefinition;
using WorkbookCell = Tests.UnitTests.CopilotInvestmentWorkbookLanguageTests.Cell;
using XlsxCellStyle = Common.Entities.Xlsx.XlsxCellStyle;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot Spend report: billed Studio usage and latest credit balances kept apart and never added, unknown never read
    /// as zero, tenant-only facts withheld from a filtered view, and the same scope and redaction in the workbook as on the page.
    /// </summary>
    [TestClass]
    public class CopilotSpendTests
    {
        private static readonly DateTime From = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Snapshot = new DateTime(2026, 10, 26);

        // Synthetic tenant text in a non-Latin script; sign-in names stay ASCII, as Entra requires.
        private const string GreekDepartment = "\u0391\u03b8\u03ae\u03bd\u03b1 \u03a0\u03c9\u03bb\u03ae\u03c3\u03b5\u03b9\u03c2";

        private static CreditUserFact Balance(int? userId, double? credits, int unknownRows = 0) => new CreditUserFact
        {
            ObjectId = new Guid(userId ?? 999, 0, 0, new byte[8]),
            UserId = userId,
            ObservationDate = Snapshot,
            Quality = 2,
            PeriodStatus = "unverified",
            ServiceRows = 1,
            CreditRows = credits.HasValue ? 1 : 0,
            Credits = credits,
            UnknownConsumptionRows = unknownRows,
        };

        private static CopilotSpendInput Input(Func<int, bool> inScope = null, bool filtered = false, Func<int, string> department = null)
        {
            var credits = new CreditFacts { TablesPresent = true, SnapshotDate = Snapshot, SnapshotBandFrom = Snapshot.AddDays(-2) };
            credits.Users.AddRange(new[]
            {
                Balance(1, 100),
                Balance(2, null, unknownRows: 1),   // an amount the source did not state: unknown, not zero
                Balance(3, 0),                      // a reading of zero: observed, not listed
                Balance(4, 70),                     // outside the filtered scope below
                Balance(null, 50),                  // an account no imported user matches
            });
            credits.Context = new CreditContextFact
            {
                Policies = { new CostPolicyContext { Id = "policy-1", DisplayName = "Contoso all users", Type = "#microsoft.graph.allUsersSpendingPolicy", Status = "enabled" } },
                TenantBalances = { new CostTenantBalance { BillingMethod = "payAsYouGo", ConsumedQuantity = 220, Unit = CopilotInvestmentSql.CreditUnit } },
            };
            var studio = new StudioFacts { HasData = true, EarliestDate = From.AddDays(-60) };
            studio.Users.AddRange(new[]
            {
                new StudioUserFact { UserId = 1, Credits = 10, ActiveDays = 3 },
                new StudioUserFact { UserId = 5, Credits = 20, ActiveDays = 5 },
                new StudioUserFact { UserId = 4, Credits = 30, ActiveDays = 2 },
                new StudioUserFact { UserId = null, Credits = 7, ActiveDays = 1 },
            });
            return new CopilotSpendInput
            {
                GeneratedUtc = From.AddDays(28),
                FromUtc = From,
                ToUtc = From.AddDays(27),
                WindowDays = 28,
                Filtered = filtered,
                InScope = inScope,
                Directory = id => new CopilotInvestmentDirectoryEntry
                {
                    UserPrincipalName = $"person{id}@contoso.example",
                    Department = department?.Invoke(id) ?? (id % 2 == 0 ? "Sales" : GreekDepartment),
                    Manager = "Manager " + id,
                },
                GroupOf = id => department?.Invoke(id) ?? (id % 2 == 0 ? "Sales" : GreekDepartment),
                MinGroupSize = 1,
                Credits = credits,
                Studio = studio,
                StudioImportEnabled = true,
                CreditCollectionEnabled = true,
                StudioBreakdown = new CopilotSpendStudioBreakdown
                {
                    Days = { new CopilotSpendDay { Date = From, Credits = 67, People = 3 } },
                    Environments =
                    {
                        new CopilotSpendEnvironment { EnvironmentId = "env-2", EnvironmentName = "Contoso " + GreekDepartment, Credits = 17, People = 1, Days = 1 },
                        new CopilotSpendEnvironment { EnvironmentId = "env-1", EnvironmentName = "Contoso (default)", Credits = 50, People = 3, Days = 1 },
                    },
                    Agents = new List<CopilotSpendAgent>
                    {
                        new CopilotSpendAgent { AgentId = "agent-1", AgentName = "Contoso helpdesk agent", Credits = 40, Days = 1 },
                        new CopilotSpendAgent { AgentId = "agent-2", AgentName = null, Credits = 24.5, Days = 1 },
                    },
                },
            };
        }

        [TestMethod]
        public void BilledUsageAndLatestBalances_AreKeptApart_AndUnknownIsNeverZero()
        {
            var summary = CopilotSpendCalculator.Calculate(Input());

            // Billed Studio usage sums over the period: everyone's charges, including the ones no person was matched to.
            Assert.AreEqual(67, summary.Studio.Credits.Value, 1e-9);
            Assert.AreEqual(7, summary.Studio.UnmatchedCredits.Value, 1e-9);
            Assert.AreEqual(3, summary.Studio.People);
            Assert.IsTrue(summary.Studio.CoversWindow);
            CollectionAssert.AreEqual(new[] { "env-1", "env-2" }, summary.Studio.Environments.Select(e => e.EnvironmentId).ToList(), "Largest first.");
            Assert.AreEqual(summary.Studio.Credits.Value, summary.Studio.Environments.Sum(e => e.Credits).Value, 1e-9, "The per-person ledger by environment adds up.");

            // The agent ledger is a different route: its own total, which need not match and is never added in.
            Assert.AreEqual(2, summary.Studio.Agents.Count);
            Assert.AreEqual(64.5, summary.Studio.AgentLedgerCredits.Value, 1e-9);
            Assert.AreEqual(67, summary.Studio.Credits.Value, 1e-9, "Not 131.5: the two Studio ledgers are never added.");

            // Balances are each person's latest reading, never added to the Studio figures.
            Assert.AreEqual(220, summary.Balances.Credits.Value, 1e-9, "100 + 70 + the unmatched account's 50; the unknown amount adds nothing.");
            Assert.AreEqual(4, summary.Balances.ObservedPeople);
            Assert.AreEqual(2, summary.Balances.ConsumingPeople);
            Assert.AreEqual(1, summary.Balances.UnknownAmountPeople);
            Assert.AreEqual(1, summary.Balances.UnresolvedAccounts);
            Assert.AreEqual(50, summary.Balances.UnresolvedCredits.Value, 1e-9);
            Assert.AreEqual("unverified", summary.Balances.PeriodStatus);
            Assert.AreEqual(1, summary.Balances.Policies.Count);
            Assert.AreEqual(1, summary.Balances.TenantBalances.Count);

            // People: anyone billed, with a balance, or with an amount the source could not state - not a plain zero.
            CollectionAssert.AreEquivalent(new[] { 1, 2, 4, 5 }, summary.People.Select(p => p.UserId).ToList());
            var unknown = summary.People.Single(p => p.UserId == 2);
            Assert.IsNull(unknown.BalanceCredits, "An amount the source did not state is absent, not zero.");
            Assert.IsTrue(unknown.UnknownAmounts);
            var studioOnly = summary.People.Single(p => p.UserId == 5);
            Assert.IsNull(studioOnly.BalanceDate, "No reading at all: no balance date either.");
            Assert.AreEqual(4, summary.PeopleCount);
        }

        [TestMethod]
        public void AFilteredView_CountsOnlyItsPeople_AndWithholdsTenantOnlyFacts()
        {
            var summary = CopilotSpendCalculator.Calculate(Input(id => id != 4, filtered: true));

            Assert.AreEqual(30, summary.Studio.Credits.Value, 1e-9, "Only the people in view: 10 + 20.");
            Assert.IsNull(summary.Studio.UnmatchedCredits, "Charges no person was matched to cannot be shown to belong to this view.");
            Assert.IsNull(summary.Studio.Agents, "The agent ledger is not kept per person, so a filtered view has no share of it to show.");
            Assert.IsNull(summary.Studio.AgentLedgerCredits);
            Assert.AreEqual(100, summary.Balances.Credits.Value, 1e-9);
            Assert.IsNull(summary.Balances.UnresolvedAccounts);
            Assert.IsNull(summary.Balances.UnresolvedCredits);
            Assert.AreEqual(0, summary.Balances.Policies.Count);
            Assert.AreEqual(0, summary.Balances.TenantBalances.Count, "A pool balance is a tenant total.");
            Assert.IsFalse(summary.People.Any(p => p.UserId == 4));
        }

        [TestMethod]
        public void Groups_SumPeopleWithoutAddingTheSources_AndFoldGroupsTooSmallToShow()
        {
            var input = Input(department: id => id == 5 ? "Finance" : "Sales");
            input.MinGroupSize = 2;
            var summary = CopilotSpendCalculator.Calculate(input);

            var sales = summary.Organisation.Groups.Single(g => g.Label == "Sales");
            Assert.AreEqual(3, sales.People);
            Assert.AreEqual(40, sales.StudioCredits.Value, 1e-9, "Persons 1 and 4: 10 + 30.");
            Assert.AreEqual(170, sales.BalanceCredits.Value, 1e-9, "Persons 1 and 4: 100 + 70; person 2's amount is unknown.");
            Assert.AreEqual(1, sales.UnknownAmountPeople);
            var folded = summary.Organisation.Groups.Single(g => g.Label == null);
            Assert.AreEqual(CopilotInvestmentCalculator.GroupKeys.OtherSmallGroups, folded.EmptyKey);
            Assert.AreEqual(1, summary.Organisation.FoldedGroups);
            // The combined row holds one person - fewer than the smallest group shown - so it shows no figures at all.
            Assert.AreEqual(1, folded.People);
            Assert.IsTrue(folded.Withheld);
            Assert.IsNull(folded.StudioCredits);
            Assert.IsNull(folded.BalanceCredits);
            Assert.IsNull(folded.ShareOfStudio);
            Assert.IsNull(folded.UnknownAmountPeople);
        }

        [TestMethod]
        public void EveryCalendarDayKeepsItsPlace_AndOnlyTheLedgersOwnDaysCanHaveNoCharges()
        {
            // Two weeks from Monday 12 Oct: charges on weekdays to Thursday 22 Oct, the ledger's latest stored day.
            var monday = new DateTime(2026, 10, 12);
            var input = Input();
            input.FromUtc = monday;
            input.LastDay = monday.AddDays(13);
            input.Studio.LatestDate = new DateTime(2026, 10, 22);
            input.StudioBreakdown.Days.Clear();
            foreach (var day in Enumerable.Range(0, 11).Select(d => monday.AddDays(d)).Where(d => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday))
                input.StudioBreakdown.Days.Add(new CopilotSpendDay { Date = day, Credits = 10, People = 2 });

            var days = CopilotSpendCalculator.Calculate(input).Studio.Days;

            CollectionAssert.AreEqual(Enumerable.Range(0, 14).Select(d => monday.AddDays(d)).ToList(), days.Select(d => d.Date).ToList(), "Every day, weekends included.");
            Assert.AreEqual(CopilotSpendDayStatus.NoneRecorded, days.Single(d => d.Date == new DateTime(2026, 10, 17)).Status, "A Saturday inside the ledger: nothing recorded.");
            Assert.IsNull(days.Single(d => d.Date == new DateTime(2026, 10, 17)).Credits, "No charges recorded is not a reported zero.");
            Assert.AreEqual(CopilotSpendDayStatus.Recorded, days.Single(d => d.Date == new DateTime(2026, 10, 22)).Status);
            CollectionAssert.AreEqual(new[] { CopilotSpendDayStatus.NotReported, CopilotSpendDayStatus.NotReported },
                days.Where(d => d.Date > new DateTime(2026, 10, 23)).Select(d => d.Status).ToList(), "After the latest stored day: not reported yet, not zero.");
            Assert.AreEqual(CopilotSpendDayStatus.NotReported, days.Single(d => d.Date == new DateTime(2026, 10, 23)).Status, "Friday 23rd is after the ledger's last day.");
            Assert.AreEqual(90, days.Sum(d => d.Credits ?? 0), 1e-9, "Nine recorded weekdays, nothing invented.");
        }

        [TestMethod]
        public void ASourceThatIsOffOrUnreadable_ReadsAsNotAvailable_NeverAsZero_WhileAKnownZeroStaysZero()
        {
            // The Studio ledger was never imported (import off, no rows): every Studio figure is unknown.
            var input = Input();
            input.Studio = new StudioFacts();
            input.StudioImportEnabled = false;
            var summary = CopilotSpendCalculator.Calculate(input);
            Assert.IsFalse(summary.Studio.Available);
            Assert.IsNull(summary.Studio.Credits);
            Assert.IsNull(summary.Studio.People);
            Assert.AreEqual(0, summary.Studio.Days.Count);
            Assert.IsTrue(summary.People.All(p => p.StudioCredits == null && p.StudioDays == null), "Nobody's Studio use is known.");
            Assert.IsTrue(summary.Organisation.Groups.All(g => g.StudioCredits == null && g.ShareOfStudio == null && g.StudioPeople == null));
            Assert.IsTrue(summary.Organisation.Groups.Any(g => g.BalanceCredits > 0), "The other source still reports.");

            // An unreadable ledger is the same, even with its import switched on.
            input.Studio = new StudioFacts { FailureCode = "studioUnavailable" };
            input.StudioImportEnabled = true;
            Assert.IsNull(CopilotSpendCalculator.Calculate(input).Studio.Credits);

            // Retained history: the import is off now but the ledger holds rows, so its figures - zeros included - are real.
            input = Input();
            input.StudioImportEnabled = false;
            summary = CopilotSpendCalculator.Calculate(input);
            Assert.IsTrue(summary.Studio.Available);
            Assert.AreEqual(67, summary.Studio.Credits.Value, 1e-9);
            Assert.AreEqual(0, summary.People.Single(p => p.UserId == 2).StudioCredits, "A person the readable ledger has no charges for: a known zero.");

            // No balance readings at all: balance figures are unknown, the Studio ones unaffected.
            input = Input();
            input.Credits = new CreditFacts { TablesPresent = false };
            summary = CopilotSpendCalculator.Calculate(input);
            Assert.IsFalse(summary.Balances.Available);
            Assert.IsNull(summary.Balances.Credits);
            Assert.IsNull(summary.Balances.UnresolvedAccounts);
            Assert.IsTrue(summary.Organisation.Groups.All(g => g.BalanceCredits == null && g.ShareOfBalances == null && g.UnknownAmountPeople == null));
            Assert.AreEqual(67, summary.Studio.Credits.Value, 1e-9);

            // The workbook leaves those cells empty and says why, in the reader's language.
            input = Input();
            input.Studio = new StudioFacts();
            var sheets = CopilotInvestmentWorkbookLanguageTests.Sheets(CopilotSpendWorkbook.Build(CopilotSpendCalculator.Calculate(input), true, "test", PortalLanguage.Spanish));
            var es = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish);
            var texts = CopilotInvestmentWorkbookLanguageTests.Texts(sheets);
            CollectionAssert.Contains(texts, es.Text("copilotSpend.workbook.studio.notAvailable"));
            var studioCredits = sheets[0].Cells.Single(c => c.Text == es.Text("copilotSpend.workbook.studio.credits"));
            var row = studioCredits.Ref.Substring(1);
            Assert.IsNull(sheets[0].Cells.SingleOrDefault(c => c.Ref == "B" + row)?.Value, "An empty cell, not a 0.");
            var people = sheets.Last().Cells;
            Assert.IsFalse(people.Any(c => c.Ref.StartsWith("G", StringComparison.Ordinal) && c.Ref != "G1" && c.Value == "0"), "No person's Studio credits written as 0.");
        }

        [TestMethod]
        public void AReadingOlderThanThePeriod_IsFlagged_NotPassedOffAsThePeriodsConsumption()
        {
            // A seven-day view whose newest reading is from before it starts (the collector stopped).
            var input = Input();
            input.FromUtc = Snapshot.AddDays(5);
            input.LastDay = Snapshot.AddDays(11);
            var summary = CopilotSpendCalculator.Calculate(input);
            Assert.IsTrue(summary.Balances.SnapshotBeforeWindow);
            var es = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish);
            CollectionAssert.Contains(CopilotInvestmentWorkbookLanguageTests.Texts(CopilotInvestmentWorkbookLanguageTests.Sheets(
                    CopilotSpendWorkbook.Build(summary, false, "test", PortalLanguage.Spanish))),
                es.Text("copilotSpend.workbook.balances.snapshotBeforeWindow", ("date", "26 oct 2026")));

            Assert.IsFalse(CopilotSpendCalculator.Calculate(Input()).Balances.SnapshotBeforeWindow, "The control: a reading inside the period.");
        }

        [TestMethod]
        public void SmallEnvironments_AreCombinedForAReaderWithoutSeePii_OnACopy()
        {
            var input = Input();
            input.StudioBreakdown.Environments.Clear();
            input.StudioBreakdown.Environments.Add(new CopilotSpendEnvironment { EnvironmentId = "env-1", EnvironmentName = "Contoso (default)", Credits = 50, People = 12, Days = 9 });
            input.StudioBreakdown.Environments.Add(new CopilotSpendEnvironment { EnvironmentId = "env-2", EnvironmentName = "Contoso \u2013 " + GreekDepartment + " dev", Credits = 17, People = 1, Days = 3 });
            var summary = CopilotSpendCalculator.Calculate(input);

            var trimmed = summary.WithoutIndividualData();
            Assert.AreEqual(2, summary.Studio.Environments.Count, "The cached summary keeps every environment for a reader with See PII.");
            Assert.IsFalse(trimmed.Studio.Environments.Any(e => e.EnvironmentId == "env-2" || (e.EnvironmentName ?? string.Empty).Contains("dev")),
                "A one-person environment's name does not reach a reader without See PII.");
            var combined = trimmed.Studio.Environments.Single(e => e.EmptyKey == CopilotSpendGroupKeys.OtherEnvironments);
            Assert.AreEqual(1, combined.People);
            Assert.IsTrue(combined.Withheld && combined.Credits == null && combined.Days == null, "Still one person: no amount either.");
            Assert.IsFalse(CopilotInvestmentWorkbookLanguageTests.Texts(CopilotInvestmentWorkbookLanguageTests.Sheets(
                CopilotSpendWorkbook.Build(trimmed, false, "test", PortalLanguage.English))).Any(t => t.Contains(" dev")));
        }

        [TestMethod]
        public void SeveralSmallEnvironments_AreCombinedWithNoFiguresAndNoCount_BecauseThePeopleInThemOverlap()
        {
            // Three environments of two people each add up to six, but the same two people can be in all three. The ledger
            // keeps each environment's own distinct count and none for the combination, so nothing shows it reaches the
            // smallest group the report shows: it must not pass on a sum.
            var input = Input();
            input.StudioBreakdown.Environments.Clear();
            input.StudioBreakdown.Environments.Add(new CopilotSpendEnvironment { EnvironmentId = "env-1", EnvironmentName = "Contoso (default)", Credits = 50, People = 12, Days = 9 });
            foreach (var n in new[] { 2, 3, 4 })
                input.StudioBreakdown.Environments.Add(new CopilotSpendEnvironment { EnvironmentId = "env-" + n, EnvironmentName = "Contoso sandbox " + n, Credits = 7.5, People = 2, Days = 4 });
            var summary = CopilotSpendCalculator.Calculate(input);

            var trimmed = summary.WithoutIndividualData();

            var combined = trimmed.Studio.Environments.Single(e => e.EmptyKey == CopilotSpendGroupKeys.OtherEnvironments);
            Assert.AreEqual(3, combined.Folded);
            Assert.IsTrue(combined.Withheld, "Six is a sum of three counts that can overlap, not a count of people.");
            Assert.IsNull(combined.Credits);
            Assert.IsNull(combined.Days);
            Assert.IsNull(combined.People, "No distinct count of the combination exists, so none is stated.");
            Assert.IsFalse(trimmed.Studio.Environments.Any(e => (e.EnvironmentName ?? string.Empty).Contains("sandbox")), "No small environment keeps its name.");

            var shown = trimmed.Studio.Environments.Single(e => e.EnvironmentId == "env-1");
            Assert.AreEqual(50d, shown.Credits, "An environment of enough people on its own keeps its name and figures.");
            Assert.AreEqual(12, shown.People);
            Assert.IsFalse(shown.Withheld);
            Assert.AreEqual(4, summary.Studio.Environments.Count, "The cached summary, which a reader with See PII is given, is never edited.");

            var texts = CopilotInvestmentWorkbookLanguageTests.Texts(CopilotInvestmentWorkbookLanguageTests.Sheets(
                CopilotSpendWorkbook.Build(trimmed, false, "test", PortalLanguage.English)));
            CollectionAssert.Contains(texts, PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.English).Text("copilotSpend.workbook.studio.environmentsWithheld"));
            Assert.IsFalse(texts.Any(t => t.Contains("sandbox")), "The workbook names no small environment either.");
        }

        [TestMethod]
        public void AReaderWithoutSeePii_GetsAggregatesOnly_OnACopy()
        {
            var summary = CopilotSpendCalculator.Calculate(Input());
            summary.Organisation.Dimension = "manager";
            var trimmed = summary.WithoutIndividualData();

            Assert.AreEqual(0, trimmed.People.Count);
            Assert.IsTrue(trimmed.IndividualDataHidden);
            Assert.IsTrue(trimmed.Organisation.Hidden && trimmed.Organisation.Groups.Count == 0, "A manager grouping names a person in every row.");
            Assert.AreEqual(4, summary.People.Count, "The cached summary is never edited.");
            Assert.IsTrue(summary.Organisation.Groups.Count > 0);
            Assert.AreEqual(summary.Studio.Credits, trimmed.Studio.Credits);
        }

        [TestMethod]
        public void ThePeopleList_SortsEveryoneWithMissingBalancesLast_AndFiltersBySource()
        {
            var people = CopilotSpendCalculator.Calculate(Input()).People;
            List<int> Ids(string key, bool descending) => CopilotSpendPeople.Sort(people, key, descending).Select(p => p.UserId).ToList();

            CollectionAssert.AreEqual(new[] { 4, 5, 1, 2 }, Ids("studioCredits", true), "Billed credits, then the balance as a tie-break.");
            CollectionAssert.AreEqual(new[] { 1, 4, 2, 5 }, Ids("balanceCredits", true), "No balance sorts last.");
            CollectionAssert.AreEqual(new[] { 4, 1, 2, 5 }, Ids("balanceCredits", false), "Whichever way the column is read.");
            CollectionAssert.AreEqual(new[] { 1, 2, 4, 5 }, Ids("user", false));
            CollectionAssert.AreEquivalent(new[] { 1, 4, 5 }, CopilotSpendPeople.Filter(people, "studio", null).Select(p => p.UserId).ToList());
            CollectionAssert.AreEquivalent(new[] { 1, 2, 4 }, CopilotSpendPeople.Filter(people, "balances", null).Select(p => p.UserId).ToList());
            CollectionAssert.AreEquivalent(new[] { 2, 4 }, CopilotSpendPeople.Filter(people, null, "sales").Select(p => p.UserId).ToList());
        }

        [TestMethod]
        public void TheWorkbook_IsWrittenInTheReadersLanguage_WithTheSameTypedCells_AndPeopleOnlyWithSeePii()
        {
            var input = Input();
            input.Directory = id => new CopilotInvestmentDirectoryEntry
            {
                UserPrincipalName = $"person{id}@contoso.example",
                Department = id == 1 ? "=HYPERLINK(\"https://contoso.example\",\"x\")" : GreekDepartment,
            };
            input.GroupOf = id => id == 1 ? "=HYPERLINK(\"https://contoso.example\",\"x\")" : GreekDepartment;
            var summary = CopilotSpendCalculator.Calculate(input);

            var english = CopilotSpendWorkbook.Build(summary, true, "test", PortalLanguage.English);
            var spanish = CopilotSpendWorkbook.Build(summary, true, "test", PortalLanguage.Spanish);
            CopilotInvestmentWorkbookLanguageTests.AssertSpanishCounterpart(english, spanish, CopilotSpendWorkbook.CatalogModule);

            var es = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish);
            var sheets = CopilotInvestmentWorkbookLanguageTests.Sheets(spanish);
            CollectionAssert.AreEqual(new[] { "summary", "organisation", "studio", "balances", "distribution", "top", "lowest", "people" }
                .Select(k => es.Text("copilotSpend.workbook.sheet." + k)).ToList(), sheets.Select(s => s.Name).ToList());
            var texts = CopilotInvestmentWorkbookLanguageTests.Texts(sheets);
            CollectionAssert.Contains(texts, es.Text("copilotSpend.workbook.summary.sourcesNote"));
            CollectionAssert.Contains(texts, GreekDepartment);
            CollectionAssert.Contains(texts, "Contoso helpdesk agent");
            CollectionAssert.Contains(texts, "Contoso " + GreekDepartment, "Environment names round-trip as stored.");
            CollectionAssert.Contains(texts, es.Text("copilotSpend.workbook.studio.agentsNote"));
            CollectionAssert.Contains(texts, es.Text("copilotSpend.workbook.studio.unnamedAgent"));
            Assert.IsTrue(sheets.SelectMany(s => s.Cells).Any(c => c.Type == "inlineStr" && c.Text.StartsWith("=HYPERLINK", StringComparison.Ordinal)),
                "A value that looks like a formula stays text.");
            Assert.AreEqual(CopilotSpendWorkbook.FileName(summary), CopilotSpendWorkbook.FileName(summary, PortalLanguage.English));
            StringAssert.StartsWith(CopilotSpendWorkbook.FileName(summary, PortalLanguage.Spanish), "gasto-copilot-");

            // Without See PII: no top spenders, lowest spenders or people sheet, and no sign-in name anywhere.
            var aggregates = CopilotInvestmentWorkbookLanguageTests.Sheets(CopilotSpendWorkbook.Build(summary.WithoutIndividualData(), false, "test", PortalLanguage.Spanish));
            Assert.AreEqual(sheets.Count - 3, aggregates.Count);
            Assert.IsFalse(CopilotInvestmentWorkbookLanguageTests.Texts(aggregates).Any(t => t.Contains("@contoso.example")));
            CollectionAssert.Contains(CopilotInvestmentWorkbookLanguageTests.Texts(aggregates), es.Text("copilotSpend.workbook.summary.personDataExcluded"));

            // A filtered workbook leaves the tenant's own policies, pool balances and agent ledger out, and says so.
            var filtered = CopilotSpendCalculator.Calculate(Input(id => id != 4, filtered: true));
            var filteredTexts = CopilotInvestmentWorkbookLanguageTests.Texts(CopilotInvestmentWorkbookLanguageTests.Sheets(
                CopilotSpendWorkbook.Build(filtered, true, "test", PortalLanguage.Spanish)));
            CollectionAssert.Contains(filteredTexts, es.Text("copilotSpend.workbook.balances.tenantWithheld"));
            CollectionAssert.Contains(filteredTexts, es.Text("copilotSpend.workbook.studio.agentsWithheld"));
            CollectionAssert.DoesNotContain(filteredTexts, "Contoso helpdesk agent");
        }

        [TestMethod]
        public void AWorkbookThatCannotBeWritten_SaysWhyInTheReadersLanguage()
        {
            var response = CopilotSpendAPIController.WorkbookError(HttpStatusCode.BadRequest, "invalid", PortalLanguage.Spanish);
            Assert.AreEqual((int)HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("text/plain; charset=utf-8", response.ContentType);
            Assert.AreEqual(PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish).Text("copilotSpend.workbook.error.invalid"),
                response.Content);
        }

        [TestMethod]
        public void TheDistributionSheet_GivesAReaderWithoutSeePiiTheAnonymisedFigures_AndLeavesAWithheldFigureEmptyWithItsReason()
        {
            var summary = CopilotSpendCalculator.Calculate(Input());
            summary.Studio.Distribution = CopilotSpendCalculator.Distribution(WorkedExample(), p => p.StudioCredits, null);
            var trimmed = summary.WithoutIndividualData();

            var english = CopilotSpendWorkbook.Build(trimmed, false, "test", PortalLanguage.English);
            var spanish = CopilotSpendWorkbook.Build(trimmed, false, "test", PortalLanguage.Spanish);
            CopilotInvestmentWorkbookLanguageTests.AssertSpanishCounterpart(english, spanish, CopilotSpendWorkbook.CatalogModule);

            var fewerThanFive = ("count", "5");
            foreach (var (language, book) in new[] { (PortalLanguage.English, english), (PortalLanguage.Spanish, spanish) })
            {
                var text = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, language);
                string W(string key) => text.Text("copilotSpend.workbook." + key);
                var sheets = CopilotInvestmentWorkbookLanguageTests.Sheets(book);
                CollectionAssert.AreEqual(new[] { "summary", "organisation", "studio", "balances", "distribution" }.Select(k => W("sheet." + k)).ToList(),
                    sheets.Select(s => s.Name).ToList(), language + ": no sheet that names anyone.");
                Assert.IsFalse(CopilotInvestmentWorkbookLanguageTests.Texts(sheets).Any(t => t.Contains("@contoso.example")), language.ToString());

                var rows = SheetRows(sheets.Single(s => s.Name == W("sheet.distribution")).Cells);
                var consumers = RowsLabelled(rows, W("dist.consumers"));
                Assert.AreEqual(2, consumers.Count, "One section per source.");
                Assert.AreEqual(10d, CellNumber(consumers[0], "B"), 1e-9);
                Assert.AreEqual(ThousandsStyle, consumers[0]["B"].Style, "A count of people.");
                Assert.AreEqual(200d, CellNumber(RowsLabelled(rows, W("dist.total")).Single(), "B"), 1e-9);
                Assert.AreEqual(5d, CellNumber(RowsLabelled(rows, W("dist.median")).Single(), "B"), 1e-9);
                Assert.AreEqual(20d, CellNumber(RowsLabelled(rows, W("dist.mean")).Single(), "B"), 1e-9);
                var gini = RowsLabelled(rows, W("dist.gini")).Single();
                Assert.AreEqual(0.665, CellNumber(gini, "B"), 1e-9);
                Assert.AreEqual(W("dist.giniNote"), CellText(gini, "D"));
                foreach (var key in new[] { "dist.p90", "dist.top10", "dist.top20" })
                {
                    var row = RowsLabelled(rows, W(key)).Single();
                    Assert.IsFalse(row.ContainsKey("B"), key + ": a withheld figure is an empty cell, never a 0.");
                    Assert.AreEqual(text.Text("copilotSpend.workbook.dist.figureWithheld", fewerThanFive), CellText(row, "D"), key);
                }
                var half = RowsLabelled(rows, W("dist.half")).Single();
                Assert.IsFalse(half.ContainsKey("B"));
                Assert.AreEqual(text.Text("copilotSpend.workbook.dist.halfFewer", fewerThanFive), CellText(half, "D"));

                // The bands are combined until each holds at least five people, and carry no credits: so there is no credits column.
                Assert.AreEqual(1, RowsLabelled(rows, text.Text("copilotSpend.workbook.dist.bandsCombined", fewerThanFive)).Count);
                var bands = rows.FindIndex(r => CellText(r, "A") == W("dist.col.from"));
                Assert.IsFalse(rows[bands].ContainsKey("D"), "No credits column.");
                AssertClose(new[] { 1d, 200d, 10d }, new[] { "A", "B", "C" }.Select(c => CellNumber(rows[bands + 1], c)).ToList(), "The one band.");
                Assert.IsFalse(rows[bands + 1].ContainsKey("D"));

                // Only the cuts at least five people apart, and not the curve's origin.
                var pareto = rows.FindIndex(r => CellText(r, "A") == W("dist.col.people"));
                AssertClose(new[] { 5d, 0.5, 0.925 }, new[] { "A", "B", "C" }.Select(c => CellNumber(rows[pareto + 1], c)).ToList(), "Five people.");
                AssertClose(new[] { 10d, 1, 1 }, new[] { "A", "B", "C" }.Select(c => CellNumber(rows[pareto + 2], c)).ToList(), "Everyone.");
                Assert.AreEqual(PercentStyle, rows[pareto + 1]["C"].Style);
                Assert.AreEqual(W("summary.col.balances"), CellText(rows[pareto + 3], "A"), "Then the other source, on its own.");

                // Two balance consumers are too few to describe: the counts, then why there is nothing more.
                Assert.AreEqual(2d, CellNumber(consumers[1], "B"), 1e-9);
                Assert.AreEqual(1d, CellNumber(RowsLabelled(rows, W("dist.zeroPeople")).Single(), "B"), 1e-9);
                Assert.AreEqual(text.Text("copilotSpend.workbook.dist.withheld", fewerThanFive), CellText(rows.Last(), "A"));
                Assert.IsFalse(rows.SelectMany(r => r.Values).Any(c => c.Type == null && double.Parse(c.Value, CultureInfo.InvariantCulture) == 0),
                    language + ": no zero anywhere on the sheet.");
            }
        }

        [TestMethod]
        public void ADistributionThatIsWithheldOrNotAvailable_IsSaidInWords_NeverWrittenAsZero()
        {
            // Three Studio consumers are too few to describe without See PII, and there are no balance readings at all.
            var input = Input();
            input.Credits = new CreditFacts { TablesPresent = false };
            var trimmed = CopilotSpendCalculator.Calculate(input).WithoutIndividualData();
            Assert.IsTrue(trimmed.Studio.Distribution.Withheld);
            Assert.IsNull(trimmed.Balances.Distribution);

            var es = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish);
            var cells = CopilotInvestmentWorkbookLanguageTests.Sheets(CopilotSpendWorkbook.Build(trimmed, false, "test", PortalLanguage.Spanish))
                .Single(s => s.Name == es.Text("copilotSpend.workbook.sheet.distribution")).Cells;
            AssertClose(new[] { 3d }, cells.Where(c => c.Type == null).Select(c => double.Parse(c.Value, CultureInfo.InvariantCulture)).ToList(),
                "Only the count of consumers is a number: nothing withheld or unknown is written as 0.");
            var texts = cells.Where(c => c.Text != null).Select(c => c.Text).ToList();
            CollectionAssert.Contains(texts, es.Text("copilotSpend.workbook.dist.withheld", ("count", "5")));
            CollectionAssert.Contains(texts, es.Text("copilotSpend.workbook.dist.notAvailable"));
            CollectionAssert.DoesNotContain(texts, es.Text("copilotSpend.workbook.dist.total"));

            // A readable ledger with no charges: no one consumed any, a known zero, and every sheet says so.
            input = Input();
            input.Studio.Users.Clear();
            var idle = CopilotSpendCalculator.Calculate(input);
            Assert.AreEqual(0, idle.Studio.Distribution.Consumers);
            var idleSheets = CopilotInvestmentWorkbookLanguageTests.Sheets(CopilotSpendWorkbook.Build(idle, true, "test", PortalLanguage.Spanish));
            foreach (var key in new[] { "distribution", "top", "lowest" })
                CollectionAssert.Contains(CopilotInvestmentWorkbookLanguageTests.Texts(idleSheets.Where(s => s.Name == es.Text("copilotSpend.workbook.sheet." + key))),
                    es.Text("copilotSpend.workbook.dist.noConsumers"), key);
        }

        [TestMethod]
        public void TheTopAndLowestSpenderSheets_NameEachSourcesLargestAndSmallestConsumers_ForAReaderWithSeePii()
        {
            var summary = CopilotSpendCalculator.Calculate(Input());
            summary.Studio.Distribution = CopilotSpendCalculator.Distribution(WorkedExample(), p => p.StudioCredits, null);

            var english = CopilotSpendWorkbook.Build(summary, true, "test", PortalLanguage.English);
            var spanish = CopilotSpendWorkbook.Build(summary, true, "test", PortalLanguage.Spanish);
            CopilotInvestmentWorkbookLanguageTests.AssertSpanishCounterpart(english, spanish, CopilotSpendWorkbook.CatalogModule);

            // A tie on 5 credits is ordered by sign-in name, whatever its case, in both lists.
            var largest = new[] { "person01", "person02", "person03", "person04", "anna", "Bea", "person07", "person08", "person09", "person10" };
            var smallest = new[] { "person10", "person09", "person08", "person07", "anna", "Bea", "person04", "person03", "person02", "person01" };
            foreach (var (language, book) in new[] { (PortalLanguage.English, english), (PortalLanguage.Spanish, spanish) })
            {
                var text = PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, language);
                string W(string key) => text.Text("copilotSpend.workbook." + key);
                var sheets = CopilotInvestmentWorkbookLanguageTests.Sheets(book);
                CollectionAssert.AreEqual(new[] { "summary", "organisation", "studio", "balances", "distribution", "top", "lowest", "people" }
                    .Select(k => W("sheet." + k)).ToList(), sheets.Select(s => s.Name).ToList(), language.ToString());

                foreach (var top in new[] { true, false })
                {
                    var rows = SheetRows(sheets.Single(s => s.Name == W(top ? "sheet.top" : "sheet.lowest")).Cells);
                    Assert.AreEqual(W(top ? "ranked.topTitle" : "ranked.lowestTitle"), CellText(rows[0], "A"));
                    Assert.AreEqual(text.Text("copilotSpend.workbook.ranked.intro", ("count", "10")), CellText(rows[1], "A"));

                    // One list per source, each with its own credits column: never added together.
                    var headers = Enumerable.Range(0, rows.Count).Where(i => CellText(rows[i], "A") == W("people.col.upn")).ToList();
                    Assert.AreEqual(2, headers.Count, "Studio, then balances.");
                    Assert.AreEqual(W("people.col.studioCredits"), CellText(rows[headers[0]], "C"));
                    Assert.AreEqual(W("summary.col.balances"), CellText(rows[headers[1] - 1], "A"));
                    Assert.AreEqual(W("people.col.balance"), CellText(rows[headers[1]], "C"));

                    var studio = rows.Skip(headers[0] + 1).Take(headers[1] - headers[0] - 2).ToList();
                    CollectionAssert.AreEqual((top ? largest : smallest).Select(n => n + "@contoso.example").ToList(), studio.Select(r => CellText(r, "A")).ToList(),
                        top ? "Largest first." : "Smallest above zero first.");
                    Assert.AreEqual(GreekDepartment, CellText(studio[0], "B"), "Tenant text as stored.");
                    Assert.AreEqual(top ? 100d : 1d, CellNumber(studio[0], "C"), 1e-9);
                    Assert.IsNull(studio[0]["C"].Style, "Credits as a plain number.");
                    Assert.AreEqual(top ? 0.5 : 0.005, CellNumber(studio[0], "D"), 1e-9, "A share of the credits matched to the people in view.");
                    Assert.AreEqual(PercentStyle, studio[0]["D"].Style);

                    var balances = rows.Skip(headers[1] + 1).ToList();
                    CollectionAssert.AreEqual(top ? new[] { "person1@contoso.example", "person4@contoso.example" } : new[] { "person4@contoso.example", "person1@contoso.example" },
                        balances.Select(r => CellText(r, "A")).ToList());
                    Assert.AreEqual(top ? 100d / 170 : 70d / 170, CellNumber(balances[0], "D"), 1e-4, "Of the 170 matched, not the account no user matches.");
                }

                // With See PII the bands keep their credits.
                var distribution = SheetRows(sheets.Single(s => s.Name == W("sheet.distribution")).Cells);
                var bands = distribution.FindIndex(r => CellText(r, "A") == W("dist.col.from"));
                Assert.AreEqual(W("dist.col.credits"), CellText(distribution[bands], "D"));
                Assert.AreEqual(W("dist.bandsNote"), CellText(distribution[bands - 1], "A"));
                AssertClose(new[] { 1d, 2d, 1d, 1d }, new[] { "A", "B", "C", "D" }.Select(c => CellNumber(distribution[bands + 1], c)).ToList(), "The first band.");
                Assert.AreEqual(50d, CellNumber(RowsLabelled(distribution, W("dist.p90"))[0], "B"), 1e-9);
                var top10 = RowsLabelled(distribution, W("dist.top10"))[0];
                Assert.AreEqual(0.5, CellNumber(top10, "B"), 1e-9);
                Assert.AreEqual(PercentStyle, top10["B"].Style);
            }
        }

        private static readonly string ThousandsStyle = ((int)XlsxCellStyle.Thousands).ToString(CultureInfo.InvariantCulture);
        private static readonly string PercentStyle = ((int)XlsxCellStyle.Percent).ToString(CultureInfo.InvariantCulture);

        /// <summary>A sheet's rows in order, each cell by its column letter. A blank row has no cells, so it is not listed.</summary>
        private static List<Dictionary<string, WorkbookCell>> SheetRows(IEnumerable<WorkbookCell> cells) =>
            cells.GroupBy(c => int.Parse(new string(c.Ref.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture))
                .OrderBy(g => g.Key)
                .Select(g => g.ToDictionary(c => new string(c.Ref.Where(char.IsLetter).ToArray())))
                .ToList();

        private static List<Dictionary<string, WorkbookCell>> RowsLabelled(List<Dictionary<string, WorkbookCell>> rows, string label) =>
            rows.Where(r => CellText(r, "A") == label).ToList();

        private static string CellText(Dictionary<string, WorkbookCell> row, string column) => row.TryGetValue(column, out var cell) ? cell.Text : null;

        private static double CellNumber(Dictionary<string, WorkbookCell> row, string column)
        {
            Assert.IsTrue(row.TryGetValue(column, out var cell), "A value in column " + column + ".");
            Assert.IsNull(cell.Type, "A typed number, not text: " + cell.Text);
            return double.Parse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static CopilotSpendPersonRow Row(int id, double? studio, double? balance = null, string upn = null) => new CopilotSpendPersonRow
        {
            UserId = id,
            UserPrincipalName = upn ?? $"person{id:00}@contoso.example",
            Department = GreekDepartment,
            StudioCredits = studio,
            BalanceCredits = balance,
        };

        /// <summary>Ten consumers of 200 credits between them, a zero and an unknown amount. Two tie on 5, so they are ordered by sign-in name.</summary>
        private static List<CopilotSpendPersonRow> WorkedExample() => new List<CopilotSpendPersonRow>
        {
            Row(1, 100), Row(2, 50), Row(3, 20), Row(4, 10),
            Row(5, 5, upn: "Bea@contoso.example"), Row(6, 5, upn: "anna@contoso.example"),
            Row(7, 4), Row(8, 3), Row(9, 2), Row(10, 1),
            Row(11, 0), Row(12, null),
        };

        private static void AssertClose(IList<double> expected, IList<double> actual, string message)
        {
            Assert.AreEqual(expected.Count, actual.Count, message);
            for (var i = 0; i < expected.Count; i++) Assert.AreEqual(expected[i], actual[i], 1e-9, $"{message} [{i}]");
        }

        private static void AssertBands(IList<CopilotSpendBand> actual, params (double From, double To, int People, double? Credits)[] expected)
        {
            Assert.AreEqual(expected.Length, actual.Count, "Bands.");
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].From, actual[i].From, 1e-9, $"Band {i} from.");
                Assert.AreEqual(expected[i].To, actual[i].To, 1e-9, $"Band {i} to.");
                Assert.AreEqual(expected[i].People, actual[i].People, $"Band {i} people.");
                if (expected[i].Credits.HasValue)
                {
                    Assert.IsNotNull(actual[i].Credits, $"Band {i} credits.");
                    Assert.AreEqual(expected[i].Credits.Value, actual[i].Credits.Value, 1e-9, $"Band {i} credits.");
                }
                else Assert.IsNull(actual[i].Credits, $"Band {i} credits are withheld.");
            }
        }

        private static CopilotSpendBand Band(double from, double to, int people, double credits) =>
            new CopilotSpendBand { From = from, To = to, People = people, Credits = credits };

        [TestMethod]
        public void ADistribution_DescribesHowConcentratedTheSpendIs_FromTheConsumersAlone()
        {
            var d = CopilotSpendCalculator.Distribution(WorkedExample(), p => p.StudioCredits, 1);

            Assert.AreEqual(10, d.Consumers, "A zero and an unknown amount are not consumers.");
            Assert.AreEqual(CopilotSpendCalculator.DefaultMinGroupSize, d.MinPeople);
            Assert.AreEqual(1, d.ZeroPeople);
            Assert.AreEqual(200d, d.Total);
            Assert.AreEqual(20d, d.Mean);
            Assert.AreEqual(5d, d.Median);
            Assert.AreEqual(50d, d.P90, "Nearest rank: nine in ten consumers are at or below it.");
            Assert.AreEqual(100d, d.Max);
            Assert.AreEqual(1d, d.Min);
            Assert.AreEqual(0.665, d.Gini.Value, 1e-9);
            Assert.AreEqual(0.5, d.Top10Share.Value, 1e-9, "The top tenth is one person here.");
            Assert.AreEqual(0.75, d.Top20Share.Value, 1e-9);
            Assert.AreEqual(1, d.PeopleForHalf);
            Assert.IsFalse(d.HalfFromFewerThanMin);
            Assert.IsFalse(d.Withheld);

            CollectionAssert.AreEqual(Enumerable.Range(0, 11).ToList(), d.Pareto.Select(p => p.People).ToList(), "Every consumer is a point when there are 50 or fewer.");
            AssertClose(Enumerable.Range(0, 11).Select(k => k / 10d).ToList(), d.Pareto.Select(p => p.PeopleShare).ToList(), "Share of consumers.");
            AssertClose(new[] { 0, 0.5, 0.75, 0.85, 0.9, 0.925, 0.95, 0.97, 0.985, 0.995, 1 }, d.Pareto.Select(p => p.CreditShare).ToList(), "Share of credits.");
            AssertBands(d.Bands, (1, 2, 1, 1), (2, 5, 3, 9), (5, 10, 2, 10), (10, 20, 1, 10), (20, 50, 1, 20), (50, 100, 1, 50), (100, 200, 1, 100));

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 6, 5, 7, 8, 9, 10 }, d.Top.Select(p => p.UserId).ToList(), "Largest first; a tie by sign-in name, whatever its case.");
            Assert.AreEqual("person01@contoso.example", d.Top[0].UserPrincipalName);
            Assert.AreEqual(GreekDepartment, d.Top[0].Department);
            Assert.AreEqual(100, d.Top[0].Credits, 1e-9);
            Assert.AreEqual(0.5, d.Top[0].Share, 1e-9);
            CollectionAssert.AreEqual(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, d.Lowest.Select(p => p.UserId).ToList(), "Smallest above zero first.");
            Assert.AreEqual(0.005, d.Lowest[0].Share, 1e-9);

            foreach (var none in new[] { new List<CopilotSpendPersonRow>(), new List<CopilotSpendPersonRow> { Row(1, 0), Row(2, null) } })
            {
                var empty = CopilotSpendCalculator.Distribution(none, p => p.StudioCredits, null);
                Assert.AreEqual(0, empty.Consumers);
                Assert.AreEqual(0d, empty.Total, "No one consumed any: a known zero.");
                Assert.IsNull(empty.Median);
                Assert.AreEqual(0, empty.Top.Count + empty.Lowest.Count + empty.Pareto.Count + empty.Bands.Count);
            }
        }

        [TestMethod]
        public void ALargeDistribution_KeepsFiftyPointsOfItsCurve_AndTenNamesAtEachEnd()
        {
            var d = CopilotSpendCalculator.Distribution(Enumerable.Range(1, 60).Select(id => Row(id, id)).ToList(), p => p.StudioCredits, null);

            Assert.AreEqual(1830d, d.Total);
            Assert.IsNull(d.ZeroPeople, "The Studio ledger records charges only, so it has no readings of zero.");
            Assert.AreEqual(54d, d.P90);
            Assert.AreEqual(0.1885, d.Top10Share.Value, 1e-9);
            Assert.AreEqual(0.3574, d.Top20Share.Value, 1e-9);
            Assert.AreEqual(18, d.PeopleForHalf);
            Assert.AreEqual(CopilotSpendCalculator.ParetoPoints + 1, d.Pareto.Count);
            var last = d.Pareto.Last();
            Assert.AreEqual(60, last.People);
            Assert.AreEqual(1, last.PeopleShare, 1e-9);
            Assert.AreEqual(1, last.CreditShare, 1e-9);
            CollectionAssert.AreEqual(Enumerable.Range(51, 10).Reverse().ToList(), d.Top.Select(p => p.UserId).ToList());
            CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToList(), d.Lowest.Select(p => p.UserId).ToList());
            AssertBands(d.Bands, (1, 2, 1, 1), (2, 5, 3, 9), (5, 10, 5, 35), (10, 20, 10, 145), (20, 50, 30, 1035), (50, 100, 11, 605));

            var anonymised = d.Anonymised(CopilotSpendCalculator.DefaultMinGroupSize);
            Assert.IsFalse(anonymised.Withheld);
            Assert.AreEqual(5, anonymised.MinPeople);
            Assert.AreEqual(54d, anonymised.P90, "Seven consumers are at or above it.");
            Assert.AreEqual(0.1885, anonymised.Top10Share.Value, 1e-9, "Six people.");
            Assert.AreEqual(0.3574, anonymised.Top20Share.Value, 1e-9, "Six more than the top tenth, so subtracting one from the other narrows to no fewer.");
            CollectionAssert.AreEqual(new[] { 0, 6, 12, 17, 22, 28, 34, 40, 46, 52, 60 }, anonymised.Pareto.Select(p => p.People).ToList());
            for (var i = 1; i < anonymised.Pareto.Count; i++)
                Assert.IsTrue(anonymised.Pareto[i].People - anonymised.Pareto[i - 1].People >= 5, "No two points of the curve are fewer than five people apart.");
            Assert.AreEqual(18, anonymised.PeopleForHalf);
            Assert.IsFalse(anonymised.HalfFromFewerThanMin);
            AssertBands(anonymised.Bands, (1, 10, 9, null), (10, 20, 10, null), (20, 50, 30, null), (50, 100, 11, null));
            Assert.IsNull(anonymised.Min);
            Assert.IsNull(anonymised.Max);
            Assert.AreEqual(0, anonymised.Top.Count + anonymised.Lowest.Count);
        }

        [TestMethod]
        public void ADistributionForAReaderWithoutSeePii_NamesNoOne_AndNoFigureNarrowsToFewerThanFivePeople()
        {
            var original = CopilotSpendCalculator.Distribution(WorkedExample(), p => p.StudioCredits, 1);
            var d = original.Anonymised(CopilotSpendCalculator.DefaultMinGroupSize);

            Assert.IsFalse(d.Withheld, "Ten consumers are enough to describe.");
            Assert.AreEqual(10, d.Consumers);
            Assert.AreEqual(1, d.ZeroPeople);
            Assert.AreEqual(200d, d.Total);
            Assert.AreEqual(5d, d.Median);
            Assert.AreEqual(20d, d.Mean);
            Assert.AreEqual(0.665, d.Gini.Value, 1e-9);
            Assert.IsNull(d.P90, "Only two consumers are at or above it.");
            Assert.IsNull(d.Top10Share, "One person.");
            Assert.IsNull(d.Top20Share, "Two people.");
            Assert.IsNull(d.Min);
            Assert.IsNull(d.Max);
            CollectionAssert.AreEqual(new[] { 0, 5, 10 }, d.Pareto.Select(p => p.People).ToList());
            Assert.IsNull(d.PeopleForHalf);
            Assert.IsTrue(d.HalfFromFewerThanMin, "Said as fewer than five people rather than left blank.");
            AssertBands(d.Bands, (1, 200, 10, null));
            Assert.AreEqual(0, d.Top.Count + d.Lowest.Count);

            // The cached original is never edited.
            Assert.AreEqual(10, original.Top.Count);
            Assert.AreEqual(10, original.Lowest.Count);
            Assert.AreEqual(50d, original.P90);
            Assert.AreEqual(100d, original.Max);
            Assert.AreEqual(1d, original.Min);
            Assert.AreEqual(0.5, original.Top10Share.Value, 1e-9);
            Assert.AreEqual(1, original.PeopleForHalf);
            Assert.AreEqual(11, original.Pareto.Count);
            Assert.AreEqual(7, original.Bands.Count);
            AssertBands(original.Bands.Take(1).ToList(), (1, 2, 1, 1));

            var few = CopilotSpendCalculator.Distribution(WorkedExample().Take(3), p => p.StudioCredits, null).Anonymised(CopilotSpendCalculator.DefaultMinGroupSize);
            Assert.IsTrue(few.Withheld, "Three consumers: only the count is sent.");
            Assert.AreEqual(3, few.Consumers);
            Assert.IsTrue(new[] { few.Total, few.Median, few.Mean, few.P90, few.Gini, few.Top10Share, few.Top20Share, few.Min, few.Max }.All(v => v == null));
            Assert.IsNull(few.PeopleForHalf);
            Assert.IsFalse(few.HalfFromFewerThanMin);
            Assert.AreEqual(0, few.Pareto.Count + few.Bands.Count + few.Top.Count + few.Lowest.Count);

            var none = CopilotSpendCalculator.Distribution(new[] { Row(1, 0) }, p => p.StudioCredits, null).Anonymised(CopilotSpendCalculator.DefaultMinGroupSize);
            Assert.IsFalse(none.Withheld, "No one consumed any: that is said, not withheld.");
            Assert.AreEqual(0, none.Consumers);
            Assert.AreEqual(0d, none.Total);
        }

        [TestMethod]
        public void Bands_RunOnAOneTwoFiveScale_KeepEmptyBandsInPlace_AndFallBackToDecadesWhenThereWouldBeTooMany()
        {
            AssertBands(CopilotSpendCalculator.Bands(new[] { 0.004, 0.3, 7 }),
                (0, 0.02, 1, 0.004), (0.02, 0.05, 0, 0), (0.05, 0.1, 0, 0), (0.1, 0.2, 0, 0), (0.2, 0.5, 1, 0.3),
                (0.5, 1, 0, 0), (1, 2, 0, 0), (2, 5, 0, 0), (5, 10, 1, 7));

            AssertBands(CopilotSpendCalculator.Bands(new[] { 0.01, 1e6 }),
                (0.01, 0.1, 1, 0.01), (0.1, 1, 0, 0), (1, 10, 0, 0), (10, 100, 0, 0), (100, 1e3, 0, 0),
                (1e3, 1e4, 0, 0), (1e4, 1e5, 0, 0), (1e5, 1e6, 0, 0), (1e6, 1e7, 1, 1e6));

            Assert.AreEqual(0, CopilotSpendCalculator.Bands(new double[0]).Count);
        }

        [TestMethod]
        public void BandsForAReaderWithoutSeePii_HoldNoOneOrAtLeastFivePeople_AndNoAmounts()
        {
            var bands = new List<CopilotSpendBand>
            {
                Band(0.01, 0.02, 0, 0), Band(0.02, 0.05, 0, 0), Band(0.05, 0.1, 6, 0.4), Band(0.1, 0.2, 0, 0),
                Band(0.2, 0.5, 2, 0.6), Band(0.5, 1, 0, 0), Band(1, 2, 1, 1),
            };

            // The three largest are too few on their own, so they join the band below them, and the empty band between goes.
            AssertBands(CopilotSpendDistribution.BandsOfAtLeast(bands, 5), (0.01, 0.02, 0, null), (0.02, 0.05, 0, null), (0.05, 2, 9, null));
            AssertBands(bands, (0.01, 0.02, 0, 0), (0.02, 0.05, 0, 0), (0.05, 0.1, 6, 0.4), (0.1, 0.2, 0, 0), (0.2, 0.5, 2, 0.6), (0.5, 1, 0, 0), (1, 2, 1, 1));

            // Fewer than five in all: one band. Anonymised withholds a distribution this small before it gets here.
            AssertBands(CopilotSpendDistribution.BandsOfAtLeast(new[] { Band(1, 2, 1, 1), Band(2, 5, 0, 0), Band(5, 10, 2, 13) }, 5), (1, 10, 3, null));
        }

        [TestMethod]
        public void TheSummaryForAReaderWithoutSeePii_CarriesAnonymisedDistributions_OnACopy()
        {
            var summary = CopilotSpendCalculator.Calculate(Input());
            var studio = summary.Studio.Distribution;
            var balances = summary.Balances.Distribution;
            Assert.AreEqual(3, studio.Consumers, "Charges no person was matched to are not anyone's.");
            Assert.IsNull(studio.ZeroPeople);
            Assert.AreEqual(60d, studio.Total);
            CollectionAssert.AreEqual(new[] { 4, 5, 1 }, studio.Top.Select(p => p.UserId).ToList());
            Assert.AreEqual(2, balances.Consumers, "An unknown amount is not a consumer.");
            Assert.AreEqual(1, balances.ZeroPeople, "A reading of zero.");
            CollectionAssert.AreEqual(new[] { 1, 4 }, balances.Top.Select(p => p.UserId).ToList());

            var trimmed = summary.WithoutIndividualData();
            foreach (var d in new[] { trimmed.Studio.Distribution, trimmed.Balances.Distribution })
            {
                Assert.IsTrue(d.Withheld, "Fewer than five consumers, whatever the report's own smallest group.");
                Assert.IsNull(d.Total);
                Assert.AreEqual(0, d.Top.Count + d.Lowest.Count);
            }
            Assert.AreEqual(3, trimmed.Studio.Distribution.Consumers);
            Assert.AreEqual(1, trimmed.Balances.Distribution.ZeroPeople);

            Assert.AreSame(studio, summary.Studio.Distribution, "The cached summary is never edited.");
            Assert.AreSame(balances, summary.Balances.Distribution);
            Assert.IsFalse(studio.Withheld);
            Assert.AreEqual(3, studio.Top.Count);
            Assert.AreEqual(2, balances.Top.Count);
        }

        private const string GreekAgent = "\u03a4\u03b1\u03be\u03af\u03b4\u03b9\u03b1 Contoso";
        private const string GreekPhrase = "\u03c0\u03c1\u03bf\u03cb\u03c0\u03bf\u03bb\u03bf\u03b3\u03b9\u03c3\u03bc\u03cc\u03c2";

        private static DateTime Oct(int day, int hour = 0) => new DateTime(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);

        private static PersonActivityRow Turn(int day, string app, int? agent, long turns, double? estimate = null) => new PersonActivityRow
        {
            ActivityDate = Oct(day),
            AppHost = app,
            AgentId = agent,
            Turns = turns,
            EstimateCredits = estimate,
            PricedTurns = estimate.HasValue ? turns : 0,
        };

        private static PersonPromptRow PromptRow(string app, long prompts, int days, string type = CopilotSpendSql.PromptInteractionType) =>
            new PersonPromptRow { AppClass = app, InteractionType = type, Interactions = prompts, Days = days };

        private static List<PromptCategoryDefinition> Taxonomy(params (string Id, string Name, string NameKey, string HumanMode)[] categories) =>
            categories.Select(c => new PromptCategoryDefinition { Id = c.Id, Name = c.Name, NameKey = c.NameKey, HumanMode = c.HumanMode }).ToList();

        /// <summary>Person 1 of five, over 20-26 October, with the Studio ledger stored from the 21st to the 25th.</summary>
        private static CopilotSpendPersonDetailInput DetailInput()
        {
            var people = new List<CopilotSpendPersonRow> { Row(1, 30, 100), Row(2, 30, 50), Row(3, 60, 0), Row(4, null, null), Row(5, 0, 25) };
            var facts = new PersonDetailFacts
            {
                ActivityAssessed = true,
                PromptsAssessed = true,
                CoworkAgentIds = { 7 },
                Activity =
                {
                    Turn(20, "Teams", null, 5),
                    Turn(20, "teams", 11, 3, 0.6),
                    Turn(21, "Cowork", null, 4),
                    Turn(22, "Outlook", 7, 2),
                    Turn(22, CopilotTurnSql.MakerTestAppHost, 11, 6, 1.2),
                    Turn(23, "  Teams ", 12, 1, 0.25),
                    Turn(26, null, 13, 2),
                    Turn(25, "Word", 14, 1),
                    Turn(25, "SharePoint", 15, 1),
                    Turn(19, "Teams", null, 100),
                    Turn(27, "Teams", 11, 100, 9),
                },
                Prompts =
                {
                    PromptRow("Teams", 10, 4),
                    PromptRow("Word", 5, 3),
                    PromptRow(null, 3, 2),
                    PromptRow(string.Empty, 1, 1),
                    PromptRow("Teams", 50, 4, type: "aiResponse"),
                },
                CreditServices = { new CreditServiceFact { Category = "chat", Consumed = 100, Unit = CopilotInvestmentSql.CreditUnit, Allowed = true } },
                CreditUnevaluated =
                {
                    new CreditUnevaluatedFact { Category = "chat", Reason = "noPolicyAssigned" },
                    new CreditUnevaluatedFact { Category = "agents", Reason = "noPolicyAssigned" },
                },
                Balances =
                {
                    new PersonBalancePoint { Date = Oct(25), Credits = 100, Quality = 2 },
                    new PersonBalancePoint { Date = Oct(21), Credits = 40, Quality = 2 },
                    new PersonBalancePoint { Date = Oct(23), Credits = 70, Quality = 2 },
                },
                StudioEnvironments =
                {
                    new PersonStudioEnvironmentRow { EnvironmentId = "env-b", EnvironmentName = "Contoso Sales", Credits = 1.5, ActiveDays = 1 },
                    new PersonStudioEnvironmentRow { EnvironmentId = "env-a", EnvironmentName = "Contoso " + GreekDepartment, Credits = 3.25, ActiveDays = 2 },
                    new PersonStudioEnvironmentRow { EnvironmentId = "env-c", EnvironmentName = null, Credits = 1.5, ActiveDays = 1 },
                },
            };
            var usage = new CopilotSpendPersonFacts
            {
                StudioAssessed = true,
                StudioDays =
                {
                    new CopilotSpendDay { Date = Oct(21), Status = CopilotSpendDayStatus.Recorded, Credits = 2.5, People = 1 },
                    new CopilotSpendDay { Date = Oct(23), Status = CopilotSpendDayStatus.Recorded, Credits = 4, People = 1 },
                },
                PromptsAssessed = true,
                PromptTotals = new CopilotSpendPromptTotals { Prompts = 19, Conversations = 7, Days = 5, AverageWords = 12.5, WithAttachments = 2 },
                Recent =
                {
                    new CopilotSpendRecentPrompt { InteractionId = 101, CreatedUtc = Oct(24, 9), AppClass = "Teams", CategoryId = "summarise", TaxonomyVersion = "v2" },
                    new CopilotSpendRecentPrompt { InteractionId = 103, CreatedUtc = Oct(25, 8), AppClass = "Word", CategoryId = "none" },
                    new CopilotSpendRecentPrompt { InteractionId = 102, CreatedUtc = Oct(25, 8), AppClass = "Teams", CategoryId = "draft", TaxonomyVersion = "v1" },
                    new CopilotSpendRecentPrompt { InteractionId = 104, CreatedUtc = Oct(20), CategoryId = "summarise", TaxonomyVersion = "v9" },
                },
                CategoriesAssessed = true,
                Categories =
                {
                    new CopilotSpendCategoryFact { CategoryId = "summarise", TaxonomyVersion = "v1", HumanMode = "automation", Prompts = 3, LastUtc = Oct(21) },
                    new CopilotSpendCategoryFact { CategoryId = "summarise", TaxonomyVersion = "v2", Prompts = 6, LastUtc = Oct(24) },
                    new CopilotSpendCategoryFact { CategoryId = "draft", TaxonomyVersion = "v1", Prompts = 4, LastUtc = Oct(25) },
                    new CopilotSpendCategoryFact { CategoryId = "draft", TaxonomyVersion = "v2", Prompts = 4, LastUtc = Oct(22) },
                    new CopilotSpendCategoryFact { CategoryId = CopilotSpendPersonDetailBuilder.NotClassifiedCategoryId, TaxonomyVersion = "v2", Prompts = 2 },
                    new CopilotSpendCategoryFact { CategoryId = CopilotSpendPersonDetailBuilder.NotClassifiedCategoryId, TaxonomyVersion = "v1", Prompts = 1 },
                    new CopilotSpendCategoryFact { CategoryId = string.Empty, TaxonomyVersion = "v2", Prompts = 5 },
                    new CopilotSpendCategoryFact { CategoryId = null, TaxonomyVersion = "v2", Prompts = 7 },
                },
                Taxonomies =
                {
                    ["v1"] = Taxonomy(("summarise", "Summarise", "k.summarise.v1", "automation"), ("draft", "Draft content", "k.draft", "augmentation")),
                    ["v2"] = Taxonomy(("summarise", "Summarise information", null, "automation"), ("draft", "Draft and rewrite", "k.draft.v2", "augmentation")),
                },
                PhrasesAssessed = true,
                Phrases =
                {
                    new CopilotSpendPhrase { Phrase = "quarterly forecast", Prompts = 3 },
                    new CopilotSpendPhrase { Phrase = GreekPhrase, Prompts = 5 },
                    new CopilotSpendPhrase { Phrase = "Budget", Prompts = 3 },
                },
                ResourcesAssessed = true,
                ResourceTypes =
                {
                    new AdoptionResourceTypeRow { Label = "File", Value = 4, Kind = CopilotResourceTypeKind.TenantContent },
                    new AdoptionResourceTypeRow { Label = "Web search", Value = 9 },
                    new AdoptionResourceTypeRow { Label = "Email", Value = 4, Kind = CopilotResourceTypeKind.TenantContent },
                },
            };
            return new CopilotSpendPersonDetailInput
            {
                Person = people[0],
                People = people,
                StudioAvailable = true,
                BalancesAvailable = true,
                Facts = facts,
                Usage = usage,
                Agents = new List<CopilotSpendAgentFact>
                {
                    new CopilotSpendAgentFact { Id = 11, AgentId = "CopilotStudio.CustomEngine.contoso-helpdesk", Name = "Contoso helpdesk" },
                    new CopilotSpendAgentFact { Id = 12, AgentId = "Copilot.Studio.Default-00000000-0000-0000-0000-000000000000-cr123_contosoTravel", Name = GreekAgent },
                    new CopilotSpendAgentFact { Id = 7, AgentId = "Copilot.M365Copilot.CoworkChat", Name = "Copilot Cowork" },
                    new CopilotSpendAgentFact { Id = 14, AgentId = "T_00000000-0000-0000-0000-000000000001", Name = "Contoso expenses", IsCustomAgent = true },
                    new CopilotSpendAgentFact { Id = 15, AgentId = "SPO_contoso-policies", Name = "Contoso policies" },
                },
                FromUtc = Oct(20),
                ToExclusiveUtc = Oct(27),
                LastDay = Oct(26),
                StudioEarliest = Oct(21),
                StudioLatest = Oct(25),
            };
        }

        [TestMethod]
        public void APersonsDrillDown_SaysWhereTheirCreditsWentAndWhatTheyUsedCopilotFor()
        {
            var input = DetailInput();
            var detail = CopilotSpendPersonDetailBuilder.Build(input);
            var usage = detail.Usage;

            Assert.AreSame(input.Person, detail.Person);
            Assert.IsNull(detail.UsageFailureCode);

            // Turns in the window only, each counted under one kind of use.
            Assert.IsTrue(usage.TurnsAssessed);
            Assert.AreEqual(25, usage.Turns);
            Assert.AreEqual(6, usage.CoworkTurns, "By the app, and by Cowork's own agent.");
            Assert.AreEqual(6, usage.MakerTestTurns);
            Assert.AreEqual(5, usage.NoAgentTurns);
            Assert.AreEqual(8, usage.AgentTurns);
            Assert.AreEqual(6, usage.ActiveDays);
            Assert.AreEqual(2.05, usage.EstimateCredits.Value, 1e-9);
            Assert.AreEqual(10, usage.PricedTurns);
            CollectionAssert.AreEqual(Enumerable.Range(20, 7).Select(day => Oct(day)).ToList(), usage.Days.Select(d => d.Date).ToList(), "Every calendar day, a quiet one too.");
            CollectionAssert.AreEqual(new long[] { 8, 4, 8, 1, 0, 2, 2 }, usage.Days.Select(d => d.Turns).ToList());
            CollectionAssert.AreEqual(new long[] { 3, 4, 8, 1, 0, 2, 2 }, usage.Days.Select(d => d.AgentTurns).ToList(), "Cowork and Studio tests are agent turns too.");

            CollectionAssert.AreEqual(new[] { "Teams", CopilotTurnSql.MakerTestAppHost, "Cowork", "Outlook", "SharePoint", "Word" }, usage.Apps.Select(a => a.App).ToList(),
                "One row per app whatever its case or spacing, the most turns first.");
            CollectionAssert.AreEqual(new long[] { 9, 6, 4, 2, 1, 1 }, usage.Apps.Select(a => a.Count).ToList());
            Assert.AreEqual(2, usage.Apps[0].Days);

            CollectionAssert.AreEqual(new[] { 11, 12, 13, 7, 14, 15 }, usage.Agents.Select(a => a.Id).ToList(), "Estimated credits first, then turns, then name.");
            var helpdesk = usage.Agents[0];
            Assert.AreEqual("Contoso helpdesk", helpdesk.Name);
            Assert.AreEqual(9, helpdesk.Turns);
            Assert.AreEqual(6, helpdesk.TestTurns);
            Assert.AreEqual(1.8, helpdesk.EstimateCredits.Value, 1e-9);
            Assert.AreEqual(9, helpdesk.PricedTurns);
            Assert.AreEqual(2, helpdesk.Days);
            Assert.AreEqual(Oct(22), helpdesk.LastDate);
            Assert.AreEqual(CopilotSpendAgentKindKeys.CustomEngine, helpdesk.KindKey);
            Assert.AreEqual(CopilotAgentOriginKeys.CustomerBuilt, helpdesk.OriginKey);
            Assert.IsFalse(helpdesk.Cowork);
            var travel = usage.Agents[1];
            Assert.AreEqual(GreekAgent, travel.Name, "The tenant's own name, as stored.");
            Assert.AreEqual(0.25, travel.EstimateCredits.Value, 1e-9);
            Assert.AreEqual(Oct(23), travel.LastDate);
            Assert.AreEqual(CopilotSpendAgentKindKeys.CustomEngine, travel.KindKey);
            Assert.AreEqual(CopilotAgentOriginKeys.CustomerBuilt, travel.OriginKey);
            var unstored = usage.Agents[2];
            Assert.IsNull(unstored.Name);
            Assert.IsNull(unstored.AgentId);
            Assert.AreEqual(CopilotSpendAgentKindKeys.Unknown, unstored.KindKey);
            Assert.AreEqual(CopilotAgentOriginKeys.Unknown, unstored.OriginKey);
            Assert.AreEqual(2, unstored.Turns);
            Assert.AreEqual(Oct(26), unstored.LastDate);
            var cowork = usage.Agents[3];
            Assert.IsTrue(cowork.Cowork);
            Assert.IsNull(cowork.EstimateCredits, "No turn carries an estimate: unknown, not zero.");
            Assert.AreEqual(0, cowork.TestTurns);
            Assert.AreEqual(CopilotSpendAgentKindKeys.Unknown, cowork.KindKey);
            Assert.AreEqual(CopilotAgentOriginKeys.Microsoft, cowork.OriginKey);
            Assert.AreEqual(CopilotSpendAgentKindKeys.Unknown, usage.Agents[4].KindKey);
            Assert.AreEqual(CopilotAgentOriginKeys.CustomerBuilt, usage.Agents[4].OriginKey, "Stored as a custom agent.");
            Assert.AreEqual(CopilotSpendAgentKindKeys.Declarative, usage.Agents[5].KindKey);
            Assert.AreEqual(CopilotAgentOriginKeys.Unknown, usage.Agents[5].OriginKey);

            Assert.IsTrue(usage.PromptsAssessed);
            CollectionAssert.AreEqual(new[] { "Teams", "Word", string.Empty }, usage.PromptApps.Select(a => a.App).ToList(), "Prompts, not responses; an unnamed app is one row.");
            CollectionAssert.AreEqual(new long[] { 10, 5, 4 }, usage.PromptApps.Select(a => a.Count).ToList());
            CollectionAssert.AreEqual(new[] { 4, 3, 2 }, usage.PromptApps.Select(a => a.Days).ToList());
            Assert.AreSame(input.Usage.PromptTotals, usage.Prompts);
            Assert.IsTrue(usage.CategoriesAssessed && usage.PhrasesAssessed && usage.ResourcesAssessed);

            CollectionAssert.AreEqual(new[] { 103, 102, 101, 104 }, usage.Recent.Select(r => r.InteractionId).ToList(), "Newest first.");
            Assert.AreEqual("Draft content", usage.Recent[1].CategoryName, "Named by the taxonomy version that classified it.");
            Assert.AreEqual("k.draft", usage.Recent[1].CategoryNameKey);
            Assert.AreEqual("Summarise information", usage.Recent[2].CategoryName);
            Assert.IsNull(usage.Recent[2].CategoryNameKey, "A goal the tenant renamed has no translation key.");
            Assert.IsNull(usage.Recent[0].CategoryName, "No taxonomy version.");
            Assert.IsNull(usage.Recent[3].CategoryName, "A taxonomy that could not be read.");

            CollectionAssert.AreEqual(new[] { "summarise", "draft" }, usage.Categories.Select(c => c.CategoryId).ToList(), "One row per goal across taxonomy versions.");
            var summarise = usage.Categories[0];
            Assert.AreEqual(9, summarise.Prompts);
            Assert.AreEqual("Summarise information", summarise.Name, "Named by the version that classified most of it.");
            Assert.IsNull(summarise.NameKey);
            Assert.AreEqual("automation", summarise.HumanMode);
            Assert.AreEqual(Oct(24), summarise.LastUtc);
            var draft = usage.Categories[1];
            Assert.AreEqual(8, draft.Prompts);
            Assert.AreEqual("Draft and rewrite", draft.Name, "A tie goes to the later version.");
            Assert.AreEqual("k.draft.v2", draft.NameKey);
            Assert.AreEqual("augmentation", draft.HumanMode);
            Assert.AreEqual(Oct(25), draft.LastUtc);
            Assert.AreEqual(3, usage.NotClassifiedPrompts, "Counted, never listed as a goal; a blank goal is neither.");

            CollectionAssert.AreEqual(new[] { GreekPhrase, "Budget", "quarterly forecast" }, usage.Phrases.Select(p => p.Phrase).ToList());
            CollectionAssert.AreEqual(new[] { "Web search", "Email", "File" }, usage.ResourceTypes.Select(r => r.Label).ToList());

            CollectionAssert.AreEqual(new[] { Oct(21), Oct(23), Oct(25) }, detail.Balances.Select(b => b.Date).ToList(), "Oldest first, for the chart.");
            CollectionAssert.AreEqual(new[] { "env-a", "env-b", "env-c" }, detail.StudioEnvironments.Select(e => e.EnvironmentId).ToList());
            Assert.AreEqual("Contoso " + GreekDepartment, detail.StudioEnvironments[0].EnvironmentName);
            Assert.AreEqual(3.25, detail.StudioEnvironments[0].Credits, 1e-9);
            CollectionAssert.AreEqual(new[] { "agents", "chat" }, detail.Services.Select(s => s.Category).ToList());
            Assert.AreEqual("noPolicyAssigned", detail.Services[0].UnevaluatedReason);
            Assert.AreEqual(100d, detail.Services[1].Consumed);
            Assert.IsNull(detail.Services[1].UnevaluatedReason, "A category with a reading is not also listed as unevaluated.");

            CollectionAssert.AreEqual(new[]
            {
                CopilotSpendDayStatus.NotReported, CopilotSpendDayStatus.Recorded, CopilotSpendDayStatus.NoneRecorded, CopilotSpendDayStatus.Recorded,
                CopilotSpendDayStatus.NoneRecorded, CopilotSpendDayStatus.NoneRecorded, CopilotSpendDayStatus.NotReported,
            }, detail.StudioDays.Select(d => d.Status).ToList(), "Before the ledger starts and after its latest day is not reported, never zero.");
            Assert.AreEqual(2.5, detail.StudioDays[1].Credits);
            Assert.AreEqual(4d, detail.StudioDays[3].Credits);
            Assert.IsNull(detail.StudioDays[0].Credits);

            Assert.AreEqual(2, detail.Ranks.StudioRank, "One person consumed more.");
            Assert.AreEqual(3, detail.Ranks.StudioConsumers, "A zero and an unknown amount are not ranked.");
            Assert.AreEqual(0.25, detail.Ranks.StudioShare.Value, 1e-9);
            Assert.AreEqual(1, detail.Ranks.BalanceRank);
            Assert.AreEqual(3, detail.Ranks.BalanceConsumers);
            Assert.AreEqual(0.5714, detail.Ranks.BalanceShare.Value, 1e-9);
        }

        [TestMethod]
        public void RanksAreAmongThePeopleInView_TiesShareARank_AndNothingNotConsumedIsRanked()
        {
            CopilotSpendPersonRanks RanksOf(int id, bool studioAvailable = true)
            {
                var input = DetailInput();
                input.Person = input.People.Single(p => p.UserId == id);
                input.StudioAvailable = studioAvailable;
                return CopilotSpendPersonDetailBuilder.Build(input).Ranks;
            }

            var second = RanksOf(2);
            Assert.AreEqual(2, second.StudioRank, "The same amount as person 1: the same rank.");
            Assert.AreEqual(0.25, second.StudioShare.Value, 1e-9);
            Assert.AreEqual(2, second.BalanceRank);
            Assert.AreEqual(0.2857, second.BalanceShare.Value, 1e-9);

            var third = RanksOf(3);
            Assert.AreEqual(1, third.StudioRank);
            Assert.AreEqual(0.5, third.StudioShare.Value, 1e-9);
            Assert.IsNull(third.BalanceRank, "A reading of zero is not ranked.");
            Assert.IsNull(third.BalanceShare);
            Assert.AreEqual(3, third.BalanceConsumers, "Still out of everyone who consumed any.");

            var fourth = RanksOf(4);
            Assert.IsNull(fourth.StudioRank, "An unknown amount is not ranked.");
            Assert.IsNull(fourth.BalanceRank);
            Assert.AreEqual(3, fourth.StudioConsumers);
            Assert.AreEqual(3, fourth.BalanceConsumers);

            var unavailable = RanksOf(1, studioAvailable: false);
            Assert.IsNull(unavailable.StudioRank, "A source that is not available ranks no one.");
            Assert.IsNull(unavailable.StudioConsumers);
            Assert.IsNull(unavailable.StudioShare);
            Assert.AreEqual(1, unavailable.BalanceRank);
        }

        [TestMethod]
        public void APartOfTheDrillDownThatCannotBeRead_ReadsAsNotAvailable_NeverAsZero()
        {
            var input = DetailInput();
            input.Usage = null;
            input.UsageFailureCode = CopilotSpendErrorCodes.PersonUsageUnavailable;
            var detail = CopilotSpendPersonDetailBuilder.Build(input);
            Assert.AreEqual(CopilotSpendErrorCodes.PersonUsageUnavailable, detail.UsageFailureCode);
            Assert.IsNull(detail.StudioDays);
            Assert.IsNull(detail.Usage.Prompts, "Not read, so not zero prompts.");
            Assert.IsFalse(detail.Usage.CategoriesAssessed || detail.Usage.PhrasesAssessed || detail.Usage.ResourcesAssessed);
            Assert.AreEqual(0, detail.Usage.Recent.Count + detail.Usage.Categories.Count + detail.Usage.Phrases.Count + detail.Usage.ResourceTypes.Count);
            // Turns and prompts by app come from the person's own facts, which were read.
            Assert.IsTrue(detail.Usage.TurnsAssessed);
            Assert.AreEqual(25, detail.Usage.Turns);
            Assert.IsTrue(detail.Usage.PromptsAssessed);
            Assert.AreEqual(3, detail.Usage.PromptApps.Count);
            Assert.AreEqual(3, detail.Balances.Count);
            Assert.AreEqual(2, detail.Services.Count);

            var noStudioAmount = DetailInput();
            noStudioAmount.Person = noStudioAmount.People.Single(p => p.UserId == 4);
            Assert.IsNull(CopilotSpendPersonDetailBuilder.Build(noStudioAmount).StudioDays, "No billed amount is known for the person, so no days either.");
            var noLedger = DetailInput();
            noLedger.StudioAvailable = false;
            Assert.IsNull(CopilotSpendPersonDetailBuilder.Build(noLedger).StudioDays);
            var notAssessed = DetailInput();
            notAssessed.Usage.StudioAssessed = false;
            Assert.IsNull(CopilotSpendPersonDetailBuilder.Build(notAssessed).StudioDays);

            var noAudit = DetailInput();
            noAudit.Facts.ActivityAssessed = false;
            var noTurns = CopilotSpendPersonDetailBuilder.Build(noAudit).Usage;
            Assert.IsFalse(noTurns.TurnsAssessed);
            Assert.AreEqual(0, noTurns.Turns);
            Assert.AreEqual(0, noTurns.Days.Count + noTurns.Apps.Count + noTurns.Agents.Count, "Not assessed: no days of zero turns.");
        }
    }
}
