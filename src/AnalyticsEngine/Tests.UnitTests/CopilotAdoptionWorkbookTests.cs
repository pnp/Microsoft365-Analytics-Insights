using Common.Entities.CopilotAdoption;
using Common.Entities.Xlsx;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
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
            })
            {
                CollectionAssert.Contains(sheetNames, expected,
                    $"The workbook is meant to be the whole report; '{expected}' is missing.");
            }
        }

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

                analysis.LicensedUsers.Add(row);
            }

            summary.LicensedUsers = analysis.LicensedUsers.Count;

            for (var i = 0; i < 20; i++)
            {
                analysis.UnlicensedUsers.Add(new UnlicensedUsageQueryRow
                {
                    UserId = 900 + i,
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
            summary.TopResourceTypes.Add(new AdoptionCategory { Label = "docx", Value = 480 });

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

            new CopilotAdoptionService().FinaliseSummary(analysis);
            return analysis;
        }

        #endregion
    }
}
