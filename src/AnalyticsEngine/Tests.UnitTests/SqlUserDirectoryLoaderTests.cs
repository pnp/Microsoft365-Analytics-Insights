using Common.Entities.Migrations;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The SQL Server directory loader, against a scratch database with production's column types and
    /// the user-organisation tables built from the migration's own <see cref="UserOrganisations.Up_Sql"/>.
    /// </summary>
    /// <remarks>
    /// <c>user_name</c> is <c>varchar(250)</c> as in production (a UPN is ASCII by Entra policy), while
    /// the lookup names are <c>nvarchar(100)</c> and organisation values <c>nvarchar(848)</c> - so the
    /// Greek values below genuinely cross the encoding boundary they would cross in a customer tenant.
    /// </remarks>
    [TestClass]
    public class SqlUserDirectoryLoaderTests
    {
        private const string GreekDepartment = "Καλημέρα κόσμε";
        private const string GreekOrgValue = "Αθήνα Λειτουργίες";

        private const string UsersSchema = @"
CREATE TABLE dbo.user_departments (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_job_titles (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_company_name (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_office_locations (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_country_or_region (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_state_or_province (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.user_usage_locations (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
CREATE TABLE dbo.users (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    user_name varchar(250) NOT NULL,
    mail nvarchar(max) NULL,
    account_enabled bit NULL,
    manager_id int NULL,
    department_id int NULL,
    job_title_id int NULL,
    company_name_id int NULL,
    office_location_id int NULL,
    country_or_region_id int NULL,
    state_or_province_id int NULL,
    usage_location_id int NULL
);";

        private const string Data = @"
INSERT INTO dbo.user_departments (name) VALUES (N'Sales'), (N'" + GreekDepartment + @"');
INSERT INTO dbo.user_job_titles (name) VALUES (N'Account Executive');
INSERT INTO dbo.user_company_name (name) VALUES (N'Contoso');
INSERT INTO dbo.user_office_locations (name) VALUES (N'Reading');
INSERT INTO dbo.user_country_or_region (name) VALUES (N'United Kingdom'), (N'Greece');
INSERT INTO dbo.user_state_or_province (name) VALUES (N'Berkshire');
INSERT INTO dbo.user_usage_locations (name) VALUES (N'GB');

SET IDENTITY_INSERT dbo.users ON;
INSERT INTO dbo.users (id, user_name, mail, account_enabled, manager_id, department_id, job_title_id, company_name_id,
                       office_location_id, country_or_region_id, state_or_province_id, usage_location_id)
VALUES
    (101, 'boss@contoso.com', NULL, 1, NULL, 1, NULL, 1, NULL, 1, NULL, NULL),
    (102, 'rep@contoso.com', NULL, 1, 101, 1, 1, 1, 1, 1, 1, 1),
    (103, 'analyst@contoso.onmicrosoft.com', N'analyst@fabrikam.com', 0, 101, 2, NULL, NULL, NULL, 2, NULL, NULL),
    -- A department id with no lookup row: must read as not set rather than fail the load.
    (104, 'orphan@contoso.com', NULL, NULL, 999, 42, NULL, NULL, NULL, NULL, NULL, NULL);
SET IDENTITY_INSERT dbo.users OFF;";

        private static ScratchDatabase _db;
        private static ScratchDatabase _legacy;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _db = ScratchDatabase.Create("userfilter");
            _db.Execute(UsersSchema);
            _db.Execute(UserOrganisations.Up_Sql);
            _db.Execute(Data);
            _db.Execute(@"
SET IDENTITY_INSERT dbo.user_org_types ON;
INSERT INTO dbo.user_org_types (id, name, source_kind, is_enabled) VALUES (1, N'Cost centre', 2, 1), (2, N'Retired grouping', 2, 0);
SET IDENTITY_INSERT dbo.user_org_types OFF;
SET IDENTITY_INSERT dbo.user_org_values ON;
INSERT INTO dbo.user_org_values (id, org_type_id, name) VALUES (10, 1, N'" + GreekOrgValue + @"'), (11, 1, N'CC-100'), (20, 2, N'Old');
SET IDENTITY_INSERT dbo.user_org_values OFF;
INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id) VALUES (102, 1, 11), (103, 1, 10), (102, 2, 20);");

            // A database that predates the user-organisation migration.
            _legacy = ScratchDatabase.Create("userfilterlegacy");
            _legacy.Execute(UsersSchema);
            _legacy.Execute(Data);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _db?.Dispose();
            _legacy?.Dispose();
        }

        [TestMethod]
        public async Task Load_ReadsEveryEntraAttribute_ResolvingLookupsAndManagers()
        {
            var snapshot = await UserFilterStores.CreateDirectoryLoader(_db.ConnectionString).LoadAsync();

            Assert.AreEqual(4, snapshot.PeopleCount);
            AssertValue(snapshot, UserFilterDimensions.Department, 102, "Sales");
            AssertValue(snapshot, UserFilterDimensions.JobTitle, 102, "Account Executive");
            AssertValue(snapshot, UserFilterDimensions.CompanyName, 102, "Contoso");
            AssertValue(snapshot, UserFilterDimensions.OfficeLocation, 102, "Reading");
            AssertValue(snapshot, UserFilterDimensions.Country, 102, "United Kingdom");
            AssertValue(snapshot, UserFilterDimensions.StateOrProvince, 102, "Berkshire");
            AssertValue(snapshot, UserFilterDimensions.UsageLocation, 102, "GB");
            AssertValue(snapshot, UserFilterDimensions.Manager, 102, "boss@contoso.com");
            AssertValue(snapshot, UserFilterDimensions.AccountStatus, 103, UserFilterTokens.Disabled);
            AssertValue(snapshot, UserFilterDimensions.UserType, 102, UserFilterTokens.Member);

            // The UPN is on the tenant's default suffix, so the mail domain names the company.
            AssertValue(snapshot, UserFilterDimensions.EmailDomain, 103, "fabrikam.com");

            // A lookup id with no row, and a manager id with no user, both read as "not set".
            AssertValue(snapshot, UserFilterDimensions.Department, 104, null);
            AssertValue(snapshot, UserFilterDimensions.Manager, 104, null);
            AssertValue(snapshot, UserFilterDimensions.AccountStatus, 104, null);
        }

        [TestMethod]
        public async Task Load_RoundTripsNonLatinValues()
        {
            var snapshot = await UserFilterStores.CreateDirectoryLoader(_db.ConnectionString).LoadAsync();

            AssertValue(snapshot, UserFilterDimensions.Department, 103, GreekDepartment);
            AssertValue(snapshot, UserFilterDimensions.ForOrgType(1), 103, GreekOrgValue);

            var filter = UserFilterCompiler.Compile(
                UserFilterCodec.Parse("[{\"d\":\"org:1\",\"v\":[\"" + GreekOrgValue + "\"]}]"), snapshot);
            Assert.IsTrue(filter.Matches(103));
            Assert.IsFalse(filter.Matches(102));
        }

        [TestMethod]
        public async Task Load_OffersOnlyEnabledOrganisationTypes()
        {
            var snapshot = await UserFilterStores.CreateDirectoryLoader(_db.ConnectionString).LoadAsync();

            var custom = snapshot.Dimensions.Where(d => d.Kind == UserFilterDimensionKind.Custom).ToList();
            Assert.AreEqual(1, custom.Count, "A disabled type is retired, so it is not offered as a filter.");
            Assert.AreEqual("Cost centre", custom[0].Name);
            Assert.AreEqual(2, custom[0].PeopleWithValue);
            Assert.IsNull(snapshot.Column(UserFilterDimensions.ForOrgType(2)));
        }

        [TestMethod]
        public async Task Load_WorksOnADatabaseWithoutUserOrganisations()
        {
            var snapshot = await UserFilterStores.CreateDirectoryLoader(_legacy.ConnectionString).LoadAsync();

            Assert.AreEqual(4, snapshot.PeopleCount);
            Assert.IsFalse(snapshot.Dimensions.Any(d => d.Kind == UserFilterDimensionKind.Custom));
            AssertValue(snapshot, UserFilterDimensions.Department, 101, "Sales");
        }

        [TestMethod]
        public async Task Load_ReadsOneCommittedMomentWhereTheDatabaseAllowsSnapshots()
        {
            // An import still writing must neither hold the load up nor leak half its changes into it.
            // Under READ COMMITTED the load would wait on the writer's lock, so finishing at all - with
            // the committed value - shows the statements really did run in one SNAPSHOT transaction.
            using (var db = ScratchDatabase.Create("userfiltersnapshot"))
            {
                db.Execute(UsersSchema);
                db.Execute(UserOrganisations.Up_Sql);
                db.Execute(Data);
                db.Execute("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");

                using (var writer = new SqlConnection(db.ConnectionString))
                {
                    await writer.OpenAsync();
                    using (var tx = writer.BeginTransaction())
                    {
                        using (var update = new SqlCommand("UPDATE dbo.users SET department_id = 2 WHERE id = 102;", writer, tx))
                        {
                            await update.ExecuteNonQueryAsync();
                        }

                        var load = UserFilterStores.CreateDirectoryLoader(db.ConnectionString).LoadAsync();
                        var first = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(30)));

                        Assert.AreSame(load, first, "The load waited for an uncommitted write, so it was not reading a snapshot.");
                        AssertValue(await load, UserFilterDimensions.Department, 102, "Sales");

                        tx.Rollback();
                    }
                }
            }
        }

        private static void AssertValue(UserDirectorySnapshot snapshot, string dimension, int userId, string expected)
        {
            var column = snapshot.Column(dimension);
            Assert.IsNotNull(column, dimension + " should exist.");
            Assert.IsTrue(snapshot.TryGetRow(userId, out var row), "User " + userId + " should be in the snapshot.");

            var filter = UserFilterCompiler.Compile(
                new UserFilterExpression(new[]
                {
                    expected == null
                        ? new UserFilterClause(UserFilterJoin.And, dimension, UserFilterOperator.Is, new string[0], includeNotSet: true)
                        : new UserFilterClause(UserFilterJoin.And, dimension, UserFilterOperator.Is, new[] { expected }, includeNotSet: false),
                }),
                snapshot);

            Assert.IsTrue(filter.Matches(userId), $"User {userId} should have {dimension} = {expected ?? "(not set)"}.");
        }
    }
}
