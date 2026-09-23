using Common.Entities.Copilot;
using Common.Entities.CopilotAdoption;
using Common.Entities.Xlsx;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the Excel export of the Copilot adoption report.
    ///
    /// The workbook is written by hand as OpenXML rather than through a library, so "it compiles" says
    /// nothing about whether Excel will open it. These tests do what a person would otherwise have to
    /// do by hand every time: generate a real workbook from synthetic data, unzip it, and prove that
    /// every part is well-formed XML and every relationship resolves. A malformed part shows up to a
    /// customer as "Excel found unreadable content", with no clue which part is at fault.
    ///
    /// The export exists to be a point-in-time snapshot that can be compared with another taken months
    /// later, so the run metadata and the thresholds used are asserted too - a snapshot that does not
    /// record the rules it was scored by cannot honestly be diffed against anything.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionWorkbookTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Deliberately includes Greek and an ampersand. SharePoint file names, user display names and
        /// department names routinely contain both, and either one written unescaped produces a file
        /// that Excel refuses to open.
        /// </summary>
        private const string GreekDepartment = "Καλημέρα κόσμε";
        private const string AmpersandDepartment = "Fish & Chips <Ltd>";

        #region Package validity

        [TestMethod]
        public void Workbook_IsAValidPackageWithWellFormedParts()
        {
            var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());
            Assert.IsTrue(bytes.Length > 0, "The workbook must not be empty.");

            var malformed = new List<string>();

            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();

                CollectionAssert.Contains(names, "[Content_Types].xml");
                CollectionAssert.Contains(names, "_rels/.rels");
                CollectionAssert.Contains(names, "xl/workbook.xml");
                CollectionAssert.Contains(names, "xl/styles.xml");

                Assert.IsTrue(names.Any(n => n.StartsWith("xl/worksheets/", StringComparison.Ordinal)),
                    "The workbook must contain at least one worksheet part.");
                Assert.IsTrue(names.Any(n => n.StartsWith("xl/charts/", StringComparison.Ordinal)),
                    "The whole point of this export is that the charts come with it.");

                // Every worksheet references workbookViewId="0", which must resolve against a declared
                // workbook view or the document is not conformant.
                using (var reader = new StreamReader(zip.GetEntry("xl/workbook.xml").Open(), Encoding.UTF8))
                {
                    StringAssert.Contains(reader.ReadToEnd(), "<workbookView",
                        "workbook.xml must declare a workbook view for the sheets' workbookViewId to reference.");
                }

                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                        && !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        using (var part = entry.Open())
                        {
                            XDocument.Load(part);
                        }
                    }
                    catch (Exception ex)
                    {
                        malformed.Add($"{entry.FullName}: {ex.Message}");
                    }
                }
            }

            Assert.AreEqual(0, malformed.Count,
                "Every part must be well-formed XML, or Excel reports unreadable content: "
                + string.Join("; ", malformed));
        }

        [TestMethod]
        public void Workbook_HasNoBrokenRelationships()
        {
            // A relationship pointing at a part that is not in the package is the single most common
            // way a hand-built xlsx triggers Excel's repair prompt, and it is invisible to an XML
            // well-formedness check.
            var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());
            var broken = new List<string>();

            using (var stream = new MemoryStream(bytes))
            using (var package = Package.Open(stream, FileMode.Open, FileAccess.Read))
            {
                foreach (var relationship in package.GetRelationships())
                {
                    var target = PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), relationship.TargetUri);
                    if (!package.PartExists(target)) broken.Add($"(root) -> {target}");
                }

                foreach (var part in package.GetParts())
                {
                    // Relationship parts cannot themselves own relationships, and asking throws.
                    if (PackUriHelper.IsRelationshipPartUri(part.Uri)) continue;

                    foreach (var relationship in part.GetRelationships())
                    {
                        if (relationship.TargetMode != TargetMode.Internal) continue;

                        var target = PackUriHelper.ResolvePartUri(part.Uri, relationship.TargetUri);
                        if (!package.PartExists(target)) broken.Add($"{part.Uri} -> {target}");
                    }
                }
            }

            Assert.AreEqual(0, broken.Count,
                "Every internal relationship must resolve: " + string.Join("; ", broken));
        }

        [TestMethod]
        public void Workbook_ChartsAreLiveAndBoundToCells()
        {
            // Charts as pictures would defeat the purpose: the reader is expected to open two snapshots
            // and re-plot them side by side, which needs the underlying ranges to still be there.
            var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());

            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var charts = zip.Entries.Where(e => e.FullName.StartsWith("xl/charts/chart", StringComparison.Ordinal)).ToList();
                Assert.IsTrue(charts.Count >= 4, $"Expected several charts, found {charts.Count}.");

                Assert.IsFalse(
                    zip.Entries.Any(e => e.FullName.StartsWith("xl/media/", StringComparison.Ordinal)),
                    "Charts must be native Excel charts, not embedded images.");

                foreach (var chart in charts)
                {
                    using (var reader = new StreamReader(chart.Open(), Encoding.UTF8))
                    {
                        var xml = reader.ReadToEnd();
                        StringAssert.Contains(xml, "!$",
                            $"{chart.FullName} does not reference a sheet range, so its data is not live.");
                    }
                }
            }
        }

        [TestMethod]
        public void Workbook_UsesLineChartForVolumeTrendWhenCoverageHasGaps()
        {
            var analysis = SyntheticAnalysis();
            analysis.Summary.WeeklyVolumeTrend.Single().Points[3].Value = null;

            var volumeChart = ChartXmls(CopilotAdoptionWorkbook.Build(analysis))
                .Single(xml => xml.Contains("Weekly Copilot volume"));

            StringAssert.Contains(volumeChart, "<c:lineChart>",
                "A stacked area collapses blank cells to the baseline; the workbook must use a line chart when gaps exist.");
            Assert.IsFalse(volumeChart.Contains("<c:areaChart>"),
                "The gapped volume trend must not be emitted as any area chart.");
        }

        #endregion

        #region Content

        [TestMethod]
        public void Workbook_RecordsTheRulesItWasScoredBy()
        {
            // A snapshot that does not carry its own thresholds cannot honestly be compared with
            // another one: the tuning is adjustable, so "adoption went up" could mean "the bar moved".
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            StringAssert.Contains(text, "Champion at");
            StringAssert.Contains(text, "Established at");
            StringAssert.Contains(text, "Period covered");
            StringAssert.Contains(text, "Generated");
            StringAssert.Contains(text, "Licence recommendation at");
            StringAssert.Contains(text, "Agent retire after");
            StringAssert.Contains(text, "Microsoft guidance catalogue");
            StringAssert.Contains(text, CopilotAdoptionGuidanceCatalogue.Version);
            StringAssert.Contains(text, "https://aka.ms/ScenarioLibrary");
            StringAssert.Contains(text, "https://aka.ms/Copilot/ImplementationSummaryGuide");
        }

        [TestMethod]
        public void Workbook_CarriesEveryMajorSectionOfTheReport()
        {
            var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());
            var sheetNames = WorkbookSheetNames(bytes);

            foreach (var expected in new[]
            {
                "Report", "Headline figures", "Adoption funnel", "Engagement", "Weekly trend",
                "Departments and apps", "Agents", "Unlicensed usage", "Enablement plan",
                "Licensed users", "Licence opportunities", "How this is calculated",
                "Snapshot facts", "Settings",
            })
            {
                CollectionAssert.Contains(sheetNames, expected,
                    $"The workbook is meant to be the whole report; '{expected}' is missing.");
            }
        }

        #endregion

        #region Snapshot comparability

        /// <summary>
        /// The build that produced the file has to be on the file.
        ///
        /// Two snapshots exist to be subtracted. A figure can move because adoption moved or because the
        /// product changed how it counts, and without the build on the file those two are
        /// indistinguishable - which is how an enablement programme ends up credited with a bug fix.
        /// </summary>
        [TestMethod]
        public void Workbook_RecordsTheBuildItWasGeneratedBy()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            StringAssert.Contains(text, "Product build",
                "The Report sheet must name the build that produced the snapshot.");
            StringAssert.Contains(text, Common.Entities.BuildConstants.BuildLabel,
                "The build label itself must be written, not just its heading.");
        }

        /// <summary>
        /// Comparison lives in the files, so two exports taken on different dates have to line up
        /// row for row on the Snapshot facts sheet - same keys, same order, nothing conditional.
        ///
        /// This is the test that replaces the in-product period comparison. There is no stored
        /// history any more: an admin who wants to know what moved exports the workbook twice and
        /// diffs the two files by key. That only works if the key list is a function of the MODEL and
        /// not of the DATA - the moment a row appears only when a figure is non-null, the two files
        /// stop aligning and every lookup written against them silently returns the wrong row.
        /// </summary>
        [TestMethod]
        public void Workbook_TwoSnapshotsFromDifferentDatesDiffByKeyLookup()
        {
            // Deliberately different in date, population and - critically - in which figures are
            // measurable at all. The later snapshot has no Cowork retention figure and no usage
            // report, which is exactly the case that would tempt a writer into omitting rows.
            var earlier = SyntheticAnalysis();

            var later = SyntheticAnalysis();
            later.Summary.GeneratedUtc = Now.AddDays(90);
            later.Summary.FromUtc = Now.AddDays(90 - later.Summary.WindowDays);
            later.Summary.ToUtc = Now.AddDays(90);
            later.Summary.CoworkReportRetentionPct = null;
            later.Summary.DataSources.CopilotUsageReportAvailable = false;
            later.Summary.ScoredUsers = earlier.Summary.ScoredUsers + 25;
            later.Summary.AdoptionRatePct = earlier.Summary.AdoptionRatePct + 6.5;

            // Run diagnostics are the one part of the file whose keys legitimately vary with the run -
            // a step only appears if it reported. They live on their own sheet for exactly this reason,
            // and this is the case that proves they stay off Snapshot facts: two runs that timed
            // different steps must still produce identical Snapshot fact keys.
            earlier.Summary.Diagnostics = null;
            later.Summary.Diagnostics = new CopilotAdoptionDiagnostics
            {
                TotalMs = 1234,
                Steps = new List<CopilotAdoptionStepTiming>
                {
                    new CopilotAdoptionStepTiming { Step = CopilotAdoptionSteps.LicensedUsers, DurationMs = 900 },
                    new CopilotAdoptionStepTiming { Step = CopilotAdoptionSteps.CoworkReadiness, DurationMs = 334, Failed = true },
                },
            };

            var earlierFacts = SnapshotFactKeys(CopilotAdoptionWorkbook.Build(earlier));
            var laterFacts = SnapshotFactKeys(CopilotAdoptionWorkbook.Build(later));

            CollectionAssert.AreEqual(earlierFacts, laterFacts,
                "Two exports must carry the same Snapshot fact keys in the same order, whatever the data "
                + "says. A key that appears in one file and not the other cannot be diffed by lookup.");

            // The WHOLE key column, not just the cells that happen to match a reflected summary
            // property. Filtering by the expected key list is exactly the blind spot that let the run
            // diagnostics sit on this sheet emitting run-dependent rows: they were discarded by the
            // filter, so the assertion above could never have seen them. Any row anywhere on the sheet
            // that appears in one export and not the other shifts every row below it.
            var earlierColumnA = SheetKeyColumn(CopilotAdoptionWorkbook.Build(earlier));
            var laterColumnA = SheetKeyColumn(CopilotAdoptionWorkbook.Build(later));

            CollectionAssert.AreEqual(earlierColumnA, laterColumnA,
                "Every row of the Snapshot facts sheet must be identical between two exports, not just the "
                + "reflected metric rows. Run diagnostics were the original offender - their keys depend on "
                + "which steps reported, so they live on their own sheet now.");

            Assert.IsFalse(earlierColumnA.Concat(laterColumnA).Any(c => c.StartsWith("diagnostics", StringComparison.Ordinal)),
                "Run diagnostics are run-dependent and belong on their own sheet, not on the sheet built to be diffed.");

            // They must still be IN the file - moving them off Snapshot facts must not lose them.
            var diagnosticCells = SheetCells(CopilotAdoptionWorkbook.Build(later), "Run diagnostics");
            CollectionAssert.Contains(diagnosticCells, "diagnostics.step." + CopilotAdoptionSteps.CoworkReadiness,
                "The run diagnostics must still be exported, just not on the sheet built for diffing.");

            // A null list must still produce its .count row, blank rather than absent. An absent row is
            // the same row-shift bug in a different disguise.
            var nulledCollections = SyntheticAnalysis();
            nulledCollections.Summary.Warnings = null;
            CollectionAssert.AreEqual(
                earlierColumnA,
                SheetKeyColumn(CopilotAdoptionWorkbook.Build(nulledCollections)),
                "A null collection must still write its '.count' key with a blank value. Dropping the row "
                + "instead shifts every row below it and misaligns the diff.");

            // And the row order is the one a diff can rely on, not reflection order.
            CollectionAssert.AreEqual(
                earlierFacts.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                earlierFacts,
                "Snapshot fact keys must be ordinally sorted so the two files align row for row.");

            // The comparison must be answerable from the files alone. Nothing on either sheet may
            // point at server-side state that no longer exists.
            var earlierText = SheetText(CopilotAdoptionWorkbook.Build(earlier));
            foreach (var goneFromTheProduct in new[] { "Period movement", "Closed-period movement", "Cohort transitions" })
            {
                Assert.IsFalse(earlierText.Contains(goneFromTheProduct),
                    $"'{goneFromTheProduct}' describes stored period history, which the product no longer keeps.");
            }
        }

        /// <summary>
        /// Both files must agree on the rules and the build, and the workbook has to say so - the
        /// reader doing the diff has no product UI to consult.
        /// </summary>
        [TestMethod]
        public void Workbook_ExplainsHowToCompareTwoExports()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            StringAssert.Contains(text, "Comparing two exports",
                "The method sheet must tell the reader how a comparison is done now that the product does not do one.");
            StringAssert.Contains(text, "Snapshot facts",
                "It must name the sheet to diff.");
            StringAssert.Contains(text, "Settings",
                "It must say the settings have to match for the comparison to be fair.");
            StringAssert.Contains(text, "Product build",
                "It must say the build has to match for the comparison to be fair.");
        }

        /// <summary>
        /// Every scalar measure in the analysis must reach the Snapshot facts sheet.
        ///
        /// Reflected rather than listed, because a hand-written expectation is precisely what failed
        /// before: twenty-four measures - among them every Cowork usage-report metric - existed in the
        /// summary for months without ever reaching the workbook, and no test noticed because no test
        /// knew they existed. This one cannot be out of date.
        /// </summary>
        [TestMethod]
        public void Workbook_SnapshotFactsCarryEveryScalarSummaryMetric()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            var missing = ExpectedFactKeys(typeof(CopilotAdoptionSummary))
                .Where(key => text.IndexOf(key, StringComparison.Ordinal) < 0)
                .ToList();

            Assert.AreEqual(0, missing.Count,
                "Every scalar figure must reach the Snapshot facts sheet so two snapshots can be diffed. "
                + "Missing: " + string.Join(", ", missing));
        }

        /// <summary>
        /// Every tuning option must reach the Settings sheet, for the same reason: "were both files
        /// scored by the same rules?" has to be answerable by lookup, not by reading prose.
        /// </summary>
        [TestMethod]
        public void Workbook_SettingsSheetCarriesEveryOption()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            var missing = ExpectedFactKeys(typeof(CopilotAdoptionOptions))
                .Where(key => text.IndexOf(key, StringComparison.Ordinal) < 0)
                .ToList();

            Assert.AreEqual(0, missing.Count,
                "Every option must reach the Settings sheet. Missing: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The facts sheet is sorted and unconditional so a lookup against the other snapshot always
        /// resolves. A run that emitted keys in reflection order would shift rows between files and
        /// quietly break every formula written against it.
        /// </summary>
        [TestMethod]
        public void Workbook_SnapshotFactKeysAreSortedAndUnique()
        {
            var keys = SheetCells(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()), "Snapshot facts")
                .Where(c => ExpectedFactKeys(typeof(CopilotAdoptionSummary)).Contains(c))
                .ToList();

            CollectionAssert.AllItemsAreUnique(keys, "A key written twice makes a lookup ambiguous.");
            CollectionAssert.AreEqual(
                keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                keys,
                "Snapshot fact keys must be in a stable sort order, or two files will not line up.");
        }

        /// <summary>
        /// A null must never be written as a zero. Across this report a null means "not reported" or
        /// "not attributable", and in a file built for comparison a zero reads as a measured decline.
        /// </summary>
        [TestMethod]
        public void Workbook_SnapshotFactsLeaveUnknownValuesEmptyRatherThanZero()
        {
            var analysis = SyntheticAnalysis();
            analysis.Summary.CoworkReportRetentionPct = null;

            var cells = SheetCells(CopilotAdoptionWorkbook.Build(analysis), "Snapshot facts");
            var index = cells.IndexOf("coworkReportRetentionPct");

            Assert.AreNotEqual(-1, index, "The key is missing from the Snapshot facts sheet.");
            Assert.AreNotEqual("0", cells.ElementAtOrDefault(index + 1),
                "An unreported measure must be blank, never zero - zero would read as a measured fall to nothing.");
        }

        #endregion

        #region Per-user sheet parity

        /// <summary>
        /// Every column of the CSV export must also be in the corresponding workbook sheet.
        ///
        /// The workbook used to declare its own headers while the CSV declared its own, and the two
        /// drifted: the Licensed users sheet ended up missing twenty-eight columns the CSV already had,
        /// including the reclaim eligibility that a headline figure on the first sheet is counted from.
        /// The email-domain change is the clean example - it added a column to all three CSVs and to
        /// none of the sheets, and nothing failed. Both now come from one definition, and this test is
        /// what keeps it that way.
        /// </summary>
        [TestMethod]
        public void Workbook_PerUserSheetsCarryEveryCsvColumn()
        {
            var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());

            AssertSheetHasColumns(bytes, "Licensed users",
                CopilotAdoptionExports.LicensedUserColumns().Select(c => c.Header));
            AssertSheetHasColumns(bytes, "Licence opportunities",
                CopilotAdoptionExports.LicenceOpportunityColumns().Select(c => c.Header));
            AssertSheetHasColumns(bytes, "Cowork readiness",
                CopilotAdoptionExports.CoworkReadinessColumns().Select(c => c.Header));
        }

        /// <summary>
        /// The governance columns specifically. These are the ones an admin exports the workbook for -
        /// the reclaim verdict per person, and who excluded a seat from reclaim - and they were the
        /// most damaging of the twenty-eight omissions, because the Report sheet counts them in a
        /// headline while the per-user sheet could not say which people they were.
        /// </summary>
        [TestMethod]
        public void Workbook_LicensedUsersCarryTheReclaimVerdictPerPerson()
        {
            var cells = SheetCells(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()), "Licensed users");

            foreach (var expected in new[]
            {
                "Reclaim eligibility", "Reclaim eligibility reason", "Reclaim excluded by",
                "Email domain", "Country", "Copilot licences", "Too new to judge",
                "Frequency score", "Depth score", "Breadth score",
            })
            {
                CollectionAssert.Contains(cells, expected,
                    $"'{expected}' is counted in a headline figure but absent from the per-user sheet.");
            }
        }

        /// <summary>
        /// Dates must stay dates. Written as text they sort alphabetically under the auto-filter, which
        /// puts 2026-01 after 2025-12 but also puts every "-" placeholder in the middle of the range.
        /// </summary>
        [TestMethod]
        public void Workbook_PerUserDatesAreWrittenAsDatesNotText()
        {
            var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());

            var dateStyled = SheetCellElements(bytes, "Licensed users")
                .Any(c => (string)c.Attribute("t") == null && (string)c.Attribute("s") != null);

            Assert.IsTrue(dateStyled,
                "At least one cell on the Licensed users sheet must be a styled numeric (a date), "
                + "not a string - otherwise the sheet cannot be sorted chronologically.");
        }

        #endregion

        #region Row cap

        /// <summary>
        /// The row cap comes from options rather than from a constant, so a tenant that wants its whole
        /// population in one file can have it without a rebuild. Asserted in both directions: the cap
        /// must bite, and it must say that it did.
        /// </summary>
        [TestMethod]
        public void Workbook_RespectsAConfiguredRowCap()
        {
            var analysis = SyntheticAnalysis();
            analysis.Summary.Options.MaxWorkbookUserRows = 2;

            var text = SheetText(CopilotAdoptionWorkbook.Build(analysis));
            Assert.IsTrue(analysis.LicensedUsers.Count > 2, "The fixture must have more rows than the cap to prove anything.");
            StringAssert.Contains(text, "Licensed users - TRUNCATED",
                "A sheet that stops at a cap must say so where it cannot be missed.");

            var cells = SheetCells(CopilotAdoptionWorkbook.Build(analysis), "Licensed users");
            var listed = analysis.LicensedUsers.Count(u => cells.Contains(u.UserPrincipalName));
            Assert.AreEqual(2, listed, "The configured cap must actually limit the rows written.");
        }

        /// <summary>
        /// A cap of zero means "unset", not "write nothing". A blank or mistyped configuration value
        /// must not silently produce an empty sheet in a file someone is about to quote from.
        /// </summary>
        [TestMethod]
        public void Workbook_TreatsAnUnsetRowCapAsTheDefault()
        {
            var analysis = SyntheticAnalysis();
            analysis.Summary.Options.MaxWorkbookUserRows = 0;

            var cells = SheetCells(CopilotAdoptionWorkbook.Build(analysis), "Licensed users");
            var listed = analysis.LicensedUsers.Count(u => cells.Contains(u.UserPrincipalName));

            Assert.AreEqual(analysis.LicensedUsers.Count, listed,
                "An unset cap must fall back to the default, not write an empty sheet.");
        }

        #endregion

        #region Presentation

        [TestMethod]
        public void Workbook_SurvivesGreekAndAmpersandsInTenantText()
        {
            // Department names, user names and file titles come from the customer's tenant. Greek is
            // the canonical non-Latin case in this codebase; an unescaped ampersand is the canonical
            // way to produce a file Excel refuses to open.
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            StringAssert.Contains(text, GreekDepartment,
                "Non-Latin department names must survive the export verbatim.");
            StringAssert.Contains(text, AmpersandDepartment,
                "An ampersand and angle brackets must be escaped on write and decode back to the original.");
        }

        [TestMethod]
        public void Workbook_ComparesEmailDomainsOnceThereIsMoreThanOne()
        {
            // On the single-domain tenant the other tests use, this sheet is deliberately absent - a
            // table comparing an organisation with itself is noise in a board pack.
            CollectionAssert.DoesNotContain(
                WorkbookSheetNames(CopilotAdoptionWorkbook.Build(SyntheticAnalysis())),
                "Email domains");

            var multiDomain = SyntheticAnalysis();
            for (var i = 0; i < 20; i++)
            {
                multiDomain.LicensedUsers[i].UserPrincipalName = "user" + i + "@fabrikam.example";
                multiDomain.LicensedUsers[i].EmailDomain = "fabrikam.example";
            }

            new CopilotAdoptionService(multiDomain.Summary.Options).FinaliseSummary(multiDomain);
            var bytes = CopilotAdoptionWorkbook.Build(multiDomain);

            CollectionAssert.Contains(WorkbookSheetNames(bytes), "Email domains");

            var text = SheetText(bytes);
            StringAssert.Contains(text, "Adoption by email domain");
            StringAssert.Contains(text, "fabrikam.example");
            StringAssert.Contains(text, "contoso.com");
            StringAssert.Contains(text, "Licence candidates");
        }

        [TestMethod]
        public void Workbook_SaysWhenItHasBeenNarrowedToOneEmailDomain()
        {
            // A spreadsheet outlives the screen it came from and gets forwarded without that context.
            // A file narrowed to one of several organisations must say so on its own first sheet, or
            // it gets quoted in a licence negotiation as the whole tenant's position.
            var analysis = SyntheticAnalysis();
            var service = new CopilotAdoptionService(analysis.Summary.Options);
            service.FinaliseSummary(analysis);

            var tenantText = SheetText(CopilotAdoptionWorkbook.Build(analysis));
            StringAssert.Contains(tenantText, "Whole tenant");
            Assert.IsFalse(tenantText.Contains("NARROWED TO ONE EMAIL DOMAIN"),
                "An unnarrowed workbook must not carry a scope warning.");

            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis,
                CopilotAdoptionScope.ForEmailDomain("contoso.com"),
                service.FinaliseSummary);

            var scopedText = SheetText(CopilotAdoptionWorkbook.Build(scoped));

            StringAssert.Contains(scopedText, "NARROWED TO ONE EMAIL DOMAIN: contoso.com");
            StringAssert.Contains(scopedText, "must not be quoted as a "
                + "tenant-wide figure");
            StringAssert.Contains(scopedText, "the agent inventory",
                "The sections that stayed tenant-wide have to be named, not merely counted.");
            StringAssert.Contains(scopedText, "Email domain contoso.com");
        }

        [TestMethod]
        public void Workbook_IncludesTheAccountabilityRollup()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            StringAssert.Contains(text, "Accountability roll-up");
            StringAssert.Contains(text, "Action opportunity");
            StringAssert.Contains(text, "manager@contoso.com");
        }

        [TestMethod]
        public void Workbook_SaysWhatEachResourceTypeValueActuallyDescribes()
        {
            // Issue #468: the workbook used to introduce these rows as "what Copilot grounded its
            // answers in", which is false for CITATION - the value that is usually the biggest row.
            // The exported sheet has to carry the same classification the portal shows, or the two
            // deliverables tell an admin different things about the same numbers.
            //
            // The expected labels are written out literally rather than read back from KindLabel:
            // deriving them from the same method production uses would make this test pass even if
            // the two kinds' labels were swapped.
            var lines = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()))
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();

            // Scope the search to the resource-type table. Searching the whole flattened workbook
            // would still pass today, but would silently start checking the wrong cell the moment
            // another table gained a "Kind" header or a "docx" row.
            var tableStart = lines.IndexOf("Resource type");
            Assert.AreNotEqual(-1, tableStart, "The resource-type table is missing from the workbook.");
            var table = lines.Skip(tableStart).ToList();

            AssertFollowedBy(table, "Resource type", "Kind");
            AssertFollowedBy(table, "Kind", "References");
            AssertFollowedBy(table, "CITATION", "How it was used");
            AssertFollowedBy(table, "docx", "Tenant content");
        }

        /// <summary>
        /// The workbook, like the page, reports seats and people rather than money.
        /// </summary>
        /// <remarks>
        /// It used to carry an "Idle spend exposure" row and the seat prices behind it, derived from
        /// prices typed into the page header. Those were withdrawn: a typed-in price is not a source of
        /// truth about what a tenant pays, and a workbook cell is exactly where such a figure gets
        /// re-used as though it were. The Cowork time saving - in hours - is the only value estimate
        /// this report makes.
        /// </remarks>
        [TestMethod]
        public void Workbook_QuotesNoMoney()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            foreach (var banned in new[] { "Idle spend", "Seat price used", "reducible at renewal", "Currency" })
            {
                Assert.IsFalse(
                    text.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0,
                    $"The Copilot Adoption workbook must not contain '{banned}'.");
            }

            // The seat inventory itself is still reported - those are counts, not prices.
            StringAssert.Contains(text, "Purchased Copilot seats");
        }

        /// <summary>
        /// The portal's headline quotes two cohorts - the people ready for Cowork now and every Copilot
        /// seat holder - so the workbook has to carry both, with the working that turns volume into hours.
        /// </summary>
        [TestMethod]
        public void Workbook_CoworkEstimateCarriesBothCohortsAndItsWorking()
        {
            var analysis = SyntheticAnalysis();
            var cells = SheetCells(CopilotAdoptionWorkbook.Build(analysis), "Cowork estimate (modelled)");

            foreach (var expected in new[]
            {
                "Ready now", "Every Copilot seat holder", "People covered",
                "Meetings a month (observed)", "Minutes saved per meeting (assumption)",
                "Modelled hours a month - meetings", "Modelled hours a month - email",
                "Modelled hours a month - documents", "Modelled hours a month (low)", "Modelled hours a month (high)",
            })
            {
                CollectionAssert.Contains(cells, expected, $"The estimate sheet is missing '{expected}'.");
            }

            var full = analysis.Summary.CoworkFullRolloutEstimate;
            Assert.IsTrue(full.CohortUsers > 0, "The synthetic analysis must exercise the full-adoption cohort.");
            AssertFollowedBy(cells, "People covered", analysis.Summary.CoworkValueEstimate.CohortUsers.ToString(CultureInfo.InvariantCulture));
            CollectionAssert.Contains(cells, full.HoursPerMonthHigh.ToString(CultureInfo.InvariantCulture));
            Assert.IsTrue(cells.Any(c => c.StartsWith("Assumptions: the product defaults", StringComparison.Ordinal)),
                "An uncustomised export must say its assumptions are the product defaults.");
        }

        /// <summary>
        /// A reader's own time-saved figures live in their browser only, so an export from a customised page
        /// has to carry them - or the workbook models different hours from the screen it came from.
        /// </summary>
        [TestMethod]
        public void Workbook_AppliesThePortalsTimeSavedFigures_WithoutTouchingTheCachedAnalysis()
        {
            var analysis = SyntheticAnalysis();
            var cachedHours = analysis.Summary.CoworkFullRolloutEstimate.HoursPerMonthHigh;
            var overrides = new CoworkTimeSavedOverrides { MinutesSavedPerMeeting = 50 };

            var standard = CopilotAdoptionWorkbook.Build(analysis);
            var customised = CopilotAdoptionWorkbook.Build(analysis, overrides);

            var full = analysis.Summary.CoworkFullRolloutEstimate;
            var expected = CopilotAdoptionScoring.ModelCoworkValue(
                full.CohortUsers, full.AddressableMeetings, full.AddressableMailThreads, full.AddressableDocuments,
                overrides.ApplyTo(analysis.Summary.Options));
            Assert.AreNotEqual(cachedHours, expected.HoursPerMonthHigh, "The test must change the modelled hours.");

            var cells = SheetCells(customised, "Cowork estimate (modelled)");
            CollectionAssert.Contains(cells, expected.HoursPerMonthHigh.ToString(CultureInfo.InvariantCulture),
                "The estimate must be restated under the reader's figures.");
            Assert.IsTrue(cells.Any(c => c.StartsWith("ASSUMPTIONS ENTERED IN THE PORTAL", StringComparison.Ordinal)),
                "A customised export must say its figures came from the portal, not from the product.");

            // Nothing the next caller reads may have moved: the analysis is cached and shared.
            Assert.AreEqual(5d, analysis.Summary.Options.CoworkMinutesSavedPerMeeting);
            Assert.AreEqual(cachedHours, analysis.Summary.CoworkFullRolloutEstimate.HoursPerMonthHigh);

            // The Settings sheet records the reader's figure against the same key, on the same row, so two
            // exports still line up for a lookup.
            var settings = SheetCells(customised, "Settings");
            Assert.IsTrue(settings.Any(c => c.EndsWith("entered in the portal for this export; the product default is 5", StringComparison.Ordinal)));
            AssertFollowedBy(settings, "coworkMinutesSavedPerMeeting", "50");
            CollectionAssert.AreEqual(SettingKeys(standard), SettingKeys(customised),
                "A customised export must have exactly the same Settings rows, in the same order.");
        }

        private static List<string> SettingKeys(byte[] bytes)
        {
            return SheetCellElements(bytes, "Settings")
                .Where(c => ((string)c.Attribute("r") ?? string.Empty).StartsWith("A", StringComparison.Ordinal))
                .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                .ToList();
        }

        /// <summary>
        /// Asserts that a cell holding <paramref name="value"/> is immediately followed by one holding
        /// <paramref name="expectedNext"/>. Cells come back in document order, so this pins the value
        /// to its own row rather than merely finding both somewhere in the workbook.
        /// </summary>
        private static void AssertFollowedBy(List<string> cells, string value, string expectedNext)
        {
            var index = cells.IndexOf(value);
            Assert.AreNotEqual(-1, index, $"'{value}' is missing from the workbook.");
            Assert.IsTrue(index + 1 < cells.Count, $"'{value}' is the last cell in the workbook.");
            Assert.AreEqual(expectedNext, cells[index + 1],
                $"'{value}' should be followed by '{expectedNext}', not '{cells[index + 1]}'.");
        }

        [TestMethod]
        public void Workbook_CarriesMethodologyPositionAndDualSourceComparison()
        {
            var text = SheetText(CopilotAdoptionWorkbook.Build(SyntheticAnalysis()));

            StringAssert.Contains(text, "Why our figures differ from Microsoft's");
            StringAssert.Contains(text, "unlicensed Copilot Chat usage is not available through Microsoft Graph reports APIs");
            StringAssert.Contains(text, "https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft-365-copilot-usage");
            StringAssert.Contains(text, "https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/api/admin-settings/reports/copilotreportroot-getmicrosoft365copilotusageuserdetail");
            StringAssert.Contains(text, "Do not average or silently reconcile them into one number");
            StringAssert.Contains(text, "Audit log (selected D28): 12 interactions, 4 active days, 2 apps");
            StringAssert.Contains(text, "Microsoft Copilot usage report (D28, snapshot 2026-08-20): 18 prompts, 5 active days");
        }

        [TestMethod]
        public void Workbook_UsesInvariantNumberFormatting()
        {
            // A European decimal comma inside a cell value produces a corrupt workbook. This is the
            // single most likely regression in a hand-built writer, and it only shows up on machines
            // whose locale differs from the developer's.
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                var bytes = CopilotAdoptionWorkbook.Build(SyntheticAnalysis());

                using (var stream = new MemoryStream(bytes))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)))
                    {
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        {
                            var xml = reader.ReadToEnd();
                            Assert.IsFalse(
                                System.Text.RegularExpressions.Regex.IsMatch(xml, @"<v>-?\d+,\d+</v>"),
                                $"{entry.FullName} contains a comma decimal separator, which corrupts the workbook.");
                        }
                    }

                    // And the document must still be readable at all under a non-invariant culture.
                    foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                    {
                        using (var part = entry.Open())
                        {
                            XDocument.Load(part);
                        }
                    }
                }
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void Workbook_HandlesAnEmptyTenantWithoutFallingOver()
        {
            // A deployment whose imports have not run yet must get an explanatory workbook, not an
            // exception - the export is reachable from the page before any data exists.
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.Options = CopilotAdoptionOptions.Default;
            analysis.Summary.GeneratedUtc = Now;
            new CopilotAdoptionService().FinaliseSummary(analysis);

            var bytes = CopilotAdoptionWorkbook.Build(analysis);

            Assert.IsTrue(bytes.Length > 0);
            CollectionAssert.Contains(WorkbookSheetNames(bytes), "Report");
        }

        [TestMethod]
        public void WorkbookFileName_CarriesThePeriodAndRunDate()
        {
            // Two snapshots of the same tenant must not collide in a downloads folder, and the reader
            // has to be able to tell which is which without opening them.
            var summary = new CopilotAdoptionSummary
            {
                WindowDays = 28,
                GeneratedUtc = new DateTime(2026, 8, 23, 11, 30, 0, DateTimeKind.Utc),
            };

            Assert.AreEqual("copilot-adoption-28d-2026-08-23.xlsx", CopilotAdoptionWorkbook.FileName(summary));
        }

        [TestMethod]
        public void Workbook_BandPercentagesUseTheAnalysedDenominator()
        {
            // The bands partition the SCORED population. Dividing them by the seat count made the
            // column sum to well under 100% on a capped tenant, and contradicted both the workbook's
            // own funnel and the doughnut on screen. The uncapped fixture cannot catch this, because
            // the two denominators are equal - so this test caps it deliberately.
            var analysis = SyntheticAnalysis();
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count * 4;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            var scored = analysis.Summary.ScoredUsers;
            Assert.IsTrue(scored < analysis.Summary.LicensedUsers, "The fixture must actually be capped.");

            var expected = analysis.Summary.BandBreakdown
                .Select(b => Math.Round(b.Value / (double)scored * 100d, 1, MidpointRounding.AwayFromZero))
                .Where(v => v > 0)
                .ToList();

            var text = SheetText(CopilotAdoptionWorkbook.Build(analysis));

            StringAssert.Contains(text, "% of analysed",
                "The column must say which denominator it used once it differs from the seat count.");

            foreach (var pct in expected)
            {
                StringAssert.Contains(text, pct.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"Band share {pct}% (of the analysed population) is missing from the workbook.");
            }
        }

        #endregion

        #region Writer primitives

        [TestMethod]
        public void ColumnNames_AreCorrectPastZ()
        {
            // Bijective base-26, which is the classic off-by-one: 26 is Z, not AA, and 27 is AA, not AB.
            // Every chart range in the workbook is built from this, so an error here silently points
            // charts at the wrong data rather than failing loudly.
            Assert.AreEqual("A", XlsxSheet.ColumnName(1));
            Assert.AreEqual("Z", XlsxSheet.ColumnName(26));
            Assert.AreEqual("AA", XlsxSheet.ColumnName(27));
            Assert.AreEqual("AZ", XlsxSheet.ColumnName(52));
            Assert.AreEqual("BA", XlsxSheet.ColumnName(53));
            Assert.AreEqual("ZZ", XlsxSheet.ColumnName(702));
            Assert.AreEqual("AAA", XlsxSheet.ColumnName(703));
        }

        [TestMethod]
        public void SheetNames_AreSanitisedAndMadeUnique()
        {
            // Excel rejects several characters outright and caps names at 31 chars; a duplicate name
            // makes the whole workbook unopenable. Department and agent names reach these paths.
            using (var workbook = new XlsxWriter())
            {
                var a = workbook.AddSheet("Report");
                var b = workbook.AddSheet("Report");
                var c = workbook.AddSheet("Bad/Name:With*Chars?[here]");
                var d = workbook.AddSheet(new string('x', 60));
                // Truncation can expose an apostrophe that was safely mid-name. Excel rejects a sheet
                // name that starts or ends with one, so the trim has to happen after the cut as well.
                var e = workbook.AddSheet(new string('a', 30) + "'x");

                var names = new[] { a.Name, b.Name, c.Name, d.Name, e.Name };

                Assert.AreEqual(5, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    "Duplicate sheet names make the workbook unopenable.");

                foreach (var name in names)
                {
                    Assert.IsTrue(name.Length <= 31, $"'{name}' exceeds Excel's 31-character limit.");
                    Assert.IsTrue(name.IndexOfAny(new[] { '\\', '/', '?', '*', '[', ']', ':' }) < 0,
                        $"'{name}' contains a character Excel rejects.");
                    Assert.IsFalse(name.StartsWith("'", StringComparison.Ordinal), $"'{name}' starts with an apostrophe.");
                    Assert.IsFalse(name.EndsWith("'", StringComparison.Ordinal), $"'{name}' ends with an apostrophe.");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(name));
                }
            }
        }

        [TestMethod]
        public void Save_LeavesTheCallersStreamOpen()
        {
            // The controller writes into a response stream it does not own. Closing it would truncate
            // the download in a way that only shows up under load.
            using (var stream = new MemoryStream())
            {
                using (var workbook = new XlsxWriter())
                {
                    workbook.AddSheet("Data").AddRow("Contoso", 1);
                    workbook.Save(stream);
                }

                Assert.IsTrue(stream.CanWrite, "Save() must not close or dispose the caller's stream.");
                Assert.IsTrue(stream.Length > 0);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// The keys the reflection-driven sheets are expected to write for a type: the serialised name
        /// of every readable, non-ignored property, with collections keyed by their row count.
        ///
        /// Mirrors the writer deliberately rather than sharing code with it. A test that called the
        /// production flattener would pass even if that flattener skipped half the model, which is the
        /// failure this is here to catch.
        /// </summary>
        private static List<string> ExpectedFactKeys(Type type)
        {
            var keys = new List<string>();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;

                var name = property.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? property.Name;
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (propertyType.IsPrimitive || propertyType.IsEnum || propertyType == typeof(string)
                    || propertyType == typeof(decimal) || propertyType == typeof(DateTime))
                {
                    keys.Add(name);
                }
                else if (typeof(ICollection).IsAssignableFrom(propertyType))
                {
                    keys.Add(name + ".count");
                }
            }

            return keys;
        }

        private static void AssertSheetHasColumns(byte[] bytes, string sheetName, IEnumerable<string> headers)
        {
            var cells = SheetCells(bytes, sheetName);

            var missing = headers.Where(h => !cells.Contains(h)).ToList();

            Assert.AreEqual(0, missing.Count,
                $"The '{sheetName}' sheet must carry every column its CSV export does, or the workbook is "
                + "a lesser snapshot than the CSV it sits beside. Missing: " + string.Join(", ", missing));
        }

        /// <summary>
        /// Column A of the <c>Snapshot facts</c> sheet, every row of it.
        ///
        /// <para>Deliberately NOT filtered to the reflected key list. Filtering is what hid the run
        /// diagnostics: they were on this sheet emitting rows whose keys depended on which steps
        /// reported, and every assertion written against a filtered view discarded them before it
        /// could notice. Any row that appears in one export and not the other shifts every row below
        /// it, so the assertion has to see all of them.</para>
        /// </summary>
        private static List<string> SheetKeyColumn(byte[] bytes)
        {
            return SheetCellElements(bytes, "Snapshot facts")
                .Where(c => ((string)c.Attribute("r") ?? string.Empty).StartsWith("A", StringComparison.Ordinal))
                .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                .ToList();
        }

        /// <summary>
        /// The Snapshot fact KEYS only, in sheet order. The keys are whatever the reflection over
        /// <see cref="CopilotAdoptionSummary"/> produces, so this filters the sheet's cells down to
        /// them rather than assuming a column layout.
        /// </summary>
        private static List<string> SnapshotFactKeys(byte[] bytes)
        {
            var expected = new HashSet<string>(ExpectedFactKeys(typeof(CopilotAdoptionSummary)), StringComparer.Ordinal);
            return SheetCells(bytes, "Snapshot facts").Where(expected.Contains).ToList();
        }

        /// <summary>The decoded text of every cell on one named sheet, in document order.</summary>
        private static List<string> SheetCells(byte[] bytes, string sheetName)
        {
            return SheetCellElements(bytes, sheetName)
                .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                .ToList();
        }

        /// <summary>
        /// The raw <c>c</c> elements of one named sheet. Sheets are written as
        /// <c>xl/worksheets/sheetN.xml</c> in the same order they appear in <c>xl/workbook.xml</c>.
        /// </summary>
        private static List<XElement> SheetCellElements(byte[] bytes, string sheetName)
        {
            var index = WorkbookSheetNames(bytes).IndexOf(sheetName);
            Assert.AreNotEqual(-1, index, $"Sheet '{sheetName}' is missing from the workbook.");

            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml");
                Assert.IsNotNull(entry, $"The worksheet part for '{sheetName}' is missing.");

                using (var part = entry.Open())
                {
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    return XDocument.Load(part).Descendants(ns + "c").ToList();
                }
            }
        }

        private static List<string> WorkbookSheetNames(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("xl/workbook.xml");
                Assert.IsNotNull(entry, "xl/workbook.xml is missing.");

                using (var part = entry.Open())
                {
                    var doc = XDocument.Load(part);
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    return doc.Descendants(ns + "sheet")
                        .Select(s => (string)s.Attribute("name"))
                        .ToList();
                }
            }
        }

        private static List<string> ChartXmls(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                return zip.Entries
                    .Where(e => e.FullName.StartsWith("xl/charts/chart", StringComparison.Ordinal)
                             && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .Select(e =>
                    {
                        using (var reader = new StreamReader(e.Open(), Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    })
                    .ToList();
            }
        }

        /// <summary>All worksheet XML concatenated and XML-decoded, for asserting on visible content.</summary>
        private static string SheetText(byte[] bytes)
        {
            var builder = new StringBuilder();

            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                                                          && e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    using (var part = entry.Open())
                    {
                        var doc = XDocument.Load(part);
                        foreach (var text in doc.Descendants().Where(e => !e.HasElements))
                        {
                            builder.AppendLine(text.Value);
                        }
                    }
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// A complete analysis built entirely from synthetic values - no tenant data of any kind.
        /// Covers every section the workbook writes, so a section that throws is caught here rather
        /// than by a customer.
        /// </summary>
        private static CopilotAdoptionAnalysis SyntheticAnalysis()
        {
            var options = CopilotAdoptionOptions.Default;
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;

            summary.GeneratedUtc = Now;
            summary.WindowDays = options.WindowDays;
            summary.FromUtc = Now.AddDays(-options.WindowDays);
            summary.ToUtc = Now;
            summary.Options = options;
            summary.DataSources.AuditAvailable = true;
            summary.DataSources.CopilotUsageReportAvailable = true;
            summary.DataSources.CopilotUsageReportDate = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
            summary.DataSources.CopilotUsageReportPeriodDays = 28;
            summary.DataSources.UserMetadataAvailable = true;
            summary.Warnings.Add("Synthetic warning containing an ampersand & an <element>.");

            summary.SeatLicenceTypes.Add(new LicenceTypeClassification
            {
                Id = 1,
                Name = "Microsoft 365 Copilot",
                SkuPartNumber = "Microsoft_365_Copilot",
                AssignedUsers = 60,
                IsCopilotSeat = true,
            });

            var departments = new[] { "Finance", "Legal", GreekDepartment, AmpersandDepartment };
            var random = new Random(7);

            for (var i = 0; i < 60; i++)
            {
                var activeDays = i % 6 == 0 ? 0 : random.Next(1, 18);

                var row = CopilotAdoptionScoring.Score(
                    new LicensedUserUsageRow
                    {
                        UserId = i,
                        UserPrincipalName = "user" + i + "@contoso.com",
                        Department = departments[i % departments.Length],
                        JobTitle = "Analyst",
                        ManagerUserPrincipalName = "manager@contoso.com",
                        Interactions = activeDays * random.Next(1, 8),
                        ActiveDays = activeDays,
                        AppsUsed = activeDays == 0 ? 0 : random.Next(1, 4),
                        AgentsUsed = i % 4 == 0 ? 1 : 0,
                        LastInteractionUtc = activeDays == 0 ? (DateTime?)null : Now.AddDays(-random.Next(1, 20)),
                        FirstInteractionUtc = activeDays == 0 ? (DateTime?)null : Now.AddDays(-90),
                    },
                    summary.FromUtc,
                    Now,
                    true,
                    options);

                if (i == 1)
                {
                    row.ReportPrompts = 18;
                    row.ReportActiveDays = 5;
                    row.ReportLastActivityUtc = Now.AddDays(-3);
                    row.AuditInteractions = 12;
                    row.AuditActiveDays = 4;
                    row.AuditAppsUsed = 2;
                    row.SourceComparisonAvailable = true;
                }

                analysis.LicensedUsers.Add(row);
            }

            summary.LicensedUsers = analysis.LicensedUsers.Count;

            for (var i = 0; i < 20; i++)
            {
                analysis.UnlicensedUsers.Add(new UnlicensedUsageQueryRow
                {
                    UserId = 900 + i,
                    // The unlicensed query selects the UPN so this population can be grouped and
                    // filtered by email domain like every other one.
                    UserPrincipalName = "unlicensed" + i + "@contoso.com",
                    EmailDomain = "contoso.com",
                    Department = departments[i % departments.Length],
                    Interactions = random.Next(1, 120),
                    ActiveDays = random.Next(1, 20),
                    AppsUsed = 2,
                    AgentsUsed = i % 3 == 0 ? 1 : 0,
                    LastInteractionUtc = Now.AddDays(-random.Next(1, 12)),
                });
            }

            analysis.Opportunities.Add(CopilotAdoptionScoring.ScoreOpportunity(
                new UnlicensedUserSignalRow
                {
                    UserId = 5000,
                    UserPrincipalName = "candidate@contoso.com",
                    Department = AmpersandDepartment,
                    JobTitle = "Counsel",
                    UnlicensedCopilotInteractions = 60,
                    UnlicensedCopilotActiveDays = 12,
                    TeamsMessages = 400,
                    EmailsSent = 90,
                    EmailsRead = 300,
                    FilesViewedOrEdited = 120,
                },
                options));

            foreach (var name in new[] { "Contoso Expenses Agent", GreekDepartment + " agent", AmpersandDepartment + " agent" })
            {
                analysis.Agents.Add(CopilotAdoptionScoring.ScoreAgent(
                    new AgentUsageQueryRow
                    {
                        AgentId = Math.Abs(name.GetHashCode()),
                        Name = name,
                        AgentKey = "Contoso.Agent." + Math.Abs(name.GetHashCode()),
                        IsCustomAgent = true,
                        Interactions = random.Next(10, 600),
                        Users = random.Next(1, 30),
                        LicensedUsers = random.Next(1, 15),
                        ActiveDays = random.Next(1, 20),
                        AppsUsed = random.Next(1, 4),
                        FirstUsedUtc = Now.AddDays(-random.Next(40, 300)),
                        LastUsedUtc = Now.AddDays(-random.Next(1, 120)),
                    },
                    Now,
                    options));
            }

            summary.UsageByApp.Add(new AdoptionCategory { Label = "Teams", Value = 1988 });
            summary.UsageByApp.Add(new AdoptionCategory { Label = AmpersandDepartment, Value = 534 });
            summary.TopResourceTypes.Add(new AdoptionResourceTypeRow
            {
                Label = "docx",
                Value = 480,
                Kind = CopilotAccessedResourceTaxonomy.Classify("docx"),
            });
            summary.TopResourceTypes.Add(new AdoptionResourceTypeRow
            {
                Label = "CITATION",
                Value = 1200,
                Kind = CopilotAccessedResourceTaxonomy.Classify("CITATION"),
            });

            var users = new AdoptionSeries { Name = "Active licensed users" };
            var volume = new AdoptionSeries { Name = "Licensed interactions" };
            for (var w = 0; w < 10; w++)
            {
                var week = Now.AddDays(-7 * (10 - w)).Date;
                users.Points.Add(new AdoptionTimePoint { WeekStart = week, Value = random.Next(10, 60) });
                volume.Points.Add(new AdoptionTimePoint { WeekStart = week, Value = random.Next(100, 2500) });
            }
            summary.WeeklyTrend.Add(users);
            summary.WeeklyVolumeTrend.Add(volume);

            // Cowork readiness signals. Without these the Cowork sheet is never written, so until now
            // no test built it at all - the sheet with the widest per-user table in the workbook was
            // the one sheet nothing exercised.
            for (var i = 0; i < 30; i++)
            {
                var usesCowork = i % 5 == 0;

                analysis.CoworkSignals.Add(new CoworkReadinessSignalRow
                {
                    UserId = i,
                    UserPrincipalName = "user" + i + "@contoso.com",
                    Mail = "user" + i + "@contoso.com",
                    EmailDomain = "contoso.com",
                    Department = departments[i % departments.Length],
                    JobTitle = "Analyst",
                    ManagerUserPrincipalName = "manager@contoso.com",
                    Country = "Ruritania",
                    OfficeLocation = GreekDepartment,
                    CompanyName = "Contoso",
                    AccountEnabled = true,
                    CoworkInteractions = usesCowork ? random.Next(5, 60) : 0,
                    CoworkActiveDays = usesCowork ? random.Next(1, 12) : 0,
                    LastCoworkInteractionUtc = usesCowork ? Now.AddDays(-random.Next(1, 14)) : (DateTime?)null,
                    CoworkReportTotalTasks = usesCowork ? random.Next(2, 40) : (int?)null,
                    CoworkReportScheduledTasks = usesCowork ? random.Next(0, 20) : (int?)null,
                    CoworkReportUserInitiatedTasks = usesCowork ? random.Next(0, 20) : (int?)null,
                    CoworkReportActiveDays = usesCowork ? random.Next(1, 12) : (int?)null,
                    CoworkReportLastActivityDate = usesCowork ? Now.AddDays(-random.Next(1, 10)) : (DateTime?)null,
                    CoworkReportRetainedUser = usesCowork ? (i % 10 == 0) : (bool?)null,
                    TeamsMessages = random.Next(40, 600),
                    TeamsMeetings = random.Next(1, 30),
                    EmailsSent = random.Next(10, 200),
                    EmailsRead = random.Next(40, 700),
                    FilesViewedOrEdited = random.Next(5, 250),
                    LastM365ActivityUtc = Now.AddDays(-random.Next(1, 20)),
                });
            }

            new CopilotAdoptionService().FinaliseSummary(analysis);
            return analysis;
        }

        #endregion
    }
}
