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

        /// <summary>
        /// The exclusion is qualified by schema, so a history table in the wrong place is still a
        /// difference rather than being silently waved through.
        /// </summary>
        [TestMethod]
        public void MigrationHistoryTable_InAnUnexpectedSchema_IsNotIgnored()
        {
            const string correct = BaseTableConst + "\nCREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL);";
            const string wrongSchema = BaseTableConst + "\nGO\nCREATE SCHEMA other;\nGO\nCREATE TABLE other.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL);";

            WithScripts(correct, wrongSchema, (left, right) =>
            {
                var differences = left.DifferencesFrom(right);
                Assert.IsTrue(differences.Any(d => d.Contains("other.__EFMigrationsHistory")),
                    "A history table outside dbo must not be swallowed by the exclusion. " +
                    SchemaSnapshot.Describe(differences));
            });
        }

        #region Index options that migrations in this repo actually depend on

        /// <summary>
        /// The unique index on <c>urls.full_url</c> sets <c>IGNORE_DUP_KEY</c> deliberately, so one racing
        /// duplicate cannot abort an entire insert batch, and its migration uses <c>ignore_dup_key = 1</c>
        /// as part of the target-state check. Two indexes differing only in that option are not the same
        /// index.
        /// </summary>
        [TestMethod]
        public void ChangedIgnoreDupKey_IsDetected()
        {
            const string off = "CREATE TABLE dbo.t (a int NOT NULL);CREATE UNIQUE INDEX IX_t ON dbo.t (a) WITH (IGNORE_DUP_KEY = OFF);";
            const string on = "CREATE TABLE dbo.t (a int NOT NULL);CREATE UNIQUE INDEX IX_t ON dbo.t (a) WITH (IGNORE_DUP_KEY = ON);";

            WithPair(off, on, (left, right) => AssertDetected(left, right, "IGNORE_DUP_KEY(0)"));
        }

        [TestMethod]
        public void DisabledIndex_IsDetected()
        {
            const string enabled = "CREATE TABLE dbo.t (a int NOT NULL);CREATE INDEX IX_t ON dbo.t (a);";
            const string disabled = enabled + "ALTER INDEX IX_t ON dbo.t DISABLE;";

            WithPair(enabled, disabled, (left, right) => AssertDetected(left, right, "DISABLED(0)"));
        }

        #endregion

        #region Constraint state

        /// <summary>
        /// A migration here creates a foreign key <c>WITH NOCHECK</c> and then runs the expensive
        /// <c>WITH CHECK CHECK CONSTRAINT</c> specifically to make it trusted. Without this the
        /// intermediate state would certify as identical to the finished one, even though an untrusted
        /// constraint is unusable by the optimiser and may be hiding invalid rows.
        /// </summary>
        [TestMethod]
        public void UntrustedForeignKey_IsDetected()
        {
            const string trusted = @"
CREATE TABLE dbo.parent (id int NOT NULL CONSTRAINT PK_parent PRIMARY KEY);
CREATE TABLE dbo.child (id int NOT NULL, parent_id int NOT NULL);
ALTER TABLE dbo.child WITH CHECK ADD CONSTRAINT FK_child_parent FOREIGN KEY (parent_id) REFERENCES dbo.parent(id);";

            const string untrusted = @"
CREATE TABLE dbo.parent (id int NOT NULL CONSTRAINT PK_parent PRIMARY KEY);
CREATE TABLE dbo.child (id int NOT NULL, parent_id int NOT NULL);
ALTER TABLE dbo.child WITH NOCHECK ADD CONSTRAINT FK_child_parent FOREIGN KEY (parent_id) REFERENCES dbo.parent(id);";

            WithPair(trusted, untrusted, (left, right) => AssertDetected(left, right, "NOTTRUSTED(0)"));
        }

        [TestMethod]
        public void DisabledCheckConstraint_IsDetected()
        {
            const string enabled = "CREATE TABLE dbo.t (a int NOT NULL CONSTRAINT CK_t CHECK (a > 0));";
            const string disabled = enabled + "ALTER TABLE dbo.t NOCHECK CONSTRAINT CK_t;";

            WithPair(enabled, disabled, (left, right) => AssertDetected(left, right, "DISABLED(0)"));
        }

        /// <summary>
        /// Constraint names are deliberately not compared, but cardinality is - otherwise a database with
        /// two structurally identical foreign keys would compare equal to one with a single foreign key.
        /// </summary>
        [TestMethod]
        public void DuplicateStructurallyIdenticalForeignKey_IsDetected()
        {
            const string one = @"
CREATE TABLE dbo.parent (id int NOT NULL CONSTRAINT PK_parent PRIMARY KEY);
CREATE TABLE dbo.child (id int NOT NULL, parent_id int NOT NULL);
ALTER TABLE dbo.child ADD CONSTRAINT FK_a FOREIGN KEY (parent_id) REFERENCES dbo.parent(id);";

            const string two = one + "\nALTER TABLE dbo.child ADD CONSTRAINT FK_b FOREIGN KEY (parent_id) REFERENCES dbo.parent(id);";

            WithPair(one, two, (left, right) => AssertDetected(left, right, "count differs"));
        }

        #endregion

        #region Generated columns

        [TestMethod]
        public void ChangedIdentitySeedAndIncrement_IsDetected()
        {
            const string standard = "CREATE TABLE dbo.t (id int IDENTITY(1,1) NOT NULL);";
            const string unusual = "CREATE TABLE dbo.t (id int IDENTITY(1000,5) NOT NULL);";

            WithPair(standard, unusual, (left, right) => AssertDetected(left, right, "IDENTITY(1,1)"));
        }

        [TestMethod]
        public void ChangedComputedColumnExpression_IsDetected()
        {
            const string plus = "CREATE TABLE dbo.t (a int NOT NULL, b AS (a + 1));";
            const string times = "CREATE TABLE dbo.t (a int NOT NULL, b AS (a * 2));";

            WithPair(plus, times, (left, right) => AssertDetected(left, right, "COMPUTED("));
        }

        #endregion

        #region Modules - views, procedures, functions

        /// <summary>
        /// Views are created and later rewritten by migrations in this repository, so a database with a
        /// missing or stale view must not compare equal to one with the right view.
        /// </summary>
        [TestMethod]
        public void MissingView_IsDetected()
        {
            const string withView = BaseTableConst + "\nGO\nCREATE VIEW dbo.v_widgets AS SELECT id FROM dbo.widgets;";

            WithScripts(withView, BaseTableConst, (left, right) => AssertDetected(left, right, "MODULE VIEW dbo.v_widgets"));
        }

        [TestMethod]
        public void ChangedViewBody_IsDetected()
        {
            const string original = "CREATE TABLE dbo.t (a int NOT NULL, b int NOT NULL);\nGO\nCREATE VIEW dbo.v AS SELECT a FROM dbo.t;";
            const string altered = "CREATE TABLE dbo.t (a int NOT NULL, b int NOT NULL);\nGO\nCREATE VIEW dbo.v AS SELECT b FROM dbo.t;";

            WithScripts(original, altered, (left, right) => AssertDetected(left, right, "MODULE VIEW dbo.v"));
        }

        /// <summary>
        /// Reformatting a view must NOT be reported, or the tool produces noise on every tidy-up and stops
        /// being trusted. Existing tests in this repo also treat quoted-identifier state as part of a
        /// view's required state, which is why it is captured separately from the body.
        /// </summary>
        [TestMethod]
        public void ReformattedViewBody_IsNotReported()
        {
            const string compact = "CREATE TABLE dbo.t (a int NOT NULL);\nGO\nCREATE VIEW dbo.v AS SELECT a FROM dbo.t;";
            const string spacedOut = "CREATE TABLE dbo.t (a int NOT NULL);\nGO\nCREATE VIEW dbo.v AS\r\n    SELECT   a\r\n    FROM     dbo.t;";

            WithScripts(compact, spacedOut, (left, right) =>
            {
                var differences = left.DifferencesFrom(right);
                Assert.AreEqual(0, differences.Count,
                    "Whitespace-only differences in a module body must not be reported. " +
                    SchemaSnapshot.Describe(differences));
            });
        }

        [TestMethod]
        public void MissingStoredProcedure_IsDetected()
        {
            const string withProc = BaseTableConst + "\nGO\nCREATE PROCEDURE dbo.usp_widgets AS SELECT 1;";

            WithScripts(withProc, BaseTableConst, (left, right) => AssertDetected(left, right, "MODULE SQL_STORED_PROCEDURE dbo.usp_widgets"));
        }

        #endregion


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
        /// Creating a database on LocalDB costs several seconds, and creating two per test dominated the
        /// runtime of this class. One pair is created for the whole class and emptied between tests
        /// instead, which is equivalent for these purposes because every test defines its own objects.
        /// </summary>
        static ScratchDatabase _left;
        static ScratchDatabase _right;

        [ClassInitialize]
        public static void CreateDatabases(TestContext context)
        {
            _left = ScratchDatabase.Create("schemaL");
            _right = ScratchDatabase.Create("schemaR");
        }

        [ClassCleanup]
        public static void DropDatabases()
        {
            _left?.Dispose();
            _right?.Dispose();
        }

        [TestCleanup]
        public void EmptyDatabases()
        {
            _left?.Execute(DropEverythingSql);
            _right?.Execute(DropEverythingSql);
        }

        /// <summary>
        /// Drops every user object, in dependency order: foreign keys, then modules, then tables, then any
        /// non-default schema.
        /// </summary>
        const string DropEverythingSql = @"
DECLARE @sql nvarchar(max) = N'';

SELECT @sql += 'ALTER TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
             + ' DROP CONSTRAINT ' + QUOTENAME(f.name) + ';'
FROM sys.foreign_keys f
JOIN sys.tables t ON t.object_id = f.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id;

SELECT @sql += 'DROP VIEW ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + ';'
FROM sys.views o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.is_ms_shipped = 0;

SELECT @sql += 'DROP PROCEDURE ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + ';'
FROM sys.procedures o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.is_ms_shipped = 0;

SELECT @sql += 'DROP FUNCTION ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + ';'
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('FN','IF','TF') AND o.is_ms_shipped = 0;

SELECT @sql += 'DROP TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ';'
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0;

SELECT @sql += 'DROP SCHEMA ' + QUOTENAME(s.name) + ';'
FROM sys.schemas s
WHERE s.schema_id BETWEEN 5 AND 16383 AND s.name <> 'dbo';

IF @sql <> N'' EXEC sp_executesql @sql;";

        /// <summary>
        /// Applies a script to each database and hands back their snapshots.
        /// </summary>
        static void WithPair(string leftScript, string rightScript, System.Action<SchemaSnapshot, SchemaSnapshot> assert)
        {
            _left.Execute(leftScript);
            _right.Execute(rightScript);

            assert(
                SchemaSnapshot.Capture(_left.ConnectionString, "left"),
                SchemaSnapshot.Capture(_right.ConnectionString, "right"));
        }

        /// <summary>
        /// As <see cref="WithPair"/>, but runs the scripts batch by batch so they can contain GO - which
        /// CREATE VIEW and CREATE PROCEDURE require, since both must be the first statement in a batch.
        /// </summary>
        static void WithScripts(string leftScript, string rightScript, System.Action<SchemaSnapshot, SchemaSnapshot> assert)
        {
            _left.ExecuteScript(leftScript, quotedIdentifierOn: true);
            _right.ExecuteScript(rightScript, quotedIdentifierOn: true);

            assert(
                SchemaSnapshot.Capture(_left.ConnectionString, "left"),
                SchemaSnapshot.Capture(_right.ConnectionString, "right"));
        }
    }
}
