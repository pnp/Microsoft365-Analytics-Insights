using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    /// <summary>
    /// <c>demo --recreate</c> and <c>--connection-string</c>: what the Container Apps demo's nightly
    /// job runs against its Azure SQL database. The option rules are unit tests; the two reset paths
    /// (LocalDB drop, connection-string empty-in-place) run for real against LocalDB, because what
    /// they have to prove is what SQL Server does with them.
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoRecreateTests
    {
        private const string AzureTarget =
            "Server=tcp:contoso-demo.database.windows.net,1433;Database=ContosoDemo_Aca;Authentication=Active Directory Managed Identity;User Id=00000000-0000-0000-0000-000000000000;Encrypt=True";

        [TestMethod]
        public void Options_ConnectionStringTargetTakesItsDatabaseAndRequiresRecreate()
        {
            var options = DemoOptions.Parse(new[] { "--connection-string", AzureTarget, "--recreate" }, new DateTime(2026, 9, 1));
            Assert.AreEqual("ContosoDemo_Aca", options.Database);
            Assert.AreEqual(AzureTarget, options.TargetConnectionString);
            Assert.IsTrue(options.Recreate);

            var local = DemoOptions.Parse(new[] { "--database", "ContosoDemo_Nightly", "--recreate" }, new DateTime(2026, 9, 1));
            Assert.IsTrue(local.Recreate);
            Assert.IsNull(local.TargetConnectionString);

            var invalid = new[]
            {
                new[] { "--connection-string", AzureTarget },
                new[] { "--connection-string", AzureTarget, "--recreate", "--database", "ContosoDemo_Aca" },
                new[] { "--connection-string", AzureTarget.Replace("ContosoDemo_Aca", "M365Analytics"), "--recreate" },
                new[] { "--connection-string", "Server=tcp:contoso-demo.database.windows.net;Encrypt=True", "--recreate" },
                new[] { "--connection-string", "Server=(localdb)\\MSSQLLocalDB;AttachDbFilename=C:\\demo.mdf;Database=ContosoDemo_File", "--recreate" },
                new[] { "--connection-string", "this is not a connection string", "--recreate" },
                new[] { "--connection-string", AzureTarget, "--recreate", "--preview" },
                new[] { "--preview", "--recreate" },
                new[] { "--database", "ContosoDemo_Twice", "--recreate", "--recreate" },
            };
            foreach (var args in invalid)
                Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(args, DateTime.UtcNow), string.Join(" ", args));

            // append modifies nothing that exists, so neither option belongs there.
            Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(new[] { "--recreate" }, DateTime.UtcNow, existingTarget: true));
            Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(new[] { "--connection-string", AzureTarget, "--recreate" }, DateTime.UtcNow, existingTarget: true));
        }

        [TestMethod]
        public void Options_RefusedConnectionStringIsNeverEchoed()
        {
            // Not named "secret", which gitleaks' generic-api-key rule would flag as a leaked credential.
            const string planted = "Sup3r-Secret-Contoso-Pa55";
            var ex = Assert.ThrowsException<ArgumentException>(() => DemoOptions.Parse(new[]
            {
                "--connection-string", "Server=tcp:contoso-demo.database.windows.net;Database=Production;User Id=sqladmin;Password=" + planted, "--recreate"
            }, DateTime.UtcNow));
            StringAssert.Contains(ex.Message, "ContosoDemo_");
            Assert.IsFalse(ex.Message.Contains(planted), "The refusal must not print the connection string it refused.");
        }

        [TestMethod]
        public void Options_RecreateAndTargetDoNotChangeTheGeneratedData()
        {
            var asOf = new DateTime(2026, 9, 1);
            var local = DemoOptions.Parse(new[] { "--database", "ContosoDemo_Local", "--as-of", "2026-09-01" }, asOf);
            var remote = DemoOptions.Parse(new[] { "--connection-string", AzureTarget, "--recreate", "--as-of", "2026-09-01" }, asOf);
            Assert.AreEqual(local.Fingerprint, remote.Fingerprint);
        }

        [TestMethod]
        public void TargetConnection_IsUnpooledAndKeepsTheCallersDatabaseAndAuthentication()
        {
            var builder = new SqlConnectionStringBuilder(SqlDemoDatabase.TargetConnection(AzureTarget));
            Assert.IsFalse(builder.Pooling, "The session-owned applock must be released when the run's connection closes.");
            Assert.AreEqual("ContosoDemo_Aca", builder.InitialCatalog);
            Assert.AreEqual(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, builder.Authentication);
            Assert.AreEqual("tcp:contoso-demo.database.windows.net,1433", builder.DataSource);
        }

        [TestMethod]
        [TestCategory("Integration")]
        [DoNotParallelize]
        public void Recreate_LocalTarget_DropsAndRebuildsOnlyADatabaseThisGeneratorCreated()
        {
            string name = "ContosoDemo_Recreate_" + Guid.NewGuid().ToString("N");
            string stranger = "ContosoDemo_Stranger_" + Guid.NewGuid().ToString("N");
            try
            {
                Generate("--database", name, "--seed", "42");
                Execute(SqlDemoDatabase.LocalConnection(name), "CREATE TABLE dbo.ContosoSentinel (id int NOT NULL);");

                var rebuilt = Generate("--database", name, "--seed", "43", "--recreate");
                using (var connection = Open(SqlDemoDatabase.LocalConnection(name)))
                {
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.tables WHERE name = N'ContosoSentinel';"),
                        "A LocalDB --recreate drops the database, so nothing from the previous one survives.");
                    Assert.AreEqual(rebuilt.Fingerprint, Property(connection, SqlDemoDatabase.FingerprintMarker));
                    Assert.AreEqual("complete", Property(connection, SqlDemoDatabase.StateMarker));
                    Assert.AreEqual(30L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                }

                // A database with content and no marker was not made here: refused, and left exactly as it was.
                Execute(SqlDemoDatabase.LocalConnection("master"), "CREATE DATABASE [" + stranger + "];");
                Execute(SqlDemoDatabase.LocalConnection(stranger), "CREATE TABLE dbo.ContosoSentinel (id int NOT NULL); INSERT dbo.ContosoSentinel VALUES (1);");
                var refused = DemoOptions.Parse(new[] { "--database", stranger, "--recreate" }, new DateTime(2026, 9, 1));
                using (var database = new SqlDemoDatabase(refused, CancellationToken.None))
                    Assert.ThrowsException<InvalidOperationException>(() => database.Open(null));
                using (var connection = Open(SqlDemoDatabase.LocalConnection(stranger)))
                {
                    Assert.AreEqual(1L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.ContosoSentinel;"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.extended_properties WHERE class = 0;"));
                }
            }
            finally
            {
                DropScratch(name);
                DropScratch(stranger);
            }
        }

        [TestMethod]
        [TestCategory("Integration")]
        [DoNotParallelize]
        public void Recreate_ConnectionStringTarget_EmptiesInPlaceAndKeepsTheDatabasesPrincipals()
        {
            string name = "ContosoDemo_InPlace_" + Guid.NewGuid().ToString("N");
            string target = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=" + name + ";Integrated Security=True;TrustServerCertificate=True";
            try
            {
                // The deployment creates the (empty) database; the job never creates one.
                Execute(SqlDemoDatabase.LocalConnection("master"), "CREATE DATABASE [" + name + "];");
                Execute(target, @"CREATE USER [contoso_portal] WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER [contoso_portal];");

                Generate("--connection-string", target, "--recreate", "--seed", "42");

                // Things a reset must clear even though no migration creates them: a stray table, a
                // user-defined type, a schema-bound view over a product table, and a schema of its own.
                Execute(target, "CREATE TABLE dbo.ContosoSentinel (id int NOT NULL);");
                Execute(target, "CREATE TYPE dbo.ContosoIds AS TABLE (id int NOT NULL);");
                Execute(target, "CREATE SCHEMA contoso_scratch;");
                Execute(target, "CREATE VIEW contoso_scratch.UserCount WITH SCHEMABINDING AS SELECT COUNT_BIG(*) AS users FROM dbo.users;");

                var rebuilt = Generate("--connection-string", target, "--recreate", "--seed", "43");
                using (var connection = Open(target))
                {
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.tables WHERE name = N'ContosoSentinel';"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.types WHERE name = N'ContosoIds';"));
                    Assert.AreEqual(0L, Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.schemas WHERE name = N'contoso_scratch';"));
                    Assert.AreEqual(1L, Scalar(connection, @"SELECT COUNT_BIG(*) FROM sys.database_role_members m
JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
WHERE u.name = N'contoso_portal' AND r.name = N'db_datareader';"),
                        "Emptying in place must keep the database's users and their roles - the portal and the job sign in as them.");
                    Assert.AreEqual(rebuilt.Fingerprint, Property(connection, SqlDemoDatabase.FingerprintMarker));
                    Assert.AreEqual("complete", Property(connection, SqlDemoDatabase.StateMarker));
                    Assert.AreEqual(30L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
                    Assert.IsTrue(Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.__MigrationHistory;") > 0,
                        "The rebuilt schema comes from the migrations, as it does for a brand-new database.");
                }

                // Same database, marker removed: it now looks like somebody else's, so it is refused.
                Execute(target, "EXEC sys.sp_dropextendedproperty @name = N'" + SqlDemoDatabase.Marker + "';");
                var refused = DemoOptions.Parse(new[] { "--connection-string", target, "--recreate" }, new DateTime(2026, 9, 1));
                using (var database = new SqlDemoDatabase(refused, CancellationToken.None))
                    Assert.ThrowsException<InvalidOperationException>(() => database.Open(null));
                using (var connection = Open(target))
                    Assert.AreEqual(30L, Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.users;"));
            }
            finally { DropScratch(name); }
        }

        /// <summary>A deliberately small, fast demo: these tests are about the target, not the data.</summary>
        private static DemoOptions Generate(params string[] target)
        {
            var args = new[] { "--users", "30", "--days", "35", "--as-of", "2026-09-01", "--areas", "directory", "--no-profiles", "--batch-size", "1000" };
            var options = DemoOptions.Parse(Concat(args, target), DateTime.UtcNow);
            using (var database = new SqlDemoDatabase(options, CancellationToken.None))
            {
                database.Open(null);
                var summary = DemoCommand.NewSummary(options);
                using (var sink = new CountingDemoSink(summary, database.CreateSink()))
                    new DemoGenerator(options).Generate(sink, summary, null);
                database.ValidateAndComplete(summary, null);
            }
            return options;
        }

        private static string[] Concat(string[] first, string[] second)
        {
            var all = new string[first.Length + second.Length];
            first.CopyTo(all, 0);
            second.CopyTo(all, first.Length);
            return all;
        }

        private static SqlConnection Open(string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = Open(connectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static long Scalar(SqlConnection connection, string sql)
        {
            using (var command = connection.CreateCommand()) { command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        }

        private static string Property(SqlConnection connection, string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT CONVERT(nvarchar(4000), value) FROM sys.extended_properties WHERE class = 0 AND name = @name;";
                command.Parameters.AddWithValue("@name", name);
                return command.ExecuteScalar() as string;
            }
        }

        private static void DropScratch(string name)
        {
            using (var connection = Open(SqlDemoDatabase.LocalConnection("master")))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [" + name
                    + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + name + "]; END;";
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }
    }
}
