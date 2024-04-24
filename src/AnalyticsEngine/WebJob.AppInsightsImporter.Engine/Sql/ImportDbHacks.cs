using Common.Entities;
using System.Data.Entity;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    /// <summary>
    /// The class of shame.
    /// </summary>
    internal static class ImportDbHacks
    {

        /// <summary>
        /// A hack because we should've included in an EF migration. SQL_Latin1_General_CP1_CS_AS (case sensitive) is the wanted colation 
        /// (it used to be the default of SQL_Latin1_General_CP1_CI_AS (case insensitive) but had to handle AppInsights wierd session IDs in multiple cases).
        /// Fun fact: we don't even use AppInsights generated session Ids anymore, so this change is useless but we can't change this index now as it would take forever to recreate in production DBs
        /// </summary>
        public static void EnsureSessionTableHasRightCollation(Database database)
        {
            // Check if we need to convert collation + insert index
            database.ExecuteSqlCommand(@"
                if (SELECT c.collation_name 
                    FROM SYS.COLUMNS c
                    JOIN SYS.TABLES t ON t.object_id = c.object_id

                    WHERE t.name = 'sessions' and c.name = 'ai_session_id') != 'SQL_Latin1_General_CP1_CS_AS'
                begin

                    print 'Altering sessions ai_session_id collation'

                    ALTER TABLE[sessions] ALTER COLUMN ai_session_id varchar(50) COLLATE SQL_Latin1_General_CP1_CS_AS
                end
                else 
                begin
                    print 'sessions table ai_session_id already correct collation.'
                end

                -- Add index on session
                IF NOT EXISTS(SELECT * FROM sys.indexes WHERE name = 'IX_ai_session_id' AND object_id = OBJECT_ID('sessions'))
                BEGIN

                    print 'Creating index on sessions'

                    BEGIN TRANSACTION

                    CREATE NONCLUSTERED INDEX [IX_ai_session_id] ON [dbo].[sessions]
                    (
	                    [ai_session_id] ASC
                    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)

                    ALTER TABLE dbo.sessions SET(LOCK_ESCALATION = TABLE)


                    COMMIT
                end
                else begin
                    print 'Index on sessions already exists'

                    if (
                    SELECT i.is_unique
                    FROM sys.indexes i
                        INNER JOIN sys.objects o ON i.object_id = o.object_id
                        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                    WHERE s.name = 'dbo'
                        AND o.name = 'sessions'
                        AND i.name = 'IX_ai_session_id'
	                    ) =1
	                    begin

		                    print 'Recreating index with non-unique'
		                    CREATE NONCLUSTERED INDEX [IX_ai_session_id] ON [dbo].[sessions]
		                    (
			                    [ai_session_id] ASC
		                    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = ON, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]

	                    end

                END
");
        }




        /// <summary>
        /// Runs script to clean hits with duplicate page-request IDs, and creates unique index on page_request_id if doesn't exist.
        /// This only needs to be run once ever really.
        /// </summary>
        public static void CleanDuplicateHits()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.ExecuteSqlCommand(Properties.Resources.Delete_Duplicate_Hits_and_Create_ReqID_IDX);
            }
        }
    }
}
