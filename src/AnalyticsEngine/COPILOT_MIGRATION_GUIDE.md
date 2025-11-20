# Copilot Extended Data Migration Guide

## Migration Details

**Migration Name**: `CopilotAccessedResources`  
**Timestamp**: `202512191200000`  
**File**: `Common\Entities\Migrations\202512191200000_CopilotAccessedResources.cs`

## What This Migration Creates

This migration creates all necessary tables for storing Copilot extended data, including:

### 1. Accessed Resources Tables (Original)
- `copilot_accessed_resource_ids` - Resource IDs
- `copilot_accessed_resource_names` - Resource names
- `copilot_accessed_resource_types` - Resource types
- `sensitivity_labels` - Sensitivity label IDs
- `event_copilot_accessed_resources` - Junction table

### 2. Messages Tables (New)
- `copilot_message_types` - Message type lookup (Classic, Generative, TenantGraph)
- `event_copilot_messages` - Individual messages in Copilot chats

### 3. Agent Actions Tables (New)
- `copilot_agent_action_types` - Action type lookup
- `event_copilot_agent_actions` - Agent actions performed

### 4. AI Tool Usages Tables (New)
- `copilot_ai_tool_tiers` - Tool tier lookup (Basic, Standard, Premium)
- `event_copilot_ai_tool_usages` - AI tool usage records

### 5. Flow Actions Table (New)
- `event_copilot_flow_actions` - Flow action counts

## Table Structure Details

### Messages Schema
```sql
CREATE TABLE copilot_message_types (
    id INT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(100) NOT NULL UNIQUE
);

CREATE TABLE event_copilot_messages (
    id INT IDENTITY(1,1) PRIMARY KEY,
    copilot_chat_id UNIQUEIDENTIFIER NOT NULL,
    message_id UNIQUEIDENTIFIER NOT NULL,
    is_prompt BIT NOT NULL,
    message_type_id INT NULL,
    CONSTRAINT FK_Messages_Chat FOREIGN KEY (copilot_chat_id) 
        REFERENCES event_copilot_chats(event_id) ON DELETE CASCADE,
    CONSTRAINT FK_Messages_Type FOREIGN KEY (message_type_id) 
        REFERENCES copilot_message_types(id)
);

CREATE INDEX IX_Messages_ChatId ON event_copilot_messages(copilot_chat_id);
CREATE INDEX IX_Messages_MessageId ON event_copilot_messages(message_id);
CREATE INDEX IX_Messages_TypeId ON event_copilot_messages(message_type_id);
```

### Agent Actions Schema
```sql
CREATE TABLE copilot_agent_action_types (
    id INT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(100) NOT NULL UNIQUE
);

CREATE TABLE event_copilot_agent_actions (
    id INT IDENTITY(1,1) PRIMARY KEY,
    copilot_chat_id UNIQUEIDENTIFIER NOT NULL,
    action_id UNIQUEIDENTIFIER NOT NULL,
    action_type_id INT NULL,
    CONSTRAINT FK_AgentActions_Chat FOREIGN KEY (copilot_chat_id) 
        REFERENCES event_copilot_chats(event_id) ON DELETE CASCADE,
    CONSTRAINT FK_AgentActions_Type FOREIGN KEY (action_type_id) 
        REFERENCES copilot_agent_action_types(id)
);

CREATE INDEX IX_AgentActions_ChatId ON event_copilot_agent_actions(copilot_chat_id);
CREATE INDEX IX_AgentActions_ActionId ON event_copilot_agent_actions(action_id);
CREATE INDEX IX_AgentActions_TypeId ON event_copilot_agent_actions(action_type_id);
```

### AI Tool Usages Schema
```sql
CREATE TABLE copilot_ai_tool_tiers (
    id INT IDENTITY(1,1) PRIMARY KEY,
    name NVARCHAR(50) NOT NULL UNIQUE
);

CREATE TABLE event_copilot_ai_tool_usages (
    id INT IDENTITY(1,1) PRIMARY KEY,
    copilot_chat_id UNIQUEIDENTIFIER NOT NULL,
    tool_id NVARCHAR(500) NOT NULL,
    tier_id INT NULL,
    response_count INT NOT NULL,
    CONSTRAINT FK_AIToolUsages_Chat FOREIGN KEY (copilot_chat_id) 
        REFERENCES event_copilot_chats(event_id) ON DELETE CASCADE,
    CONSTRAINT FK_AIToolUsages_Tier FOREIGN KEY (tier_id) 
        REFERENCES copilot_ai_tool_tiers(id)
);

CREATE INDEX IX_AIToolUsages_ChatId ON event_copilot_ai_tool_usages(copilot_chat_id);
CREATE INDEX IX_AIToolUsages_TierId ON event_copilot_ai_tool_usages(tier_id);
```

### Flow Actions Schema
```sql
CREATE TABLE event_copilot_flow_actions (
    id INT IDENTITY(1,1) PRIMARY KEY,
    copilot_chat_id UNIQUEIDENTIFIER NOT NULL,
    action_count INT NOT NULL,
    CONSTRAINT FK_FlowActions_Chat FOREIGN KEY (copilot_chat_id) 
        REFERENCES event_copilot_chats(event_id) ON DELETE CASCADE
);

CREATE INDEX IX_FlowActions_ChatId ON event_copilot_flow_actions(copilot_chat_id);
```

## Running the Migration

### Option 1: Visual Studio Package Manager Console
```powershell
# Set the startup project
Set-StartupProject WebJob.Office365ActivityImporter

# Run migration
Update-Database -ProjectName Entities -StartupProjectName WebJob.Office365ActivityImporter -Verbose
```

### Option 2: Command Line
```powershell
# From the solution directory
dotnet ef database update --project Common\Entities\Entities.csproj --startup-project WebJob.Office365ActivityImporter\WebJob.Office365ActivityImporter.csproj
```

### Option 3: Specify Target Migration
```powershell
# Update to specific migration
Update-Database -ProjectName Entities -TargetMigration CopilotAccessedResources
```

## Verification Steps

After running the migration, verify tables were created:

```sql
-- Check all tables exist
SELECT TABLE_NAME 
FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_NAME IN (
    'copilot_message_types',
    'event_copilot_messages',
    'copilot_agent_action_types',
    'event_copilot_agent_actions',
    'copilot_ai_tool_tiers',
    'event_copilot_ai_tool_usages',
    'event_copilot_flow_actions',
    'copilot_accessed_resource_ids',
    'copilot_accessed_resource_names',
    'copilot_accessed_resource_types',
    'sensitivity_labels',
    'event_copilot_accessed_resources'
)
ORDER BY TABLE_NAME;

-- Expected: 12 tables

-- Check foreign keys
SELECT 
    fk.name AS ForeignKey,
    OBJECT_NAME(fk.parent_object_id) AS TableName,
    COL_NAME(fc.parent_object_id, fc.parent_column_id) AS ColumnName,
    OBJECT_NAME (fk.referenced_object_id) AS ReferenceTableName
FROM sys.foreign_keys AS fk
INNER JOIN sys.foreign_key_columns AS fc 
    ON fk.object_id = fc.constraint_object_id
WHERE OBJECT_NAME(fk.parent_object_id) LIKE '%copilot%'
ORDER BY TableName, ForeignKey;

-- Check indexes
SELECT 
    i.name AS IndexName,
    t.name AS TableName,
    c.name AS ColumnName
FROM sys.indexes i
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
INNER JOIN sys.tables t ON i.object_id = t.object_id
WHERE t.name LIKE '%copilot%'
ORDER BY TableName, IndexName;
```

## Rollback Instructions

If you need to rollback this migration:

```powershell
# Rollback to previous migration
Update-Database -ProjectName Entities -TargetMigration [PreviousMigrationName]

# Or rollback completely
Update-Database -ProjectName Entities -TargetMigration 0
```

The `Down()` method will:
1. Drop Flow Actions table
2. Drop AI Tool Usages tables
3. Drop Agent Actions tables
4. Drop Messages tables
5. Drop Accessed Resources tables

**?? Warning**: Rollback will delete all data in these tables!

## Migration Dependencies

This migration requires:
- ? `event_copilot_chats` table (from previous migration)
- ? All referenced entity classes in `Common.Entities`
- ? Entity Framework 6.x

## Post-Migration Steps

After successful migration:

1. **Run Unit Tests**:
```powershell
dotnet test --filter FullyQualifiedName~CopilotExtendedDataTests
```

2. **Verify SQL Merge Scripts**:
- Check `common_upsert_copilot_agents.sql` references new tables
- Verify conditional `IF OBJECT_ID` checks are working

3. **Test Data Flow**:
- Import some Copilot audit events
- Verify data populates new tables
- Check foreign key relationships

4. **Monitor Performance**:
- Check index usage
- Verify query performance
- Monitor staging table processing time

## Common Migration Issues

### Issue 1: Migration Already Applied
```
Error: There is already an object named 'copilot_message_types' in the database.
```

**Solution**: Migration was already run. Check `__MigrationHistory` table:
```sql
SELECT * FROM __MigrationHistory 
WHERE MigrationId LIKE '%CopilotAccessedResources%';
```

### Issue 2: Foreign Key Constraint Violation
```
Error: FK constraint on event_copilot_chats
```

**Solution**: Ensure parent `event_copilot_chats` table exists and has data.

### Issue 3: Permission Denied
```
Error: Cannot create table 'dbo.copilot_message_types'
```

**Solution**: Verify SQL account has DDL permissions:
```sql
GRANT CREATE TABLE TO [YourUser];
GRANT ALTER ON SCHEMA::dbo TO [YourUser];
```

### Issue 4: Connection String Issues
```
Error: Cannot connect to database
```

**Solution**: Check connection string in `App.config` or `Web.config`.

## Migration Console Output

Successful migration will display:
```
DB SCHEMA: Applied 'Copilot Accessed Resources' tables successfully.
DB SCHEMA: Applied 'Copilot Messages' tables successfully.
DB SCHEMA: Applied 'Copilot Agent Actions' tables successfully.
DB SCHEMA: Applied 'Copilot AI Tool Usages' tables successfully.
DB SCHEMA: Applied 'Copilot Flow Actions' table successfully.
DB SCHEMA: All Copilot extended data tables created successfully.
```

## Database Size Impact

### Estimated Storage Requirements

Based on 100,000 Copilot chat events:

| Table | Est. Rows | Est. Size |
|-------|-----------|-----------|
| copilot_message_types | ~3 | < 1 KB |
| event_copilot_messages | ~400,000 | ~50 MB |
| copilot_agent_action_types | ~10 | < 1 KB |
| event_copilot_agent_actions | ~500,000 | ~60 MB |
| copilot_ai_tool_tiers | ~3 | < 1 KB |
| event_copilot_ai_tool_usages | ~300,000 | ~40 MB |
| event_copilot_flow_actions | ~50,000 | ~5 MB |
| **Total Extended Data** | | **~155 MB** |

**Note**: Actual size depends on:
- Average messages per chat (estimated 4)
- Agent action frequency
- AI tool usage patterns
- Flow action adoption

## Index Considerations

### Existing Indexes
The migration creates indexes on:
- All foreign keys (for join performance)
- `message_id` and `action_id` (for lookups)
- Unique constraints on lookup table names

### Additional Indexes to Consider

For high-volume environments, consider:

```sql
-- Index for date-range queries on messages
CREATE INDEX IX_Messages_ChatId_MessageId 
ON event_copilot_messages(copilot_chat_id, message_id);

-- Index for credit estimation queries
CREATE INDEX IX_AIToolUsages_ChatId_TierId_ResponseCount
ON event_copilot_ai_tool_usages(copilot_chat_id, tier_id, response_count);

-- Index for agent action analysis
CREATE INDEX IX_AgentActions_ChatId_TypeId
ON event_copilot_agent_actions(copilot_chat_id, action_type_id);
```

## Maintenance

### Cleanup Old Data
```sql
-- Delete messages older than 90 days
DELETE m
FROM event_copilot_messages m
INNER JOIN event_copilot_chats c ON m.copilot_chat_id = c.event_id
INNER JOIN audit_events_common a ON c.event_id = a.id
WHERE a.time_stamp < DATEADD(day, -90, GETUTCDATE());

-- Similar pattern for other tables (cascading deletes handle related records)
```

### Monitor Lookup Table Growth
```sql
-- Check lookup table sizes
SELECT 
    'Message Types' AS TableType, COUNT(*) AS RowCount FROM copilot_message_types
UNION ALL
SELECT 'Agent Action Types', COUNT(*) FROM copilot_agent_action_types
UNION ALL
SELECT 'AI Tool Tiers', COUNT(*) FROM copilot_ai_tool_tiers;

-- Should remain small (< 100 rows each)
```

## Related Documentation
- Implementation: `COPILOT_EXTENDED_DATA_STORAGE.md`
- Testing: `COPILOT_EXTENDED_DATA_TESTS.md`
- Refactoring: `REFACTORING_SUMMARY.md`
