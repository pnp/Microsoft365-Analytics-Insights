/* ============================================================================================
   Sizing script for migration 202609101100001_UniqueUrlsFullUrlIndex
   (makes dbo.urls.full_url UNIQUE)

   PURPOSE
     Estimates how long the migration will take and how much work it will do, by measuring the
     things that actually drive its cost. Run this on a customer/production database BEFORE
     scheduling the upgrade window.

   SAFETY
     100% READ-ONLY. No DDL, no DML, no hints, no temp objects. Safe to run on production while
     the importer is live (it takes no locks beyond ordinary reads; add
     SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED if you want to avoid blocking entirely).

   *** DATA HANDLING — READ BEFORE SHARING RESULTS ***
     This script deliberately emits ONLY aggregate counts and sizes. It never selects a URL, a
     user, a tenant name or a database name. Even so, exact row counts are treated as customer
     data by this project's policy: when reporting results back into the repo, a PR, an issue or
     release notes, ROUND them to a synthetic bucket (e.g. "~10M urls, ~2% duplicated") and never
     paste raw output verbatim.
   ============================================================================================ */

SET NOCOUNT ON;

/* --------------------------------------------------------------------------------------------
   0. Server edition — decides whether the index build can be ONLINE (non-blocking).
        3 = Enterprise, 5 = Azure SQL DB, 8 = Azure SQL MI  -> ONLINE supported
        2 = Standard,   4 = Express/LocalDB                 -> OFFLINE only (table locked)
   -------------------------------------------------------------------------------------------- */
SELECT
    engine_edition          = CAST(SERVERPROPERTY('EngineEdition') AS int),
    edition                 = CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
    product_version         = CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(32)),
    online_index_supported  = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3,5,8)
                                   THEN 'YES' ELSE 'NO - dbo.urls will be LOCKED for the rebuild' END,
    db_collation            = CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
    collation_is_CI         = CASE WHEN CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(),'Collation'))
                                        LIKE '%[_]CI[_]%' THEN 'YES - /Foo and /foo are the SAME url'
                                   ELSE 'NO - case-sensitive; duplicates are matched case-sensitively' END;

/* --------------------------------------------------------------------------------------------
   1. Is the prerequisite in place? full_url must already be nvarchar(850) NOT NULL.
      If this reports anything else, the earlier migrations (ShrinkUrlsFullUrlColumn /
      UrlFullUrlNvarchar) have not been applied and this migration will refuse to run.
   -------------------------------------------------------------------------------------------- */
SELECT
    column_type   = t.name,
    max_length_bytes = c.max_length,
    is_nullable   = c.is_nullable,
    verdict       = CASE WHEN t.name = 'nvarchar' AND c.max_length = 1700 AND c.is_nullable = 0
                         THEN 'OK - ready for a unique index'
                         ELSE 'NOT READY - apply the earlier url migrations first' END
FROM sys.columns c
JOIN sys.types  t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url';

/* --------------------------------------------------------------------------------------------
   2. Size of dbo.urls — drives the index REBUILD cost (usually the dominant cost).
   -------------------------------------------------------------------------------------------- */
SELECT
    total_url_rows   = SUM(CASE WHEN i.index_id IN (0,1) THEN p.rows ELSE 0 END),
    reserved_MB      = CAST(SUM(a.total_pages) * 8.0 / 1024 AS decimal(18,1)),
    data_MB          = CAST(SUM(a.data_pages)  * 8.0 / 1024 AS decimal(18,1))
FROM sys.indexes i
JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
WHERE i.object_id = OBJECT_ID(N'dbo.urls');

/* Current state of the index we are about to make unique. */
SELECT
    index_name     = i.name,
    is_unique      = i.is_unique,
    ignore_dup_key = i.ignore_dup_key,
    is_disabled    = i.is_disabled,
    verdict        = CASE WHEN i.is_unique = 1 THEN 'ALREADY UNIQUE - migration is a no-op'
                          ELSE 'non-unique - migration will rebuild it' END
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.urls') AND i.name = N'IX_urls_full_url';

/* --------------------------------------------------------------------------------------------
   3. THE HEADLINE NUMBER — how many duplicate URLs exist.
      Grouped under the database collation, i.e. exactly how the unique index will group them.
      surplus_rows is the number of dbo.urls rows the migration will DELETE.
   -------------------------------------------------------------------------------------------- */
SELECT
    duplicate_groups = COUNT(*),
    surplus_rows     = ISNULL(SUM(c) - COUNT(*), 0),
    worst_group_size = ISNULL(MAX(c), 0)
FROM (
    SELECT full_url, COUNT_BIG(*) AS c
    FROM dbo.urls
    GROUP BY full_url
    HAVING COUNT_BIG(*) > 1
) g;

/* --------------------------------------------------------------------------------------------
   4. Referencing tables — drives the REPOINT cost. Every FK that points at dbo.urls, plus the
      legacy non-FK event_meta_sharepoint.url_id. Row counts here are the upper bound on what
      the migration has to scan; only rows pointing at a surplus url are actually updated.
   -------------------------------------------------------------------------------------------- */
SELECT
    referencing_table = OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id),
    referencing_column = c.name,
    delete_action     = fk.delete_referential_action_desc,
    approx_rows       = (SELECT SUM(p.rows) FROM sys.partitions p
                         WHERE p.object_id = fk.parent_object_id AND p.index_id IN (0,1))
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.urls')
UNION ALL
SELECT
    'dbo.event_meta_sharepoint (legacy, NO FK)', 'url_id', 'NO_ACTION - rows would be ORPHANED',
    (SELECT SUM(p.rows) FROM sys.partitions p
     WHERE p.object_id = OBJECT_ID(N'dbo.event_meta_sharepoint') AND p.index_id IN (0,1))
WHERE OBJECT_ID(N'dbo.event_meta_sharepoint') IS NOT NULL
ORDER BY approx_rows DESC;

/* --------------------------------------------------------------------------------------------
   5. COLLISIONS — rows the migration will DELETE from child tables because repointing them onto
      the canonical url would violate a UNIQUE index that includes url_id.
      These are the two stock ones. Both queries only touch rows in duplicate groups.
   -------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.file_metadata_property_values') IS NOT NULL
BEGIN
    ;WITH canon AS (
        SELECT id, MIN(id) OVER (PARTITION BY full_url) AS canonical_id
        FROM dbo.urls
    ), dup_groups AS (
        SELECT DISTINCT canonical_id FROM canon WHERE id <> canonical_id
    ), mapped AS (
        -- Every row landing in a group that HAS duplicates, INCLUDING rows already pointing at the
        -- canonical url. Those must be counted: the migration ranks the whole post-remap partition
        -- together (see the ranked CTE in the migration's Up_Sql), so a canonical-side row and a
        -- repointed row with the same field_id collide and one of them is deleted. Counting only the
        -- repointed side - as this query used to - reports 0 pruned rows for the commonest collision
        -- there is: one property row on the canonical url and one on its duplicate.
        SELECT cn.canonical_id, f.field_id,
               CASE WHEN cn.id = cn.canonical_id THEN 0 ELSE 1 END AS is_repointed
        FROM dbo.file_metadata_property_values f
        JOIN canon cn ON cn.id = f.url_id
        JOIN dup_groups g ON g.canonical_id = cn.canonical_id
    )
    SELECT
        child_table       = 'dbo.file_metadata_property_values',
        rows_to_repoint   = (SELECT COUNT_BIG(*) FROM mapped WHERE is_repointed = 1),
        -- total - distinct post-remap keys = rows the unique index cannot hold, i.e. rows deleted.
        -- ISNULL because a UNIQUE index treats NULLs as equal but COUNT(DISTINCT) discards them.
        rows_pruned       = (SELECT COUNT_BIG(*)
                                    - COUNT(DISTINCT CAST(canonical_id AS varchar(20))
                                            + ':' + ISNULL(CAST(field_id AS varchar(20)), '<null>'))
                             FROM mapped);
END

IF OBJECT_ID(N'dbo.hits_clicked_elements') IS NOT NULL
BEGIN
    ;WITH canon AS (
        SELECT id, MIN(id) OVER (PARTITION BY full_url) AS canonical_id
        FROM dbo.urls
    ), dup_groups AS (
        SELECT DISTINCT canonical_id FROM canon WHERE id <> canonical_id
    ), mapped AS (
        -- See the note above: canonical-side rows take part in the collision and must be counted.
        SELECT cn.canonical_id, h.hit_id, h.[timestamp],
               CASE WHEN cn.id = cn.canonical_id THEN 0 ELSE 1 END AS is_repointed
        FROM dbo.hits_clicked_elements h
        JOIN canon cn ON cn.id = h.url_id
        JOIN dup_groups g ON g.canonical_id = cn.canonical_id
    )
    SELECT
        child_table       = 'dbo.hits_clicked_elements',
        rows_to_repoint   = (SELECT COUNT_BIG(*) FROM mapped WHERE is_repointed = 1),
        rows_pruned       = (SELECT COUNT_BIG(*)
                                    - COUNT(DISTINCT CAST(canonical_id AS varchar(20))
                                            + ':' + ISNULL(CAST(hit_id AS varchar(20)), '<null>')
                                            + ':' + ISNULL(CONVERT(varchar(30), [timestamp], 126), '<null>'))
                             FROM mapped);
END

/* --------------------------------------------------------------------------------------------
   6. Any OTHER unique index containing url_id that the migration will prune from.
      The migration discovers these dynamically, so a customer-specific index shows up here.
   -------------------------------------------------------------------------------------------- */
SELECT
    table_name  = OBJECT_SCHEMA_NAME(i.object_id) + '.' + OBJECT_NAME(i.object_id),
    index_name  = i.name,
    is_filtered = i.has_filter,
    is_disabled = i.is_disabled
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_unique = 1 AND c.name = N'url_id' AND ic.is_included_column = 0
ORDER BY table_name, index_name;
