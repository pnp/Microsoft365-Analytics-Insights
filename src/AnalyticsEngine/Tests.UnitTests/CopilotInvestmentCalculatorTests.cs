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
using AdoptionScoring = Common.Entities.CopilotAdoption.CopilotAdoptionScoring;
using AdoptionTimePoint = Common.Entities.CopilotAdoption.AdoptionTimePoint;
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
        internal static readonly DateTime From = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        internal static CopilotInvestmentAssumptions Assumptions(Action<CopilotInvestmentAssumptions> change = null)
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

        internal static CopilotInvestmentPerson Holder(int id, int activeDays, string band = "Established", long interactions = 10,
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

        internal static CreditUserFact Balance(int userId, double credits, string period = "unverified") => new CreditUserFact
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

        internal static CopilotInvestmentInput Input(List<CopilotInvestmentPerson> people, CopilotInvestmentAssumptions assumptions = null, int windowDays = 28)
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
        public void EverythingThePageReads_IsSerialisedWithTheNamesItExpects()
        {
            // Web API serialises with Newtonsoft defaults, so a property without [JsonProperty] reaches the page in
            // PascalCase and silently reads as undefined there.
            var people = new List<CopilotInvestmentPerson>
            {
                Holder(1, 10, credit: Balance(1, 50), studio: new StudioUserFact { UserId = 1, Credits = 10 }),
                Holder(2, 0, "Dormant"),
            };
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, Assumptions()));
            var page = new CopilotInvestmentPeoplePage { Total = summary.People.Count, Rows = summary.People, Currency = "EUR", ValueAvailable = true };
            var detail = CopilotInvestmentPersonDetailBuilder.Build(summary.People[0], new PersonDetailFacts(), "EUR", From, From.AddDays(28));

            foreach (var payload in new object[] { summary, page, detail, new CreditTrendPoint() })
            {
                var names = ((Newtonsoft.Json.Linq.JContainer)Newtonsoft.Json.Linq.JToken.FromObject(payload)).Descendants().OfType<Newtonsoft.Json.Linq.JProperty>()
                    .Where(p => !(p.Parent?.Parent is Newtonsoft.Json.Linq.JProperty owner && (owner.Name == "values" || owner.Name == "provenance")))
                    .Select(p => p.Name).Distinct().ToList();
                var wrong = names.Where(n => !char.IsLower(n[0])).ToList();
                Assert.AreEqual(0, wrong.Count, payload.GetType().Name + ": " + string.Join(", ", wrong));
            }
            StringAssert.Contains(JsonConvert.SerializeObject(summary), "\"roiBaseIfBalancesMonthly\"");
            Assert.IsFalse(JsonConvert.SerializeObject(summary).Contains("person1@contoso.example"), "The summary never carries the people.");
        }

        [TestMethod]
        public void UsageReportHolders_CountAsSeats_ButStayOutOfValueAndReturn()
        {
            // Microsoft's report covers its own period: the seat is a cost, but its days are not this window's.
            var otherPeriod = Holder(2, 25, "Champion");
            otherPeriod.EvidenceOtherPeriod = true;
            otherPeriod.SignalSource = AdoptionScoring.SignalSourceUsageReport;
            var withOther = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10), otherPeriod }, Assumptions()));
            var alone = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { Holder(1, 10) }, Assumptions()));

            Assert.AreEqual(2, withOther.Investment.Seats.Holders);
            Assert.AreEqual(1, withOther.Investment.Seats.AnalysedHolders);
            Assert.AreEqual(1, withOther.Investment.Seats.OtherPeriodHolders);
            Assert.AreEqual(alone.Investment.ModelledWindowCost, withOther.Investment.ModelledWindowCost, "Only the matched seat is in the cost.");
            Assert.AreEqual(alone.Value.Scenarios.Single(s => s.Scenario == "base").RoiMultiple, withOther.Value.Scenarios.Single(s => s.Scenario == "base").RoiMultiple);
            Assert.AreEqual(2 * 30d, withOther.Investment.Seats.MonthlyRunRate.Value, 0.001, "The run rate still counts every seat.");
            var row = withOther.People.Single(p => p.UserId == 2);
            Assert.AreEqual(CopilotInvestmentSignals.OtherPeriod, row.Signal);
            Assert.IsNull(row.ValueBase);
            Assert.IsTrue(withOther.Trust.Reasons.Any(r => r.Code == CopilotInvestmentTrustCodes.UsageReportSignal && r.Area == CopilotInvestmentTrustAreas.Evidence));

            var onlyOther = CopilotInvestmentCalculator.Calculate(Input(new List<CopilotInvestmentPerson> { otherPeriod }, Assumptions()));
            Assert.IsFalse(onlyOther.Value.Available);
            Assert.AreEqual(CopilotInvestmentValueUnavailable.NoMatchedHolders, onlyOther.Value.UnavailableReason);
            Assert.IsFalse(onlyOther.BreakEven.Available);
            Assert.IsFalse(onlyOther.Path.Available);
            Assert.IsTrue(onlyOther.Findings.Any(f => f.Code == CopilotInvestmentFindingCodes.NoMatchedHolders));
            Assert.IsFalse(onlyOther.Findings.Any(f => f.Code == CopilotInvestmentFindingCodes.AssumptionsMissing), "The assumptions are all set.");
        }

        [TestMethod]
        public void ASeatHolderTheAnalysisDidNotScore_IsNeverShownAsSomeoneWithoutASeat()
        {
            var credits = new List<CreditUserFact> { Balance(7, 400) };
            var studio = new List<StudioUserFact> { new StudioUserFact { UserId = 8, Credits = 5000 } };
            var people = CopilotInvestmentPeopleBuilder.Build(null, credits, studio, _ => true, _ => null, out _, out var scoped, new[] { 7, 8 });
            Assert.IsTrue(people.All(p => p.Licensed && p.NotAnalysed));

            var input = Input(people, Assumptions());
            input.ScopedCredits = scoped;
            input.HoldersNotAnalysed = 2;
            var summary = CopilotInvestmentCalculator.Calculate(input);
            Assert.AreEqual(2, summary.Investment.Seats.Holders, "Counted once, through the not-analysed total.");
            Assert.AreEqual(0, summary.Investment.Seats.AnalysedHolders);
            Assert.AreEqual(0, summary.Metered.People, "A seat holder is not metered use.");
            Assert.IsTrue(summary.People.All(p => p.Licensed && p.Signal == CopilotInvestmentSignals.NotAnalysed));
            Assert.IsFalse(summary.Actions.Any(a => a.Code == CopilotInvestmentActionCodes.LicenceMeteredHeavy));
        }

        [TestMethod]
        public void AStudioLedgerThatCouldNotBeRead_LowersConfidence_RatherThanReadingAsZeroCost()
        {
            var input = Input(new List<CopilotInvestmentPerson> { Holder(1, 10) }, Assumptions());
            input.Studio = new StudioFacts { FailureCode = "studioUnavailable" };
            var summary = CopilotInvestmentCalculator.Calculate(input);

            var reason = summary.Trust.Reasons.Single(r => r.Code == CopilotInvestmentTrustCodes.StudioUnavailable);
            Assert.AreEqual("low", reason.Impact, "With a Studio price set, the cost behind the return is missing a part.");
            Assert.AreEqual(CopilotInvestmentTrustAreas.Cost, reason.Area);
            Assert.AreEqual("low", summary.Trust.Areas.Single(a => a.Area == CopilotInvestmentTrustAreas.Cost).Level);
            Assert.AreEqual("low", summary.Trust.Level);

            input.Credits = new CreditFacts { FailureCode = "creditsUnavailable" };
            input.Studio = new StudioFacts();
            var balances = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsTrue(balances.Trust.Reasons.Any(r => r.Code == CopilotInvestmentTrustCodes.CreditsUnavailable));
            Assert.IsFalse(balances.Trust.Reasons.Any(r => r.Code == CopilotInvestmentTrustCodes.CreditsMissing), "Unreadable is not the same as empty.");
            Assert.AreEqual("medium", balances.Trust.Level, "Balances are outside the return, so they do not lower its level.");
        }

        [TestMethod]
        public void EachPersonsBreakEven_IsAgainstTheirOwnCost_AndActionsDrillToExactlyThePeopleTheyCount()
        {
            // Seat-only break-even: 30 / (15 / 60 x 0.5 x 60) = 4 active days a month.
            var withStudio = Holder(3, 4, studio: new StudioUserFact { UserId = 3, Credits = 2000 });
            var review = Holder(2, 0);
            review.ReclaimEligibility = AdoptionScoring.ReclaimEligibilityTiers.Review;
            var people = new List<CopilotInvestmentPerson>
            {
                Holder(1, 0),
                review,
                withStudio,
                Holder(4, 20, "Champion", credit: Balance(4, 1200)),
                new CopilotInvestmentPerson { UserId = 5, ActiveDays = 12 },
                new CopilotInvestmentPerson { UserId = 6, Studio = new StudioUserFact { UserId = 6, Credits = 3000 } },
            };
            people[3].Credit.LimitExhaustedRows = 1;
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, Assumptions()));

            var studioRow = summary.People.Single(p => p.UserId == 3);
            var months = 28 / CopilotInvestmentCalculator.AverageMonthDays;
            var ownCost = 30 * months + 2000 * 0.02;
            Assert.AreEqual(ownCost / 7.5 / months, studioRow.BreakEvenDaysPerMonth.Value, 0.006);
            Assert.IsTrue(studioRow.BreakEvenGapDaysPerMonth > 0, "Their Studio credits add days a seat alone would not need.");
            Assert.IsTrue(studioRow.Flags.Contains(CopilotInvestmentFlags.BelowBaseBreakEven));

            foreach (var action in summary.Actions.Where(a => a.Flag != null))
                Assert.AreEqual(action.People, summary.People.Count(p => p.Flags.Contains(action.Flag)), action.Code);
            CollectionAssert.IsSubsetOf(new[]
            {
                CopilotInvestmentActionCodes.ReclaimIdle, CopilotInvestmentActionCodes.ReviewIdle, CopilotInvestmentActionCodes.EnableBelowBreakEven,
                CopilotInvestmentActionCodes.ExpandEvidenced, CopilotInvestmentActionCodes.RaiseLimitEvidenced, CopilotInvestmentActionCodes.LicenceMeteredHeavy,
            }, summary.Actions.Select(a => a.Code).ToList());
            Assert.IsTrue(summary.Actions.All(a => a.Effect != null && CopilotInvestmentActionEffects.All.Contains(a.Effect)));
        }

        [TestMethod]
        public void ReclaimingSeats_FreesTheSeatCostOnly_AndThePathSaysWhatClosesTheGap()
        {
            var idle = Holder(1, 0, studio: new StudioUserFact { UserId = 1, Credits = 1000 });
            var people = new List<CopilotInvestmentPerson> { idle, Holder(2, 3), new CopilotInvestmentPerson { UserId = 3, ActiveDays = 12 } };
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, Assumptions()));

            var months = 28 / CopilotInvestmentCalculator.AverageMonthDays;
            var seat = 30 * months;
            var cost = 2 * seat + 1000 * 0.02;
            var value = 3 * 7.5;
            Assert.AreEqual(Math.Round(value / (cost - seat), 4), summary.Sensitivity.RoiBaseAfterReclaim.Value, 0.0001,
                "The idle holder's Studio use is billed whether or not they keep a seat.");
            var path = summary.Path;
            Assert.IsTrue(path.Available);
            Assert.AreEqual(Math.Round((cost - value) / months, 2), path.MonthlyGap.Value, 0.01);
            Assert.AreEqual(Math.Round((cost - value) / months / 7.5, 1), path.ExtraActiveDaysPerMonth);
            Assert.AreEqual(1, path.Reclaimable);
            Assert.AreEqual(1, path.EvidencedWithoutSeat);
            Assert.AreEqual(1, path.Reassignable);
            Assert.AreEqual(0, path.NetNewSeats);
            Assert.AreEqual(Math.Round(seat / months, 2), summary.Actions.Single(a => a.Code == CopilotInvestmentActionCodes.ReclaimIdle).MonthlyAmount);
        }

        [TestMethod]
        public void TheDistribution_SplitsAtTheBreakEven_SoNoBarStraddlesIt()
        {
            // Break-even is 30 / 7 = 4.29 days a month. 28 days is 0.92 months, so 4 active days is 4.35 a month - just at
            // or above it - and 3 is below it.
            var people = new List<CopilotInvestmentPerson> { Holder(1, 0), Holder(2, 1), Holder(3, 3), Holder(4, 4), Holder(5, 12) };
            var assumptions = Assumptions(a => a.MinutesPerActiveDay = new CopilotInvestmentRange { Conservative = 10, Base = 14, Upside = 30 });
            var summary = CopilotInvestmentCalculator.Calculate(Input(people, assumptions));
            var breakEven = 30 / (14 / 60d * 0.5 * 60);

            var buckets = summary.Evidence.Distribution;
            Assert.AreEqual(people.Count, buckets.Sum(b => b.Holders));
            Assert.IsTrue(buckets.Any(b => b.MaxDaysPerMonth.HasValue && Math.Abs(b.MaxDaysPerMonth.Value - Math.Round(breakEven, 2)) < 0.01));
            Assert.IsTrue(buckets.All(b => b.BelowBreakEven.HasValue));
            Assert.IsFalse(buckets.Any(b => b.Key == "range" && b.MaxDaysPerMonth.HasValue && b.MaxDaysPerMonth.Value - b.MinDaysPerMonth < 1),
                "The fixed edge at 5 gives way to the break-even rather than leaving a sliver beside it.");
            var below = buckets.Where(b => b.BelowBreakEven == true).Sum(b => b.Holders);
            var above = buckets.Where(b => b.BelowBreakEven == false).Sum(b => b.Holders);
            var months = 28 / CopilotInvestmentCalculator.AverageMonthDays;
            Assert.AreEqual(people.Count(p => p.ActiveDays / months >= breakEven), above);
            Assert.AreEqual(people.Count - above, below);
        }

        [TestMethod]
        public void StudioTrend_IsOnlyComparedLikeForLike_AndTheWeeklyTrendOnlyForTheWholeTenant()
        {
            var input = Input(new List<CopilotInvestmentPerson> { Holder(1, 10, studio: new StudioUserFact { UserId = 1, Credits = 500, PriorCredits = 50 }) }, Assumptions());
            input.Studio = new StudioFacts { HasData = true, PriorComplete = false };
            var partial = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsNull(partial.Investment.Studio.PriorCredits, "A ledger that starts inside the previous period would read as growth.");
            Assert.IsNull(partial.Findings.Single(f => f.Code == CopilotInvestmentFindingCodes.StudioTrend).Values["prior"]);
            input.Studio.PriorComplete = true;
            Assert.AreEqual(50d, CopilotInvestmentCalculator.Calculate(input).Investment.Studio.PriorCredits);

            input.WeeklyActiveHoldersUnavailable = CopilotInvestmentTrajectoryUnavailable.Filtered;
            var filtered = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsFalse(filtered.Trajectory.Available);
            Assert.AreEqual(CopilotInvestmentTrajectoryUnavailable.Filtered, filtered.Trajectory.UnavailableReason);

            input.WeeklyActiveHoldersUnavailable = null;
            input.WeeklyActiveHolders = Enumerable.Range(0, 14)
                .Select(i => new AdoptionTimePoint { WeekStart = From.AddDays(7 * (i - 14)), Value = i == 5 ? (double?)null : 1 })
                .ToList();
            var tenant = CopilotInvestmentCalculator.Calculate(input);
            Assert.IsTrue(tenant.Trajectory.Available);
            Assert.AreEqual(CopilotInvestmentCalculator.TrajectoryWeeks, tenant.Trajectory.Weeks.Count, "The most recent weeks only.");
            Assert.IsTrue(tenant.Trajectory.Weeks.Any(w => w.ActiveHolders == null), "A week the audit import misses stays absent, not zero.");
            Assert.AreEqual(1d, tenant.Trajectory.Weeks.Last().Share);
            Assert.IsTrue(tenant.Findings.Any(f => f.Code == CopilotInvestmentFindingCodes.Trajectory));
        }

        [TestMethod]
        public void APersonsTrend_ComparesEqualHalvesOfTheWindow_SoSteadyUseIsNeverADecline()
        {
            var from = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
            var to = from.AddDays(28);
            var person = new CopilotInvestmentPersonRow { UserId = 1, Licensed = true, Flags = new List<string>() };
            PersonDetailFacts Facts(Func<DateTime, bool> active)
            {
                var facts = new PersonDetailFacts { ActivityAssessed = true };
                for (var day = from; day < to; day = day.AddDays(1))
                    if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday && active(day))
                        facts.Activity.Add(new PersonActivityRow { ActivityDate = day, AppHost = "Word", Turns = day < from.AddDays(3) ? 40 : 2 });
                return facts;
            }

            // Every working day, with a burst of prompts at the start: steady use, not a decline.
            var steady = CopilotInvestmentPersonDetailBuilder.Build(person, Facts(_ => true), "EUR", from, to);
            CollectionAssert.DoesNotContain(steady.Evidence, CopilotInvestmentPersonDetailBuilder.EvidenceCodes.Declining);
            CollectionAssert.DoesNotContain(steady.Evidence, CopilotInvestmentPersonDetailBuilder.EvidenceCodes.Growing);

            var fading = CopilotInvestmentPersonDetailBuilder.Build(person, Facts(d => d < from.AddDays(14)), "EUR", from, to);
            CollectionAssert.Contains(fading.Evidence, CopilotInvestmentPersonDetailBuilder.EvidenceCodes.Declining);
            var building = CopilotInvestmentPersonDetailBuilder.Build(person, Facts(d => d >= from.AddDays(14)), "EUR", from, to);
            CollectionAssert.Contains(building.Evidence, CopilotInvestmentPersonDetailBuilder.EvidenceCodes.Growing);
        }

        [TestMethod]
        public void ThePeopleList_SortsEveryoneBeforePaging_MissingValuesLast_TiesByName()
        {
            var rows = Enumerable.Range(1, 120).Select(i => new CopilotInvestmentPersonRow
            {
                UserId = i,
                UserPrincipalName = $"person{i:000}@contoso.example",
                Licensed = i % 2 == 0,
                Band = i % 3 == 0 ? null : i % 3 == 1 ? "Developing" : "Dormant",
                ActiveDaysPerMonth = i % 10,
                AppsUsed = i % 4,
                Cost = i % 5 == 0 ? (double?)null : i,
                Credits = i == 117 ? 5000 : i % 7 == 0 ? (double?)null : i,
                StudioCredits = 200 - i,
                BreakEvenDaysPerMonth = i % 6 == 0 ? (double?)null : 30 - i % 30,
                Signal = i == 101 ? CopilotInvestmentSignals.Supported : i == 2 ? "someFutureSignal" : CopilotInvestmentSignals.NoEvidence,
            }).ToList();
            List<int> Ids(string key, bool descending) => CopilotInvestmentAPIController.SortPeople(rows, key, descending).Select(r => r.UserId).ToList();
            bool HasCredits(int id) => rows[id - 1].Credits.HasValue;

            // The largest balance is the 117th person's: past the first page of fifty, and first once the column is sorted.
            Assert.AreEqual(117, Ids("credits", true).First());
            Assert.AreEqual(1, Ids("credits", false).First());
            // People with no balance sort last whichever way the column is read.
            var withBalance = rows.Count(r => r.Credits.HasValue);
            foreach (var descending in new[] { true, false })
            {
                var order = Ids("credits", descending);
                Assert.IsTrue(order.Take(withBalance).All(HasCredits) && order.Skip(withBalance).All(id => !HasCredits(id)), "descending: " + descending);
            }
            Assert.IsTrue(Ids("cost", false).Skip(rows.Count(r => r.Cost.HasValue)).All(id => !rows[id - 1].Cost.HasValue));
            Assert.IsTrue(Ids("breakEven", true).Skip(rows.Count(r => r.BreakEvenDaysPerMonth.HasValue)).All(id => !rows[id - 1].BreakEvenDaysPerMonth.HasValue));

            // Text, yes/no and numbers both ways.
            Assert.AreEqual(1, Ids("user", false).First());
            Assert.AreEqual(120, Ids("user", true).First());
            CollectionAssert.AreEqual(rows.Where(r => r.Licensed).Select(r => r.UserId).ToList(), Ids("licensed", true).Take(60).ToList(),
                "Licensed people first, in name order.");
            Assert.IsFalse(rows[Ids("licensed", false).First() - 1].Licensed);
            Assert.AreEqual(1, Ids("studioCredits", true).First());
            Assert.AreEqual(120, Ids("studioCredits", false).First());

            // Derived orders: a band by how established the habit is, a signal by the strength of its evidence; neither is alphabetical.
            var bands = Ids("band", true);
            Assert.AreEqual("Developing", rows[bands.First() - 1].Band);
            Assert.IsNull(rows[bands.Last() - 1].Band, "No band sorts last.");
            Assert.AreEqual("Dormant", rows[Ids("band", false).First() - 1].Band);
            Assert.AreEqual(101, Ids("signal", true).First());
            Assert.AreEqual(2, Ids("signal", true).Last(), "A signal this build does not know has no rank, so it sorts last.");
            Assert.AreEqual(2, Ids("signal", false).Last());

            // Ties fall back to the name, then the id, so a page boundary never moves between two identical requests.
            var apps = Ids("apps", true);
            CollectionAssert.AreEqual(apps, Ids("apps", true));
            CollectionAssert.AreEqual(rows.Where(r => r.AppsUsed == 3).Select(r => r.UserId).ToList(), apps.Take(30).ToList());
            CollectionAssert.AreEqual(new[] { 3, 13, 23 }, Ids("activeDays", true).Where(id => rows[id - 1].ActiveDaysPerMonth == 3).Take(3).ToList());
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
        public void EveryApiThatTakesAUserFilter_HasTheLargeQueryStringAllowanceInTheHostConfig()
        {
            // A <location> allowance covers one controller's route prefix and nothing else: api/CopilotSpend shipped with a
            // free-form userFilter on four routes and no block of its own, so a long (but valid) filter got IIS's bare 404.15.
            // The pins above name one controller each; this one finds every controller that takes a filter, so the next one
            // cannot ship without its block.
            var withFilter = PortalTestHost.WebControllers()
                .Where(c => c.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Any(m => m.GetParameters().Any(p => p.Name == "userFilter" && p.ParameterType == typeof(string))))
                .ToList();
            CollectionAssert.IsSubsetOf(
                new[] { "CopilotAdoptionAPIController", "CopilotInvestmentAPIController", "CopilotSpendAPIController", "ActivityAnalysisAPIController" },
                withFilter.Select(c => c.Name).ToList(),
                "The scan no longer finds the controllers known to take a userFilter, so it would pass vacuously.");

            var directory = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string config = null;
            while (directory != null && config == null)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, "Web", "Web.Template.config");
                config = System.IO.File.Exists(candidate) ? System.IO.File.ReadAllText(candidate) : null;
                directory = directory.Parent;
            }

            Assert.IsNotNull(config, "Web.Template.config was not found.");
            foreach (var controller in withFilter)
            {
                var prefix = controller.GetCustomAttributes(typeof(System.Web.Http.RoutePrefixAttribute), true)
                    .Cast<System.Web.Http.RoutePrefixAttribute>().Single().Prefix;
                var location = System.Text.RegularExpressions.Regex.Match(config,
                    "<location path=\"" + System.Text.RegularExpressions.Regex.Escape(prefix) + "\">(.*?)</location>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                Assert.IsTrue(location.Success, $"Web.Template.config has no <location path=\"{prefix}\"> block, but {controller.Name} takes a free-form userFilter on the query string.");
                StringAssert.Contains(location.Groups[1].Value, "maxQueryStringLength=\"16384\"", prefix);
                StringAssert.Contains(location.Groups[1].Value, "maxQueryString=\"16384\"", prefix);
            }
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

        [TestMethod]
        public void Ranks_CountTheExactFraction_EvenWhereFloatingPointLandsJustAboveAWholeNumber()
        {
            int people = 100;
            double fraction = 0.07;
            Assert.AreEqual(8d, Math.Ceiling(people * fraction), "The premise: 100 x 0.07 is 7.000000000000001, so a plain ceiling is one too many.");
            Assert.AreEqual(7, CopilotInvestmentStatistics.TopCount(people, fraction));
            Assert.AreEqual(7, CopilotInvestmentStatistics.NearestRank(people, fraction));

            Assert.AreEqual(3, CopilotInvestmentStatistics.TopCount(30, 0.1), "30 x 0.1 is exactly 3: the fractions the reports use are unaffected.");
        }
    }
}
