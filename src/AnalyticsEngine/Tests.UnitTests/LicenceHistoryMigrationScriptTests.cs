using Common.Entities.Migrations;

using Microsoft.Data.SqlClient;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;

using System.IO;



namespace Tests.UnitTests

{

    /// <summary>The by-hand upgrade path for <c>202610021200001_LicenceHistory</c>.</summary>

    [TestClass]

    public class LicenceHistoryMigrationScriptTests

    {

        private const string MigrationId = "202610021200001_LicenceHistory";

        private const string PredecessorId = "202609221200001_UserOrganisations";

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

            var db = ScratchDatabase.Create("licencehistorymanual");

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

            => Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'"));



        [TestMethod]

        public void TheScriptEmbedsTheMigrationsOwnUpSqlVerbatim()

        {

            StringAssert.Contains(Script(), LicenceHistory.Up_Sql.Trim(),

                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade applies something different from the installer.");

        }



        [TestMethod]

        public void RunningItCreatesTheSchemaAndStampsTheMigration()

        {

            using (var db = NewDatabase())

            {

                StampPredecessor(db);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);



                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'license_refresh_runs'")));

                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'user_license_history'")));

                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'license_seat_count_history'")));

                Assert.AreEqual(1, StampCount(db));

                Assert.AreEqual("yes", db.Scalar(

                    $"SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}') = " +

                    $"(SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'{PredecessorId}') THEN 'yes' ELSE 'no' END"));

            }

        }



        [TestMethod]

        public void ReRunningItIsACleanNoOp()

        {

            using (var db = NewDatabase())

            {

                StampPredecessor(db);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);



                Assert.AreEqual(1, StampCount(db), "The stamp must not be duplicated.");

                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_user_license_history_open'")));

            }

        }



        [TestMethod]

        public void WithoutThePredecessorItRefusesToStamp()

        {

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



                Assert.AreEqual(0, StampCount(db));

                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'user_license_history'")),

                    "The additive schema may be present, but EF must not be told the migration completed without its predecessor.");

            }

        }

    }

}
