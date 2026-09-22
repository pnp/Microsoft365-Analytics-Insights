using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    /// <summary>
    /// The by-hand upgrade path for <c>202609221200001_UserOrganisations</c>.
    /// </summary>
    /// <remarks>
    /// Some customers upgrade the database with a DBA running the script in a maintenance window
    /// instead of running the installer, so the script is a shipped artifact and CI has to execute it -
    /// not merely grep it. It is run here the way a DBA's client runs it: batch by batch, on one
    /// connection, with <c>QUOTED_IDENTIFIER</c> OFF, which is sqlcmd's default and differs from
    /// SqlClient's.
    /// </remarks>
    [TestClass]
    public class UserOrgMigrationScriptTests
    {
        private const string MigrationId = "202609221200001_UserOrganisations";
        private const string PredecessorId = "202609201430001_DropCopilotAdoptionPeriodTables";

        /// <summary>A stand-in for the predecessor's gzipped EDMX snapshot; only its equality matters here.</summary>
        private const string PredecessorModel = "0x1F8B0800AABBCCDD";

        private static string Script()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(
                    directory.FullName, "Common", "Entities", "Migrations", MigrationId + ".manual.sql");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
                directory = directory.Parent;
            }

            Assert.Fail(
                $"Could not find {MigrationId}.manual.sql by walking up from the test assembly. Every schema "
                + "migration must ship a manual upgrade script for DBAs who do not run the installer.");
            return null;
        }

        /// <summary>A database with the tables the migration depends on, and an empty migration history.</summary>
        private static ScratchDatabase NewDatabase()
        {
            var db = ScratchDatabase.Create("userorgmanual");
            db.Execute(@"
CREATE TABLE dbo.users (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    user_name varchar(250) NOT NULL
);
CREATE TABLE dbo.__MigrationHistory (
    MigrationId nvarchar(150) NOT NULL CONSTRAINT PK___MigrationHistory PRIMARY KEY,
    ContextKey nvarchar(300) NOT NULL,
    Model varbinary(max) NOT NULL,
    ProductVersion nvarchar(32) NOT NULL
);");
            return db;
        }

        private static void StampPredecessor(ScratchDatabase db)
        {
            db.Execute(
                "INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion) VALUES ("
                + $"N'{PredecessorId}', N'Common.Entities.Migrations.Configuration', {PredecessorModel}, N'6.5.2');");
        }

        private static int TableCount(ScratchDatabase db)
        {
            return Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'user_org%'"));
        }

        private static int StampCount(ScratchDatabase db)
        {
            return Convert.ToInt32(db.Scalar(
                $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'"));
        }

        [TestMethod]
        public void TheScriptEmbedsTheMigrationsOwnUpSqlVerbatim()
        {
            // A hand-maintained copy drifts. The installer and the DBA must apply the same SQL.
            StringAssert.Contains(
                Script(),
                UserOrganisations.Up_Sql.Trim(),
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade applies "
                + "something different from what the installer applies.");
        }

        [TestMethod]
        public void RunningItCreatesTheSchemaAndStampsTheMigration()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(5, TableCount(db), "All five user_org tables should exist.");
                Assert.AreEqual(1, StampCount(db), "EF and the Health page decide from __MigrationHistory.");

                Assert.AreEqual(
                    "yes",
                    db.Scalar(
                        "SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'"
                        + MigrationId + "') = (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'"
                        + PredecessorId + "') THEN 'yes' ELSE 'no' END"),
                    "The model snapshot must be copied from the predecessor - this migration changes no entity.");

                Assert.AreEqual(
                    "Common.Entities.Migrations.Configuration",
                    db.Scalar($"SELECT ContextKey FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'"));
            }
        }

        [TestMethod]
        public void ReRunningItIsACleanNoOp()
        {
            // A DBA who is not sure whether it applied must be able to run it again safely.
            using (var db = NewDatabase())
            {
                StampPredecessor(db);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(5, TableCount(db));
                Assert.AreEqual(1, StampCount(db), "The stamp must not be duplicated.");
            }
        }

        [TestMethod]
        public void WithoutThePredecessorItRefusesToStamp()
        {
            // The manual scripts form a prerequisite chain. Recording this one as applied when the
            // previous release was skipped would leave EF believing a migration ran that never did.
            using (var db = NewDatabase())
            {
                try
                {
                    db.ExecuteScript(Script(), quotedIdentifierOn: false);
                    Assert.Fail("The script must refuse to stamp when the predecessor is missing.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, PredecessorId);
                    StringAssert.Contains(ex.Message, "NOT stamped");
                }

                Assert.AreEqual(0, StampCount(db), "Nothing may be recorded when the chain is broken.");
                Assert.AreEqual(
                    5,
                    TableCount(db),
                    "The schema itself is additive and harmless, so it is created; only the stamp is withheld. "
                    + "Re-running after the predecessor is applied then stamps it.");
            }
        }

        [TestMethod]
        public void AfterTheChainIsRepairedTheSameScriptStamps()
        {
            using (var db = NewDatabase())
            {
                try
                {
                    db.ExecuteScript(Script(), quotedIdentifierOn: false);
                }
                catch (SqlException)
                {
                    // Expected - the predecessor is missing on this first run.
                }

                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db));
            }
        }

        [TestMethod]
        public void TheSchemaItCreatesEnforcesOneValuePerOrgTypePerUser()
        {
            // The script is the customer-facing artifact, so the guarantees are asserted against what IT
            // produces rather than only against the migration constant.
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                db.Execute(@"
INSERT INTO dbo.users (user_name) VALUES ('a@contoso.com');
INSERT INTO dbo.user_org_types (name, source_kind) VALUES (N'Cost Centre', 2), (N'Business Unit', 2);
INSERT INTO dbo.user_org_values (org_type_id, name) VALUES (1, N'CC-1'), (1, N'CC-2'), (2, N'Retail');
INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id) VALUES (1, 1, 1);");

                AssertRejected(db, "INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id) VALUES (1, 1, 2);",
                    "a second value for the same user and org type");

                AssertRejected(db, "INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id) VALUES (1, 2, 1);",
                    "a value belonging to a different org type");

                db.Execute("DELETE FROM dbo.users WHERE id = 1;");
                Assert.AreEqual(
                    0,
                    Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.user_org_assignments")),
                    "Deleting a user must cascade their org assignments.");
            }
        }

        private static void AssertRejected(ScratchDatabase db, string sql, string what)
        {
            try
            {
                db.Execute(sql);
                Assert.Fail($"The schema should reject {what}.");
            }
            catch (SqlException)
            {
            }
        }
    }
}
