# Copilot Extended Data Storage Implementation

## Overview
This implementation extends the Copilot audit event storage to include comprehensive tracking of Messages, Agent Actions, AI Tool Usages, and Flow Actions. This allows for full reconstruction of `CopilotCreditEstimation` data from SQL without relying solely on JSON parsing.

## Problem Statement
Previously, `CopilotCreditEstimation` analyzed JSON data to calculate credit usage, but we weren't storing all the component data in structured SQL tables. We had:
- ? `CopilotAccessedResource` - stored over lookup tables
- ? Messages - not stored
- ? AgentActions - not stored  
- ? AIToolUsages - not stored
- ? FlowActions - not stored

This meant we couldn't reconstruct credit estimates from SQL data alone.

## Solution Architecture

### 1. New Entity Tables (CopilotEvents.cs)

#### Message Tracking
- **`CopilotMessage`** - Individual messages in conversations
  - Links to `CopilotChat` via `copilot_chat_id`
  - Contains `message_id`, `is_prompt`, and `message_type_id`
- **`CopilotMessageType`** - Lookup table for message types
  - Classic (1 credit)
  - Generative (2 credits)
  - TenantGraph (10 credits)

#### Agent Action Tracking
- **`CopilotAgentAction`** - Agent actions like triggers, deep reasoning
  - Links to `CopilotChat` via `copilot_chat_id`
  - Contains `action_id` and `action_type_id`
  - Each action = 5 credits
- **`CopilotAgentActionType`** - Lookup table for action types
  - Trigger, DeepReasoning, TopicTransition, KnowledgeSearch, etc.

#### AI Tool Usage Tracking
- **`CopilotAIToolUsage`** - AI tool usage with tiered billing
  - Links to `CopilotChat` via `copilot_chat_id`
  - Contains `tool_id`, `tier_id`, and `response_count`
- **`CopilotAIToolTier`** - Lookup table for tool tiers
  - Basic (1 credit per 10 responses)
  - Standard (15 credits per 10 responses)
  - Premium (100 credits per 10 responses)

#### Flow Action Tracking
- **`CopilotFlowAction`** - Agent flow actions (predefined sequences)
  - Links to `CopilotChat` via `copilot_chat_id`
  - Contains `action_count`
  - Billed at 13 credits per 100 actions

### 2. Staging Table Updates (StagingClasses.cs)

Added JSON columns to `BaseCopilotLogTempEntity`:
- `messages_json` - Serialized message array
- `agent_actions_json` - Serialized agent action array
- `ai_tool_usages_json` - Serialized AI tool usage array
- `flow_actions_json` - Serialized flow action object

These staging columns temporarily hold JSON data before being parsed and inserted into the normalized tables.

### 3. Data Serialization (CopilotAuditEventManager.cs)

Added serialization methods that convert `CopilotCreditEstimation` data back to JSON:

#### `SerializeMessages()`
Reconstructs message array from credit counts:
```json
[
  { "Type": "Classic", "IsPrompt": false },
  { "Type": "Generative", "IsPrompt": false },
  { "Type": "TenantGraph", "IsPrompt": false }
]
```

#### `SerializeAgentActions()`
Creates agent action array based on count:
```json
[
  { "Type": "Action" },
  { "Type": "Action" }
]
```

#### `SerializeAIToolUsages()`
Builds tool usage array with tiers:
```json
[
  { "Tier": "Basic", "ResponseCount": 10 },
  { "Tier": "Standard", "ResponseCount": 5 }
]
```

#### `SerializeFlowActions()`
Serializes flow action count:
```json
{ "ActionCount": 150 }
```

### 4. SQL Merge Logic (common_upsert_copilot_agents.sql)

Extended the SQL merge script to process the new JSON columns using `OPENJSON`:

#### Message Processing
1. Extract unique message types ? insert into `copilot_message_types`
2. Parse message JSON ? insert into `event_copilot_messages`
3. Links messages to chat via `copilot_chat_id`

#### Agent Action Processing
1. Extract unique action types ? insert into `copilot_agent_action_types`
2. Parse action JSON ? insert into `event_copilot_agent_actions`

#### AI Tool Usage Processing
1. Extract unique tier names ? insert into `copilot_ai_tool_tiers`
2. Parse tool usage JSON ? insert into `event_copilot_ai_tool_usages`

#### Flow Action Processing
1. Parse flow action JSON ? insert into `event_copilot_flow_actions`

All processing uses conditional checks (`IF OBJECT_ID(...) IS NOT NULL`) to ensure backward compatibility with databases that haven't run the migration yet.

### 5. Database Context Updates (AnalyticsEntitiesContext.cs)

Added DbSet properties for all new entity types:
```csharp
public DbSet<CopilotMessage> CopilotMessages { get; set; }
public DbSet<CopilotMessageType> CopilotMessageTypes { get; set; }
public DbSet<CopilotAgentAction> CopilotAgentActions { get; set; }
public DbSet<CopilotAgentActionType> CopilotAgentActionTypes { get; set; }
public DbSet<CopilotAIToolUsage> CopilotAIToolUsages { get; set; }
public DbSet<CopilotAIToolTier> CopilotAIToolTiers { get; set; }
public DbSet<CopilotFlowAction> CopilotFlowActions { get; set; }
```

## Data Flow

```
1. Copilot Audit Event (JSON)
   ?
2. CopilotCreditEstimation.Analyze() - Parse JSON, calculate credits
   ?
3. CopilotAuditEventManager serialization methods - Convert back to JSON
   ?
4. Staging tables (temp tables with JSON columns)
   ?
5. SQL OPENJSON parsing - Extract to normalized tables
   ?
6. Entity tables - Structured, queryable data
```

## Benefits

1. **Full SQL Reconstruction** - Can rebuild `CopilotCreditEstimation` from SQL without JSON parsing
2. **Queryable Data** - Can query messages, actions, tool usage directly via SQL/LINQ
3. **Analytics** - Enable rich analytics on Copilot usage patterns:
   - Message type distribution
   - Agent action frequency
   - AI tool tier usage
   - Flow action efficiency
4. **Backward Compatible** - SQL script uses conditional checks for tables
5. **Credit Audit Trail** - Complete breakdown of what contributed to credit consumption

## Migration Required

To activate this feature in an existing database, create and run an Entity Framework migration:

```powershell
Add-Migration -Name "CopilotExtendedData" -ProjectName "Entities" -StartUpProjectName "WebJob.Office365ActivityImporter"
Update-Database -ProjectName "Entities" -StartUpProjectName "WebJob.Office365ActivityImporter"
```

This will create the following tables:
- `event_copilot_messages`
- `copilot_message_types`
- `event_copilot_agent_actions`
- `copilot_agent_action_types`
- `event_copilot_ai_tool_usages`
- `copilot_ai_tool_tiers`
- `event_copilot_flow_actions`

## Example Queries

### Get all messages for a specific chat:
```sql
SELECT 
    m.message_id,
    m.is_prompt,
    mt.name AS message_type
FROM event_copilot_messages m
JOIN copilot_message_types mt ON m.message_type_id = mt.id
WHERE m.copilot_chat_id = 'YOUR-CHAT-GUID'
```

### Get AI tool usage summary:
```sql
SELECT 
    tier.name AS tier,
    SUM(usage.response_count) AS total_responses
FROM event_copilot_ai_tool_usages usage
JOIN copilot_ai_tool_tiers tier ON usage.tier_id = tier.id
GROUP BY tier.name
```

### Get agent action distribution:
```sql
SELECT 
    at.name AS action_type,
    COUNT(*) AS action_count
FROM event_copilot_agent_actions aa
JOIN copilot_agent_action_types at ON aa.action_type_id = at.id
GROUP BY at.name
ORDER BY action_count DESC
```

### Reconstruct credit usage from SQL:
```sql
SELECT 
    cc.event_id,
    -- Message credits
    SUM(CASE 
        WHEN mt.name = 'Classic' THEN 1
        WHEN mt.name = 'Generative' THEN 2
        WHEN mt.name = 'TenantGraph' THEN 10
        ELSE 0
    END) AS message_credits,
    -- Agent action credits (5 per action)
    (SELECT COUNT(*) * 5 FROM event_copilot_agent_actions WHERE copilot_chat_id = cc.event_id) AS agent_action_credits,
    -- AI Tool credits
    (SELECT 
        SUM(CASE 
            WHEN tier.name = 'Basic' THEN CEILING(usage.response_count / 10.0) * 1
            WHEN tier.name = 'Standard' THEN CEILING(usage.response_count / 10.0) * 15
            WHEN tier.name = 'Premium' THEN CEILING(usage.response_count / 10.0) * 100
            ELSE 0
        END)
     FROM event_copilot_ai_tool_usages usage
     JOIN copilot_ai_tool_tiers tier ON usage.tier_id = tier.id
     WHERE usage.copilot_chat_id = cc.event_id) AS ai_tool_credits,
    -- Flow action credits (13 per 100 actions)
    (SELECT SUM(CEILING(action_count / 100.0) * 13) 
     FROM event_copilot_flow_actions 
     WHERE copilot_chat_id = cc.event_id) AS flow_action_credits
FROM event_copilot_chats cc
LEFT JOIN event_copilot_messages m ON m.copilot_chat_id = cc.event_id
LEFT JOIN copilot_message_types mt ON m.message_type_id = mt.id
GROUP BY cc.event_id
```

## Unit Tests

Comprehensive unit tests are provided in `Tests.UnitTests/CopilotExtendedDataTests.cs`:

### Serialization Tests
- `SerializeMessages_WithClassicAnswers_ReturnsCorrectJson` - Validates Classic message serialization
- `SerializeMessages_WithMixedAnswers_ReturnsCorrectJson` - Tests multiple message types
- `SerializeMessages_WithNullCost_ReturnsNull` - Null handling
- `SerializeAgentActions_WithActions_ReturnsCorrectJson` - Agent action serialization
- `SerializeAIToolUsages_WithMultipleTiers_ReturnsCorrectJson` - Multi-tier tool usage
- `SerializeFlowActions_WithActions_ReturnsCorrectJson` - Flow action serialization

### Database Integration Tests
- `SaveCopilotEvent_WithMessages_SavesCorrectly` - End-to-end message storage
- `SaveCopilotEvent_WithAgentActions_SavesCorrectly` - Agent action storage
- `SaveCopilotEvent_WithAIToolUsages_SavesCorrectly` - AI tool usage storage
- `SaveCopilotEvent_WithFlowActions_SavesCorrectly` - Flow action storage
- `SaveCopilotEvent_WithAllDataTypes_SavesCorrectly` - Comprehensive test of all data types

### Running Tests
```powershell
# Run all Copilot extended data tests
dotnet test --filter FullyQualifiedName~CopilotExtendedDataTests

# Run specific test
dotnet test --filter FullyQualifiedName~SaveCopilotEvent_WithAllDataTypes_SavesCorrectly
```

## Files Modified

1. **Common/Entities/Entities/AuditLog/CopilotEvents.cs** - New entity classes
2. **WebJob.Office365ActivityImporter.Engine/ActivityAPI/Copilot/StagingClasses.cs** - New JSON columns
3. **WebJob.Office365ActivityImporter.Engine/ActivityAPI/Copilot/CopilotAuditEventManager.cs** - Serialization methods
4. **WebJob.Office365ActivityImporter.Engine/ActivityAPI/Copilot/SQL/common_upsert_copilot_agents.sql** - Extended merge logic
5. **Common/Entities/AnalyticsEntitiesContext.cs** - New DbSet properties
6. **Tests.UnitTests/CopilotExtendedDataTests.cs** - Unit tests

## Testing Recommendations

1. ? **Serialization Tests** - Verify JSON is correctly generated from `CopilotCreditEstimation`
2. ? **Database Tests** - Verify data is correctly saved to SQL via staging tables
3. ? **Null Handling** - Verify null/empty data doesn't cause errors
4. ? **Multiple Events** - Verify multiple events can be saved in same batch
5. **Migration Test** - Verify backward compatibility with pre-migration databases
6. **Performance Test** - Test with large batches (1000+ events)
7. **Credit Reconstruction** - Verify SQL queries correctly reconstruct credit estimates
8. **Lookup Table Deduplication** - Verify lookup tables don't accumulate duplicates

## Known Limitations

1. **Message Details** - We reconstruct messages from credit counts, not original message IDs from the API. If Microsoft adds Message IDs to the audit log in future, we should capture and store them.
2. **Action Type Granularity** - Currently agent actions are stored generically as "Action". If more detail becomes available in the API, update the serialization logic.
3. **Tool IDs** - AI tool usages default to "Unknown" tool ID. If specific tool IDs become available, update serialization.
4. **Aggregated Data** - Data is derived from aggregated credit counts, not individual API objects. This is by design for current API schema.

## Future Enhancements

1. **Direct API Object Storage** - If Microsoft enhances the API to include detailed Messages/AgentActions/AIToolUsages objects, update to store those directly instead of reconstructing from credit counts.
2. **Historical Analysis** - Add indexed views or materialized queries for time-series analysis of credit consumption patterns.
3. **Alerting** - Add triggers or scheduled queries to alert on unusual credit consumption patterns.
4. **Cost Optimization** - Analyze stored data to identify opportunities for reducing Copilot credit usage.
