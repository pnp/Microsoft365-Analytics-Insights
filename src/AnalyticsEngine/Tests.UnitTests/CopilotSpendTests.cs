extern alias AnalyticsWeb;

using Common.Entities.CopilotCosts;
using Common.Entities.CopilotInvestment;
using Common.Entities.CopilotSpend;
using Common.Entities.Localisation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using CopilotSpendAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotSpendAPIController;

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
            Assert.AreEqual(67, summary.Studio.Credits, 1e-9);
            Assert.AreEqual(7, summary.Studio.UnmatchedCredits.Value, 1e-9);
            Assert.AreEqual(3, summary.Studio.People);
            Assert.IsTrue(summary.Studio.CoversWindow);
            CollectionAssert.AreEqual(new[] { "env-1", "env-2" }, summary.Studio.Environments.Select(e => e.EnvironmentId).ToList(), "Largest first.");
            Assert.AreEqual(summary.Studio.Credits, summary.Studio.Environments.Sum(e => e.Credits), 1e-9, "The per-person ledger by environment adds up.");

            // The agent ledger is a different route: its own total, which need not match and is never added in.
            Assert.AreEqual(2, summary.Studio.Agents.Count);
            Assert.AreEqual(64.5, summary.Studio.AgentLedgerCredits.Value, 1e-9);
            Assert.AreEqual(67, summary.Studio.Credits, 1e-9, "Not 131.5: the two Studio ledgers are never added.");

            // Balances are each person's latest reading, never added to the Studio figures.
            Assert.AreEqual(220, summary.Balances.Credits, 1e-9, "100 + 70 + the unmatched account's 50; the unknown amount adds nothing.");
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

            Assert.AreEqual(30, summary.Studio.Credits, 1e-9, "Only the people in view: 10 + 20.");
            Assert.IsNull(summary.Studio.UnmatchedCredits, "Charges no person was matched to cannot be shown to belong to this view.");
            Assert.IsNull(summary.Studio.Agents, "The agent ledger is not kept per person, so a filtered view has no share of it to show.");
            Assert.IsNull(summary.Studio.AgentLedgerCredits);
            Assert.AreEqual(100, summary.Balances.Credits, 1e-9);
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
            Assert.AreEqual(40, sales.StudioCredits, 1e-9, "Persons 1 and 4: 10 + 30.");
            Assert.AreEqual(170, sales.BalanceCredits, 1e-9, "Persons 1 and 4: 100 + 70; person 2's amount is unknown.");
            Assert.AreEqual(1, sales.UnknownAmountPeople);
            var folded = summary.Organisation.Groups.Single(g => g.Label == null);
            Assert.AreEqual(CopilotInvestmentCalculator.GroupKeys.OtherSmallGroups, folded.EmptyKey, "A group of one would stand for one person.");
            Assert.AreEqual(1, summary.Organisation.FoldedGroups);
            Assert.AreEqual(sales.StudioCredits + folded.StudioCredits, summary.People.Sum(p => p.StudioCredits), 1e-9);
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
            CollectionAssert.AreEqual(new[] { "summary", "organisation", "studio", "balances", "people" }
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

            // Without See PII: no people sheet, and no sign-in name anywhere.
            var aggregates = CopilotInvestmentWorkbookLanguageTests.Sheets(CopilotSpendWorkbook.Build(summary.WithoutIndividualData(), false, "test", PortalLanguage.Spanish));
            Assert.AreEqual(sheets.Count - 1, aggregates.Count);
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
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("text/plain", response.Content.Headers.ContentType.MediaType);
            Assert.AreEqual(PortalCatalog.For(CopilotSpendWorkbook.CatalogModule, PortalLanguage.Spanish).Text("copilotSpend.workbook.error.invalid"),
                response.Content.ReadAsStringAsync().Result);
        }
    }
}
