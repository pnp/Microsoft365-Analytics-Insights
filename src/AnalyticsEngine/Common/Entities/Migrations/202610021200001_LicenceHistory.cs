namespace Common.Entities.Migrations

{

    using System.Data.Entity.Migrations;



    /// <summary>

    /// Adds licence assignment history, completed licence-refresh markers and per-refresh SKU seat

    /// counts. This is a purely additive migration: new empty tables and indexes only, no rewrite of

    /// existing licence tables and no performance-motivated schema change. The importer seeds history

    /// on the first completed refresh after deployment.

    /// </summary>

    public partial class LicenceHistory : DbMigration

    {

        /// <summary>

        /// Additive, guarded and re-runnable schema for licence history. Exposed so the manual DBA

        /// script can embed exactly the same SQL that EF runs.

        /// </summary>

        public const string Up_Sql = @"SET QUOTED_IDENTIFIER ON;

SET NOCOUNT ON;



RAISERROR('LicenceHistory: adding licence refresh, assignment-history and seat-count-history tables. Purely additive schema; performance-motivated migration benchmark gate does not apply.', 0, 1) WITH NOWAIT;



IF OBJECT_ID(N'dbo.license_refresh_runs', N'U') IS NULL

BEGIN

    CREATE TABLE [dbo].[license_refresh_runs]

    (

        [id] int IDENTITY(1,1) NOT NULL,

        [completed_utc] datetime2(0) NOT NULL,

        [previous_completed_utc] datetime2(0) NULL,

        CONSTRAINT [PK_license_refresh_runs] PRIMARY KEY CLUSTERED ([id] ASC)

    );

    RAISERROR('LicenceHistory: created dbo.license_refresh_runs.', 0, 1) WITH NOWAIT;

END

ELSE

BEGIN

    RAISERROR('LicenceHistory: dbo.license_refresh_runs already exists.', 0, 1) WITH NOWAIT;

END



IF OBJECT_ID(N'dbo.license_refresh_runs', N'U') IS NOT NULL

   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.license_refresh_runs') AND name = N'IX_license_refresh_runs_completed_utc')

BEGIN

    CREATE NONCLUSTERED INDEX [IX_license_refresh_runs_completed_utc]

        ON [dbo].[license_refresh_runs] ([completed_utc] ASC)

        INCLUDE ([previous_completed_utc]);

    RAISERROR('LicenceHistory: created IX_license_refresh_runs_completed_utc.', 0, 1) WITH NOWAIT;

END



IF OBJECT_ID(N'dbo.user_license_history', N'U') IS NULL

BEGIN

    CREATE TABLE [dbo].[user_license_history]

    (

        [id] bigint IDENTITY(1,1) NOT NULL,

        [user_id] int NOT NULL,

        [license_type_id] int NOT NULL,

        [valid_from_utc] datetime2(0) NOT NULL,

        [valid_to_utc] datetime2(0) NULL,

        [from_source] tinyint NOT NULL,

        [valid_from_previous_refresh_utc] datetime2(0) NULL,

        [valid_to_previous_refresh_utc] datetime2(0) NULL,

        [from_refresh_run_id] int NULL,

        [to_refresh_run_id] int NULL,

        CONSTRAINT [PK_user_license_history] PRIMARY KEY CLUSTERED ([id] ASC)

    );

    RAISERROR('LicenceHistory: created dbo.user_license_history.', 0, 1) WITH NOWAIT;

END

ELSE

BEGIN

    RAISERROR('LicenceHistory: dbo.user_license_history already exists.', 0, 1) WITH NOWAIT;

END



IF OBJECT_ID(N'dbo.user_license_history', N'U') IS NOT NULL

BEGIN

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_license_history') AND name = N'IX_user_license_history_license_from')

    BEGIN

        CREATE NONCLUSTERED INDEX [IX_user_license_history_license_from]

            ON [dbo].[user_license_history] ([license_type_id] ASC, [valid_from_utc] ASC)

            INCLUDE ([user_id], [valid_to_utc], [valid_from_previous_refresh_utc], [valid_to_previous_refresh_utc], [from_source]);

        RAISERROR('LicenceHistory: created IX_user_license_history_license_from.', 0, 1) WITH NOWAIT;

    END



    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_license_history') AND name = N'IX_user_license_history_user_from')

    BEGIN

        CREATE NONCLUSTERED INDEX [IX_user_license_history_user_from]

            ON [dbo].[user_license_history] ([user_id] ASC, [valid_from_utc] ASC)

            INCLUDE ([license_type_id], [valid_to_utc], [valid_from_previous_refresh_utc], [valid_to_previous_refresh_utc], [from_source]);

        RAISERROR('LicenceHistory: created IX_user_license_history_user_from.', 0, 1) WITH NOWAIT;

    END



    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.user_license_history') AND name = N'UX_user_license_history_open')

    BEGIN

        CREATE UNIQUE NONCLUSTERED INDEX [UX_user_license_history_open]

            ON [dbo].[user_license_history] ([license_type_id] ASC, [user_id] ASC)

            WHERE [valid_to_utc] IS NULL;

        RAISERROR('LicenceHistory: created UX_user_license_history_open.', 0, 1) WITH NOWAIT;

    END

END



IF OBJECT_ID(N'dbo.license_seat_count_history', N'U') IS NULL

BEGIN

    CREATE TABLE [dbo].[license_seat_count_history]

    (

        [id] bigint IDENTITY(1,1) NOT NULL,

        [refresh_run_id] int NOT NULL,

        [license_type_id] int NOT NULL,

        [observed_utc] datetime2(0) NOT NULL,

        [consumed_units] int NULL,

        [prepaid_enabled_units] int NULL,

        [prepaid_warning_units] int NULL,

        [prepaid_suspended_units] int NULL,

        CONSTRAINT [PK_license_seat_count_history] PRIMARY KEY CLUSTERED ([id] ASC)

    );

    RAISERROR('LicenceHistory: created dbo.license_seat_count_history.', 0, 1) WITH NOWAIT;

END

ELSE

BEGIN

    RAISERROR('LicenceHistory: dbo.license_seat_count_history already exists.', 0, 1) WITH NOWAIT;

END



IF OBJECT_ID(N'dbo.license_seat_count_history', N'U') IS NOT NULL

BEGIN

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.license_seat_count_history') AND name = N'UX_license_seat_count_history_run_license')

    BEGIN

        CREATE UNIQUE NONCLUSTERED INDEX [UX_license_seat_count_history_run_license]

            ON [dbo].[license_seat_count_history] ([refresh_run_id] ASC, [license_type_id] ASC);

        RAISERROR('LicenceHistory: created UX_license_seat_count_history_run_license.', 0, 1) WITH NOWAIT;

    END



    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.license_seat_count_history') AND name = N'IX_license_seat_count_history_license_observed')

    BEGIN

        CREATE NONCLUSTERED INDEX [IX_license_seat_count_history_license_observed]

            ON [dbo].[license_seat_count_history] ([license_type_id] ASC, [observed_utc] ASC)

            INCLUDE ([consumed_units], [prepaid_enabled_units], [prepaid_warning_units], [prepaid_suspended_units]);

        RAISERROR('LicenceHistory: created IX_license_seat_count_history_license_observed.', 0, 1) WITH NOWAIT;

    END

END



RAISERROR('LicenceHistory: schema is present.', 0, 1) WITH NOWAIT;";



        public override void Up()

        {

            Sql(Up_Sql, suppressTransaction: true);

        }



        public override void Down()

        {

        }

    }

}
