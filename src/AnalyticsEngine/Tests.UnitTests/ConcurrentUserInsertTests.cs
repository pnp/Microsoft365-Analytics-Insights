using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using DataUtils;
using DataUtils.Sql;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// A user that another import created a moment earlier (#714). <c>dbo.users.user_name</c> is unique
    /// (<c>IX_users</c>) and several imports create users, so the user import's bulk insert and the Copilot per-user
    /// report's new-user save can each meet a user that did not exist when they loaded their list. Both used to fail
    /// the whole batch - and the user import with it every later Graph section of the cycle.
    /// </summary>
    /// <remarks>
    /// Against LocalDB, because the behaviour under test is SQL Server's: the unique index, its collation, and what
    /// a failed bulk-copy batch leaves behind. The concurrent import is simulated by inserting the user over a separate
    /// connection, either before the call or through the writers' test seams. Synthetic contoso.com names only.
    /// </remarks>
    [TestClass]
    public class ConcurrentUserInsertTests
    {
        private const string BulkInsertCreatedMeanwhile = "created by another import since the user list was loaded";
        private const string CopilotCreatedMeanwhile = "created by another import since the existing users were loaded";

        private static string _connectionString;

        /// <summary>Unique per test, and part of every name it creates, so cleanup can find them all.</summary>
        private string _run;

        [TestInitialize]
        public void StartRun()
        {
            _run = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        [TestCleanup]
        public void DeleteRunUsers()
        {
            using (var connection = OpenConnection())
            using (var command = new SqlCommand("DELETE FROM dbo.users WHERE user_name LIKE @pattern;", connection))
            {
                command.Parameters.Add("@pattern", SqlDbType.VarChar, 250).Value = "%" + _run + "%";
                command.ExecuteNonQuery();
            }
        }

        #region What the fix relies on

        /// <summary>
        /// A bulk-copy batch that hits a duplicate inserts none of its rows, wherever the duplicate is - even as the
        /// last row, when every row before it has already been sent. So a failed batch can be written again in full,
        /// less the users that exist, without counting anything twice.
        /// </summary>
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(4)]
        public async Task SqlBulkCopy_ABatchWithOneDuplicate_InsertsNothing(int duplicateAt)
        {
            var upns = Enumerable.Range(1, 5).Select(Upn).ToList();
            var existingId = InsertUserDirectly(upns[duplicateAt]);

            var table = new DataTable();
            table.Columns.Add("user_name", typeof(string));
            foreach (var upn in upns)
            {
                table.Rows.Add(upn);
            }

            using (var connection = OpenConnection())
            {
                // As the user import writes: one batch, no transaction of its own, default options.
                using (var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = "dbo.users", BatchSize = 10 })
                {
                    bulkCopy.ColumnMappings.Add("user_name", "user_name");
                    var failure = await Assert.ThrowsExceptionAsync<SqlException>(() => bulkCopy.WriteToServerAsync(table));
                    Assert.IsTrue(SqlDuplicateKey.IsViolation(failure), failure.Message);
                }

                using (var command = new SqlCommand("SELECT @@TRANCOUNT;", connection))
                {
                    Assert.AreEqual(0, (int)command.ExecuteScalar(), "The failed batch must not leave a transaction open on the connection it reuses.");
                }
            }

            var stored = ReadRunUsers();
            Assert.AreEqual(1, stored.Count, "Nothing from the failed batch may be left behind.");
            Assert.AreEqual(existingId, stored[0].Id);
        }

        [TestMethod]
        public void SqlDuplicateKey_RecognisesTheErrorAtEveryDepthItArrivesAt()
        {
            var upn = Upn(1);
            InsertUserDirectly(upn);

            SqlException direct = null;
            try
            {
                InsertUserDirectly(upn);
            }
            catch (SqlException ex)
            {
                direct = ex;
            }
            Assert.IsNotNull(direct, "A second row with the same user_name must be refused by IX_users.");
            Assert.IsTrue(SqlDuplicateKey.IsViolation(direct));
            Assert.IsTrue(SqlDuplicateKey.IsViolation(new AggregateException(new InvalidOperationException("other"), direct)));
            Assert.IsTrue(SqlDuplicateKey.IsViolation(new InvalidOperationException("wrapped", direct)));

            DbUpdateException viaEf = null;
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new User { UserPrincipalName = upn });
                try
                {
                    db.SaveChanges();
                }
                catch (DbUpdateException ex)
                {
                    viaEf = ex;
                }
            }
            Assert.IsNotNull(viaEf);
            Assert.IsTrue(SqlDuplicateKey.IsViolation(viaEf), "EF6 wraps the SqlException twice.");

            SqlException other = null;
            try
            {
                using (var connection = OpenConnection())
                using (var command = new SqlCommand("SELECT 1 FROM dbo.no_such_table_714;", connection))
                {
                    command.ExecuteScalar();
                }
            }
            catch (SqlException ex)
            {
                other = ex;
            }
            Assert.IsNotNull(other);
            Assert.IsFalse(SqlDuplicateKey.IsViolation(other), "Any other SQL error is not a duplicate.");
            Assert.IsFalse(SqlDuplicateKey.IsViolation(new InvalidOperationException("not SQL")));
            Assert.IsFalse(SqlDuplicateKey.IsViolation(null));
        }

        /// <summary>
        /// The get-or-create path already tolerated the race; it now shares the duplicate-key detection, so pin that
        /// a real duplicate from SQL Server still reaches it through EF.
        /// </summary>
        [TestMethod]
        public async Task UserCache_GetOrCreate_AUserCreatedByAnotherImportMeanwhile_ReturnsThatUser()
        {
            var upn = Upn(1);
            var existingId = 0;

            using (var db = new AnalyticsEntitiesContext())
            {
                var cache = new UserCache(db);
                cache.NewObjectCreating += (sender, template) => existingId = InsertUserDirectly(upn);

                var user = await cache.GetOrCreateNewResource(upn, new User { UserPrincipalName = upn }, commitChangeOnSaveNew: true);

                Assert.AreNotEqual(0, existingId, "The test must have created the user in between.");
                Assert.AreEqual(existingId, user.ID);
            }

            Assert.AreEqual(1, ReadRunUsers().Count);
        }

        #endregion

        #region ExistingUserIds

        [TestMethod]
        public async Task ExistingUserIds_AnswersByPosition_WithTheDatabasesComparison()
        {
            RequireCaseInsensitiveCollation();

            var exactId = InsertUserDirectly(Upn(1));
            var otherCaseId = InsertUserDirectly(Upn(2).ToUpperInvariant());
            var names = new List<string> { Upn(3), Upn(1), null, Upn(2), Upn(1) };
            var expected = new int?[] { null, exactId, null, otherCaseId, exactId };

            int?[] overConnection;
            using (var connection = OpenConnection())
            {
                overConnection = await ExistingUserIds.FindAsync(connection, names);
            }
            CollectionAssert.AreEqual(expected, overConnection);

            using (var db = new AnalyticsEntitiesContext())
            {
                CollectionAssert.AreEqual(expected, await ExistingUserIds.FindAsync(db, names), "Both entry points run the same query.");
            }
        }

        [TestMethod]
        public async Task ExistingUserIds_SplitsALongListAcrossQueries()
        {
            var firstId = InsertUserDirectly(Upn(0));
            var lastId = InsertUserDirectly(Upn(ExistingUserIds.MaxNamesPerQuery));
            var names = Enumerable.Range(0, ExistingUserIds.MaxNamesPerQuery + 1).Select(Upn).ToList();

            int?[] ids;
            using (var connection = OpenConnection())
            {
                ids = await ExistingUserIds.FindAsync(connection, names);
            }

            Assert.AreEqual(names.Count, ids.Length);
            Assert.AreEqual(firstId, ids[0]);
            Assert.AreEqual(lastId, ids[ExistingUserIds.MaxNamesPerQuery], "The second query's slots must be offset by the first's.");
            Assert.AreEqual(2, ids.Count(id => id.HasValue));
        }

        /// <summary>
        /// The names go as <c>varchar</c>, like <c>user_name</c>. An <c>nvarchar</c> parameter makes SQL Server convert
        /// the column, which under a SQL collation cannot seek <c>IX_users</c> (#713).
        /// </summary>
        [TestMethod]
        public void ExistingUserIds_SendsVarcharParameters_SoTheColumnIsNeverConverted()
        {
            var longName = new string('x', 300) + "@contoso.com";
            var query = ExistingUserIds.BuildQuery(new[] { Upn(1), null, longName }, 0, 3);

            Assert.AreEqual(2, query.Parameters.Length, "A null name is not sent.");
            Assert.IsTrue(query.Parameters.All(p => p.SqlDbType == SqlDbType.VarChar));
            Assert.AreEqual(250, query.Parameters[0].Size, "Sized to the column.");
            Assert.AreEqual(longName.Length, query.Parameters[1].Size, "Never smaller than the value: SqlClient would truncate it silently.");
            StringAssert.Contains(query.Sql, "(0,@n0),(2,@n2)", "Slots keep their positions around a skipped null.");

            InsertUserDirectly(Upn(1));
            using (var connection = OpenConnection())
            {
                var plan = ActualPlan(connection, query.Sql, ExistingUserIds.BuildQuery(new[] { Upn(1), null, longName }, 0, 3).Parameters);
                Assert.IsFalse(plan.Contains("CONVERT_IMPLICIT"), "No conversion of user_name, or of anything else.");

                // The same query with nvarchar parameters, to prove the check above can fail.
                var nvarchar = new[]
                {
                    new SqlParameter("@n0", SqlDbType.NVarChar, 4000) { Value = Upn(1) },
                    new SqlParameter("@n2", SqlDbType.NVarChar, 4000) { Value = longName },
                };
                Assert.IsTrue(ActualPlan(connection, query.Sql, nvarchar).Contains("CONVERT_IMPLICIT"));
            }
        }

        #endregion

        #region User import: UserInsertProcessor.BulkInsertUsers

        [TestMethod]
        public async Task BulkInsert_AUserCreatedByAnotherImportMeanwhile_IsLeftAsItIs_AndEveryOtherUserIsInsertedOnce()
        {
            var upns = Enumerable.Range(1, 5).Select(Upn).ToList();
            var graphUsers = upns.Select(NewGraphUser).ToList();
            var existingId = InsertUserDirectly(upns[3]);

            using (var log = new RecordedLog())
            {
                // Batches of two, so the duplicate fails the second of three batches.
                await NewProcessor(log.Logger).BulkInsertUsers(ConnectionString, graphUsers, batchSize: 2);

                Assert.AreEqual(1, log.Information.Count(m => m.Contains(BulkInsertCreatedMeanwhile)), string.Join(Environment.NewLine, log.Information));
            }

            var stored = ReadRunUsers();
            Assert.AreEqual(upns.Count, stored.Count, "Every user exactly once.");
            foreach (var graphUser in graphUsers)
            {
                var row = stored.Single(r => r.UserName == graphUser.UserPrincipalName);
                if (graphUser.UserPrincipalName == upns[3])
                {
                    Assert.AreEqual(existingId, row.Id, "The other import's row is kept...");
                    Assert.IsNull(row.AzureAdId, "...and not written over: phase 2, not this insert, applies its Graph metadata.");
                    Assert.AreEqual(OtherImportMail, row.Mail);
                    Assert.AreEqual(GraphAccountCreated, row.CreatedUtc, "Only the account-creation date it lacked is filled in.");
                }
                else
                {
                    Assert.AreEqual(graphUser.Id, row.AzureAdId);
                    Assert.AreEqual(GraphAccountCreated, row.CreatedUtc);
                }
            }
        }

        [TestMethod]
        public async Task BulkInsert_AUserCreatedMeanwhile_KeepsAnAccountCreationDateItAlreadyHas()
        {
            var upns = Enumerable.Range(1, 2).Select(Upn).ToList();
            var otherImportsDate = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            var existingId = InsertUserDirectly(upns[0], otherImportsDate);

            using (var log = new RecordedLog())
            {
                await NewProcessor(log.Logger).BulkInsertUsers(ConnectionString, upns.Select(NewGraphUser).ToList(), batchSize: 10);
            }

            var stored = ReadRunUsers();
            Assert.AreEqual(2, stored.Count);
            Assert.AreEqual(otherImportsDate, stored.Single(r => r.Id == existingId).CreatedUtc, "A value is never overwritten.");
            Assert.AreEqual(GraphAccountCreated, stored.Single(r => r.Id != existingId).CreatedUtc);
        }

        [TestMethod]
        public async Task BulkInsert_AUserCreatedMeanwhileInADifferentCase_IsRecognisedAsTheSameUser()
        {
            RequireCaseInsensitiveCollation();

            var upns = Enumerable.Range(1, 3).Select(Upn).ToList();
            var existingId = InsertUserDirectly(upns[1].ToUpperInvariant());

            using (var log = new RecordedLog())
            {
                await NewProcessor(log.Logger).BulkInsertUsers(ConnectionString, upns.Select(NewGraphUser).ToList(), batchSize: 10);

                Assert.AreEqual(1, log.Information.Count(m => m.Contains(BulkInsertCreatedMeanwhile)));
            }

            var stored = ReadRunUsers();
            Assert.AreEqual(3, stored.Count);
            var other = stored.Single(r => r.Id == existingId);
            Assert.AreEqual(upns[1].ToUpperInvariant(), other.UserName, "Left exactly as the other import wrote it.");
        }

        [TestMethod]
        public async Task BulkInsert_AnyOtherError_StillFails_WithoutARetry()
        {
            var tooLong = $"{_run}.{new string('x', 300)}@contoso.com";
            var graphUsers = new List<GraphUser> { NewGraphUser(Upn(1)), NewGraphUser(tooLong) };
            var attempts = 0;

            using (var log = new RecordedLog())
            {
                var processor = NewProcessor(log.Logger);
                processor.BeforeBulkInsertAttemptAsync = users => { attempts++; return Task.CompletedTask; };

                Exception failure = null;
                try
                {
                    await processor.BulkInsertUsers(ConnectionString, graphUsers, batchSize: 10);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure, "A user name longer than the column must still fail the insert.");
                Assert.IsInstanceOfType(failure, typeof(SqlException), "A real SQL error (4815 from the server), not a duplicate.");
                Assert.IsFalse(SqlDuplicateKey.IsViolation(failure));
                Assert.AreEqual(1, attempts, "Only a duplicate key is worth another attempt.");
                Assert.IsFalse(log.Information.Any(m => m.Contains(BulkInsertCreatedMeanwhile)));
            }

            Assert.AreEqual(0, ReadRunUsers().Count);
        }

        [TestMethod]
        public async Task BulkInsert_DuplicatesThatKeepAppearing_FailAfterTheAttemptLimit()
        {
            var upns = Enumerable.Range(1, 5).Select(Upn).ToList();
            var attempts = 0;

            using (var log = new RecordedLog())
            {
                var processor = NewProcessor(log.Logger);

                // Another import wins the race for one more of these users before every attempt.
                processor.BeforeBulkInsertAttemptAsync = users =>
                {
                    attempts++;
                    InsertUserDirectly(users[0].UserPrincipalName);
                    return Task.CompletedTask;
                };

                Exception failure = null;
                try
                {
                    await processor.BulkInsertUsers(ConnectionString, upns.Select(NewGraphUser).ToList(), batchSize: 10);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure, "The import must give up rather than retry for ever.");
                Assert.IsTrue(SqlDuplicateKey.IsViolation(failure), "The last duplicate-key error is rethrown as it was.");
                Assert.AreEqual(UserInsertProcessor.MaxBulkInsertAttemptsPerBatch, attempts);
                Assert.AreEqual(UserInsertProcessor.MaxBulkInsertAttemptsPerBatch - 1, log.Information.Count(m => m.Contains(BulkInsertCreatedMeanwhile)));
            }

            CollectionAssert.AreEquivalent(upns.Take(UserInsertProcessor.MaxBulkInsertAttemptsPerBatch).ToList(), ReadRunUsers().Select(r => r.UserName).ToList(),
                "Only the other import's users are stored; the failed last attempt inserts nothing.");
        }

        [TestMethod]
        public async Task BulkInsert_EveryUserOfABatchCreatedMeanwhile_WritesNothingMore_AndCarriesOn()
        {
            var upns = Enumerable.Range(1, 4).Select(Upn).ToList();
            var firstBatchIds = upns.Take(2).Select(InsertUserDirectly).ToList();
            var attempts = 0;

            using (var log = new RecordedLog())
            {
                var processor = NewProcessor(log.Logger);
                processor.BeforeBulkInsertAttemptAsync = users => { attempts++; return Task.CompletedTask; };

                await processor.BulkInsertUsers(ConnectionString, upns.Select(NewGraphUser).ToList(), batchSize: 2);

                Assert.AreEqual(2, attempts, "One failed attempt for the first batch, with nothing left to retry, then the second batch.");
                Assert.AreEqual(1, log.Information.Count(m => m.Contains(BulkInsertCreatedMeanwhile)));
                Assert.IsTrue(log.Information.Any(m => m.Contains("Bulk inserted 2/4 users to SQL (2 already created by another import)")),
                    string.Join(Environment.NewLine, log.Information));
            }

            var stored = ReadRunUsers();
            Assert.AreEqual(4, stored.Count);
            CollectionAssert.AreEquivalent(firstBatchIds, stored.Where(r => r.Mail == OtherImportMail).Select(r => r.Id).ToList());
        }

        /// <summary>
        /// Phase 2 reloads every user of the insert by UPN, so a user another import created in the meantime is not
        /// inserted again but still gets its Graph metadata in the same cycle.
        /// </summary>
        [TestMethod]
        public async Task InsertMissingUsers_AUserCreatedByAnotherImportMeanwhile_StillGetsItsGraphMetadata()
        {
            var upns = Enumerable.Range(1, 3).Select(Upn).ToList();
            var graphUsers = upns.Select(NewGraphUser).ToList();
            var existingId = InsertUserDirectly(upns[1]);
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                var updater = new UserMetadataUpdater(logger, new AppConfig(), new FakeUserMetadataLoader(graphUsers));

                // The user list this import loaded predates the other import's user: none of them was in it.
                var inserted = await updater.InsertMissingUsers(db, graphUsers, new List<User>(), readUserSkus: false);

                Assert.AreEqual(3, inserted.Count);
            }

            var stored = ReadRunUsers();
            Assert.AreEqual(3, stored.Count);
            var other = stored.Single(r => r.UserName == upns[1]);
            Assert.AreEqual(existingId, other.Id, "Not inserted a second time.");
            Assert.AreEqual(graphUsers[1].Id, other.AzureAdId, "Phase 2 still writes its Graph metadata.");
            Assert.AreEqual(graphUsers[1].Mail, other.Mail);
            Assert.AreEqual(GraphAccountCreated, other.CreatedUtc, "Phase 1 fills the account-creation date, which phase 2 cannot write.");
        }

        #endregion

        #region Copilot per-user report: SqlCopilotUsagePersistenceManager new users

        [TestMethod]
        public async Task CopilotNewUsers_AUserCreatedByAnotherImportMeanwhile_IsUsed_AndTheRestAreCreatedOnce()
        {
            var report = Enumerable.Range(1, 4).Select(Upn).ToList();
            var existingId = 0;
            var attempts = 0;
            var logger = new RecordingLogger();
            CopilotUserIdResolution resolution;

            InsertUserDirectly(AnchorUpn());
            using (var db = new AnalyticsEntitiesContext())
            {
                var persistence = new SqlCopilotUsagePersistenceManager(db, logger);
                persistence.BeforeNewUsersSaveAttemptAsync = users =>
                {
                    if (attempts++ == 0)
                    {
                        existingId = InsertUserDirectly(report[2]);
                    }
                    return Task.CompletedTask;
                };

                resolution = await persistence.ResolveUserIdsAsync(report);
            }

            Assert.AreEqual(2, attempts);
            Assert.AreEqual(existingId, resolution.IdsByUpn[report[2]], "The other import's user is used...");
            Assert.AreEqual(report.Count - 1, resolution.Created, "...and only the others are counted as created.");
            Assert.AreEqual(1, logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains(CopilotCreatedMeanwhile)));

            var stored = ReadRunUsers();
            Assert.AreEqual(report.Count + 1, stored.Count, "The anchor plus every report user, each once.");
            foreach (var upn in report)
            {
                Assert.AreEqual(stored.Single(r => r.UserName == upn).Id, resolution.IdsByUpn[upn]);
            }
            Assert.AreEqual(OtherImportMail, stored.Single(r => r.Id == existingId).Mail, "Left as the other import wrote it.");
        }

        [TestMethod]
        public async Task CopilotNewUsers_AUserCreatedMeanwhileInADifferentCase_IsRecognisedAsTheSameUser()
        {
            RequireCaseInsensitiveCollation();

            var report = Enumerable.Range(1, 3).Select(Upn).ToList();
            var existingId = 0;
            var logger = new RecordingLogger();
            CopilotUserIdResolution resolution;

            InsertUserDirectly(AnchorUpn());
            using (var db = new AnalyticsEntitiesContext())
            {
                var persistence = new SqlCopilotUsagePersistenceManager(db, logger);
                persistence.BeforeNewUsersSaveAttemptAsync = users =>
                {
                    if (existingId == 0)
                    {
                        existingId = InsertUserDirectly(report[0].ToUpperInvariant());
                    }
                    return Task.CompletedTask;
                };

                resolution = await persistence.ResolveUserIdsAsync(report);
            }

            Assert.AreEqual(existingId, resolution.IdsByUpn[report[0]]);
            var stored = ReadRunUsers();
            Assert.AreEqual(report.Count + 1, stored.Count);
            Assert.AreEqual(report[0].ToUpperInvariant(), stored.Single(r => r.Id == existingId).UserName);
        }

        [TestMethod]
        public async Task CopilotNewUsers_EveryUserOfABatchCreatedMeanwhile_SavesNothingMore_AndCarriesOn()
        {
            var report = Enumerable.Range(1, 4).Select(Upn).ToList();
            var attempts = 0;
            var logger = new RecordingLogger();
            var otherImportIds = new Dictionary<string, int>();
            CopilotUserIdResolution resolution;

            InsertUserDirectly(AnchorUpn());
            using (var db = new AnalyticsEntitiesContext())
            {
                // Batches of two: the other import creates both users of the first batch before it is saved.
                var persistence = new SqlCopilotUsagePersistenceManager(db, logger) { SaveBatchSize = 2 };
                persistence.BeforeNewUsersSaveAttemptAsync = users =>
                {
                    if (attempts++ == 0)
                    {
                        foreach (var user in users)
                        {
                            otherImportIds[user.UserPrincipalName] = InsertUserDirectly(user.UserPrincipalName);
                        }
                    }
                    return Task.CompletedTask;
                };

                resolution = await persistence.ResolveUserIdsAsync(report);
            }

            Assert.AreEqual(2, attempts, "One failed attempt for the first batch, with nothing left to retry, then the second batch.");
            Assert.AreEqual(2, otherImportIds.Count);
            foreach (var created in otherImportIds)
            {
                Assert.AreEqual(created.Value, resolution.IdsByUpn[created.Key]);
            }
            Assert.AreEqual(2, resolution.Created);
            Assert.AreEqual(report.Count + 1, ReadRunUsers().Count);
        }

        [TestMethod]
        public async Task CopilotNewUsers_AnyOtherError_StillFails_WithoutARetry()
        {
            var report = new List<string> { Upn(1), $"{_run}.{new string('x', 300)}@contoso.com" };
            var attempts = 0;
            var logger = new RecordingLogger();

            InsertUserDirectly(AnchorUpn());
            using (var db = new AnalyticsEntitiesContext())
            {
                var persistence = new SqlCopilotUsagePersistenceManager(db, logger);
                persistence.BeforeNewUsersSaveAttemptAsync = users => { attempts++; return Task.CompletedTask; };

                var failure = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => persistence.ResolveUserIdsAsync(report));

                Assert.IsInstanceOfType(failure.GetBaseException(), typeof(SqlException), "A real SQL error (truncation), not a duplicate.");
                Assert.IsFalse(SqlDuplicateKey.IsViolation(failure));
            }

            Assert.AreEqual(1, attempts, "Only a duplicate key is worth another attempt.");
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains(CopilotCreatedMeanwhile)));
            Assert.AreEqual(1, ReadRunUsers().Count, "Only the anchor: the failed batch was rolled back.");
        }

        [TestMethod]
        public async Task CopilotNewUsers_DuplicatesThatKeepAppearing_FailAfterTheAttemptLimit()
        {
            var report = Enumerable.Range(1, 5).Select(Upn).ToList();
            var attempts = 0;
            var logger = new RecordingLogger();
            var hookInserted = new List<string>();

            InsertUserDirectly(AnchorUpn());
            using (var db = new AnalyticsEntitiesContext())
            {
                var persistence = new SqlCopilotUsagePersistenceManager(db, logger);
                persistence.BeforeNewUsersSaveAttemptAsync = users =>
                {
                    attempts++;
                    hookInserted.Add(users[0].UserPrincipalName);
                    InsertUserDirectly(users[0].UserPrincipalName);
                    return Task.CompletedTask;
                };

                var failure = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => persistence.ResolveUserIdsAsync(report));
                Assert.IsTrue(SqlDuplicateKey.IsViolation(failure), "The last duplicate-key error is rethrown as it was.");
            }

            Assert.AreEqual(SqlCopilotUsagePersistenceManager.MaxNewUserSaveAttempts, attempts);
            Assert.AreEqual(SqlCopilotUsagePersistenceManager.MaxNewUserSaveAttempts - 1,
                logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains(CopilotCreatedMeanwhile)));
            CollectionAssert.AreEquivalent(hookInserted.Concat(new[] { AnchorUpn() }).ToList(), ReadRunUsers().Select(r => r.UserName).ToList(),
                "Only the anchor and the other import's users are stored; the failed last attempt saved nothing.");
        }

        #endregion

        #region Helpers

        private const string OtherImportMail = "created.by.another.import@contoso.com";

        private string Upn(int n) => $"concurrent.{_run}.{n}@contoso.com";

        /// <summary>
        /// The Copilot report only creates users on an email domain the database already holds users for, so each
        /// of its tests seeds one first.
        /// </summary>
        private string AnchorUpn() => $"anchor.{_run}@contoso.com";

        private static GraphUser NewGraphUser(string upn)
            => new GraphUser { Id = Guid.NewGuid().ToString(), UserPrincipalName = upn, AccountEnabled = true, Mail = upn, PostalCode = "10001", CreatedDateTime = GraphAccountCreated };

        /// <summary>Synthetic Graph <c>createdDateTime</c> for every test user.</summary>
        private static readonly DateTime GraphAccountCreated = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private static UserInsertProcessor NewProcessor(AnalyticsLogger logger)
            => new UserInsertProcessor(logger, new UserBatchProcessor(logger));

        private static string ConnectionString
        {
            get
            {
                if (_connectionString == null)
                {
                    using (var db = new AnalyticsEntitiesContext())
                    {
                        db.Database.Initialize(false);
                        _connectionString = db.Database.Connection.ConnectionString;
                    }
                }
                return _connectionString;
            }
        }

        private static SqlConnection OpenConnection()
        {
            var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>Creates a user over its own connection, as another import would, and returns its id.</summary>
        private static int InsertUserDirectly(string upn) => InsertUserDirectly(upn, null);

        private static int InsertUserDirectly(string upn, DateTime? createdUtc)
        {
            using (var connection = OpenConnection())
            using (var command = new SqlCommand("INSERT INTO dbo.users (user_name, mail, created_utc) VALUES (@upn, @mail, @created); SELECT CAST(SCOPE_IDENTITY() AS int);", connection))
            {
                command.Parameters.Add("@upn", SqlDbType.VarChar, 400).Value = upn;
                command.Parameters.Add("@mail", SqlDbType.NVarChar, 250).Value = OtherImportMail;
                command.Parameters.Add("@created", SqlDbType.DateTime2).Value = (object)createdUtc ?? DBNull.Value;
                return (int)command.ExecuteScalar();
            }
        }

        private List<StoredUser> ReadRunUsers()
        {
            var users = new List<StoredUser>();
            using (var connection = OpenConnection())
            using (var command = new SqlCommand("SELECT id, user_name, azure_ad_id, mail, created_utc FROM dbo.users WHERE user_name LIKE @pattern ORDER BY id;", connection))
            {
                command.Parameters.Add("@pattern", SqlDbType.VarChar, 250).Value = "%" + _run + "%";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        users.Add(new StoredUser
                        {
                            Id = reader.GetInt32(0),
                            UserName = reader.GetString(1),
                            AzureAdId = reader.IsDBNull(2) ? null : reader.GetString(2),
                            Mail = reader.IsDBNull(3) ? null : reader.GetString(3),
                            CreatedUtc = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
                        });
                    }
                }
            }
            return users;
        }

        private static void RequireCaseInsensitiveCollation()
        {
            string collation;
            using (var connection = OpenConnection())
            using (var command = new SqlCommand("SELECT CONVERT(nvarchar(128), COLUMNPROPERTYEX(OBJECT_ID('dbo.users'), 'user_name', 'Collation'));", connection))
            {
                collation = (string)command.ExecuteScalar();
            }

            if (collation == null || collation.IndexOf("_CI_", StringComparison.Ordinal) < 0)
            {
                Assert.Inconclusive($"dbo.users.user_name has collation '{collation}'; this test is about the default, case-insensitive one.");
            }
        }

        /// <summary>Runs a query and returns its actual execution plan.</summary>
        private static string ActualPlan(SqlConnection connection, string sql, SqlParameter[] parameters)
        {
            using (var on = new SqlCommand("SET STATISTICS XML ON;", connection))
            {
                on.ExecuteNonQuery();
            }
            try
            {
                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddRange(parameters);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                        }
                        Assert.IsTrue(reader.NextResult() && reader.Read(), "SET STATISTICS XML returns the plan as a second result set.");
                        return reader.GetString(0);
                    }
                }
            }
            finally
            {
                using (var off = new SqlCommand("SET STATISTICS XML OFF;", connection))
                {
                    off.ExecuteNonQuery();
                }
            }
        }

        private sealed class StoredUser
        {
            public int Id { get; set; }
            public string UserName { get; set; }
            public string AzureAdId { get; set; }
            public string Mail { get; set; }
            public DateTime? CreatedUtc { get; set; }
        }

        /// <summary>An <see cref="AnalyticsLogger"/> whose traces can be read back.</summary>
        private sealed class RecordedLog : IDisposable
        {
            private readonly RecordingTelemetryChannel _channel = new RecordingTelemetryChannel();
            private readonly TelemetryConfiguration _configuration;

            public RecordedLog()
            {
                _configuration = new TelemetryConfiguration
                {
                    TelemetryChannel = _channel,
                    ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
                };
                Logger = new AnalyticsLogger(new TelemetryClient(_configuration), "ConcurrentUserInsertTests");
            }

            public AnalyticsLogger Logger { get; }

            public List<string> Information => _channel.Sent.OfType<TraceTelemetry>()
                .Where(t => t.SeverityLevel == SeverityLevel.Information)
                .Select(t => t.Message)
                .ToList();

            public void Dispose() => _configuration.Dispose();
        }

        private sealed class RecordingTelemetryChannel : ITelemetryChannel
        {
            private readonly object _gate = new object();
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();

            public IList<ITelemetry> Sent
            {
                get
                {
                    lock (_gate)
                    {
                        return _sent.ToList();
                    }
                }
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(ITelemetry item)
            {
                lock (_gate)
                {
                    _sent.Add(item);
                }
            }

            public void Flush()
            {
            }

            public void Dispose()
            {
            }
        }

        #endregion
    }
}
