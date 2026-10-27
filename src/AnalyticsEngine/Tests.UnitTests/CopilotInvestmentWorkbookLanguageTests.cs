extern alias AnalyticsWeb;

using Common.Entities.CopilotCosts;
using Common.Entities.CopilotInvestment;
using Common.Entities.Localisation;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdoptionScoring = Common.Entities.CopilotAdoption.CopilotAdoptionScoring;
using CopilotInvestmentAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotInvestmentAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot investment workbook in the reader's language: the product's words from the portal's own catalog, the
    /// tenant's data exactly as stored, and the same typed cells and the same redaction whichever language it is in.
    /// </summary>
    [TestClass]
    public class CopilotInvestmentWorkbookLanguageTests
    {
        private const string Module = CopilotInvestmentWorkbook.CatalogModule;
        private static readonly string[] SheetKeys = { "executive", "value", "path", "cohorts", "organisation", "credits", "method", "people" };

        // Synthetic tenant text in a non-Latin script. A sign-in name stays ASCII, as Entra requires.
        private const string GreekDepartment = "\u0391\u03b8\u03ae\u03bd\u03b1 \u03a0\u03c9\u03bb\u03ae\u03c3\u03b5\u03b9\u03c2";
        private const string GreekManager = "\u0394\u03ae\u03bc\u03b7\u03c4\u03c1\u03b1 \u0399\u03c9\u03ac\u03bd\u03bd\u03bf\u03c5";
        private const string GreekService = "\u03c8\u03b7\u03c6\u03b9\u03b1\u03ba\u03cc\u03c2\u0392\u03bf\u03b7\u03b8\u03cc\u03c2";
        private const string GreekPolicy = "\u03a0\u03bf\u03bb\u03b9\u03c4\u03b9\u03ba\u03ae \u03ba\u03b1\u03c4\u03b1\u03bd\u03ac\u03bb\u03c9\u03c3\u03b7\u03c2 Contoso";
        private const string FormulaLikeDepartment = "=SUM(1,2)";

        private static CopilotInvestmentSummary Summary()
        {
            var people = new List<CopilotInvestmentPerson>
            {
                CopilotInvestmentCalculatorTests.Holder(1, 14, credit: CopilotInvestmentCalculatorTests.Balance(1, 1234.5), department: GreekDepartment),
                CopilotInvestmentCalculatorTests.Holder(2, 0, band: "Dormant", interactions: 0, department: GreekDepartment),
                CopilotInvestmentCalculatorTests.Holder(3, 5, credit: CopilotInvestmentCalculatorTests.Balance(3, 18.25), department: FormulaLikeDepartment),
            };
            people[0].Manager = GreekManager;
            var input = CopilotInvestmentCalculatorTests.Input(people, CopilotInvestmentCalculatorTests.Assumptions());
            input.Credits.SnapshotDate = CopilotInvestmentCalculatorTests.From.AddDays(27);
            input.Credits.Services.Add(new CreditServiceFact
            {
                ObjectId = people[0].Credit.ObjectId, Category = GreekService, PolicyId = "policy-1", Consumed = 1234.5, Unit = CopilotInvestmentSql.CreditUnit,
            });
            input.Credits.Services.Add(new CreditServiceFact
            {
                ObjectId = people[2].Credit.ObjectId, Category = "copilotChatAgents", PolicyId = "policy-1", Consumed = null, Unit = CopilotInvestmentSql.CreditUnit,
            });
            input.Credits.Context = new CreditContextFact
            {
                Policies =
                {
                    new CostPolicyContext
                    {
                        Id = "policy-1", DisplayName = GreekPolicy, Type = "#microsoft.graph.allUsersSpendingPolicy", Status = "enabled",
                        BillingMethods = "prepaidCredits,payAsYouGo",
                        PeriodLimit = new CostQuantity { Type = "#microsoft.graph.unlimitedCostManagementQuantity" },
                        PerUserLimit = new CostQuantity { Type = "#microsoft.graph.limitedCostManagementQuantity", Value = 2500, Unit = CopilotInvestmentSql.CreditUnit },
                    },
                },
                PolicyServices = { ["policy-1"] = new List<CostPolicyServiceContext> { new CostPolicyServiceContext { ServiceId = "copilotChatAgents", State = "enabled" } } },
            };
            return CopilotInvestmentCalculator.Calculate(input);
        }

        [TestMethod]
        public void AWorkbookFromTheSpanishPage_IsInSpanish_AndOneFromAnOlderLinkIsInEnglish()
        {
            var summary = Summary();
            var unspecified = Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test"));
            var english = Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test", PortalLanguage.English));
            var spanish = Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test", PortalLanguage.Spanish));
            var en = PortalCatalog.For(Module, PortalLanguage.English);
            var es = PortalCatalog.For(Module, PortalLanguage.Spanish);

            Assert.AreEqual(Flatten(english), Flatten(unspecified), "A caller that names no language gets exactly the English workbook.");
            CollectionAssert.AreEqual(SheetKeys.Select(k => en.Text("copilotInvestment.workbook.sheet." + k)).ToList(), english.Select(s => s.Name).ToList());
            CollectionAssert.AreEqual(SheetKeys.Select(k => es.Text("copilotInvestment.workbook.sheet." + k)).ToList(), spanish.Select(s => s.Name).ToList());
            Assert.AreEqual("Resumen ejecutivo", spanish[0].Name);
            Assert.AreEqual("Personas", spanish[SheetKeys.Length - 1].Name);
            Assert.AreEqual(CopilotInvestmentWorkbook.FileName(summary), CopilotInvestmentWorkbook.FileName(summary, PortalLanguage.English));
            StringAssert.StartsWith(CopilotInvestmentWorkbook.FileName(summary, PortalLanguage.Spanish), "inversion-copilot-");
            StringAssert.EndsWith(CopilotInvestmentWorkbook.FileName(summary, PortalLanguage.Spanish), "-28d.xlsx");

            var spanishText = Texts(spanish);
            foreach (var key in new[] { "exec.title", "exec.moneyNote", "method.value.definition", "method.notSaving.definition", "credits.intro", "exec.personDataIncluded" })
                CollectionAssert.Contains(spanishText, es.Text("copilotInvestment.workbook." + key), key);
            // Composed sentences too: every finding and action is a Spanish sentence, labelled in Spanish.
            CollectionAssert.Contains(spanishText, es.Text("copilotInvestment.workbook.tone.neutral"));
            Assert.IsTrue(spanishText.Any(t => t.StartsWith(Prefix(es, "copilotInvestment.workbook.finding.runRate"), StringComparison.Ordinal)), "The run-rate finding in Spanish.");
            // An action reads its effect first ("[Importe: ...]"), then what it means.
            Assert.IsTrue(spanishText.Any(t => t.StartsWith(es.Text("copilotInvestment.workbook.effect.freesSeatCost") + " " + Prefix(es, "copilotInvestment.workbook.action.reclaimIdle"), StringComparison.Ordinal)), "The reclaim action in Spanish.");
            CollectionAssert.Contains(spanishText, es.Text("copilotInvestment.policies.status.enabled"));
            CollectionAssert.Contains(spanishText,
                es.Text("copilotInvestment.policies.billing.prepaidCredits") + " \u00b7 " + es.Text("copilotInvestment.policies.billing.payAsYouGo"));
            CollectionAssert.Contains(spanishText, es.Text("copilotInvestment.workbook.credits.unlimited"));

            Assert.IsTrue(Texts(english).Contains(en.Text("copilotInvestment.workbook.exec.title")));
        }

        [TestMethod]
        public void EveryNumberAndDate_IsTheSameTypedCellInEitherLanguage()
        {
            var summary = Summary();
            var englishBook = CopilotInvestmentWorkbook.Build(summary, true, "test", PortalLanguage.English);
            var spanishBook = CopilotInvestmentWorkbook.Build(summary, true, "test", PortalLanguage.Spanish);
            AssertSpanishCounterpart(englishBook, spanishBook);

            var executive = Sheets(spanishBook)[0].Cells;
            var period = executive.Where(c => c.Type == null && c.Style != null && c.Value != null
                && double.Parse(c.Value, CultureInfo.InvariantCulture) == summary.FromUtc.ToOADate()).ToList();
            Assert.AreEqual(1, period.Count, "The period start is a date cell, not text.");
            Assert.IsTrue(executive.Count(c => c.Type == null && c.Value != null) >= 15, "The headline figures are numbers.");
        }

        /// <summary>
        /// The Spanish workbook is the English one in other words: the same cells, every number, date and yes/no the same
        /// typed value, no formula anywhere, no figure turned into text, and not one cell of product wording left in English.
        /// </summary>
        internal static void AssertSpanishCounterpart(byte[] englishBook, byte[] spanishBook)
        {
            var english = Sheets(englishBook);
            var spanish = Sheets(spanishBook);
            Assert.AreEqual(english.Count, spanish.Count);
            for (var i = 0; i < english.Count; i++)
            {
                var name = english[i].Name;
                var en = english[i].Cells.ToDictionary(c => c.Ref);
                var es = spanish[i].Cells.ToDictionary(c => c.Ref);
                CollectionAssert.AreEquivalent(en.Keys.ToList(), es.Keys.ToList(), name + ": the same cells in either language.");
                foreach (var cell in en.Values)
                {
                    var other = es[cell.Ref];
                    Assert.AreEqual(cell.Type, other.Type, name + "!" + cell.Ref);
                    Assert.AreEqual(cell.Style, other.Style, name + "!" + cell.Ref);
                    if (cell.Type != "inlineStr") Assert.AreEqual(cell.Value, other.Value, name + "!" + cell.Ref + ": a number is the same number in Spanish.");
                }
                Assert.IsFalse(spanish[i].Cells.Any(c => c.Formula), name + ": no cell is ever a formula.");
            }

            // A formatted number is never written as text: the figures stay typed, only sentences carry formatted numbers.
            var numberAsText = spanish.SelectMany(s => s.Cells).Where(c => c.Type == "inlineStr" && IsNumber(c.Text)).Select(c => c.Text).ToList();
            Assert.AreEqual(0, numberAsText.Count, "Numbers written as text: " + string.Join(" | ", numberAsText));

            var enText = PortalCatalog.For(Module, PortalLanguage.English);
            var esText = PortalCatalog.For(Module, PortalLanguage.Spanish);
            var englishOnly = new HashSet<string>(enText.Entries
                .Where(e => esText.Entries.TryGetValue(e.Key, out var s) && !string.Equals(s, e.Value, StringComparison.Ordinal))
                .Select(e => e.Value));
            var leaked = Texts(spanish).Where(englishOnly.Contains).Distinct().ToList();
            Assert.AreEqual(0, leaked.Count, "English in the Spanish workbook: " + string.Join(" | ", leaked));
        }

        [TestMethod]
        public void PeopleAreWrittenOnlyWithSeePii_InEitherLanguage()
        {
            var summary = Summary();
            foreach (var language in new[] { PortalLanguage.English, PortalLanguage.Spanish })
            {
                var text = PortalCatalog.For(Module, language);
                var full = Sheets(CopilotInvestmentWorkbook.Build(summary, includeIndividualData: true, buildLabel: "test", language: language));
                var aggregates = Sheets(CopilotInvestmentWorkbook.Build(summary.WithoutIndividualData(), includeIndividualData: false, buildLabel: "test", language: language));
                var people = text.Text("copilotInvestment.workbook.sheet.people");

                Assert.AreEqual(SheetKeys.Length, full.Count, language.ToString());
                Assert.AreEqual(SheetKeys.Length - 1, aggregates.Count, language.ToString());
                Assert.AreEqual(people, full.Last().Name);
                CollectionAssert.DoesNotContain(aggregates.Select(s => s.Name).ToList(), people);
                CollectionAssert.Contains(Texts(full), "person1@contoso.example");
                CollectionAssert.Contains(Texts(full), GreekManager);
                CollectionAssert.Contains(Texts(full), text.Text("copilotInvestment.workbook.exec.personDataIncluded"));

                var withheld = Texts(aggregates);
                Assert.IsFalse(withheld.Any(t => t.Contains("@contoso.example")), language + ": no sign-in name without See PII.");
                CollectionAssert.DoesNotContain(withheld, GreekManager, language + ": no manager's name without See PII.");
                CollectionAssert.Contains(withheld, text.Text("copilotInvestment.workbook.exec.personDataExcluded"));
            }
        }

        [TestMethod]
        public void TenantText_IsWrittenAsStored_InEitherLanguage()
        {
            var summary = Summary();
            foreach (var language in new[] { PortalLanguage.English, PortalLanguage.Spanish })
            {
                var sheets = Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test", language));
                var organisation = sheets[Array.IndexOf(SheetKeys, "organisation")].Cells;
                var credits = sheets[Array.IndexOf(SheetKeys, "credits")].Cells;
                var people = sheets[Array.IndexOf(SheetKeys, "people")].Cells;

                Assert.IsTrue(organisation.Any(c => c.Type == "inlineStr" && c.Text == GreekDepartment), language + ": a Greek department, as stored.");
                Assert.IsTrue(organisation.Any(c => c.Type == "inlineStr" && c.Text == FormulaLikeDepartment), language + ": a value that looks like a formula stays text.");
                Assert.IsTrue(credits.Any(c => c.Text == GreekService), language + ": a service category, as the source wrote it.");
                Assert.IsTrue(credits.Any(c => c.Text == GreekPolicy), language + ": a policy's name, as the administrator wrote it.");
                Assert.IsTrue(people.Any(c => c.Text == GreekDepartment) && people.Any(c => c.Text == GreekManager), language.ToString());
                Assert.IsTrue(people.Any(c => c.Text == "person3@contoso.example"), language.ToString());
            }
        }

        [TestMethod]
        public void TheCoverageLine_DescribesBothFilters_InTheReadersLanguage()
        {
            var summary = Summary();
            summary.Filtered = true;
            summary.GlobalFilter = new GlobalFilterEcho
            {
                Clauses =
                {
                    new GlobalFilterClauseModel
                    {
                        Join = "and", Dimension = UserFilterDimensions.Department, Operator = "is", Values = { "Ventas" },
                        ViewerAttribute = UserFilterDimensions.Department, ViewerValue = GreekDepartment,
                    },
                },
            };
            summary.UserFilter = new UserFilterEcho
            {
                Clauses = { new UserFilterClauseModel { Join = "and", Dimension = UserFilterDimensions.Country, Operator = "is", Values = { "Espa\u00f1a", "Oman" } } },
            };

            var spanish = Texts(Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test", PortalLanguage.Spanish)));
            CollectionAssert.Contains(spanish,
                "Filtro del administrador: Departamento es Ventas o " + GreekDepartment + " (seg\u00fan su perfil)\n"
                + "Su filtro: Pa\u00eds o regi\u00f3n es Espa\u00f1a u Oman");
            var english = Texts(Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test")));
            CollectionAssert.Contains(english,
                "Administrator\u2019s filter: Department is Ventas or " + GreekDepartment + " (from your profile)\n"
                + "Your filter: Country or region is Espa\u00f1a or Oman");

            summary.GlobalFilter = null;
            summary.UserFilter = null;
            CollectionAssert.Contains(Texts(Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test", PortalLanguage.Spanish))),
                PortalCatalog.For(Module, PortalLanguage.Spanish).Text("copilotInvestment.workbook.exec.filteredUnknown"));
        }

        [TestMethod]
        public void AFilter_ReadsAsThePageWordsIt_InEitherLanguage()
        {
            var userFilter = PortalCatalog.For("userFilter", PortalLanguage.Spanish);
            UserFilterEcho Filter(params UserFilterClauseModel[] clauses)
            {
                var filter = new UserFilterEcho();
                filter.Clauses.AddRange(clauses);
                return filter;
            }

            // Fixed tokens are the product's words; values the tenant typed are not; groups are bracketed.
            var echo = Filter(
                new UserFilterClauseModel { Join = "and", Dimension = UserFilterDimensions.UserType, Operator = "is", Values = { "member" } },
                new UserFilterClauseModel { Join = "or", Dimension = UserFilterDimensions.Department, Operator = "contains", Values = { "Ventas" } });
            Assert.AreEqual("(User type is Member) or (Department contains \u201cVentas\u201d)", FilterWording.Describe(echo, PortalLanguage.English));
            Assert.AreEqual("(Tipo de usuario es Miembro) o (Departamento contiene \u00abVentas\u00bb)", FilterWording.Describe(echo, PortalLanguage.Spanish));

            // "o" becomes "u" before a word sounding like o - by ICU's rule, as Intl.ListFormat on the page applies it.
            Assert.AreEqual("a u Oeste", FilterWording.OrList(new[] { "a", "Oeste" }, PortalLanguage.Spanish));
            Assert.AreEqual("a u hotel", FilterWording.OrList(new[] { "a", "hotel" }, PortalLanguage.Spanish));
            Assert.AreEqual("a u 8 sedes", FilterWording.OrList(new[] { "a", "8 sedes" }, PortalLanguage.Spanish));
            Assert.AreEqual("a u 11", FilterWording.OrList(new[] { "a", "11" }, PortalLanguage.Spanish));
            Assert.AreEqual("a o 110", FilterWording.OrList(new[] { "a", "110" }, PortalLanguage.Spanish));
            Assert.AreEqual("a o \u00f3leo", FilterWording.OrList(new[] { "a", "\u00f3leo" }, PortalLanguage.Spanish));
            Assert.AreEqual("a, b or c", FilterWording.OrList(new[] { "a", "b", "c" }, PortalLanguage.English));

            // The server's "(no domain)" placeholder is product text; a real domain is not.
            var domains = Filter(new UserFilterClauseModel { Join = "and", Dimension = "emailDomain", Operator = "isNot", Values = { "(no domain)", "contoso.example" } });
            Assert.AreEqual(userFilter.Text("userFilter.dimension.emailDomain") + " no es (sin dominio) o contoso.example",
                FilterWording.Describe(domains, PortalLanguage.Spanish));

            // A custom organisation type keeps its administrator's name, in either language.
            var custom = Filter(new UserFilterClauseModel { Join = "and", Dimension = "org:12", Operator = "is", Values = { GreekDepartment } });
            custom.DimensionNames["org:12"] = "Centro de coste";
            Assert.AreEqual("Centro de coste is " + GreekDepartment, FilterWording.Describe(custom, PortalLanguage.English));
            Assert.AreEqual(FilterWording.DimensionLabel("org:12", "Centro de coste", PortalLanguage.Spanish), "Centro de coste");
            Assert.AreEqual(userFilter.Text("userFilter.dimension.department"), FilterWording.DimensionLabel(UserFilterDimensions.Department, null, PortalLanguage.Spanish));

            // From the reader's side: a condition they have no value for matches nobody, and withheld names are counted.
            var global = new GlobalFilterEcho
            {
                Clauses =
                {
                    new GlobalFilterClauseModel { Join = "and", Dimension = UserFilterDimensions.Department, Operator = "is", ViewerAttribute = UserFilterDimensions.Manager, Unresolved = true },
                    new GlobalFilterClauseModel { Join = "and", Dimension = UserFilterDimensions.Manager, Operator = "is", HiddenValues = 2 },
                },
            };
            var globalText = PortalCatalog.For("globalFilter", PortalLanguage.Spanish);
            Assert.AreEqual(
                globalText.Text("globalFilter.reader.unresolved.manager", ("dimension", "Departamento")) + " y "
                + "Responsable es " + globalText.Text("globalFilter.reader.hiddenPeople.other", ("count", "2")),
                FilterWording.Describe(global, PortalLanguage.Spanish));
            Assert.AreEqual(string.Empty, FilterWording.Describe((UserFilterEcho)null, PortalLanguage.Spanish));
        }

        [TestMethod]
        public void AReclaimAction_WithoutAModelledReturn_QuotesNoReturn()
        {
            var people = new List<CopilotInvestmentPerson>
            {
                CopilotInvestmentCalculatorTests.Holder(1, 10),
                CopilotInvestmentCalculatorTests.Holder(2, 0, band: "Dormant", interactions: 0),
            };
            var summary = CopilotInvestmentCalculator.Calculate(CopilotInvestmentCalculatorTests.Input(people));
            Assert.IsNull(summary.Actions.Single(a => a.Code == CopilotInvestmentActionCodes.ReclaimIdle).RoiBaseAfter, "No prices, so no return.");
            foreach (var language in new[] { PortalLanguage.English, PortalLanguage.Spanish })
            {
                var text = PortalCatalog.For(Module, language);
                CollectionAssert.Contains(Texts(Sheets(CopilotInvestmentWorkbook.Build(summary, true, "test", language))),
                    text.Text("copilotInvestment.workbook.effect.freesSeatCost") + " " + text.Text("copilotInvestment.workbook.action.reclaimIdle"),
                    language + ": the action, with no \"-x\" return.");
            }
        }

        [TestMethod]
        public void AWorkbookThatCannotBeWritten_SaysWhyInTheReadersLanguage()
        {
            var es = PortalCatalog.For(Module, PortalLanguage.Spanish);
            var notReady = CopilotInvestmentAPIController.WorkbookError(HttpStatusCode.ServiceUnavailable, "notReady", PortalLanguage.Spanish);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
            Assert.AreEqual("text/plain", notReady.Content.Headers.ContentType.MediaType);
            Assert.AreEqual(es.Text("copilotInvestment.workbook.error.notReady"), notReady.Content.ReadAsStringAsync().Result);

            var broken = CopilotInvestmentAPIController.WhatIfWorkbookError(new CopilotInvestmentAssumptionsRejectedException(
                CopilotInvestmentErrorCodes.ScenarioInvalid, new[] { "seatPriceOutOfRange", "minutesOrder" }), PortalLanguage.Spanish);
            Assert.AreEqual(HttpStatusCode.BadRequest, broken.StatusCode);
            Assert.AreEqual(es.Text("copilotInvestment.workbook.error.whatIf", ("errors",
                    es.Text("copilotInvestment.settings.error.seatPriceOutOfRange") + " " + es.Text("copilotInvestment.settings.error.minutesOrder"))),
                broken.Content.ReadAsStringAsync().Result);

            // A figure that could not be read names a field, not a rule: the general message, never the field's code.
            var unreadable = CopilotInvestmentAPIController.WhatIfWorkbookError(new CopilotInvestmentAssumptionsRejectedException(
                CopilotInvestmentErrorCodes.ScenarioInvalid, new[] { "seatPrice" }), PortalLanguage.Spanish).Content.ReadAsStringAsync().Result;
            Assert.AreEqual(es.Text("copilotInvestment.workbook.error.whatIf", ("errors", es.Text("copilotInvestment.settings.error.scenarioInvalid"))), unreadable);

            // A link from before the language parameter reads in English.
            Assert.AreEqual(PortalCatalog.For(Module, PortalLanguage.English).Text("copilotInvestment.workbook.error.failed"),
                CopilotInvestmentAPIController.WorkbookError(HttpStatusCode.InternalServerError, "failed", PortalLanguages.Parse(null)).Content.ReadAsStringAsync().Result);
        }

        [TestMethod]
        public void OnlyTheLanguagesThePortalShips_AreRecognised_AndAnythingElseIsEnglish()
        {
            foreach (var code in new[] { "es", "ES", "es-ES", "es-MX", "es_ES", " es " })
                Assert.AreEqual(PortalLanguage.Spanish, PortalLanguages.Parse(code), code);
            foreach (var code in new[] { null, string.Empty, " ", "en", "en-GB", "fr", "de-DE", "espa\u00f1ol", "e", "../es", "es;q=0.9" })
                Assert.AreEqual(PortalLanguage.English, PortalLanguages.Parse(code), code ?? "(null)");
            Assert.AreEqual("es-ES", PortalLanguages.Culture(PortalLanguage.Spanish).Name);
            Assert.AreEqual("en-GB", PortalLanguages.Culture(PortalLanguage.English).Name);
        }

        [TestMethod]
        public void EveryEmbeddedCatalog_ReadsEveryEntry_WithTheSameKeysAndPlaceholdersInBothLanguages()
        {
            var resources = typeof(PortalCatalog).Assembly.GetManifestResourceNames();
            var entryLine = new Regex(@"^\s*'[A-Za-z0-9_.\-]+':", RegexOptions.Multiline);
            foreach (var module in new[] { Module, "userFilter", "globalFilter", "common" })
            {
                var en = PortalCatalog.For(module, PortalLanguage.English);
                var es = PortalCatalog.For(module, PortalLanguage.Spanish);
                foreach (var language in new[] { PortalLanguage.English, PortalLanguage.Spanish })
                {
                    var name = PortalCatalog.ResourceName(module, language);
                    CollectionAssert.Contains(resources, name);
                    using (var reader = new StreamReader(typeof(PortalCatalog).Assembly.GetManifestResourceStream(name), Encoding.UTF8))
                        Assert.AreEqual(entryLine.Matches(reader.ReadToEnd()).Count, PortalCatalog.For(module, language).Entries.Count, name + ": every entry is read.");
                }

                Assert.IsTrue(en.Entries.Count >= 20, module);
                Assert.IsTrue(en.Entries.Keys.All(k => k.StartsWith(module + ".", StringComparison.Ordinal)), module);
                CollectionAssert.AreEquivalent(en.Entries.Keys.ToList(), es.Entries.Keys.ToList(), module);
                foreach (var entry in en.Entries)
                    CollectionAssert.AreEqual(PortalCatalog.PlaceholdersOf(entry.Value).ToList(), PortalCatalog.PlaceholdersOf(es.Entries[entry.Key]).ToList(), entry.Key);
            }
            // Escapes and non-ASCII text are read as the browser reads them.
            StringAssert.Contains(PortalCatalog.For(Module, PortalLanguage.English).Text("copilotInvestment.workbook.exec.filterAdmin"), "Administrator\u2019s");
            StringAssert.Contains(PortalCatalog.For(Module, PortalLanguage.Spanish).Text("copilotInvestment.workbook.sheet.path"), "concentraci\u00f3n");
        }

        [TestMethod]
        public void TheCatalogReader_AcceptsOnlyTheFormatTheCatalogsAreWrittenIn()
        {
            var parsed = PortalCatalog.Parse(string.Join("\n",
                "/** A comment that mentions const x = { */",
                "export const sample = {",
                "  // a section comment",
                "  'sample.one': 'One',",
                "  'sample.two':",
                "    'It\\u2019s \\'quoted\\' \\u{1F600}\\n', // trailing comment",
                "  /* block",
                "     comment */",
                "  'sample.three': '\u0391\u03b8\u03ae\u03bd\u03b1 {count}',",
                "} as const;",
                "",
                "export default sample;"));
            Assert.AreEqual(3, parsed.Count);
            Assert.AreEqual("One", parsed["sample.one"]);
            Assert.AreEqual("It\u2019s 'quoted' \U0001F600\n", parsed["sample.two"]);
            Assert.AreEqual("\u0391\u03b8\u03ae\u03bd\u03b1 {count}", parsed["sample.three"]);

            var typed = PortalCatalog.Parse("const es: Record<keyof typeof en, string> = {\r\n  'sample.one': 'Uno',\r\n};\r\n");
            Assert.AreEqual("Uno", typed["sample.one"]);

            foreach (var broken in new[]
            {
                "const x = {\n  'a.b': 'one' + 'two',\n};",
                "const x = {\n  'a.b': \"double quoted\",\n};",
                "const x = {\n  'a.b': `template ${x}`,\n};",
                "const x = {\n  a: 'unquoted key',\n};",
                "const x = {\n  'a.b': 'one',\n  'a.b': 'twice',\n};",
                "const x = {\n  'a.b': 'unterminated,\n};",
                "const x = {\n  'a.b': 'one',\n",
                "'a.b': 'outside any object',",
            })
                Assert.ThrowsException<FormatException>(() => PortalCatalog.Parse(broken), broken);
        }

        [TestMethod]
        public void EveryCodeTheWorkbookLabels_HasWordsInBothLanguages()
        {
            var families = new Dictionary<string, IEnumerable<string>>
            {
                [CopilotInvestmentWorkbook.Words.Tone] = new[] { "good", "neutral", "warning", "critical", "opportunity" },
                [CopilotInvestmentWorkbook.Words.Effect] = Constants(typeof(CopilotInvestmentActionEffects)).Where(e => e != CopilotInvestmentActionEffects.None),
                [CopilotInvestmentWorkbook.Words.SeatBasis] = new[] { CopilotInvestmentCalculator.SeatBases.LicenceHistory, CopilotInvestmentCalculator.SeatBases.CurrentAssignments },
                [CopilotInvestmentWorkbook.Words.SignalSource] = new[] { AdoptionScoring.SignalSourceAudit, AdoptionScoring.SignalSourceUsageReport },
                [CopilotInvestmentWorkbook.Words.Reclaim] = Constants(typeof(AdoptionScoring.ReclaimEligibilityTiers)),
                [CopilotInvestmentWorkbook.Words.PeriodStatus] = new[] { "verified", "unverified", "mixed" },
                [CopilotInvestmentWorkbook.Words.Flag] = Constants(typeof(CopilotInvestmentFlags)),
                [CopilotInvestmentWorkbook.Words.Signal] = Constants(typeof(CopilotInvestmentSignals)),
                [CopilotInvestmentWorkbook.Words.TrustArea] = Constants(typeof(CopilotInvestmentTrustAreas)),
                [CopilotInvestmentWorkbook.Words.TrustReason] = Constants(typeof(CopilotInvestmentTrustCodes)),
            };
            var english = new CopilotInvestmentWorkbook.Words(PortalLanguage.English);
            var spanish = new CopilotInvestmentWorkbook.Words(PortalLanguage.Spanish);
            foreach (var family in families)
            {
                var codes = family.Value.ToList();
                Assert.IsTrue(codes.Count > 0, family.Key);
                foreach (var code in codes)
                {
                    Assert.IsTrue(english.Has(family.Key + code), family.Key + code);
                    Assert.AreNotEqual(english.Label(family.Key, code), spanish.Label(family.Key, code), family.Key + code + " is translated.");
                }
            }
        }

        private static IEnumerable<string> Constants(Type type) =>
            type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue());

        /// <summary>A catalog text up to its first placeholder: what every sentence built from it starts with.</summary>
        private static string Prefix(PortalCatalog catalog, string key)
        {
            var text = catalog.Text(key);
            var brace = text.IndexOf('{');
            return brace < 0 ? text : text.Substring(0, brace);
        }

        private static bool IsNumber(string text) =>
            !string.IsNullOrWhiteSpace(text)
            && (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)
                || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.GetCultureInfo("es-ES"), out _));

        private sealed class Cell
        {
            public string Ref;
            public string Type;
            public string Style;
            public string Text;
            public string Value;
            public bool Formula;
        }

        private static List<string> Texts(IEnumerable<(string Name, List<Cell> Cells)> sheets) =>
            sheets.SelectMany(s => s.Cells).Where(c => c.Text != null).Select(c => c.Text).ToList();

        private static string Flatten(IEnumerable<(string Name, List<Cell> Cells)> sheets) =>
            string.Join("\n", sheets.SelectMany(s => s.Cells.Select(c => string.Join("|", s.Name, c.Ref, c.Type, c.Style, c.Text, c.Value))));

        /// <summary>Every sheet's cells, in workbook order.</summary>
        private static List<(string Name, List<Cell> Cells)> Sheets(byte[] bytes)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheets = new List<(string Name, List<Cell> Cells)>();
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> names;
                using (var workbook = zip.GetEntry("xl/workbook.xml").Open())
                    names = XDocument.Load(workbook).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
                for (var i = 0; i < names.Count; i++)
                    using (var part = zip.GetEntry("xl/worksheets/sheet" + (i + 1) + ".xml").Open())
                        sheets.Add((names[i], XDocument.Load(part).Descendants(ns + "c").Select(c => new Cell
                        {
                            Ref = (string)c.Attribute("r"),
                            Type = (string)c.Attribute("t"),
                            Style = (string)c.Attribute("s"),
                            Text = c.Element(ns + "is")?.Element(ns + "t")?.Value,
                            Value = c.Element(ns + "v")?.Value,
                            Formula = c.Element(ns + "f") != null,
                        }).ToList()));
            }
            return sheets;
        }
    }
}
