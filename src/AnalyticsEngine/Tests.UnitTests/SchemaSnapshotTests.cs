using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Proves <see cref="SchemaSnapshot"/> detects each class of difference it claims to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot exists to underwrite the EF Core V1 baseline (issue #528): a database built by the
    /// baseline must be identical to one built by applying every EF6 migration. A comparison tool that
    /// quietly misses a difference is worse than no tool, because it would certify a wrong baseline.
    /// </para>
    /// <para>
    /// So each test below makes exactly one change to an otherwise identical pair of databases and
    /// asserts the difference is both detected and named. The <c>nvarchar</c>/<c>varchar</c> case is
    /// called out separately because this codebase has a hard rule about it and the two are otherwise
    /// easy to conflate.
    /// </para>
    /// </remarks>
    [TestClass]
    public class SchemaSnapshotTests
    {
        const string BaseTable = @"
CREATE TABLE dbo.widgets (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_widgets PRIMARY KEY,
    name nvarchar(200) NOT NULL,
    note nvarchar(max) NULL,
    created_utc datetime2(7) NOT NULL CONSTRAINT DF_widgets_created DEFAULT (SYSUTCDATETIME())
);
CREATE NONCLUSTERED INDEX IX_widgets_name ON dbo.widgets (name) INCLUDE (created_utc);";

        [TestMethod]
        public void IdenticalDatabases_ProduceNoDifferences()
        {
            WithPair(BaseTable, BaseTable, (left, right) =>
            {
                var differences = left.DifferencesFrom(right);
                Assert.AreEqual(0, differences.Count, SchemaSnapshot.Describe(differences));
            });
        }

        [TestMethod]
        public void AddedTable_IsDetected()
        {
            WithPair(BaseTable, BaseTable + "\nCREATE TABLE dbo.extra (id int NOT NULL);", (left, right) =>
            {
                AssertDetected(left, right, "TABLE dbo.extra");
            });
        }

        [TestMethod]
        public void AddedColumn_IsDetected()
        {
            WithPair(BaseTable, BaseTable + "\nALTER TABLE dbo.widgets ADD extra_col int NULL;", (left, right) =>
            {
                AssertDetected(left, right, "COLUMN dbo.widgets.extra_col");
            });
        }

        /// <summary>
        /// The distinction this codebase cares most about: <c>varchar</c> silently corrupts non-Latin
        /// customer text, so a baseline that got this wrong would be a data-loss bug.
        /// </summary>
        [TestMethod]
        public void NvarcharVersusVarchar_IsDetected()
        {
            const string asNvarchar = "CREATE TABLE dbo.t (v nvarchar(250) NOT NULL);";
            const string asVarchar = "CREATE TABLE dbo.t (v varchar(250) NOT NULL);";

            WithPair(asNvarchar, asVarchar, (left, right) =>
            {
                var differences = left.DifferencesFrom(right);
                Assert.IsTrue(differences.Any(d => d.Contains("nvarchar(250)")),
                    "The nvarchar side was not reported. " + SchemaSnapshot.Describe(differences));
                Assert.IsTrue(differences.Any(d => d.Contains("varchar(250)") && !d.Contains("nvarchar")),
                    "The varchar side was not reported. " + SchemaSnapshot.Describe(differences));
            });
        }

        [TestMethod]
        public void ChangedColumnWidth_IsDetected()
        {
            WithPair("CREATE TABLE dbo.t (v nvarchar(850) NOT NULL);",
                     "CREATE TABLE dbo.t (v nvarchar(450) NOT NULL);",
                (left, right) => AssertDetected(left, right, "nvarchar(850)"));
        }

        [TestMethod]
        public void ChangedNullability_IsDetected()
        {
            WithPair("CREATE TABLE dbo.t (v int NOT NULL);",
                     "CREATE TABLE dbo.t (v int NULL);",
                (left, right) => AssertDetected(left, right, "COLUMN dbo.t.v int NOT NULL"));
        }

        [TestMethod]
        public void MissingIndex_IsDetected()
        {
            WithPair(BaseTable, BaseTable.Replace("CREATE NONCLUSTERED INDEX IX_widgets_name ON dbo.widgets (name) INCLUDE (created_utc);", ""),
                (left, right) => AssertDetected(left, right, "INDEX dbo.widgets.IX_widgets_name"));
        }

        /// <summary>
        /// Key order changes the plans an index can serve, so two indexes over the same columns in a
        /// different order are genuinely different indexes.
        /// </summary>
        [TestMethod]
        public void ChangedIndexKeyOrder_IsDetected()
        {
            const string ab = "CREATE TABLE dbo.t (a int NOT NULL, b int NOT NULL);CREATE INDEX IX_t ON dbo.t (a,b);";
            const string ba = "CREATE TABLE dbo.t (a int NOT NULL, b int NOT NULL);CREATE INDEX IX_t ON dbo.t (b,a);";

            WithPair(ab, ba, (left, right) => AssertDetected(left, right, "KEYS(a,b)"));
        }

        /// <summary>
        /// An INCLUDE list is not a seekable access path, so moving a column between KEYS and INCLUDE is a
        /// real behavioural change - one this repo has measured costing 5.5x on wall-clock.
        /// </summary>
        [TestMethod]
        public void KeyColumnMovedToInclude_IsDetected()
        {
            const string asKey = "CREATE TABLE dbo.t (a int NOT NULL, b int NOT NULL);CREATE INDEX IX_t ON dbo.t (a,b);";
            const string asInclude = "CREATE TABLE dbo.t (a int NOT NULL, b int NOT NULL);CREATE INDEX IX_t ON dbo.t (a) INCLUDE (b);";

            WithPair(asKey, asInclude, (left, right) =>
            {
                var differences = left.DifferencesFrom(right);
                Assert.IsTrue(differences.Any(d => d.Contains("KEYS(a,b)")),
                    "The composite-key form was not reported. " + SchemaSnapshot.Describe(differences));
                Assert.IsTrue(differences.Any(d => d.Contains("INCLUDE(b)")),
                    "The INCLUDE form was not reported. " + SchemaSnapshot.Describe(differences));
            });
        }

        [TestMethod]
        public void ChangedIndexUniqueness_IsDetected()
        {
            const string nonUnique = "CREATE TABLE dbo.t (a int NOT NULL);CREATE INDEX IX_t ON dbo.t (a);";
            const string unique = "CREATE TABLE dbo.t (a int NOT NULL);CREATE UNIQUE INDEX IX_t ON dbo.t (a);";

            WithPair(nonUnique, unique, (left, right) => AssertDetected(left, right, "UNIQUE"));
        }

        [TestMethod]
        public void ChangedIndexFilter_IsDetected()
        {
            const string unfiltered = "CREATE TABLE dbo.t (a int NULL);CREATE INDEX IX_t ON dbo.t (a);";
            const string filtered = "CREATE TABLE dbo.t (a int NULL);CREATE INDEX IX_t ON dbo.t (a) WHERE a IS NOT NULL;";

            WithPair(unfiltered, filtered, (left, right) => AssertDetected(left, right, "FILTER("));
        }

        [TestMethod]
        public void MissingForeignKey_IsDetected()
        {
            const string withFk = @"
CREATE TABLE dbo.parent (id int NOT NULL CONSTRAINT PK_parent PRIMARY KEY);
CREATE TABLE dbo.child (id int NOT NULL, parent_id int NOT NULL CONSTRAINT FK_child_parent FOREIGN KEY REFERENCES dbo.parent(id));";
            const string withoutFk = @"
CREATE TABLE dbo.parent (id int NOT NULL CONSTRAINT PK_parent PRIMARY KEY);
CREATE TABLE dbo.child (id int NOT NULL, parent_id int NOT NULL);";

            WithPair(withFk, withoutFk, (left, right) => AssertDetected(left, right, "FOREIGNKEY dbo.child(parent_id) -> dbo.parent(id)"));
        }

        [TestMethod]
        public void ChangedDefault_IsDetected()
        {
            const string zero = "CREATE TABLE dbo.t (a int NOT NULL CONSTRAINT DF_t DEFAULT (0));";
            const string one = "CREATE TABLE dbo.t (a int NOT NULL CONSTRAINT DF_t DEFAULT (1));";

            WithPair(zero, one, (left, right) => AssertDetected(left, right, "DEFAULT ((0))"));
        }

        /// <summary>
        /// The two frameworks deliberately use different history tables, so that difference must NOT be
        /// reported - otherwise every real comparison starts with a false positive and gets ignored.
        /// </summary>
        [TestMethod]
        public void MigrationHistoryTables_AreIgnored()
        {
            const string ef6 = BaseTableConst + "\nCREATE TABLE dbo.__MigrationHistory (MigrationId nvarchar(150) NOT NULL);";
            const string efCore = BaseTableConst + "\nCREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL);";

            WithPair(ef6, efCore, (left, right) =>
            {
                var differences = left.DifferencesFrom(right);
                Assert.AreEqual(0, differences.Count,
                    "EF6 and EF Core history tables are expected to differ and must not be reported. " +
                    SchemaSnapshot.Describe(differences));
            });
        }

        const string BaseTableConst = "CREATE TABLE dbo.widgets (id int NOT NULL);";

        static void AssertDetected(SchemaSnapshot left, SchemaSnapshot right, string expectedFragment)
        {
            var differences = left.DifferencesFrom(right);

            Assert.IsTrue(differences.Count > 0,
                $"Expected a difference containing '{expectedFragment}' but the snapshots compared equal.");
            Assert.IsTrue(differences.Any(d => d.Contains(expectedFragment)),
                $"No reported difference mentioned '{expectedFragment}'. " + SchemaSnapshot.Describe(differences));
        }

        /// <summary>
        /// Builds two scratch databases, applies a script to each, and hands back their snapshots.
        /// </summary>
        static void WithPair(string leftScript, string rightScript, System.Action<SchemaSnapshot, SchemaSnapshot> assert)
        {
            using (var left = ScratchDatabase.Create("schemaL"))
            using (var right = ScratchDatabase.Create("schemaR"))
            {
                left.Execute(leftScript);
                right.Execute(rightScript);

                assert(
                    SchemaSnapshot.Capture(left.ConnectionString, "left"),
                    SchemaSnapshot.Capture(right.ConnectionString, "right"));
            }
        }
    }
}
