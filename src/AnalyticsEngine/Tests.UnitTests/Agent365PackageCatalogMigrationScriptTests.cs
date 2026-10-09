using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    [TestClass]
    public class Agent365PackageCatalogMigrationScriptTests
    {
        private const string MigrationId = "202610071200001_Agent365PackageCatalog";
        private const string PredecessorId = "202610021200001_LicenceHistory";
        private const string PredecessorModel = "0x1F8B0800AABBCCDD";

        private static string Script()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Common", "Entities", "Migrations", MigrationId + ".manual.sql");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {MigrationId}.manual.sql by walking up from the test assembly.");
            return null;
        }

        private static ScratchDatabase NewDatabase()
        {
            var db = ScratchDatabase.Create("agent365catalogmanual");
            db.Execute(@"
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
            db.Execute("INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion) VALUES ("
                + $"N'{PredecessorId}', N'Common.Entities.Migrations.Configuration', {PredecessorModel}, N'6.5.2');");
        }

        private static int StampCount(ScratchDatabase db)
        {
            return Convert.ToInt32(db.Scalar(
                $"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'"));
        }

        [TestMethod]
        public void TheManualScriptEmbedsTheMigrationUpSqlVerbatim()
        {
            StringAssert.Contains(Script(), Agent365PackageCatalog.Up_Sql.Trim(),
                "The manual script must apply exactly the same guarded schema as the installer migration.");
        }

        [TestMethod]
        public void RunningTheManualScriptCreatesTheSchemaAndCopiesThePredecessorStamp()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.tables WHERE name = 'copilot_agent_catalog_import_log'")));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.tables WHERE name = 'copilot_agent_packages'")));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.tables WHERE name = 'copilot_agent_package_elements'")));
                Assert.AreEqual(1, StampCount(db));
                Assert.AreEqual("yes", db.Scalar(
                    $"SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}') = " +
                    $"(SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}') THEN 'yes' ELSE 'no' END"));
            }
        }

        [TestMethod]
        public void ReRunningTheManualScriptIsACompleteNoOp()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db), "The migration history row must not be duplicated.");
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_copilot_agent_packages_never_used'")));
            }
        }

        [TestMethod]
        public void MissingPredecessorLeavesSchemaButDoesNotStampTheMigration()
        {
            using (var db = NewDatabase())
            {
                try
                {
                    db.ExecuteScript(Script(), quotedIdentifierOn: false);
                    Assert.Fail("The manual script must refuse to stamp before its predecessor.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, PredecessorId);
                    StringAssert.Contains(ex.Message, "not stamped");
                }

                Assert.AreEqual(0, StampCount(db));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar(
                    "SELECT COUNT(*) FROM sys.tables WHERE name = 'copilot_agent_packages'")));
            }
        }

        [TestMethod]
        public void IncompleteExistingSchemaDoesNotGetStamped()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.Execute(Agent365PackageCatalog.Up_Sql);
                db.Execute("ALTER TABLE dbo.copilot_agent_packages DROP COLUMN agent_identity_id;");

                try
                {
                    db.ExecuteScript(Script(), quotedIdentifierOn: false);
                    Assert.Fail("The manual script must refuse to stamp an incomplete schema.");
                }
                catch (SqlException ex)
                {
                    StringAssert.Contains(ex.Message, "schema validation failed");
                }

                Assert.AreEqual(0, StampCount(db));
            }
        }
    }
}
