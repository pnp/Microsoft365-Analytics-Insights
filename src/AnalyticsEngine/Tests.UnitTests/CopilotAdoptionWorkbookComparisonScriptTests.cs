using Common.Entities.CopilotAdoption;
using Common.Entities.Xlsx;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for <c>scripts/CopilotAdoption/Compare-CopilotAdoptionWorkbooks.ps1</c> (#644): the script that
    /// compares two exported Copilot Adoption workbooks and refuses to compare two that cannot be
    /// compared fairly.
    ///
    /// <para>Every workbook here is a real one, built by the product's own <see cref="CopilotAdoptionWorkbook"/>
    /// from synthetic analyses, and the script is run exactly as an administrator runs it - by Windows
    /// PowerShell, with <c>-File</c> - so what is asserted is the exit code and the output a person sees.
    /// Where a case cannot be produced through the product (a different build, a key a later build
    /// added, a file Excel has saved again), the package's XML is edited directly.</para>
    ///
    /// <para>The script lives outside the solution, at the repository root. If it cannot be found these
    /// tests FAIL rather than skip: a comparison script that silently stopped being tested is how a
    /// baseline procedure quietly breaks.</para>
    /// </summary>
    [TestClass]
    public class CopilotAdoptionWorkbookComparisonScriptTests
    {
        private const string ScriptRelativePath = @"scripts\CopilotAdoption\Compare-CopilotAdoptionWorkbooks.ps1";
        private const string GreekText = "Καλημέρα κόσμε";
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

        private string _folder;

        [TestInitialize]
        public void CreateWorkingFolder()
        {
            // Under the test output rather than the machine's temp folder, and unique per test, so parallel
            // runs never share a file.
            _folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CopilotAdoptionCompareScript", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TestCleanup]
        public void DeleteWorkingFolder()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        #region The script itself

        /// <summary>
        /// Windows PowerShell 5.1 reads a script without a byte-order mark in the system code page, so a
        /// single non-ASCII character - a dash pasted from a document - can corrupt a string or break the
        /// parse on a customer's machine while PowerShell 7 runs it perfectly.
        /// </summary>
        [TestMethod]
        public void Script_IsAsciiForWindowsPowerShell()
        {
            var bytes = File.ReadAllBytes(ScriptPath);
            var offending = bytes.Select((b, i) => new { b, i }).Where(x => x.b > 0x7E && x.b != 0x0D && x.b != 0x0A).ToList();

            Assert.AreEqual(0, offending.Count,
                "The script must be plain ASCII. First non-ASCII byte at offset "
                + (offending.Count > 0 ? offending[0].i.ToString(CultureInfo.InvariantCulture) : "-") + ".");
        }

        [TestMethod]
        public void Script_HasHelp()
        {
            var result = Run(WindowsPowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-Help -Full '" + ScriptPath + "'\"");

            Assert.AreEqual(0, result.ExitCode, result.ToString());
            StringAssert.Contains(result.Output, "SYNOPSIS");
            StringAssert.Contains(result.Output, "Exit codes");
            StringAssert.Contains(result.Output, "-Force");
            StringAssert.Contains(result.Output, "-OutFile");
        }

        #endregion

        #region Comparable files

        /// <summary>
        /// Two exports with the same build, settings, period length and population: the comparison is
        /// produced, every changed figure is listed with its change, and a figure that became unknown
        /// is reported as blank rather than as a fall to zero.
        /// </summary>
        [TestMethod]
        public void IdenticalSettings_ProduceAComparison()
        {
            var earlier = Earlier();
            var later = LaterOf(earlier);

            var result = Compare(Save("before.xlsx", earlier), Save("after.xlsx", later));

            Assert.AreEqual(0, result.ExitCode, result.ToString());
            Assert.AreEqual(string.Empty, result.Error.Trim(), "A comparable pair must not write anything to the error stream.");
            foreach (var check in new[] { "Product build", "Settings", "Reporting period length", "Population scope" })
            {
                Assert.IsTrue(result.Lines.Any(l => l.StartsWith("  OK", StringComparison.Ordinal) && l.Contains(check)),
                    $"The '{check}' check must be reported as passed.\n{result}");
            }

            StringAssert.Contains(FactLine(result, "activeUsers"), "+7");
            StringAssert.Contains(FactLine(result, "adoptionRatePct"), "+6.5");
            StringAssert.Contains(FactLine(result, "agents.activeAgents"), "+3",
                "A nested figure (#640) must be compared like any other.");

            var generated = FactLine(result, "generatedUtc");
            StringAssert.Contains(generated, "2026-08-23", "Dates are shown as dates, not as Excel serial numbers.");
            StringAssert.Contains(generated, "2026-11-21");
            StringAssert.Contains(generated, "+90 days");

            var unknown = FactLine(result, "coworkAdoptionPct");
            StringAssert.Contains(unknown, "blank in the later file");
            Assert.IsFalse(unknown.Contains("-12.5"), "A blank is unknown, never zero: no change may be calculated for it.");

            Assert.IsFalse(result.Lines.Any(l => l.StartsWith("licensedUsers ", StringComparison.Ordinal)),
                "An unchanged figure is not listed unless -IncludeUnchanged is used.");
            StringAssert.Contains(FactLine(Compare(Path.Combine(_folder, "before.xlsx"), Path.Combine(_folder, "after.xlsx"), "-IncludeUnchanged"), "licensedUsers"), "0");
        }

        /// <summary>
        /// A key a later build added is reported, not treated as a failure: it has no value in the earlier
        /// export. A key the later build removed is reported the same way.
        /// </summary>
        [TestMethod]
        public void KeyAddedInALaterBuild_IsReportedNotRefused()
        {
            var earlier = Earlier();
            var before = RemoveFact(CopilotAdoptionWorkbook.Build(earlier), "agents.activeAgents");
            var after = RemoveFact(CopilotAdoptionWorkbook.Build(LaterOf(earlier)), "unlicensed.activeUsers");

            var result = Compare(Save("before.xlsx", before), Save("after.xlsx", after));

            Assert.AreEqual(0, result.ExitCode, "A key only one file has is a product change to report, not a reason to refuse.\n" + result);
            StringAssert.Contains(FactLine(result, "agents.activeAgents"), "added in a later build");
            StringAssert.Contains(FactLine(result, "unlicensed.activeUsers"), "removed in the later build");
            StringAssert.Contains(result.Output, "1 added in a later build, 1 removed in the later build");
        }

        /// <summary>
        /// The CSV is for a board pack: it opens in Excel with non-Latin text intact, it shows on its own
        /// that the comparison was fair, and tenant text cannot be read by Excel as a formula.
        /// </summary>
        [TestMethod]
        public void Csv_IsWrittenForABoardPack()
        {
            var earlier = Earlier();
            earlier.Summary.Agents.MostPopularAgent = "Contoso Expenses Agent";
            earlier.Summary.Agents.MostVersatileAgent = "=HYPERLINK(\"https://contoso.example\")";
            var later = LaterOf(earlier);
            later.Summary.Agents.MostPopularAgent = GreekText + " agent";
            later.Summary.Agents.MostVersatileAgent = "Contoso Expenses Agent";
            var output = Path.Combine(_folder, "comparison.csv");

            var result = Compare(Save("before.xlsx", earlier), Save("after.xlsx", later), "-Format", "Csv", "-OutFile", output);

            Assert.AreEqual(0, result.ExitCode, result.ToString());
            StringAssert.Contains(result.Output, "Wrote the comparison to");

            var bytes = File.ReadAllBytes(output);
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray(),
                "The CSV needs a UTF-8 byte-order mark, or Excel reads its non-Latin text in the wrong code page.");

            var lines = File.ReadAllLines(output, Encoding.UTF8);
            Assert.AreEqual("\"Section\",\"Key\",\"Before\",\"After\",\"Change\",\"Note\"", lines[0]);
            Assert.IsTrue(lines.Any(l => l.StartsWith("\"Comparability\",\"Product build\",", StringComparison.Ordinal) && l.EndsWith(",\"match\"", StringComparison.Ordinal)),
                "The CSV must carry the comparability checks, so it proves on its own that the comparison was fair.");

            var activeUsers = lines.Single(l => l.StartsWith("\"Snapshot facts\",\"activeUsers\",", StringComparison.Ordinal));
            StringAssert.Contains(activeUsers, "\"+7\"");

            Assert.IsTrue(lines.Any(l => l.StartsWith("\"Snapshot facts\",\"agents.mostPopularAgent\",", StringComparison.Ordinal) && l.Contains(GreekText + " agent")),
                "Greek must survive into the CSV.");
            Assert.IsTrue(lines.Any(l => l.Contains("\"'=HYPERLINK(\"\"https://contoso.example\"\")\"")),
                "Text that starts like a formula must be defused, and quotes doubled.");
        }

        /// <summary>
        /// A workbook that has been opened and saved again in Excel no longer looks like the one the product
        /// wrote: strings move to the shared-string table (some as rich text with phonetic runs), the parts
        /// are renamed, the relationships may be absolute, numbers gain seventeen significant digits and
        /// empty cells disappear. None of that may change the comparison.
        /// </summary>
        [TestMethod]
        public void AWorkbookSavedAgainByExcel_StillCompares()
        {
            var earlier = Earlier();
            var before = AsIfSavedByExcel(CopilotAdoptionWorkbook.Build(earlier));

            var result = Compare(Save("before.xlsx", before), Save("after.xlsx", LaterOf(earlier)));

            Assert.AreEqual(0, result.ExitCode, result.ToString());
            Assert.IsTrue(result.Lines.Any(l => l.StartsWith("  OK", StringComparison.Ordinal) && l.Contains("Settings")),
                "Seventeen-digit numbers written by Excel must still match the settings the product wrote.\n" + result);
            StringAssert.Contains(FactLine(result, "activeUsers"), "+7");
            StringAssert.Contains(FactLine(result, "agents.activeAgents"), "+3");
            StringAssert.Contains(FactLine(result, "coworkAdoptionPct"), "blank in the later file");
        }

        /// <summary>
        /// Run diagnostics are never read: their keys vary from run to run. Proved by making that sheet
        /// unreadable in both files.
        /// </summary>
        [TestMethod]
        public void RunDiagnostics_AreNeverRead()
        {
            var earlier = Earlier();
            var before = ReplaceSheetPart(CopilotAdoptionWorkbook.Build(earlier), "Run diagnostics", "this is not XML");
            var after = ReplaceSheetPart(CopilotAdoptionWorkbook.Build(LaterOf(earlier)), "Run diagnostics", "this is not XML");

            var result = Compare(Save("before.xlsx", before), Save("after.xlsx", after));

            Assert.AreEqual(0, result.ExitCode, result.ToString());
            Assert.IsFalse(result.Output.Contains("diagnostics."), "No run-diagnostic key may be compared.");
        }

        #endregion

        #region Unfair comparisons

        [TestMethod]
        public void ChangedOption_IsRefused()
        {
            var earlier = Earlier();
            var later = LaterOf(earlier);
            later.Summary.Options.ChampionScore = earlier.Summary.Options.ChampionScore + 5;

            var result = Compare(Save("before.xlsx", earlier), Save("after.xlsx", later));

            AssertRefused(result, "Settings");
            StringAssert.Contains(result.Error, "championScore: before '75', after '80'");
        }

        [TestMethod]
        public void DifferentEmailDomainScope_IsRefused()
        {
            var earlier = Earlier();
            var later = LaterOf(earlier);
            later.Summary.ScopedEmailDomain = "contoso.com";

            var result = Compare(Save("before.xlsx", earlier), Save("after.xlsx", later));

            AssertRefused(result, "Population scope");
            StringAssert.Contains(result.Error, "before 'Whole tenant', after 'Email domain contoso.com'");
            StringAssert.Contains(result.Error, "NARROWED TO ONE EMAIL DOMAIN: contoso.com",
                "The banner at the top of the Report sheet is part of the population check.");
        }

        /// <summary>
        /// A people filter - here an administrator's global filter - changes the population as surely as
        /// an email domain does, and shows on the Report sheet the same way.
        /// </summary>
        [TestMethod]
        public void DifferentPeopleFilter_IsRefused()
        {
            var earlier = Earlier();
            var later = LaterOf(earlier);
            later.Summary.GlobalFilterDescription = "Department is " + GreekText;

            var result = Compare(Save("before.xlsx", earlier), Save("after.xlsx", later));

            AssertRefused(result, "Population scope");
            StringAssert.Contains(result.Error, "set by a portal administrator");
        }

        [TestMethod]
        public void DifferentPeriod_IsRefused()
        {
            var earlier = Earlier();
            var later = LaterOf(earlier);
            later.Summary.WindowDays = 90;
            later.Summary.Options.WindowDays = 90;
            later.Summary.FromUtc = later.Summary.ToUtc.AddDays(-90);

            var result = Compare(Save("before.xlsx", earlier), Save("after.xlsx", later));

            AssertRefused(result, "Reporting period length");
            StringAssert.Contains(result.Error, "Reporting period length: before '28 days', after '90 days'");
        }

        /// <summary>
        /// Two builds may count a figure differently, so the comparison is refused - and -Force compares
        /// anyway while saying so prominently, in the report and on the error stream.
        /// </summary>
        [TestMethod]
        public void DifferentBuild_IsRefused_UnlessForced()
        {
            var earlier = Earlier();
            var after = SetReportValue(CopilotAdoptionWorkbook.Build(LaterOf(earlier)), "Product build", "synthetic-build");
            var beforePath = Save("before.xlsx", earlier);
            var afterPath = Save("after.xlsx", after);

            var refused = Compare(beforePath, afterPath);

            AssertRefused(refused, "Product build");
            StringAssert.Contains(refused.Error, "after 'synthetic-build'");
            StringAssert.Contains(refused.Error, "-Force");

            var forced = Compare(beforePath, afterPath, "-Force");

            Assert.AreEqual(0, forced.ExitCode, forced.ToString());
            StringAssert.Contains(forced.Output, "COMPARED DESPITE FAILED CHECKS (-Force)");
            Assert.IsTrue(forced.Lines.Any(l => l.StartsWith("  MISMATCH", StringComparison.Ordinal) && l.Contains("Product build")), forced.ToString());
            StringAssert.Contains(forced.Error, "COMPARED ANYWAY (-Force)", "A forced comparison must still be loud on the error stream.");
            StringAssert.Contains(FactLine(forced, "activeUsers"), "+7");
        }

        #endregion

        #region Files that cannot be read

        [TestMethod]
        public void AFileThatIsNotACopilotAdoptionWorkbook_ExitsWithTwo()
        {
            var earlierPath = Save("before.xlsx", Earlier());

            var missing = Compare(earlierPath, Path.Combine(_folder, "no-such-file.xlsx"));
            Assert.AreEqual(2, missing.ExitCode, missing.ToString());
            StringAssert.Contains(missing.Error, "was not found");

            var text = Path.Combine(_folder, "not-a-workbook.xlsx");
            File.WriteAllText(text, "Contoso");
            var notAZip = Compare(earlierPath, text);
            Assert.AreEqual(2, notAZip.ExitCode, notAZip.ToString());
            StringAssert.Contains(notAZip.Error, "is not an Excel workbook");

            byte[] other;
            using (var writer = new XlsxWriter())
            {
                writer.AddSheet("Data").AddRow("Contoso", 1);
                other = writer.ToArray();
            }
            var notOurs = Compare(earlierPath, Save("other.xlsx", other));
            Assert.AreEqual(2, notOurs.ExitCode, notOurs.ToString());
            StringAssert.Contains(notOurs.Error, "has no 'Report' sheet");
        }

        #endregion

        #region PowerShell 7

        /// <summary>
        /// The script promises Windows PowerShell 5.1 and PowerShell 7. Every other test here runs it on
        /// 5.1; this one runs the comparable case on PowerShell 7 when the machine has it.
        /// </summary>
        [TestMethod]
        public void RunsOnPowerShell7()
        {
            var pwsh = FindPowerShell7();
            if (pwsh == null)
            {
                Assert.Inconclusive("PowerShell 7 (pwsh.exe) is not installed on this machine.");
            }

            var earlier = Earlier();
            var result = Run(pwsh, ScriptArguments(Save("before.xlsx", earlier), Save("after.xlsx", LaterOf(earlier))));

            Assert.AreEqual(0, result.ExitCode, result.ToString());
            StringAssert.Contains(FactLine(result, "agents.activeAgents"), "+3");
            StringAssert.Contains(FactLine(result, "generatedUtc"), "+90 days");

            var refused = LaterOf(earlier);
            refused.Summary.ScopedEmailDomain = "contoso.com";
            Assert.AreEqual(1, Run(pwsh, ScriptArguments(Path.Combine(_folder, "before.xlsx"), Save("narrowed.xlsx", refused))).ExitCode);
        }

        #endregion

        #region Fixtures

        private static CopilotAdoptionAnalysis Earlier()
        {
            var analysis = CopilotAdoptionWorkbookTests.SyntheticAnalysis();
            analysis.Summary.CoworkAdoptionPct = 12.5;
            return analysis;
        }

        /// <summary>
        /// The same tenant ninety days on: a few figures moved, one became unknown, and the build,
        /// settings, period length and population are all unchanged.
        /// </summary>
        private static CopilotAdoptionAnalysis LaterOf(CopilotAdoptionAnalysis earlier)
        {
            var later = CopilotAdoptionWorkbookTests.SyntheticAnalysis();
            var summary = later.Summary;
            summary.GeneratedUtc = earlier.Summary.GeneratedUtc.AddDays(90);
            summary.FromUtc = earlier.Summary.FromUtc.AddDays(90);
            summary.ToUtc = earlier.Summary.ToUtc.AddDays(90);
            summary.ActiveUsers = earlier.Summary.ActiveUsers + 7;
            summary.AdoptionRatePct = earlier.Summary.AdoptionRatePct + 6.5;
            summary.CoworkAdoptionPct = null;
            summary.Agents.ActiveAgents = earlier.Summary.Agents.ActiveAgents + 3;
            return later;
        }

        private string Save(string name, CopilotAdoptionAnalysis analysis)
        {
            return Save(name, CopilotAdoptionWorkbook.Build(analysis));
        }

        private string Save(string name, byte[] bytes)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>Removes one key's row from Snapshot facts, as a build that did not have the key would.</summary>
        private static byte[] RemoveFact(byte[] workbook, string key)
        {
            return RewriteSheet(workbook, "Snapshot facts", sheet =>
            {
                var row = sheet.Descendants(Main + "row").Single(r => CellText(r, "A") == key);
                row.Remove();
            });
        }

        /// <summary>Sets the value of one row of the Report sheet's property table, e.g. the product build.</summary>
        private static byte[] SetReportValue(byte[] workbook, string property, string value)
        {
            return RewriteSheet(workbook, "Report", sheet =>
            {
                var row = sheet.Descendants(Main + "row").First(r => CellText(r, "A") == property);
                var cell = row.Elements(Main + "c").Single(c => ((string)c.Attribute("r")).StartsWith("B", StringComparison.Ordinal));
                cell.ReplaceWith(new XElement(Main + "c",
                    new XAttribute("r", (string)cell.Attribute("r")),
                    new XAttribute("t", "inlineStr"),
                    new XElement(Main + "is", new XElement(Main + "t", value))));
            });
        }

        private static string CellText(XElement row, string column)
        {
            var cell = row.Elements(Main + "c").FirstOrDefault(c => ((string)c.Attribute("r") ?? string.Empty).StartsWith(column, StringComparison.Ordinal)
                                                                  && char.IsDigit(((string)c.Attribute("r"))[column.Length]));
            return cell == null ? null : string.Concat(cell.Descendants(Main + "t").Select(t => t.Value));
        }

        private static byte[] RewriteSheet(byte[] workbook, string sheetName, Action<XDocument> edit)
        {
            return RewritePackage(workbook, zip =>
            {
                var entry = zip.GetEntry(SheetPart(zip, sheetName));
                XDocument sheet;
                using (var stream = entry.Open())
                {
                    sheet = XDocument.Load(stream);
                }

                edit(sheet);
                Replace(zip, entry.FullName, sheet);
            });
        }

        private static byte[] ReplaceSheetPart(byte[] workbook, string sheetName, string content)
        {
            return RewritePackage(workbook, zip =>
            {
                var name = SheetPart(zip, sheetName);
                zip.GetEntry(name).Delete();
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
                {
                    writer.Write(content);
                }
            });
        }

        /// <summary>The worksheet part the product wrote for a sheet: parts are numbered in tab order.</summary>
        private static string SheetPart(ZipArchive zip, string sheetName)
        {
            XDocument workbook;
            using (var stream = zip.GetEntry("xl/workbook.xml").Open())
            {
                workbook = XDocument.Load(stream);
            }

            var names = workbook.Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")).ToList();
            var index = names.IndexOf(sheetName);
            Assert.AreNotEqual(-1, index, $"The workbook has no '{sheetName}' sheet.");
            return "xl/worksheets/sheet" + (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml";
        }

        /// <summary>
        /// The package as Excel leaves it after "Save": shared strings (one in three as rich text with a
        /// phonetic run that is not part of the value), renamed worksheet parts behind absolute
        /// relationship targets, seventeen-digit numbers, and no empty cells.
        /// </summary>
        private static byte[] AsIfSavedByExcel(byte[] workbook)
        {
            return RewritePackage(workbook, zip =>
            {
                var strings = new List<string>();
                var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
                var renamed = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                                                          && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList())
                {
                    XDocument sheet;
                    using (var stream = entry.Open())
                    {
                        sheet = XDocument.Load(stream);
                    }

                    foreach (var cell in sheet.Descendants(Main + "c").ToList())
                    {
                        var type = (string)cell.Attribute("t");
                        if (type == "inlineStr")
                        {
                            var text = string.Concat(cell.Descendants(Main + "t").Select(t => t.Value));
                            if (text.Length == 0)
                            {
                                cell.Remove();
                                continue;
                            }

                            if (!indexes.TryGetValue(text, out var index))
                            {
                                index = strings.Count;
                                strings.Add(text);
                                indexes.Add(text, index);
                            }

                            cell.SetAttributeValue("t", "s");
                            cell.Elements(Main + "is").Remove();
                            cell.Add(new XElement(Main + "v", index.ToString(CultureInfo.InvariantCulture)));
                        }
                        else if (type == null)
                        {
                            var value = cell.Element(Main + "v");
                            if (value != null && double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                            {
                                value.Value = number.ToString("G17", CultureInfo.InvariantCulture);
                            }
                        }
                    }

                    var name = "xl/worksheets/saved-" + entry.FullName.Substring("xl/worksheets/".Length);
                    renamed.Add(entry.FullName, name);
                    entry.Delete();
                    Replace(zip, name, sheet);
                }

                var table = new XElement(Main + "sst",
                    new XAttribute("count", strings.Count),
                    new XAttribute("uniqueCount", strings.Count),
                    strings.Select((text, i) => i % 3 == 0 && text.Length > 1
                        ? new XElement(Main + "si",
                            new XElement(Main + "r", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text.Substring(0, 1))),
                            new XElement(Main + "r", new XElement(Main + "rPr", new XElement(Main + "b")), new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text.Substring(1))),
                            new XElement(Main + "rPh", new XAttribute("sb", "0"), new XAttribute("eb", "1"), new XElement(Main + "t", "PHONETIC")))
                        : new XElement(Main + "si", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))));
                Replace(zip, "xl/sharedStrings.xml", new XDocument(table));

                var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
                XDocument rels;
                using (var stream = relsEntry.Open())
                {
                    rels = XDocument.Load(stream);
                }

                foreach (var relationship in rels.Root.Elements(PackageRelationships + "Relationship"))
                {
                    var target = "xl/" + (string)relationship.Attribute("Target");
                    if (renamed.TryGetValue(target, out var name))
                    {
                        relationship.SetAttributeValue("Target", "/" + name);
                    }
                }

                rels.Root.Add(new XElement(PackageRelationships + "Relationship",
                    new XAttribute("Id", "rIdSharedStrings"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"),
                    new XAttribute("Target", "sharedStrings.xml")));
                Replace(zip, relsEntry.FullName, rels);

                var typesEntry = zip.GetEntry("[Content_Types].xml");
                XDocument types;
                using (var stream = typesEntry.Open())
                {
                    types = XDocument.Load(stream);
                }

                foreach (var part in types.Root.Elements(ContentTypes + "Override"))
                {
                    var partName = ((string)part.Attribute("PartName")).TrimStart('/');
                    if (renamed.TryGetValue(partName, out var name))
                    {
                        part.SetAttributeValue("PartName", "/" + name);
                    }
                }

                types.Root.Add(new XElement(ContentTypes + "Override",
                    new XAttribute("PartName", "/xl/sharedStrings.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml")));
                Replace(zip, typesEntry.FullName, types);
            });
        }

        private static byte[] RewritePackage(byte[] workbook, Action<ZipArchive> edit)
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(workbook, 0, workbook.Length);
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
                {
                    edit(zip);
                }

                return stream.ToArray();
            }
        }

        private static void Replace(ZipArchive zip, string name, XDocument document)
        {
            zip.GetEntry(name)?.Delete();
            using (var stream = zip.CreateEntry(name).Open())
            {
                document.Save(stream);
            }
        }

        #endregion

        #region Running the script

        private static string ScriptPath
        {
            get
            {
                var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (directory != null)
                {
                    var candidate = Path.Combine(directory.FullName, ScriptRelativePath);
                    if (File.Exists(candidate)) return candidate;
                    directory = directory.Parent;
                }

                Assert.Fail($"Could not find {ScriptRelativePath} above {AppDomain.CurrentDomain.BaseDirectory}. The comparison "
                            + "script is part of the repository; if it has moved, update this test rather than skipping it.");
                return null;
            }
        }

        private static string WindowsPowerShell =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

        private static string FindPowerShell7()
        {
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.Combine(p.Trim(), "pwsh.exe"))
                .Concat(new[]
                {
                    Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", @"PowerShell\7\pwsh.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"),
                });

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }

        private ScriptResult Compare(string before, string after, params string[] options)
        {
            return Run(WindowsPowerShell, ScriptArguments(before, after, options));
        }

        private static string ScriptArguments(string before, string after, params string[] options)
        {
            return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(ScriptPath)
                   + " -Before " + Quote(before) + " -After " + Quote(after)
                   + string.Concat(options.Select(o => " " + (o.StartsWith("-", StringComparison.Ordinal) ? o : Quote(o))));
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static ScriptResult Run(string shell, string arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    Assert.Fail("The comparison script did not finish within two minutes: " + arguments);
                }

                process.WaitForExit();
                return new ScriptResult(process.ExitCode, output.Result, error.Result);
            }
        }

        /// <summary>The report line for one Snapshot facts key, which starts with the key.</summary>
        private static string FactLine(ScriptResult result, string key)
        {
            var line = result.Lines.FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.Ordinal));
            Assert.IsNotNull(line, $"The report has no line for '{key}'.\n{result}");
            return line;
        }

        private static void AssertRefused(ScriptResult result, string check)
        {
            Assert.AreEqual(1, result.ExitCode, $"A comparison that fails the '{check}' check must be refused.\n{result}");
            Assert.AreEqual(string.Empty, result.Output.Trim(), "A refused comparison must not print a comparison.");
            StringAssert.Contains(result.Error, "NOT COMPARABLE");
            StringAssert.Contains(result.Error, "  " + check + ":", "The failed check must be named.");
            StringAssert.Contains(result.Error, "Nothing was compared");
        }

        private sealed class ScriptResult
        {
            public ScriptResult(int exitCode, string output, string error)
            {
                ExitCode = exitCode;
                Output = output ?? string.Empty;
                Error = error ?? string.Empty;
                Lines = Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            }

            public int ExitCode { get; }

            public string Output { get; }

            public string Error { get; }

            public List<string> Lines { get; }

            public override string ToString()
            {
                return "Exit code " + ExitCode + "\n--- output ---\n" + Output + "\n--- error ---\n" + Error;
            }
        }

        #endregion
    }
}
