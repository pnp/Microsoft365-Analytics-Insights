# Copilot AccessedResources Feature

## Overview
This update extends the Copilot event tracking system to capture and store `AccessedResources` data from Copilot audit events. Each property of an AccessedResource (Id, Name, Type, SensitivityLabelId) is stored in separate lookup tables to eliminate data duplication, following the existing normalization pattern in the database.

## What's Changed

### 1. New EF Entity Classes (`CopilotEvents.cs`)
- **`CopilotAccessedResourceId`** - Lookup table for resource IDs (up to 500 chars)
- **`CopilotAccessedResourceName`** - Lookup table for resource names (up to 100 chars)
- **`CopilotAccessedResourceType`** - Lookup table for resource types (up to 100 chars)
- **`CopilotSensitivityLabel`** - Lookup table for sensitivity label IDs (up to 100 chars)
- **`CopilotEventAccessedResource`** - Junction table linking Copilot chat events to their accessed resources

### 2. Staging Entity Extensions (`StagingClasses.cs`)
- Added `AccessedResourcesJson` field to `BaseCopilotLogTempEntity`
- This field stores the serialized JSON of AccessedResources for processing during SQL merge

### 3. CopilotAuditEventManager Updates
- Added `SerializeAccessedResources()` method to serialize the AccessedResources list to JSON
- Updated `AddChatOnly()`, `TryAddFileAsync()`, and `TryAddMeetingAsync()` to populate `AccessedResourcesJson`
- Added `using Newtonsoft.Json` for JSON serialization

### 4. SQL Processing (`common_upsert_copilot_agents.sql`)
- Added SQL logic to parse JSON and insert unique values into lookup tables:
  - `copilot_accessed_resource_ids`
  - `copilot_accessed_resource_names`
  - `copilot_accessed_resource_types`
  - `sensitivity_labels`
- Added SQL to create junction table records in `event_copilot_accessed_resources`
- Wrapped AccessedResource processing in `IF OBJECT_ID` check for graceful handling when tables don't exist yet

### 5. Database Migration
- Created migration file: `202512191200000_CopilotAccessedResources.cs`
- Migration creates all 5 new tables with appropriate primary keys, foreign keys, and indexes
- Added DbSet properties to `AnalyticsEntitiesContext.cs`

## Database Schema

### New Tables

#### copilot_accessed_resource_ids
| Column | Type | Description |
|--------|------|-------------|
| id | int (PK) | Auto-increment primary key |
| resource_id | nvarchar(500) | Resource ID from AccessedResource.Id |

#### copilot_accessed_resource_names
| Column | Type | Description |
|--------|------|-------------|
| id | int (PK) | Auto-increment primary key |
| name | nvarchar(100) | Resource name from AccessedResource.Name |

#### copilot_accessed_resource_types
| Column | Type | Description |
|--------|------|-------------|
| id | int (PK) | Auto-increment primary key |
| name | nvarchar(100) | Resource type from AccessedResource.Type |

#### sensitivity_labels
| Column | Type | Description |
|--------|------|-------------|
| id | int (PK) | Auto-increment primary key |
| label_id | nvarchar(100) | Sensitivity label ID from AccessedResource.SensitivityLabelId |

#### event_copilot_accessed_resources (Junction Table)
| Column | Type | Description |
|--------|------|-------------|
| id | int (PK) | Auto-increment primary key |
| copilot_chat_id | uniqueidentifier (FK) | References event_copilot_chats.event_id |
| resource_id_id | int (FK, nullable) | References copilot_accessed_resource_ids.id |
| resource_name_id | int (FK, nullable) | References copilot_accessed_resource_names.id |
| resource_type_id | int (FK, nullable) | References copilot_accessed_resource_types.id |
| sensitivity_label_id | int (FK, nullable) | References sensitivity_labels.id |

## Deployment Steps

### 1. Run Database Migration
The new tables need to be created in the database before the updated code can process AccessedResources.

**Option A: Automatic Migration (if AutomaticMigrationsEnabled)**
- The migration will run automatically when the application starts if the database is configured for automatic updates.

**Option B: Manual Migration**
Run the following command from Package Manager Console:
```powershell
Update-Database -ProjectName "Entities" -StartUpProjectName "WebJob.Office365ActivityImporter"
```

**Option C: Via Control Panel Application**
If using the installer/control panel application:
- Run the control panel with the database initialization switch
- The migration will be applied as part of the schema update process

### 2. Verify Migration
Check that the following tables exist in your database:
- `copilot_accessed_resource_ids`
- `copilot_accessed_resource_names`
- `copilot_accessed_resource_types`
- `sensitivity_labels`
- `event_copilot_accessed_resources`

### 3. Deploy Updated Application
Deploy the updated WebJob and any related applications.

### 4. Monitor Imports
After deployment, monitor the import logs to verify:
- AccessedResources are being serialized and stored
- SQL merge operations complete successfully
- No errors related to the new tables

## Querying AccessedResources

### Example: Get all accessed resources for a specific Copilot event
```sql
SELECT 
    c.event_id,
    c.app_host,
    rid.resource_id,
    rname.[name] AS resource_name,
    rtype.[name] AS resource_type,
    sl.label_id AS sensitivity_label
FROM event_copilot_chats c
LEFT JOIN event_copilot_accessed_resources ar ON c.event_id = ar.copilot_chat_id
LEFT JOIN copilot_accessed_resource_ids rid ON ar.resource_id_id = rid.id
LEFT JOIN copilot_accessed_resource_names rname ON ar.resource_name_id = rname.id
LEFT JOIN copilot_accessed_resource_types rtype ON ar.resource_type_id = rtype.id
LEFT JOIN sensitivity_labels sl ON ar.sensitivity_label_id = sl.id
WHERE c.event_id = 'YOUR-EVENT-GUID-HERE'
```

### Example: Count accessed resources by type
```sql
SELECT 
    rtype.[name] AS resource_type,
    COUNT(*) AS access_count
FROM event_copilot_accessed_resources ar
INNER JOIN copilot_accessed_resource_types rtype ON ar.resource_type_id = rtype.id
GROUP BY rtype.[name]
ORDER BY access_count DESC
```

### Example: Find events with sensitive content
```sql
SELECT 
    c.event_id,
    c.app_host,
    u.user_name,
    ae.time_stamp,
    sl.label_id AS sensitivity_label
FROM event_copilot_chats c
INNER JOIN event_copilot_accessed_resources ar ON c.event_id = ar.copilot_chat_id
INNER JOIN sensitivity_labels sl ON ar.sensitivity_label_id = sl.id
INNER JOIN audit_events ae ON c.event_id = ae.id
INNER JOIN users u ON ae.user_id = u.id
WHERE sl.label_id IS NOT NULL
ORDER BY ae.time_stamp DESC
```

## Testing

### Unit Tests
The existing unit tests should pass after the migration is run. If the `CopilotEventManagerSaveTest` fails with:
```
Invalid object name 'copilot_accessed_resource_ids'
```
This means the database migration hasn't been run yet. Run the migration first before running tests.

### Manual Testing
1. Trigger a Copilot event import
2. Query the new tables to verify data is being populated
3. Check that the `accessed_resources_json` column in staging tables contains valid JSON (during import)
4. Verify junction table records link correctly to chat events

## Backward Compatibility

The implementation is fully backward compatible:
- The SQL script checks if tables exist before attempting to insert AccessedResources data
- If the migration hasn't been run, Copilot events will still be processed normally, just without AccessedResources data
- Existing data and functionality remain unchanged

## Performance Considerations

- **Normalization**: Using lookup tables prevents data duplication and saves storage space
- **JSON Parsing**: OPENJSON is used efficiently with appropriate WHERE clauses
- **Indexes**: Foreign key indexes are automatically created to optimize joins
- **Bulk Processing**: AccessedResources are processed in batches along with other Copilot data

## Future Enhancements

Potential future improvements:
- Add indexes on commonly queried fields (e.g., `resource_id`, `label_id`)
- Create views for common AccessedResource queries
- Add analytics/reporting for accessed resource patterns
- Implement retention policies for AccessedResource data
