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
            var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(content);
            using (var stream = new MemoryStream(bytes))
            {
                return UserOrgCsvParser.Parse(stream, maxRows);
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
        public void HandlesAQuotedFieldSpanningLines()
        {
            var result = Parse("UPN,OrgName\r\na@contoso.com,\"Retail\nNorth\"\r\nb@contoso.com,Ops\r\n");

            Assert.AreEqual(2, result.Rows.Count);
            StringAssert.Contains(result.Rows[0].OrgValue, "Retail");
            StringAssert.Contains(result.Rows[0].OrgValue, "North");
            Assert.AreEqual("b@contoso.com", result.Rows[1].Upn);
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
            public UserOrgImportStatus? CompletedStatus;
            public string CompletedError;

            public Task<int> CreateJobWithRowsAsync(UserOrgImportJob job, IReadOnlyList<UserOrgStagedRow> rows, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(Job.Id);

            public Task<UserOrgImportJob> GetJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(Job);

            public Task<bool> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                ClaimAttempts++;
                if (ClaimSucceeds)
                {
                    Job.Status = UserOrgImportStatus.Running;
                }
                return Task.FromResult(ClaimSucceeds);
            }

            public Task HeartbeatAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                HeartbeatCalls++;
                return Task.CompletedTask;
            }

            public Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken))
            {
                ApplyCalls++;
                if (ApplyThrows != null)
                {
                    throw ApplyThrows;
                }
                Job.RowsApplied = 3;
                return Task.FromResult(Job);
            }

            public Task CompleteJobAsync(int jobId, UserOrgImportStatus status, string errorMessage, CancellationToken cancellationToken = default(CancellationToken))
            {
                CompletedStatus = status;
                CompletedError = errorMessage;
                Job.Status = status;
                return Task.CompletedTask;
            }

            public Task<UserOrgImportJob> GetActiveJobForTypeAsync(int orgTypeId, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult<UserOrgImportJob>(null);
        }

        [TestMethod]
        public async Task ClaimsAppliesAndCompletesAJob()
        {
            var store = new FakeJobStore();

            var job = await new UserOrgImportRunner(store).RunAsync(1);

            Assert.AreEqual(1, store.ClaimAttempts);
            Assert.AreEqual(1, store.ApplyCalls);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, store.CompletedStatus);
            Assert.IsNull(store.CompletedError);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
        }

        [TestMethod]
        public async Task DoesNotImportAJobAnotherInstanceAlreadyClaimed()
        {
            // Two web instances polling the same pending job must not import the file twice.
            var store = new FakeJobStore { ClaimSucceeds = false };

            await new UserOrgImportRunner(store).RunAsync(1);

            Assert.AreEqual(0, store.ApplyCalls, "A job that could not be claimed must not be applied.");
            Assert.IsNull(store.CompletedStatus);
        }

        [TestMethod]
        public async Task RecordsAFailureOnTheJobAndRethrows()
        {
            // The request that queued this has long since returned, so nothing is awaiting the task.
            // Without recording it the admin would see a job stuck on "Running" and never learn why.
            var store = new FakeJobStore { ApplyThrows = new InvalidOperationException("merge exploded") };

            try
            {
                await new UserOrgImportRunner(store).RunAsync(1);
                Assert.Fail("The original fault must still surface.");
            }
            catch (InvalidOperationException ex)
            {
                Assert.AreEqual("merge exploded", ex.Message, "The real exception goes to the service logs intact.");
            }

            Assert.AreEqual(UserOrgImportStatus.Failed, store.CompletedStatus);

            // A fixed message, not the exception text. This is rendered verbatim in the portal, and a
            // SQL or Graph exception can carry object names, index names, duplicate key values and
            // identities.
            Assert.IsFalse(
                store.CompletedError.Contains("merge exploded"),
                "The raw exception message must not reach the browser.");
            StringAssert.Contains(store.CompletedError, "could not be completed");
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
