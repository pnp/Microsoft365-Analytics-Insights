extern alias AnalyticsWeb;

using Common.Entities.CopilotInvestment;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotInvestmentAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotInvestmentAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The investment model's arithmetic and its honesty rules: value is counted per active day, the return compares
    /// like with like, preview balances never enter it, and money is never modelled from a figure nobody chose.
    /// </summary>
    [TestClass]
    public class CopilotInvestmentCalculatorTests
    {
        private static readonly DateTime From = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        private static CopilotInvestmentAssumptions Assumptions(Action<CopilotInvestmentAssumptions> change = null)
        {
            // Test inputs only - round numbers chosen so the arithmetic can be checked by hand.
            var a = new CopilotInvestmentAssumptions
            {
                Currency = "EUR",
                SeatPricePerMonth = 30,
                CreditPrice = 0.01,
                StudioCreditPrice = 0.02,
                HourlyCost = 60,
                MinutesPerActiveDay = new CopilotInvestmentRange { Conservative = 10, Base = 15, Upside = 30 },
                Realisation = new CopilotInvestmentRange { Conservative = 0.25, Base = 0.5, Upside = 0.75 },
            };
            change?.Invoke(a);
            return a;
        }

        private static CopilotInvestmentPerson Holder(int id, int activeDays, string band = "Established", long interactions = 10,
            CreditUserFact credit = null, StudioUserFact studio = null, string department = "Sales") =>
            new CopilotInvestmentPerson
            {
                UserId = id,
                UserPrincipalName = $"person{id}@contoso.example",
                Department = department,
                Licensed = true,
                Band = band,
                ActiveDays = activeDays,
                Interactions = interactions,
                Credit = credit,
                Studio = studio,
                ReclaimEligibility = "certain",
            };

        private static CreditUserFact Balance(int userId, double credits, string period = "unverified") => new CreditUserFact
        {
            ObjectId = new Guid(userId, 0, 0, new byte[8]),
            UserId = userId,
            ObservationDate = From.AddDays(27),
            Quality = 2,
            PeriodStatus = period,
            ServiceRows = 1,
            CreditRows = 1,
            Credits = credits,
        };

        private static CopilotInvestmentInput Input(List<CopilotInvestmentPerson> people, CopilotInvestmentAssumptions assumptions = null, int windowDays = 28)
        {
            // As in production, every balance a person carries is also one of the facts the sources read.
            var credits = new CreditFacts { TablesPresent = true };
            credits.Users.AddRange(people.Where(p => p.Credit != null).Select(p => p.Credit));
            return new CopilotInvestmentInput
            {
                GeneratedUtc = From.AddDays(windowDays),
                FromUtc = From,
                ToUtc = From.AddDays(windowDays),
                WindowDays = windowDays,
                People = people,
                AdoptionAvailable = true,
                AuditAvailable = true,
                Assumptions = assumptions,
                Credits = credits,
                Studio = new StudioFacts(),
                Dimension = "department",
                GroupOf = id => people.FirstOrDefault(p => p.UserId == id)?.Department,
                MinGroupSize = 1,
            };
        }

        [TestMethod]
        public void Value_IsActiveDaysTimesMinutesTimesRealisationTimesHourlyCost_AgainstTheSameHoldersSeats()
        {
            var summary = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10) }, Assumptions()));

            var months = 28 / CopilotInvestmentCalculator.AverageMonthDays;
            var cost = 30 * months;
            var value = 10 * 15 / 60d * 0.5 * 60;
            var base_ = summary.Value.Scenarios.Single(s => s.Scenario == CopilotInvestmentScenarios.Base);
            Assert.AreEqual(Math.Round(value, 2), base_.Value);
            Assert.AreEqual(Math.Round(cost, 2), summary.Investment.ModelledWindowCost);
            Assert.AreEqual(Math.Round(value / cost, 4), base_.RoiMultiple.Value, 0.0001);
            Assert.AreEqual(Math.Round(cost / (10 * 0.5 * 60 / 60d), 2), summary.BreakEven.MinutesPerActiveDay);
            Assert.AreEqual(1 - cost / 5 / 15, summary.BreakEven.MarginOfSafety.Value, 0.0001);
            Assert.AreEqual(9, summary.Sensitivity.Grid.Count);
            Assert.IsTrue(summary.Value.Scenarios.Select(s => s.Value).SequenceEqual(summary.Value.Scenarios.Select(s => s.Value).OrderBy(v => v)),
                "Conservative, base and upside must not cross.");
        }

        [TestMethod]
        public void MorePromptsOnTheSameDays_EarnNothingExtra()
        {
            var light = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 8, interactions: 8) }, Assumptions()));
            var heavy = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 8, interactions: 800) }, Assumptions()));
            Assert.AreEqual(light.Value.Scenarios.Single(s => s.Scenario == "base").Value, heavy.Value.Scenarios.Single(s => s.Scenario == "base").Value);
        }

        [TestMethod]
        public void PreviewBalances_StayOutOfTheReturn_AndOnlyAppearAsAnExplicitWhatIf()
        {
            var without = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10) }, Assumptions()));
            var with = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10, credit: Balance(1, 5000)) }, Assumptions()));

            Assert.AreEqual(without.Investment.ModelledWindowCost, with.Investment.ModelledWindowCost);
            Assert.AreEqual(without.Value.Scenarios.Single(s => s.Scenario == "base").RoiMultiple, with.Value.Scenarios.Single(s => s.Scenario == "base").RoiMultiple);
            Assert.AreEqual(5000, with.Investment.Credits.Credits);
            Assert.AreEqual(50, with.Investment.Credits.BalanceValue);
            Assert.IsNull(without.Sensitivity.RoiBaseIfBalancesMonthly);
            Assert.IsTrue(with.Sensitivity.RoiBaseIfBalancesMonthly < with.Value.Scenarios.Single(s => s.Scenario == "base").RoiMultiple);
            Assert.AreEqual("unverified", with.Investment.Credits.PeriodStatus);
            Assert.IsTrue(with.Trust.Reasons.Any(r => r.Code == CopilotInvestmentTrustCodes.BalancesUnverified));
        }

        [TestMethod]
        public void HoldersOwnStudioCredits_AreInTheirCost_WhileOtherPeoplesAreAMeteredLens()
        {
            var people = new List<CopilotInvestmentPerson>
            {
                Holder(1, 10, studio: new StudioUserFact { UserId = 1, Credits = 1000 }),
                new CopilotInvestmentPerson { UserId = 2, ActiveDays = 12, Studio = new StudioUserFact { UserId = 2, Credits = 3000 } },
            };
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, Assumptions()));
            var months = 28 / CopilotInvestmentCalculator.AverageMonthDays;
            Assert.AreEqual(Math.Round(Math.Round(30 * months, 2) + 1000 * 0.02, 2), summary.Investment.ModelledWindowCost);
            Assert.AreEqual(4000, summary.Investment.Studio.Credits);
            Assert.AreEqual(1000, summary.Investment.Studio.HolderCredits);
            Assert.AreEqual(1, summary.Metered.People);
            Assert.AreEqual(3000, summary.Metered.StudioCredits);
            // The person without a seat adds no seat value, however active.
            Assert.AreEqual(Math.Round(10 * 15 / 60d * 0.5 * 60, 2), summary.Value.Scenarios.Single(s => s.Scenario == "base").Value);
        }

        [TestMethod]
        public void MissingAssumptions_WithholdMoney_AndSayWhichInputIsMissing()
        {
            var summary = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10), Holder(2, 0) },
                new CopilotInvestmentAssumptions { Currency = "EUR", SeatPricePerMonth = 30 }));

            Assert.IsFalse(summary.Value.Available);
            CollectionAssert.AreEquivalent(new[] { "hourlyCost", "minutesPerActiveDay.conservative", "minutesPerActiveDay.base", "minutesPerActiveDay.upside",
                "realisation.conservative", "realisation.base", "realisation.upside" }, summary.Value.Missing);
            Assert.IsFalse(summary.BreakEven.Available);
            Assert.AreEqual(0, summary.Sensitivity.Grid.Count);
            Assert.IsNotNull(summary.Investment.Seats.MonthlyRunRate, "A seat price alone still prices the seats.");
            Assert.AreEqual(CopilotInvestmentAssumptionStatus.Partial, summary.Assumptions.Status);
            Assert.IsTrue(summary.Actions.Any(a => a.Code == CopilotInvestmentActionCodes.SetAssumptions));
            Assert.IsTrue(summary.People.All(p => p.ValueBase == null));

            var none = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10) }));
            Assert.AreEqual(CopilotInvestmentAssumptionStatus.Unavailable, none.Assumptions.Status);
            Assert.IsNull(none.Investment.Seats.MonthlyRunRate);
            Assert.IsNull(none.Investment.ModelledWindowCost);
        }

        [TestMethod]
        public void QuietPeopleWithoutASeat_AreCountedAsObservedButNotListed()
        {
            var credits = new List<CreditUserFact> { Balance(1, 10), Balance(2, 0), Balance(3, 0), Balance(4, 25) };
            var people = CopilotInvestmentPeopleBuilder.Build(null, credits, new List<StudioUserFact>(), _ => true, _ => null, out _, out var scoped);
            Assert.AreEqual(4, scoped.Count);
            CollectionAssert.AreEqual(new[] { 1, 4 }, people.Select(p => p.UserId).ToArray(), "A zero balance with nothing else to say adds no row.");

            var input = Input(people, Assumptions());
            input.ScopedCredits = scoped;
            var summary = CopilotInvestmentCalculator.Calculate(input);
            Assert.AreEqual(4, summary.Investment.Credits.ObservedUsers);
            Assert.AreEqual(2, summary.Investment.Credits.ConsumingUsers);
            Assert.AreEqual(35, summary.Investment.Credits.Credits);
        }

        [TestMethod]
        public void HighSpendOnThinEvidence_IsFlagged_WhileEvidencedHeavyUseIsNot()
        {
            var people = new List<CopilotInvestmentPerson>();
            for (int i = 1; i <= 9; i++) people.Add(Holder(i, 15, "Champion", credit: Balance(i, 10 * i)));
            people.Add(Holder(10, 0, "Dormant", credit: Balance(10, 2000)));
            people.Add(Holder(11, 18, "Champion", credit: Balance(11, 1900)));
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, Assumptions()));

            var flagged = summary.People.Where(p => p.Flags.Contains(CopilotInvestmentFlags.HighSpendThinEvidence)).Select(p => p.UserId).ToList();
            CollectionAssert.AreEqual(new[] { 10 }, flagged);
            Assert.IsTrue(summary.Actions.Any(a => a.Code == CopilotInvestmentActionCodes.ReviewHighSpendThinEvidence && a.People == 1));
            Assert.AreEqual(CopilotInvestmentSignals.NoEvidence, summary.People.Single(p => p.UserId == 10).Signal);
        }

        [TestMethod]
        public void IndividualData_IsTrimmedOnACopy_AndTheManagerGroupingHidden()
        {
            var input = Input(new List<CopilotInvestmentPerson> { Holder(1, 10, department: "\u0391\u03b8\u03ae\u03bd\u03b1 Sales"), Holder(2, 4) }, Assumptions());
            input.Dimension = "manager";
            var summary = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsTrue(summary.Organisation.Groups.Count > 0);

            var trimmed = summary.WithoutIndividualData();
            Assert.AreEqual(0, trimmed.People.Count);
            Assert.IsTrue(trimmed.IndividualDataHidden);
            Assert.IsTrue(trimmed.Organisation.Hidden);
            Assert.AreEqual(0, trimmed.Organisation.Groups.Count);
            Assert.AreEqual(2, summary.People.Count, "The cached original must not be edited.");
            Assert.IsTrue(summary.Organisation.Groups.Count > 0);

            input.Dimension = "department";
            var byDepartment = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsTrue(byDepartment.Organisation.Groups.Any(g => g.Label == "\u0391\u03b8\u03ae\u03bd\u03b1 Sales"), "Customer text keeps its non-Latin characters.");
            Assert.IsTrue(JsonConvert.SerializeObject(byDepartment.WithoutIndividualData()).Contains("\u0391\u03b8\u03ae\u03bd\u03b1"),
                "An aggregate grouping by department is not individual data.");
        }

        [TestMethod]
        public void AFilteredView_WithholdsTenantOnlyFacts()
        {
            var input = Input(new List<CopilotInvestmentPerson> { Holder(1, 10, credit: Balance(1, 10)) }, Assumptions());
            input.Credits.Users.Add(new CreditUserFact { ObjectId = Guid.NewGuid(), Credits = 99, Quality = 2, PeriodStatus = "unverified" });
            var whole = CopilotInvestmentCalculator.Calculate(input);
            Assert.AreEqual(1, whole.Sources.Credits.UnresolvedObjects);
            Assert.AreEqual(99, whole.Sources.Credits.UnresolvedCredits);

            input.Filtered = true;
            var filtered = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsNull(filtered.Sources.Credits.UnresolvedObjects);
            Assert.IsNull(filtered.Sources.Credits.UnresolvedCredits);
        }

        [TestMethod]
        public async Task Assumptions_StartEmpty_SaveWithHistory_AndRefuseAStaleOrInvalidWrite()
        {
            var clock = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
            var store = new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: true, () => clock);
            var empty = await store.GetAsync();
            Assert.AreEqual(0, empty.Version);
            Assert.IsNull(empty.Assumptions.SeatPricePerMonth, "No price is invented.");

            var proposed = Assumptions(a => a.Note = "Business case \u2013 \u03a0\u03b9\u03bb\u03bf\u03c4\u03b9\u03ba\u03cc");
            var saved = await store.SaveAsync(proposed, 0, "admin@contoso.example");
            Assert.AreEqual(1, saved.Version);
            Assert.AreEqual("admin@contoso.example", saved.History.Single().ChangedBy);
            Assert.IsTrue(saved.History.Single().Changes.Any(c => c.Field == CopilotInvestmentFields.SeatPricePerMonth && c.OldValue == null && c.NewValue == "30"));
            Assert.AreEqual(proposed.Note, (await store.GetAsync()).Assumptions.Note);

            var stale = await Assert.ThrowsExceptionAsync<CopilotInvestmentAssumptionsRejectedException>(() => store.SaveAsync(Assumptions(a => a.HourlyCost = 70), 0, "other@contoso.example"));
            Assert.AreEqual(CopilotInvestmentErrorCodes.VersionConflict, stale.Code);

            var invalid = await Assert.ThrowsExceptionAsync<CopilotInvestmentAssumptionsRejectedException>(() => store.SaveAsync(Assumptions(a =>
            {
                a.Currency = null;
                a.MinutesPerActiveDay = new CopilotInvestmentRange { Conservative = 20, Base = 10, Upside = 30 };
                a.Realisation = new CopilotInvestmentRange { Conservative = 0.2, Base = 0.5, Upside = 1.5 };
            }), 1, "admin@contoso.example"));
            CollectionAssert.IsSubsetOf(new[] { CopilotInvestmentErrorCodes.CurrencyRequired, CopilotInvestmentErrorCodes.MinutesOrder,
                CopilotInvestmentErrorCodes.RealisationOutOfRange }, invalid.ValidationErrors.ToList());
            Assert.AreEqual(1, (await store.GetAsync()).Version, "A refused save changes nothing.");

            var notDurable = new CopilotInvestmentAssumptionsStore(new InMemoryKeyValueStore(), isDurable: false);
            var refused = await Assert.ThrowsExceptionAsync<CopilotInvestmentAssumptionsRejectedException>(() => notDurable.SaveAsync(Assumptions(), 0, "admin@contoso.example"));
            Assert.AreEqual(CopilotInvestmentErrorCodes.StorageNotConfigured, refused.Code);
        }

        [TestMethod]
        public void TheWorkbook_IsNoLessRedactedThanThePage_AndKeepsTenantTextAsText()
        {
            var people = new List<CopilotInvestmentPerson>
            {
                Holder(1, 10, department: "=HYPERLINK(\"https://contoso.example\",\"x\")"),
                Holder(2, 3, department: "\u0391\u03b8\u03ae\u03bd\u03b1 Sales"),
            };
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, Assumptions()));

            var full = CopilotInvestmentWorkbook.Build(summary, includeIndividualData: true, buildLabel: "test");
            var aggregates = CopilotInvestmentWorkbook.Build(summary.WithoutIndividualData(), includeIndividualData: false, buildLabel: "test");

            CollectionAssert.Contains(WorkbookParts(full).Keys.ToList(), "People");
            CollectionAssert.DoesNotContain(WorkbookParts(aggregates).Keys.ToList(), "People");
            Assert.IsTrue(WorkbookParts(full)["People"].Contains("person1@contoso.example"));
            Assert.IsFalse(WorkbookParts(aggregates).Values.Any(xml => xml.Contains("@contoso.example")), "No sign-in name without See PII.");

            var organisation = WorkbookParts(aggregates)["Organisation"];
            StringAssert.Contains(organisation, "\u0391\u03b8\u03ae\u03bd\u03b1 Sales");
            Assert.IsFalse(WorkbookParts(full).Values.Any(xml => xml.Contains("<f>") || xml.Contains("<f ")), "No cell is ever a formula.");
            StringAssert.Contains(organisation, "t=\"inlineStr\"");
        }

        /// <summary>Each sheet's XML by sheet name.</summary>
        private static Dictionary<string, string> WorkbookParts(byte[] bytes)
        {
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var parts = new Dictionary<string, string>();
            using (var stream = new System.IO.MemoryStream(bytes))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
            {
                List<string> names;
                using (var workbook = zip.GetEntry("xl/workbook.xml").Open())
                    names = System.Xml.Linq.XDocument.Load(workbook).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
                for (int i = 0; i < names.Count; i++)
                    using (var reader = new System.IO.StreamReader(zip.GetEntry("xl/worksheets/sheet" + (i + 1) + ".xml").Open()))
                        parts[names[i]] = reader.ReadToEnd();
            }
            return parts;
        }

        [TestMethod]
        public void TheHost_LetsThroughAUserFilterWithEveryWhatIfFigure()
        {
            // The export is a plain link, so the reader's filter and what-if figures ride on the query string; over the
            // host's 2,048-character default IIS answers a bare 404.15 before the controller can explain anything.
            var directory = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string config = null;
            while (directory != null && config == null)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, "Web", "Web.Template.config");
                config = System.IO.File.Exists(candidate) ? System.IO.File.ReadAllText(candidate) : null;
                directory = directory.Parent;
            }

            Assert.IsNotNull(config, "Web.Template.config was not found.");
            var location = System.Text.RegularExpressions.Regex.Match(config, "<location path=\"api/CopilotInvestment\">(.*?)</location>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.IsTrue(location.Success);
            StringAssert.Contains(location.Groups[1].Value, "maxQueryStringLength=\"16384\"");
            StringAssert.Contains(location.Groups[1].Value, "maxQueryString=\"16384\"");
        }

        [TestMethod]
        public void AReadersWhatIf_ReplacesOnlyWhatItSets_SaysWhereEachFigureCameFrom_AndParsesInvariantly()
        {
            var tenant = Assumptions(a => a.CreditPrice = null);
            var provenance = new Dictionary<string, string>();
            var applied = new CopilotInvestmentScenario { HourlyCost = 80, MinutesBase = 20 }.ApplyTo(tenant, provenance);
            Assert.AreEqual(80, applied.HourlyCost);
            Assert.AreEqual(20, applied.MinutesPerActiveDay.Base);
            Assert.AreEqual(30, applied.SeatPricePerMonth);
            Assert.AreEqual(CopilotInvestmentProvenance.Scenario, provenance[CopilotInvestmentFields.HourlyCost]);
            Assert.AreEqual(CopilotInvestmentProvenance.Tenant, provenance[CopilotInvestmentFields.SeatPricePerMonth]);
            Assert.AreEqual(CopilotInvestmentProvenance.Missing, provenance[CopilotInvestmentFields.CreditPrice]);
            Assert.AreEqual(30, tenant.MinutesPerActiveDay.Upside, "The tenant defaults are never edited.");
            Assert.AreEqual(15, tenant.MinutesPerActiveDay.Base);

            var broken = Assert.ThrowsException<CopilotInvestmentAssumptionsRejectedException>(() =>
                new CopilotInvestmentScenario { MinutesBase = 45 }.ApplyTo(tenant, null));
            Assert.AreEqual(CopilotInvestmentErrorCodes.ScenarioInvalid, broken.Code);

            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("es-ES");
                var parsed = CopilotInvestmentAPIController.ParseScenario(new[] { new KeyValuePair<string, string>("realisationBase", "0.5") });
                Assert.AreEqual(0.5, parsed.RealisationBase, "A link means the same thing on every server, whatever its culture.");
                Assert.ThrowsException<CopilotInvestmentAssumptionsRejectedException>(() =>
                    CopilotInvestmentAPIController.ParseScenario(new[] { new KeyValuePair<string, string>("seatPrice", "thirty") }));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
