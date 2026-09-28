using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Reading an uploaded "UPN, organisation" CSV.
    /// </summary>
    [TestClass]
    public class UserOrgCsvParserTests
    {
        private const string GreekOrgName = "Καλημέρα κόσμε";

        private static UserOrgCsvParseResult Parse(string content, Encoding encoding = null, int maxRows = UserOrgCsvParser.MaxDataLines)
        {
            return Parse(content, encoding, new UserOrgCsvParseOptions { MaxDataLines = maxRows });
        }

        /// <summary>
        /// Encodes the text as a file on disk would hold it - byte order mark included, which
        /// <see cref="Encoding.GetBytes(string)"/> never writes by itself.
        /// </summary>
        private static UserOrgCsvParseResult Parse(string content, Encoding encoding, UserOrgCsvParseOptions options)
        {
            var enc = encoding ?? new UTF8Encoding(false);
            return ParseBytes(enc.GetPreamble().Concat(enc.GetBytes(content)).ToArray(), options);
        }

        private static UserOrgCsvParseResult ParseBytes(byte[] bytes, UserOrgCsvParseOptions options = null)
        {
            using (var stream = new MemoryStream(bytes))
            {
                return UserOrgCsvParser.Parse(stream, options ?? new UserOrgCsvParseOptions());
            }
        }

        [TestMethod]
        public void ReadsASimpleFileWithAHeader()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,Retail\r\nb@contoso.com,Wholesale\r\n");

            Assert.IsTrue(result.HeaderDetected);
            Assert.AreEqual(',', result.Delimiter);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual("a@contoso.com", result.Rows[0].Upn);
            Assert.AreEqual("Retail", result.Rows[0].OrgValue);
            Assert.AreEqual(0, result.Problems.Count);
        }

        [TestMethod]
        public void LineNumbersPointAtTheRealLineInTheFile()
        {
            // The admin has to be able to open the file and find the row we are complaining about.
            var result = Parse("UPN,OrgName\r\na@contoso.com,Retail\r\n,Orphan\r\nc@contoso.com,Ops\r\n");

            Assert.AreEqual(2, result.Rows[0].LineNumber);
            Assert.AreEqual(4, result.Rows[1].LineNumber);
            Assert.AreEqual(3, result.Problems.Single().LineNumber);
        }

        [TestMethod]
        public void AcceptsTheColumnsInEitherOrder()
        {
            var result = Parse("Organisation,UserPrincipalName\r\nRetail,a@contoso.com\r\n");

            Assert.IsTrue(result.HeaderDetected);
            Assert.AreEqual("a@contoso.com", result.Rows.Single().Upn);
            Assert.AreEqual("Retail", result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void AcceptsABespokeOrganisationColumnName()
        {
            var result = Parse("upn,Widget Group\r\na@contoso.com,Blue\r\n");

            Assert.IsTrue(result.HeaderDetected);
            Assert.AreEqual("Widget Group", result.OrgColumnName);
            Assert.AreEqual("Blue", result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void FallsBackToColumnOrderWhenThereIsNoRecognisableHeader()
        {
            var result = Parse("a@contoso.com,Retail\r\nb@contoso.com,Ops\r\n");

            Assert.IsFalse(result.HeaderDetected, "The first line is data, not a header.");
            Assert.AreEqual(2, result.Rows.Count, "No row may be swallowed as a header.");
            Assert.AreEqual("a@contoso.com", result.Rows[0].Upn);
        }

        [TestMethod]
        public void AFirstRowHoldingAUpnIsDataEvenWhenAValueReadsLikeAHeader()
        {
            // "User" is an ordinary value for a Role or Persona type - and it is also a word a header
            // uses for the user column. A header never holds an email address, so this first row is Alice.
            var result = Parse("alice@contoso.com,User\r\nbob@contoso.com,Admin\r\n");

            Assert.IsFalse(result.HeaderDetected);
            CollectionAssert.AreEqual(new[] { "alice@contoso.com", "bob@contoso.com" }, result.Rows.Select(r => r.Upn).ToArray());
            Assert.AreEqual("User", result.Rows[0].OrgValue);

            var chosen = Parse("alice@contoso.com,User\r\nbob@contoso.com,Admin\r\n", null, new UserOrgCsvParseOptions { UserColumn = 0, ValueColumn = 1 });
            Assert.AreEqual(2, chosen.Rows.Count, "Choosing the columns does not make the first row a header either.");

            var withHeader = Parse("User,Role\r\nalice@contoso.com,User\r\n");
            Assert.IsTrue(withHeader.HeaderDetected, "A real header is still recognised.");
            Assert.AreEqual("alice@contoso.com", withHeader.Rows.Single().Upn);
        }

        [TestMethod]
        public void DetectsASemicolonDelimitedFile()
        {
            // Excel writes semicolons on any machine whose locale uses a comma as the decimal
            // separator, which is most of Europe. Assuming a comma would import nothing at all.
            var result = Parse("UPN;OrgName\r\na@contoso.com;Retail\r\nb@contoso.com;Ops\r\n");

            Assert.AreEqual(';', result.Delimiter);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual("Retail", result.Rows[0].OrgValue);
        }

        [TestMethod]
        public void DetectsATabDelimitedFile()
        {
            var result = Parse("UPN\tOrgName\r\na@contoso.com\tRetail\r\n");

            Assert.AreEqual('\t', result.Delimiter);
            Assert.AreEqual("Retail", result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void HandlesQuotedFieldsContainingTheDelimiter()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,\"Retail, North\"\r\n");

            Assert.AreEqual("Retail, North", result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void HandlesEscapedQuotesInsideAQuotedField()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,\"The \"\"Blue\"\" Team\"\r\n");

            Assert.AreEqual("The \"Blue\" Team", result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void ARowRunningOnAcrossLinesIsRefusedWithTheLinesNamed()
        {
            // A two-column file of names never legitimately spans lines, so a record that does is a
            // stray quotation mark that swallowed the lines after it. Those people would look absent -
            // which a Replace treats as "clear their value" - so the file is refused, and the admin is
            // told exactly which lines to look at.
            var result = Parse("UPN,OrgName\r\na@contoso.com,\"Retail\nNorth\"\r\nb@contoso.com,Ops\r\n");

            Assert.IsNotNull(result.Blocking);
            Assert.AreEqual(UserOrgCsvBlockingCodes.RowSpansLines, result.Blocking.Code);
            Assert.AreEqual(2, result.Blocking.Line);
            Assert.AreEqual(3, result.Blocking.LastLine);
        }

        [TestMethod]
        public void TwoStrayQuotesNoLongerTurnFiveRowsIntoThree()
        {
            // The quote opening "Retail North on line 2 is closed by the one before Finance on line 4,
            // so lines 2-4 used to read as ONE row - b@contoso.com silently vanished, and a@contoso.com's
            // value became the text of two other people's rows.
            var result = Parse(
                "UPN,Site\r\n"
                + "a@contoso.com,\"Retail North\r\n"
                + "b@contoso.com,Ops\r\n"
                + "c@contoso.com,\"Finance\r\n"
                + "d@contoso.com,Legal\r\n"
                + "e@contoso.com,HR\r\n");

            Assert.AreEqual(UserOrgCsvBlockingCodes.RowSpansLines, result.Blocking?.Code);
            Assert.AreEqual(2, result.Blocking.Line);
            Assert.AreEqual(4, result.Blocking.LastLine);
        }

        [TestMethod]
        public void HandlesUnixLineEndings()
        {
            var result = Parse("UPN,OrgName\na@contoso.com,Retail\nb@contoso.com,Ops\n");

            Assert.AreEqual(2, result.Rows.Count);
        }

        [TestMethod]
        public void HandlesAUtf8ByteOrderMark()
        {
            // Excel writes a BOM. Without honouring it the first header cell would start with a
            // zero-width character and the header would not be recognised.
            var result = Parse("UPN,OrgName\r\na@contoso.com,Retail\r\n", new UTF8Encoding(true));

            Assert.IsTrue(result.HeaderDetected);
            Assert.AreEqual("a@contoso.com", result.Rows.Single().Upn);
        }

        [TestMethod]
        public void PreservesNonLatinOrganisationNames()
        {
            // This is the whole reason every org column is nvarchar.
            var result = Parse("UPN,OrgName\r\na@contoso.com," + GreekOrgName + "\r\n");

            Assert.AreEqual(GreekOrgName, result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void PreservesNonLatinNamesThroughABom()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com," + GreekOrgName + "\r\n", new UTF8Encoding(true));

            Assert.AreEqual(GreekOrgName, result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void ABlankOrganisationMeansClearTheValue()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,\r\nb@contoso.com,   \r\n");

            Assert.AreEqual(2, result.Rows.Count);
            Assert.IsNull(result.Rows[0].OrgValue);
            Assert.IsNull(result.Rows[1].OrgValue);
            Assert.AreEqual(0, result.Problems.Count, "A blank organisation is an instruction, not a problem.");
        }

        [TestMethod]
        public void ReportsRowsWithNoUser()
        {
            var result = Parse("UPN,OrgName\r\n,Retail\r\na@contoso.com,Ops\r\n");

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(1, result.Problems.Count);
            StringAssert.Contains(result.Problems[0].Reason, "empty");
        }

        [TestMethod]
        public void ReportsAUserValueThatCannotBeAnEntraUpn()
        {
            // Entra restricts a UPN to a known ASCII set, so a non-Latin value cannot match anybody.
            // Saying so beats letting the row fail silently as an unknown user later on.
            var result = Parse("UPN,OrgName\r\nΚαλημέρα@contoso.com,Retail\r\n");

            Assert.AreEqual(0, result.Rows.Count);
            StringAssert.Contains(result.Problems.Single().Reason, "user principal name");
        }

        [TestMethod]
        public void PrefersTheDelimiterThatLeavesAUsableUserPrincipalName()
        {
            // A semicolon-separated file whose organisation names contain commas splits just as
            // consistently on the comma. Breaking that tie by the order the candidates happen to be
            // listed in produced UPNs like "a@contoso.com;Retail" - which match nobody, and in a
            // Replace import "matches nobody" means every user in the file loses their value.
            var result = Parse("a@contoso.com;Retail, North\r\nb@contoso.com;Wholesale, South\r\n");

            Assert.AreEqual(';', result.Delimiter);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual("a@contoso.com", result.Rows[0].Upn);
            Assert.AreEqual("Retail, North", result.Rows[0].OrgValue);
        }

        [TestMethod]
        public void AMisreadDelimiterIsReportedRatherThanStagedAsGarbage()
        {
            // Belt and braces for the case above: even if detection somehow picked the wrong separator,
            // the resulting value cannot be an Entra UPN, so the row is refused rather than staged.
            var result = Parse("UPN,OrgName\r\na@contoso.com;Retail\r\n");

            Assert.AreEqual(0, result.Rows.Count);
            Assert.AreEqual(1, result.Problems.Count);
        }

        [TestMethod]
        public void AUserColumnWithNoAtSignIsRefused()
        {
            var result = Parse("UPN,OrgName\r\nCONTOSO\\adele,Retail\r\n");

            Assert.AreEqual(0, result.Rows.Count);
            StringAssert.Contains(result.Problems.Single().Reason, "user principal name");
        }

        [TestMethod]
        public void AnUnterminatedQuoteIsReportedInsteadOfSwallowingTheRestOfTheFile()
        {
            // A missing closing quote pulls every later line into one value. Accepting that would make
            // all those users look absent - and in a Replace import absent means their value is cleared.
            var result = Parse("UPN,OrgName\r\na@contoso.com,\"Retail\r\nb@contoso.com,Ops\r\nc@contoso.com,Finance\r\n");

            Assert.IsTrue(result.UnterminatedQuote, "The caller refuses the import on this flag.");
            Assert.AreEqual(UserOrgCsvBlockingCodes.UnterminatedQuote, result.Blocking?.Code);
            Assert.AreEqual(2, result.Blocking.Line, "The admin is told where the quote opens.");
        }

        [TestMethod]
        public void AFileSavedInAWindowsCodePageIsRefusedRatherThanCorrupted()
        {
            // Excel's plain "CSV (Comma delimited)" is saved in the Windows code page. Read as UTF-8,
            // every Greek name became a run of U+FFFD - so two different organisations of the same
            // length silently collapsed into one, while the ASCII UPNs still matched.
            var result = Parse("UPN,City\r\na@contoso.com,Paris\r\nb@contoso.com,Αθήνα\r\nc@contoso.com,Πάτρα\r\n", Encoding.GetEncoding(1253));

            Assert.AreEqual(UserOrgCsvBlockingCodes.NotUtf8, result.Blocking?.Code);
            Assert.AreEqual(3, result.Blocking.Line, "The first line with a character that is not UTF-8.");
            Assert.AreEqual(0, result.Rows.Count, "Nothing is read from a file that cannot be decoded faithfully.");
        }

        [TestMethod]
        public void AnAccentInWestern1252IsRefusedToo()
        {
            var result = Parse("UPN,Office\r\na@contoso.com,Café\r\n", Encoding.GetEncoding(1252));

            Assert.AreEqual(UserOrgCsvBlockingCodes.NotUtf8, result.Blocking?.Code);
            Assert.AreEqual(2, result.Blocking.Line);
        }

        [TestMethod]
        public void ReadsUtf16WithAByteOrderMarkInEitherByteOrder()
        {
            // Excel's "Unicode Text" is UTF-16 with a byte order mark - tab-separated, and faithful to
            // every script.
            foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode })
            {
                var result = Parse("UPN\tCity\r\na@contoso.com\t" + GreekOrgName + "\r\n", encoding);

                Assert.IsNull(result.Blocking, encoding.WebName);
                Assert.AreEqual('\t', result.Delimiter, encoding.WebName);
                Assert.AreEqual(GreekOrgName, result.Rows.Single().OrgValue, encoding.WebName);
            }
        }

        [TestMethod]
        public void RefusesUtf16WithoutAByteOrderMark()
        {
            var result = Parse("UPN,City\r\na@contoso.com,Paris\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: false));

            Assert.AreEqual(UserOrgCsvBlockingCodes.NotText, result.Blocking?.Code);
        }

        [TestMethod]
        public void RecognisesAnExcelWorkbookUploadedByMistake()
        {
            // The commonest wrong upload. Read as text it produced a wall of "not a valid user
            // principal name" problems that said nothing about the actual mistake.
            var xlsx = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00 };
            var xls = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00 };

            Assert.AreEqual(UserOrgCsvBlockingCodes.ExcelWorkbook, ParseBytes(xlsx).Blocking?.Code);
            Assert.AreEqual(UserOrgCsvBlockingCodes.ExcelWorkbook, ParseBytes(xls).Blocking?.Code);
        }

        [TestMethod]
        public void RefusesABinaryFile()
        {
            var result = ParseBytes(new byte[] { 0x55, 0x50, 0x4E, 0x00, 0x01, 0x02, 0x03, 0x2C, 0x00, 0x0A });

            Assert.AreEqual(UserOrgCsvBlockingCodes.NotText, result.Blocking?.Code);
        }

        [TestMethod]
        public void TheUserPrincipalNameColumnBeatsAnEmailColumnBeforeIt()
        {
            // An HR export commonly carries email beside UPN, and in a hybrid tenant the two differ.
            // Taking whichever came first matched nobody.
            var result = Parse(
                "Email,UserPrincipalName,Department\r\nadele@contoso.co.uk,adele@contoso.com,Retail\r\n",
                null,
                new UserOrgCsvParseOptions { OrgTypeName = "Department" });

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(1, result.UserColumnIndex);
            Assert.AreEqual(2, result.ValueColumnIndex);
            Assert.AreEqual("adele@contoso.com", result.Rows.Single().Upn);
        }

        [TestMethod]
        public void AWideExportAsksWhichColumnRatherThanGuessing()
        {
            // Taking "the first other column" imported everyone's employee number as their cost centre
            // from an ordinary HR export, with no problem reported.
            const string wide = "EmployeeId,DisplayName,UserPrincipalName,Cost Centre Code\r\n"
                + "10001,Adele Vance,adele@contoso.com,CC-100\r\n";

            var result = Parse(wide, null, new UserOrgCsvParseOptions { OrgTypeName = "Cost Centre" });

            Assert.AreEqual(UserOrgCsvBlockingCodes.ChooseColumns, result.Blocking?.Code);
            Assert.AreEqual(2, result.UserColumnIndex, "The user column is still found, so the portal can preselect it.");
            Assert.IsNull(result.ValueColumnIndex);
            CollectionAssert.AreEqual(
                new[] { "EmployeeId", "DisplayName", "UserPrincipalName", "Cost Centre Code" },
                result.Columns.ToArray(),
                "The admin chooses from the header's own names.");
            Assert.AreEqual(0, result.Rows.Count, "Nothing is read until the columns are settled.");
        }

        [TestMethod]
        public void AHeaderNamedAfterTheOrgTypePicksTheValueColumn()
        {
            const string wide = "EmployeeId,DisplayName,UserPrincipalName,Cost Centre Code\r\n"
                + "10001,Adele Vance,adele@contoso.com,CC-100\r\n";

            var result = Parse(wide, null, new UserOrgCsvParseOptions { OrgTypeName = " cost centre code " });

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(3, result.ValueColumnIndex);
            Assert.AreEqual("CC-100", result.Rows.Single().OrgValue);
        }

        [TestMethod]
        public void AnExplicitColumnChoiceIsHonoured()
        {
            const string wide = "EmployeeId,DisplayName,UserPrincipalName,Cost Centre Code\r\n"
                + "10001,Adele Vance,adele@contoso.com,CC-100\r\n";

            var result = Parse(wide, null, new UserOrgCsvParseOptions { UserColumn = 2, ValueColumn = 1 });

            Assert.IsNull(result.Blocking);
            Assert.AreEqual("adele@contoso.com", result.Rows.Single().Upn);
            Assert.AreEqual("Adele Vance", result.Rows.Single().OrgValue);
            Assert.AreEqual("DisplayName", result.OrgColumnName);
        }

        [TestMethod]
        public void AColumnChoiceOutsideTheFileAsksAgain()
        {
            var result = Parse("UPN,Department\r\na@contoso.com,Retail\r\n", null, new UserOrgCsvParseOptions { UserColumn = 0, ValueColumn = 5 });

            Assert.AreEqual(UserOrgCsvBlockingCodes.ChooseColumns, result.Blocking?.Code);
        }

        [TestMethod]
        public void AOneColumnFileIsRefused()
        {
            var result = Parse("UPN\r\na@contoso.com\r\nb@contoso.com\r\n");

            Assert.AreEqual(UserOrgCsvBlockingCodes.OneColumn, result.Blocking?.Code);
        }

        [TestMethod]
        public void ATrailingEmptyColumnIsNotAChoiceToMake()
        {
            // A spreadsheet with an empty column after the data writes a trailing delimiter on every
            // line. That is still a two-column file.
            var result = Parse("UPN,Department,\r\na@contoso.com,Retail,\r\nb@contoso.com,Ops,\r\n");

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(3, result.ColumnCount);
            Assert.AreEqual(1, result.ValueColumnIndex);
            Assert.AreEqual(2, result.Rows.Count);
        }

        [TestMethod]
        public void ARowWithMoreValuesThanTheOthersIsReportedNotCutShort()
        {
            // "Retail, North" without quotes splits into two fields, well past the rows the layout is read
            // from. Reading only the chosen columns imported "Retail" for Alice, and reported nothing.
            var lines = new StringBuilder("UPN,Team\r\n");
            for (var i = 1; i <= 25; i++)
            {
                lines.Append("user").Append(i).Append("@contoso.com,Wholesale\r\n");
            }

            lines.Append("alice@contoso.com,Retail, North\r\n");
            var result = Parse(lines.ToString());

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(25, result.Rows.Count);
            Assert.IsFalse(result.Rows.Any(r => r.Upn == "alice@contoso.com"), "Alice is not imported with half her value.");
            var problem = result.Problems.Single();
            Assert.AreEqual(UserOrgCsvProblemCodes.TooManyValues, problem.Code);
            Assert.AreEqual(27, problem.LineNumber);
            Assert.AreEqual("alice@contoso.com", problem.Upn);
            StringAssert.Contains(problem.OrgValue, "Retail, North", "Shown as it was meant, so the admin can see what to quote.");
        }

        [TestMethod]
        public void ARowWithMoreValuesAmongTheFirstRowsIsReportedToo()
        {
            // Inside the sample the extra field makes a third column, so the width is taken from the header
            // and from what most rows carry - not from the widest row.
            var result = Parse(
                "UPN,Team\r\na@contoso.com,Retail, North\r\nb@contoso.com,Wholesale\r\nc@contoso.com,Ops\r\n",
                null,
                new UserOrgCsvParseOptions { OrgTypeName = "Team" });

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual(UserOrgCsvProblemCodes.TooManyValues, result.Problems.Single().Code);
            Assert.AreEqual(2, result.Problems.Single().LineNumber);
        }

        [TestMethod]
        public void ARowWithOneValueTooManyIsReportedEvenWhenEveryLineEndsInASeparator()
        {
            // Excel writes the trailing separator on the header too. Counting the header to its last
            // separator made the allowance three columns, so exactly one stray separator got through on
            // every row of the commonest layout there is.
            var lines = new StringBuilder("UPN,Department,\r\n");
            for (var i = 1; i <= 25; i++)
            {
                lines.Append("user").Append(i).Append("@contoso.com,Wholesale,\r\n");
            }

            lines.Append("alice@contoso.com,Retail, North\r\n");
            var result = Parse(lines.ToString());

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(25, result.Rows.Count);
            Assert.AreEqual(UserOrgCsvProblemCodes.TooManyValues, result.Problems.Single().Code);
            Assert.AreEqual("alice@contoso.com", result.Problems.Single().Upn);
        }

        [TestMethod]
        public void WhenTwoWidthsAreEquallyCommonTheWiderRowsAreReported()
        {
            // Two rows, one of them ragged: nothing says which width is the file's own. Imported at the
            // wider one, Bob became "Retail" with no problem reported; reported, the admin sees why.
            var result = Parse(
                "UPN,Team\r\na@contoso.com,Wholesale\r\nb@contoso.com,Retail, North\r\n",
                null,
                new UserOrgCsvParseOptions { OrgTypeName = "Team" });

            Assert.IsNull(result.Blocking);
            Assert.AreEqual("a@contoso.com", result.Rows.Single().Upn);
            Assert.AreEqual(UserOrgCsvProblemCodes.TooManyValues, result.Problems.Single().Code);
            Assert.AreEqual(3, result.Problems.Single().LineNumber);
        }

        [TestMethod]
        public void ExtraSeparatorsAndConsistentlyWiderRowsAreStillRead()
        {
            // Trailing separators carry no text, and a file whose every row is wider than its header - an
            // export whose last column has no heading - is consistent rather than ragged.
            var trailing = Parse("UPN,Team\r\na@contoso.com,Retail,\r\nb@contoso.com,Ops,,\r\n");
            Assert.IsNull(trailing.Blocking);
            Assert.AreEqual(2, trailing.Rows.Count);
            Assert.AreEqual(0, trailing.Problems.Count);

            var wider = Parse(
                "UPN,Team\r\na@contoso.com,Retail,note\r\nb@contoso.com,Ops,note\r\nc@contoso.com,HR,note\r\n",
                null,
                new UserOrgCsvParseOptions { OrgTypeName = "Team" });
            Assert.IsNull(wider.Blocking);
            Assert.AreEqual(3, wider.Rows.Count);
            Assert.AreEqual(0, wider.Problems.Count);
            Assert.AreEqual("Retail", wider.Rows[0].OrgValue);

            // A column the header names may hold text on some rows only.
            var named = Parse(
                "UPN,Team,Notes\r\na@contoso.com,Retail\r\nb@contoso.com,Ops,started in May\r\n",
                null,
                new UserOrgCsvParseOptions { OrgTypeName = "Team" });
            Assert.IsNull(named.Blocking);
            Assert.AreEqual(2, named.Rows.Count);
            Assert.AreEqual(0, named.Problems.Count);

            // And a field holding nothing but a zero-width space or a stray byte order mark holds no text.
            var invisible = Parse(
                "UPN,Team\r\na@contoso.com,Retail\r\nb@contoso.com,Ops\r\nc@contoso.com,HR\r\nd@contoso.com,IT,\u200B\r\ne@contoso.com,Legal,\uFEFF\r\n",
                null,
                new UserOrgCsvParseOptions { OrgTypeName = "Team" });
            Assert.IsNull(invisible.Blocking);
            Assert.AreEqual(5, invisible.Rows.Count);
            Assert.AreEqual(0, invisible.Problems.Count, "An invisible character is not a value.");
        }

        [TestMethod]
        public void HonoursExcelsSeparatorLine()
        {
            var result = Parse("sep=;\r\nUPN;Department\r\na@contoso.com;Retail, North\r\n");

            Assert.IsNull(result.Blocking);
            Assert.AreEqual(';', result.Delimiter);
            Assert.AreEqual("Retail, North", result.Rows.Single().OrgValue);
            Assert.AreEqual(3, result.Rows.Single().LineNumber, "Line numbers still count the separator line.");
        }

        [TestMethod]
        public void IgnoresInvisibleCharactersAroundAUserPrincipalName()
        {
            // Zero-width characters pasted from a web page or a chat, and a byte order mark left in the
            // middle of two concatenated files, made a correct UPN "not a valid user principal name"
            // with nothing visible to explain it.
            var result = Parse("UPN,Department\r\n\u200Ba@contoso.com\u200B,Retail\r\n\uFEFFb@contoso.com,Ops\r\n");

            Assert.AreEqual(0, result.Problems.Count);
            CollectionAssert.AreEqual(new[] { "a@contoso.com", "b@contoso.com" }, result.Rows.Select(r => r.Upn).ToArray());
        }

        [TestMethod]
        public void SkipsAnEmptySpreadsheetRow()
        {
            var result = Parse("UPN,Department\r\n,\r\na@contoso.com,Retail\r\n , \r\n");

            Assert.AreEqual(0, result.Problems.Count, "A row of empty cells is not a row the admin wrote.");
            Assert.AreEqual(1, result.Rows.Count);
        }

        [TestMethod]
        public void ProblemsCarryACodeAndWhatTheRowSaid()
        {
            // So the portal can word each problem in the admin's language, and list the rows to fix.
            var result = Parse("UPN,Department\r\n,Retail\r\nnot-a-upn,Ops\r\na@contoso.com,Finance\r\n");

            Assert.AreEqual(UserOrgCsvProblemCodes.UserEmptyOrTooLong, result.Problems[0].Code);
            Assert.AreEqual("Retail", result.Problems[0].OrgValue);
            Assert.AreEqual(UserOrgCsvProblemCodes.NotAValidUpn, result.Problems[1].Code);
            Assert.AreEqual("not-a-upn", result.Problems[1].Upn);
            Assert.AreEqual("Ops", result.Problems[1].OrgValue);
            Assert.AreEqual(3, result.Problems[1].LineNumber);
        }

        [TestMethod]
        public void AProblemValueIsShortenedSoAMisreadLineCannotBalloonThePreview()
        {
            var result = Parse("UPN,Department\r\n" + new string('x', 5000) + ",Retail\r\na@contoso.com,Ops\r\n");

            Assert.IsTrue(result.Problems.Single().Upn.Length <= 256);
        }

        [TestMethod]
        public void AFileWithNoUsableRowsSaysSo()
        {
            var result = Parse("UPN,Department\r\nnobody,Retail\r\nsomebody,Ops\r\n");

            Assert.AreEqual(UserOrgCsvBlockingCodes.NoUsableRows, result.Blocking?.Code);
            Assert.AreEqual(2, result.Problems.Count, "The problems are still reported, so the admin can see why.");
        }

        [TestMethod]
        public void TooManyRowsBlocksTheFile()
        {
            var builder = new StringBuilder("UPN,OrgName\r\n");
            for (var i = 0; i < 11; i++)
            {
                builder.Append("user").Append(i).Append("@contoso.com,Org").Append(i).Append("\r\n");
            }

            var result = Parse(builder.ToString(), maxRows: 10);

            Assert.AreEqual(UserOrgCsvBlockingCodes.TooManyRows, result.Blocking?.Code);
        }

        [TestMethod]
        public void AWellFormedFileIsNotFlaggedAsUnterminated()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,\"Retail, North\"\r\nb@contoso.com,Ops\r\n");

            Assert.IsFalse(result.UnterminatedQuote);
            Assert.AreEqual(2, result.Rows.Count);
        }

        [TestMethod]
        public void SkipsTrailingBlankLinesWithoutReportingThem()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,Retail\r\n\r\n\r\n");

            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(0, result.Problems.Count);
            Assert.AreEqual(1, result.DataLinesRead);
        }

        [TestMethod]
        public void StopsAtTheRowCapAndSaysSo()
        {
            // A preview reads the first few rows of a large file; the admin must not be shown a
            // truncated sample as though it were the whole file.
            var builder = new StringBuilder("UPN,OrgName\r\n");
            for (var i = 0; i < 50; i++)
            {
                builder.Append("user").Append(i).Append("@contoso.com,Org").Append(i).Append("\r\n");
            }

            var result = Parse(builder.ToString(), maxRows: 10);

            Assert.AreEqual(10, result.Rows.Count);
            Assert.IsTrue(result.Truncated, "Hitting the cap must be reported, not silently ignored.");
        }

        [TestMethod]
        public void AFileOfExactlyTheAllowedRowsIsNotReportedAsTruncated()
        {
            // The header must not eat one row of the allowance. Reporting truncation here would make
            // the import refuse a file of exactly the supported size with a message saying it was too
            // big - which is both wrong and impossible for the admin to act on.
            var builder = new StringBuilder("UPN,OrgName\r\n");
            for (var i = 0; i < 10; i++)
            {
                builder.Append("user").Append(i).Append("@contoso.com,Org").Append(i).Append("\r\n");
            }

            var result = Parse(builder.ToString(), maxRows: 10);

            Assert.AreEqual(10, result.Rows.Count);
            Assert.IsFalse(result.Truncated, "Exactly the allowance is not over the allowance.");
        }

        [TestMethod]
        public void OneRowPastTheAllowanceIsReportedAsTruncated()
        {
            var builder = new StringBuilder("UPN,OrgName\r\n");
            for (var i = 0; i < 11; i++)
            {
                builder.Append("user").Append(i).Append("@contoso.com,Org").Append(i).Append("\r\n");
            }

            var result = Parse(builder.ToString(), maxRows: 10);

            Assert.AreEqual(10, result.Rows.Count);
            Assert.IsTrue(result.Truncated);
        }

        [TestMethod]
        public void BlankLinesDoNotConsumeTheRowAllowance()
        {
            // Counting blank lines against the cap would let a file padded with them silently lose real
            // rows off the end.
            var builder = new StringBuilder("UPN,OrgName\r\n");
            for (var i = 0; i < 10; i++)
            {
                builder.Append("user").Append(i).Append("@contoso.com,Org").Append(i).Append("\r\n\r\n");
            }

            var result = Parse(builder.ToString(), maxRows: 10);

            Assert.AreEqual(10, result.Rows.Count, "Every real row must survive.");
            Assert.IsFalse(result.Truncated);
        }

        [TestMethod]
        public void AnEmptyFileProducesNothingRatherThanThrowing()
        {
            var result = Parse(string.Empty);

            Assert.AreEqual(0, result.Rows.Count);
            Assert.AreEqual(0, result.Problems.Count);
        }

        [TestMethod]
        public void AHeaderOnlyFileProducesNoRows()
        {
            var result = Parse("UPN,OrgName\r\n");

            Assert.IsTrue(result.HeaderDetected);
            Assert.AreEqual(0, result.Rows.Count);
        }

        [TestMethod]
        public void TruncatesAnOverLongOrganisationValue()
        {
            var tooLong = new string('x', UserOrgRules.MaxOrgValueLength + 40);
            var result = Parse("UPN,OrgName\r\na@contoso.com," + tooLong + "\r\n");

            Assert.AreEqual(UserOrgRules.MaxOrgValueLength, result.Rows.Single().OrgValue.Length);
        }

        [TestMethod]
        public void CountsTruncationSoItIsNotSilent()
        {
            // Shortening beats dropping the user from the organisation, but it cannot be silent: two
            // names that differ only after the cut-off collapse into ONE organisation, and nothing
            // else in the preview or the import summary would ever reveal that.
            var prefix = new string('x', UserOrgRules.MaxOrgValueLength);
            var result = Parse(
                "UPN,OrgName\r\n"
                + "a@contoso.com," + prefix + "Alpha\r\n"
                + "b@contoso.com," + prefix + "Beta\r\n"
                + "c@contoso.com,Retail\r\n");

            Assert.AreEqual(2, result.TruncatedValueCount);
            Assert.AreEqual(
                result.Rows[0].OrgValue,
                result.Rows[1].OrgValue,
                "These two really do become one organisation - which is exactly why it has to be reported.");
        }

        [TestMethod]
        public void ARowMissingItsOrganisationColumnClearsRatherThanFails()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com\r\n");

            Assert.AreEqual(1, result.Rows.Count);
            Assert.IsNull(result.Rows.Single().OrgValue);
        }
    }

    /// <summary>
    /// The background CSV import worker.
    /// </summary>
    [TestClass]
    public class UserOrgImportRunnerTests
    {
        private sealed class FakeJobStore : IUserOrgImportJobStore
        {
            public UserOrgImportJob Job = new UserOrgImportJob
            {
                Id = 1,
                OrgTypeId = 10,
                Mode = UserOrgImportMode.Merge,
                Status = UserOrgImportStatus.Pending,
            };

            public bool ClaimSucceeds = true;
            public int ClaimAttempts;
            public int ApplyCalls;
            public int HeartbeatCalls;
            public Exception ApplyThrows;

            /// <summary>Another instance claims the job while the apply runs, as a takeover of a quiet worker does.</summary>
            public bool TakenOverDuringApply;

            /// <summary>The apply commits - the job says Succeeded - and then the answer is lost.</summary>
            public bool ApplyCommitsThenThrows;
            public TimeSpan ApplyDelay = TimeSpan.Zero;
            public int FailHeartbeatNumber;
            public Exception CompleteThrows;
            public int CompleteCalls;
            public UserOrgImportStatus? CompletedStatus;
            public string CompletedError;
            public string CompletedCode;
            public int? CompletedAttempt;

            public Task<int> CreateJobWithRowsAsync(UserOrgImportJob job, IReadOnlyList<UserOrgStagedRow> rows, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(Job.Id);

            public Task<int> CreateDraftAsync(UserOrgImportJob draft, IReadOnlyList<UserOrgStagedRow> rows, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(Job.Id);

            public Task<UserOrgDraftSummary> SummariseDraftAsync(int draftId, int sampleRows, int unknownRowLimit, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<UserOrgDraftSummary>(null);

            public Task CommitDraftAsync(int draftId, int orgTypeId, UserOrgImportMode mode, int confirmedClearCount, string startedBy, CancellationToken cancellationToken = default(CancellationToken))
                => Task.CompletedTask;

            public Task<UserOrgImportJob> GetJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(Job);

            public Task<IReadOnlyList<UserOrgImportJob>> ListJobsAsync(int orgTypeId, int take, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgImportJob>>(new[] { Job });

            public Task<int?> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                ClaimAttempts++;
                if (ClaimSucceeds)
                {
                    Job.Status = UserOrgImportStatus.Running;
                    Job.Attempts++;
                }
                return Task.FromResult(ClaimSucceeds ? Job.Attempts : (int?)null);
            }

            public Task HeartbeatAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                HeartbeatCalls++;
                if (HeartbeatCalls == FailHeartbeatNumber)
                {
                    throw new InvalidOperationException("transient heartbeat failure");
                }
                return Task.CompletedTask;
            }

            public async Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                ApplyCalls++;
                if (TakenOverDuringApply)
                {
                    Job.Attempts++;
                }
                if (ApplyThrows != null)
                {
                    throw ApplyThrows;
                }
                if (ApplyDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ApplyDelay).ConfigureAwait(false);
                }

                // As the real apply does: the success is recorded with the changes.
                Job.RowsApplied = 3;
                Job.Status = UserOrgImportStatus.Succeeded;

                if (ApplyCommitsThenThrows)
                {
                    throw new InvalidOperationException("the connection dropped after the commit");
                }

                return Job;
            }

            public Task CompleteJobAsync(
                int jobId,
                UserOrgImportStatus status,
                string errorMessage,
                CancellationToken cancellationToken = default(CancellationToken),
                string errorCode = null,
                int? claimedAttempt = null)
            {
                CompleteCalls++;
                if (CompleteThrows != null)
                {
                    throw CompleteThrows;
                }
                CompletedStatus = status;
                CompletedError = errorMessage;
                CompletedCode = errorCode;
                CompletedAttempt = claimedAttempt;

                // As the real store does: only a job that is still live is rewritten, and only by its claim.
                if ((Job.Status == UserOrgImportStatus.Pending || Job.Status == UserOrgImportStatus.Running)
                    && (!claimedAttempt.HasValue || claimedAttempt.Value == Job.Attempts))
                {
                    Job.Status = status;
                    Job.ErrorCode = errorCode;
                    Job.ErrorMessage = errorMessage;
                }
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<int>> ResumeStaleJobsAsync(CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<int>>(new int[0]);

            public Task<UserOrgImportJob> GetActiveJobForTypeAsync(int orgTypeId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<UserOrgImportJob>(null);
        }

        private sealed class RecordingTelemetry : IUserOrgImportTelemetry
        {
            public readonly List<UserOrgImportTelemetryEvent> Events = new List<UserOrgImportTelemetryEvent>();

            public List<string> Stages
            {
                get
                {
                    lock (Events)
                    {
                        return Events.Select(e => e.Stage).ToList();
                    }
                }
            }

            public void Record(UserOrgImportTelemetryEvent item)
            {
                lock (Events)
                {
                    Events.Add(item);
                }
            }
        }

        [TestMethod]
        public async Task ClaimsAppliesAndCompletesAJob()
        {
            var store = new FakeJobStore();

            var job = await new UserOrgImportRunner(store).RunAsync(1);

            Assert.AreEqual(1, store.ClaimAttempts);
            Assert.AreEqual(1, store.ApplyCalls);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, store.CompletedStatus, "The completion only tidies up the staged rows.");
            Assert.IsNull(store.CompletedError);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
        }

        [TestMethod]
        public async Task ReportsTheLifecycleToTelemetry()
        {
            var store = new FakeJobStore();
            store.Job.RowsTotal = 5;
            var telemetry = new RecordingTelemetry();

            await new UserOrgImportRunner(store, telemetry).RunAsync(1);

            CollectionAssert.AreEqual(new[] { UserOrgImportStages.Claimed, UserOrgImportStages.Succeeded }, telemetry.Stages);
            var succeeded = telemetry.Events.Last();
            Assert.AreEqual(1, succeeded.JobId);
            Assert.AreEqual(10, succeeded.OrgTypeId);
            Assert.AreEqual(UserOrgImportMode.Merge, succeeded.Mode);
            Assert.AreEqual(5, succeeded.Rows);
            Assert.AreEqual(3, succeeded.RowsApplied);
            Assert.AreEqual(1, succeeded.Attempts);
            Assert.IsNotNull(succeeded.DurationMs);
        }

        [TestMethod]
        public async Task DoesNotImportAJobAnotherInstanceAlreadyClaimed()
        {
            // Two web instances polling the same pending job must not import the file twice.
            var store = new FakeJobStore { ClaimSucceeds = false };
            var telemetry = new RecordingTelemetry();

            await new UserOrgImportRunner(store, telemetry).RunAsync(1);

            Assert.AreEqual(0, store.ApplyCalls, "A job that could not be claimed must not be applied.");
            Assert.IsNull(store.CompletedStatus);
            CollectionAssert.AreEqual(new[] { UserOrgImportStages.NotClaimed }, telemetry.Stages);
        }

        [TestMethod]
        public async Task RecordsAFailureOnTheJobAndRethrows()
        {
            // The request that queued this has long since returned, so nothing is awaiting the task.
            // Without recording it the admin would see a job stuck on "Running" and never learn why.
            var store = new FakeJobStore { ApplyThrows = new InvalidOperationException("merge exploded") };
            var telemetry = new RecordingTelemetry();

            try
            {
                await new UserOrgImportRunner(store, telemetry).RunAsync(1);
                Assert.Fail("The original fault must still surface.");
            }
            catch (InvalidOperationException ex)
            {
                Assert.AreEqual("merge exploded", ex.Message, "The real exception goes to the service logs intact.");
            }

            Assert.AreEqual(UserOrgImportStatus.Failed, store.CompletedStatus);
            Assert.AreEqual(UserOrgImportErrorCodes.Failed, store.CompletedCode);

            // A fixed message, not the exception text. This is rendered verbatim in the portal, and a
            // SQL or Graph exception can carry object names, index names, duplicate key values and
            // identities.
            Assert.IsFalse(
                store.CompletedError.Contains("merge exploded"),
                "The raw exception message must not reach the browser.");
            StringAssert.Contains(store.CompletedError, "could not be completed");

            var failed = telemetry.Events.Last();
            Assert.AreEqual(UserOrgImportStages.Failed, failed.Stage);
            Assert.AreEqual(nameof(InvalidOperationException), failed.ExceptionType, "The type, never the message.");
        }

        [TestMethod]
        public async Task AWorkerTakenOverWhileItRanRecordsNothingOverTheClaimThatTookItOver()
        {
            // It went quiet long enough for another instance to claim the job, then faulted. The failure is
            // fenced on this worker's own claim, so the takeover's is left to finish.
            var store = new FakeJobStore { ApplyThrows = new InvalidOperationException("merge exploded"), TakenOverDuringApply = true };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => new UserOrgImportRunner(store, new RecordingTelemetry()).RunAsync(1));

            Assert.AreEqual(1, store.CompletedAttempt, "The failure names the claim that is reporting it.");
            Assert.AreEqual(2, store.Job.Attempts);
            Assert.AreEqual(UserOrgImportStatus.Running, store.Job.Status, "The claim that took over is untouched.");
        }

        [TestMethod]
        public async Task ARefusalIsRecordedWithItsCodeRatherThanThrown()
        {
            // The apply's own re-check refusing the import - the type changed, or it would now clear
            // more users than were confirmed - is an answer for the admin, not a fault for an engineer.
            var store = new FakeJobStore
            {
                ApplyThrows = new UserOrgValidationException("would clear more than confirmed", UserOrgImportErrorCodes.ClearExceedsConfirmed),
            };
            var telemetry = new RecordingTelemetry();

            var job = await new UserOrgImportRunner(store, telemetry).RunAsync(1);

            Assert.AreEqual(UserOrgImportStatus.Failed, job.Status);
            Assert.AreEqual(UserOrgImportErrorCodes.ClearExceedsConfirmed, store.CompletedCode, "The portal words the outcome from this.");
            Assert.AreEqual("would clear more than confirmed", store.CompletedError);
            Assert.AreEqual(UserOrgImportStages.Refused, telemetry.Events.Last().Stage);
            Assert.AreEqual(UserOrgImportErrorCodes.ClearExceedsConfirmed, telemetry.Events.Last().Code);
        }

        [TestMethod]
        public async Task ADeclinedJobIsRecordedRatherThanLeftRunningForever()
        {
            // A job refused by the apply fence has no replacement to carry its verdict - the type was
            // simply reconfigured, or the lock was held. Leaving it Running means the portal reports
            // it as interrupted and its staged rows sit in the database indefinitely.
            var store = new DeclineOnApplyJobStore();

            var job = await new UserOrgImportRunner(store).RunAsync(1);

            Assert.AreEqual(UserOrgImportStatus.Failed, store.CompletedStatus);
            Assert.AreEqual(UserOrgImportErrorCodes.Superseded, store.CompletedCode);
            StringAssert.Contains(store.CompletedError, "overtaken");
            Assert.IsNotNull(job);
        }

        private sealed class DeclineOnApplyJobStore : IUserOrgImportJobStore
        {
            private readonly UserOrgImportJob _job = new UserOrgImportJob
            {
                Id = 1,
                OrgTypeId = 10,
                Mode = UserOrgImportMode.Merge,
                Status = UserOrgImportStatus.Pending,
            };

            public UserOrgImportStatus? CompletedStatus;
            public string CompletedError;
            public string CompletedCode;

            public Task<int> CreateJobWithRowsAsync(UserOrgImportJob job, IReadOnlyList<UserOrgStagedRow> rows, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(_job.Id);

            public Task<int> CreateDraftAsync(UserOrgImportJob draft, IReadOnlyList<UserOrgStagedRow> rows, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(_job.Id);

            public Task<UserOrgDraftSummary> SummariseDraftAsync(int draftId, int sampleRows, int unknownRowLimit, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<UserOrgDraftSummary>(null);

            public Task CommitDraftAsync(int draftId, int orgTypeId, UserOrgImportMode mode, int confirmedClearCount, string startedBy, CancellationToken cancellationToken = default(CancellationToken))
                => Task.CompletedTask;

            public Task<UserOrgImportJob> GetJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(_job);

            public Task<IReadOnlyList<UserOrgImportJob>> ListJobsAsync(int orgTypeId, int take, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<UserOrgImportJob>>(new[] { _job });

            public Task<int?> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                _job.Status = UserOrgImportStatus.Running;
                return Task.FromResult<int?>(1);
            }

            public Task HeartbeatAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.CompletedTask;

            public Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
                => throw new UserOrgJobSupersededException("declined", new InvalidOperationException());

            public Task CompleteJobAsync(
                int jobId,
                UserOrgImportStatus status,
                string errorMessage,
                CancellationToken cancellationToken = default(CancellationToken),
                string errorCode = null,
                int? claimedAttempt = null)
            {
                CompletedStatus = status;
                CompletedError = errorMessage;
                CompletedCode = errorCode;
                _job.Status = status;
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<int>> ResumeStaleJobsAsync(CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<IReadOnlyList<int>>(new int[0]);

            public Task<UserOrgImportJob> GetActiveJobForTypeAsync(int orgTypeId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<UserOrgImportJob>(null);
        }

        [TestMethod]
        public async Task AFailedCleanUpDoesNotTurnASuccessIntoAFailure()
        {
            // The apply records its success in the transaction that makes the changes, so what is left
            // afterwards is only removing the staged rows. That failing must not be reported as a failed
            // import - "No partial changes were kept" would be a flat lie inviting the admin to upload
            // again, which for a Replace is another full clear-and-repopulate. The next resume sweep
            // removes the rows instead.
            var store = new FakeJobStore { CompleteThrows = new InvalidOperationException("status write failed") };
            var telemetry = new RecordingTelemetry();

            var job = await new UserOrgImportRunner(store, telemetry).RunAsync(1);

            Assert.AreEqual(1, store.ApplyCalls, "The file was applied.");
            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            CollectionAssert.AreEqual(
                new[] { UserOrgImportStages.Claimed, UserOrgImportStages.CleanupFailed, UserOrgImportStages.Succeeded },
                telemetry.Stages);
        }

        [TestMethod]
        public async Task AnErrorAfterTheCommitIsReportedAsTheSuccessItWas()
        {
            // The connection can drop after the apply has committed, so the answer never arrives. The
            // job row is the authority: the apply wrote Succeeded with the changes, and recording a
            // failure only rewrites a job that is still live.
            var store = new FakeJobStore { ApplyCommitsThenThrows = true };
            var telemetry = new RecordingTelemetry();

            var job = await new UserOrgImportRunner(store, telemetry).RunAsync(1);

            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, store.Job.Status, "Nothing may overwrite the success.");
            var succeeded = telemetry.Events.Last();
            Assert.AreEqual(UserOrgImportStages.Succeeded, succeeded.Stage);
            Assert.AreEqual(nameof(InvalidOperationException), succeeded.ExceptionType, "Still visible to an engineer.");
        }

        [TestMethod]
        public async Task OneLostHeartbeatDoesNotStopTheRest()
        {
            // The catch used to sit outside the while loop, so a single transient SQL error stopped a
            // perfectly healthy import reporting progress for the rest of a merge. After
            // StaleHeartbeatThreshold that makes it look abandoned, which lets a SECOND import for the
            // same org type start alongside it.
            var store = new FakeJobStore
            {
                ApplyDelay = TimeSpan.FromMilliseconds(
                    UserOrgImportRunner.HeartbeatInterval.TotalMilliseconds * 2.5),
                FailHeartbeatNumber = 1,
            };
            var telemetry = new RecordingTelemetry();

            await new UserOrgImportRunner(store, telemetry).RunAsync(1);

            Assert.IsTrue(
                store.HeartbeatCalls >= 2,
                $"The loop must keep beating after a failed beat, but it stopped at {store.HeartbeatCalls}.");
            Assert.AreEqual(UserOrgImportStatus.Succeeded, store.CompletedStatus);
            Assert.AreEqual(1, telemetry.Stages.Count(s => s == UserOrgImportStages.HeartbeatFailed), "A lost beat is visible, once.");
        }

        [TestMethod]
        public void NeedsResume_AJobWhoseWorkerIsGone()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(
                UserOrgImportRunner.NeedsResume(new UserOrgImportJob { Status = UserOrgImportStatus.Pending, QueuedUtc = now.AddSeconds(-5) }, now),
                "A job queued moments ago is being dispatched.");
            Assert.IsTrue(
                UserOrgImportRunner.NeedsResume(
                    new UserOrgImportJob { Status = UserOrgImportStatus.Pending, QueuedUtc = now - UserOrgImportJobLimits.LostDispatchGrace - TimeSpan.FromSeconds(1) },
                    now),
                "Its dispatch died with the web process that queued it.");
            Assert.IsTrue(
                UserOrgImportRunner.NeedsResume(
                    new UserOrgImportJob { Status = UserOrgImportStatus.Running, HeartbeatUtc = now - UserOrgImportRunner.StaleHeartbeatThreshold - TimeSpan.FromSeconds(1) },
                    now));
            Assert.IsFalse(
                UserOrgImportRunner.NeedsResume(new UserOrgImportJob { Status = UserOrgImportStatus.Running, HeartbeatUtc = now.AddSeconds(-5) }, now),
                "A running import that is still reporting progress is left alone.");
            Assert.IsFalse(
                UserOrgImportRunner.NeedsResume(new UserOrgImportJob { Status = UserOrgImportStatus.Draft, QueuedUtc = now.AddHours(-1) }, now),
                "A draft is not an import.");
            Assert.IsFalse(UserOrgImportRunner.NeedsResume(null, now));
        }

        [TestMethod]
        public void LooksInterrupted_OnlyForARunningJobWithAStaleHeartbeat()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var running = new UserOrgImportJob
            {
                Status = UserOrgImportStatus.Running,
                StartedUtc = now.AddMinutes(-10),
                HeartbeatUtc = now.AddSeconds(-5),
            };
            Assert.IsFalse(
                UserOrgImportRunner.LooksInterrupted(running, now),
                "A long import that is still reporting progress is healthy, not interrupted.");

            running.HeartbeatUtc = now - UserOrgImportRunner.StaleHeartbeatThreshold.Add(TimeSpan.FromSeconds(5));
            Assert.IsTrue(
                UserOrgImportRunner.LooksInterrupted(running, now),
                "A recycled App Service leaves the row saying Running forever; a stale heartbeat is how that is spotted.");

            Assert.IsFalse(UserOrgImportRunner.LooksInterrupted(null, now));
            Assert.IsFalse(UserOrgImportRunner.LooksInterrupted(
                new UserOrgImportJob { Status = UserOrgImportStatus.Succeeded, HeartbeatUtc = now.AddDays(-1) }, now));
            Assert.IsFalse(
                UserOrgImportRunner.LooksInterrupted(
                    new UserOrgImportJob { Status = UserOrgImportStatus.Pending, QueuedUtc = now.AddSeconds(-5) }, now),
                "A job queued moments ago is simply waiting to be picked up.");
        }

        [TestMethod]
        public void LooksInterrupted_CatchesAPendingJobNobodyEverClaimed()
        {
            // The rows are staged and the job row committed BEFORE the background worker is dispatched,
            // so an App Service recycle in that window leaves a job nobody will ever claim. Because a
            // pending job also counts as active, it would otherwise block every later upload for that
            // organisation type permanently, with the portal polling a job that can never move.
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var stranded = new UserOrgImportJob
            {
                Status = UserOrgImportStatus.Pending,
                QueuedUtc = now - UserOrgImportRunner.StalePendingThreshold.Add(TimeSpan.FromMinutes(1)),
            };

            Assert.IsTrue(UserOrgImportRunner.LooksInterrupted(stranded, now));
        }

        [TestMethod]
        public void LooksInterrupted_FallsBackToStartedWhenNoHeartbeatWasEverRecorded()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var job = new UserOrgImportJob
            {
                Status = UserOrgImportStatus.Running,
                StartedUtc = now - UserOrgImportRunner.StaleHeartbeatThreshold.Add(TimeSpan.FromSeconds(5)),
                HeartbeatUtc = null,
            };

            Assert.IsTrue(UserOrgImportRunner.LooksInterrupted(job, now));
        }
    }
}
