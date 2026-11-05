using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// <c>src/Repair Page Load Times.sql</c>, run for real against a throwaway database holding a minimal <c>dbo.hits</c>.
    /// The script multiplies stored load times by 10, so the one thing it must never do is run twice over the same rows:
    /// these tests pin the application lock that keeps two runs apart, in both directions. Synthetic rows only.
    /// </summary>
    /// <remarks>
    /// Every connection here is unpooled, so each is its own session, and a lock is always probed while the session that
    /// ran the script is still open: closing that session would release a lock the script forgot to.
    /// </remarks>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class RepairPageLoadTimesScriptTests
    {
        private const string LockResource = "PageLoadTimeRepair";
        private const string BeforeCutoff = "hit_timestamp < '2020-01-01'";
        private ScratchDatabase _database;

        [TestInitialize]
        public void CreateDatabase()
        {
            _database = ScratchDatabase.Create("PageLoadRepair");
            using (var con = Open())
            {
                Run(con, @"
CREATE TABLE dbo.hits (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, hit_timestamp datetime NOT NULL, page_load_time float NULL);
INSERT dbo.hits (hit_timestamp, page_load_time) VALUES
    ('2019-12-30T10:00:00', 0.25), ('2019-12-30T11:00:00', 0.25), ('2019-12-31T09:00:00', 0.25),
    ('2019-12-31T10:00:00', 0),    ('2019-12-31T11:00:00', NULL),
    ('2020-01-02T10:00:00', 2.5);");
            }
        }

        [TestCleanup]
        public void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        [TestMethod]
        public void ARunStartedWhileAnotherHoldsTheLock_StopsWithoutChangingAnything()
        {
            using (var other = Open())
            using (var window = Open())
            {
                // Another run in progress: its session holds the script's lock.
                Assert.IsTrue(GetLock(other) >= 0, "The other session takes the lock first.");

                var ex = Assert.ThrowsException<SqlException>(() => RunScript(window, dryRun: false));
                StringAssert.Contains(ex.Message, "Another run of this script is in progress");
            }

            Assert.AreEqual(3, Count("page_load_time = 0.25 AND " + BeforeCutoff), "Nothing is multiplied while another run holds the lock.");
            Assert.AreEqual(0, Count(null, "SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name LIKE N'PageLoadTimeRepair.%'"),
                "Nothing is recorded either.");
        }

        [TestMethod]
        public void ARunWithNoOtherRunGoing_RepairsOnceAndReleasesTheLock()
        {
            using (var window = Open())
            {
                RunScript(window, dryRun: false);

                Assert.AreEqual(3, Count("page_load_time = 2.5 AND " + BeforeCutoff), "Every measured hit before the cutoff is multiplied by 10.");
                Assert.AreEqual(1, Count("page_load_time = 0"), "0 (not measured) stays 0.");
                Assert.AreEqual(1, Count("page_load_time IS NULL"), "NULL stays NULL.");
                Assert.AreEqual(1, Count("page_load_time = 2.5 AND hit_timestamp >= '2020-01-01'"), "A hit at or after the cutoff is left alone.");
                Assert.AreEqual(1, Count(null, "SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'PageLoadTimeRepair.CompletedUtc'"));
                AssertLockIsFree("A finished run releases its lock while its window is still open.");

                // A second run refuses (it would multiply by 10 again), and releases the lock on that way out too.
                var ex = Assert.ThrowsException<SqlException>(() => RunScript(window, dryRun: false));
                StringAssert.Contains(ex.Message, "has already run on this database");
                Assert.AreEqual(3, Count("page_load_time = 2.5 AND " + BeforeCutoff), "The refused run changed nothing.");
                AssertLockIsFree("A refused run releases its lock.");
            }
        }

        [TestMethod]
        public void ADryRun_ChangesNothingAndReleasesTheLock()
        {
            using (var window = Open())
            {
                RunScript(window, dryRun: true);

                Assert.AreEqual(3, Count("page_load_time = 0.25"), "A dry run changes nothing.");
                AssertLockIsFree("A dry run releases its lock, so the repair can then run from another window.");
            }
        }

        [TestMethod]
        public void ARunInTheWindowAnEarlierRunStoppedIn_CarriesOnAndReleasesTheLock()
        {
            // A run that stopped with an error leaves its window holding the lock. Running the script again in that same
            // window must neither be refused by its own lock nor leave it held afterwards.
            using (var window = Open())
            {
                Assert.IsTrue(GetLock(window) >= 0, "The window still holds the lock from the run that stopped.");

                RunScript(window, dryRun: false);

                Assert.AreEqual(3, Count("page_load_time = 2.5 AND " + BeforeCutoff), "The run carried on and repaired the hits.");
                AssertLockIsFree("The run released the lock its window held.");
            }
        }

        #region Helpers

        private static void RunScript(SqlConnection connection, bool dryRun)
        {
            var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Repair Page Load Times.sql"));
            script = Replace(script, "DECLARE @CutoffUtc       datetime = NULL;", "DECLARE @CutoffUtc       datetime = '2020-01-01T00:00:00';");
            script = Replace(script, "DECLARE @DryRun          bit      = 1;", "DECLARE @DryRun          bit      = " + (dryRun ? "1" : "0") + ";");
            Run(connection, script);
        }

        private static void Run(SqlConnection connection, string sql)
        {
            using (var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 })
            {
                cmd.ExecuteNonQuery();
            }
        }

        private static string Replace(string script, string setting, string value)
        {
            Assert.IsTrue(script.Contains(setting), "The script's setting line is still '" + setting + "'.");
            return script.Replace(setting, value);
        }

        private static int GetLock(SqlConnection connection)
        {
            using (var cmd = new SqlCommand("DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', " +
                "@LockOwner = N'Session', @LockTimeout = 0; SELECT @r;", connection))
            {
                cmd.Parameters.AddWithValue("@resource", LockResource);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>From a session of its own: the lock is free when another session can take it at once.</summary>
        private void AssertLockIsFree(string message)
        {
            using (var probe = Open())
            {
                Assert.IsTrue(GetLock(probe) >= 0, message);
            }
        }

        private SqlConnection Open()
        {
            var con = new SqlConnection(new SqlConnectionStringBuilder(_database.ConnectionString) { Pooling = false }.ConnectionString);
            con.Open();
            return con;
        }

        private int Count(string hitsWhere, string sql = null)
        {
            using (var con = Open())
            using (var cmd = new SqlCommand(sql ?? "SELECT COUNT(*) FROM dbo.hits WHERE " + hitsWhere, con))
            {
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, ".github", "pull_request_template.md")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Source repository was not found.");
        }

        #endregion
    }
}
