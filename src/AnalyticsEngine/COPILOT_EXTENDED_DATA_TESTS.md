# Copilot Extended Data Storage - Unit Tests Summary

## Overview
Comprehensive unit tests have been added to validate the Copilot extended data storage functionality in `Tests.UnitTests/CopilotExtendedDataTests.cs`.

## Test Structure

### Test Class: `CopilotExtendedDataTests`
- **Location**: `Tests.UnitTests/CopilotExtendedDataTests.cs`
- **Framework**: MSTest (matches existing project test framework)
- **Test Count**: 13 comprehensive tests
- **Coverage**: Serialization logic, database integration, null handling, and comprehensive scenarios
- **Access**: Tests use `internal` visibility on serialization methods via `InternalsVisibleTo` attribute

## Test Categories

### 1. Serialization Tests (9 tests)
These tests verify that `CopilotCreditEstimation` data is correctly serialized back to JSON format for staging table storage. Tests call internal serialization methods directly without reflection.

#### Message Serialization Tests
- **`SerializeMessages_WithClassicAnswers_ReturnsCorrectJson`**
  - Validates serialization of Classic-only messages
  - Verifies JSON structure and IsPrompt flag
  
- **`SerializeMessages_WithMixedAnswers_ReturnsCorrectJson`**
  - Tests serialization with multiple message types (Classic, Generative, TenantGraph)
  - Validates correct count for each type
  
- **`SerializeMessages_WithNullCost_ReturnsNull`**
  - Tests null handling for cost estimation

#### Agent Action Serialization Tests
- **`SerializeAgentActions_WithActions_ReturnsCorrectJson`**
  - Validates agent action array generation
  - Tests action count accuracy
  
- **`SerializeAgentActions_WithZeroActions_ReturnsNull`**
  - Tests null return for zero actions

#### AI Tool Usage Serialization Tests
- **`SerializeAIToolUsages_WithMultipleTiers_ReturnsCorrectJson`**
  - Tests serialization with Basic, Standard, and Premium tiers
  - Validates ResponseCount for each tier
  
- **`SerializeAIToolUsages_WithOnlyBasicTier_ReturnsCorrectJson`**
  - Tests single-tier serialization

#### Flow Action Serialization Tests
- **`SerializeFlowActions_WithActions_ReturnsCorrectJson`**
  - Validates flow action count serialization
  
- **`SerializeFlowActions_WithZeroActions_ReturnsNull`**
  - Tests null return for zero actions

### 2. Database Integration Tests (5 tests)
These tests verify end-to-end data flow from serialization through SQL staging tables to final entity tables.

- **`SaveCopilotEvent_WithMessages_SavesCorrectly`**
  - Creates event with mixed message types
  - Verifies correct storage in `event_copilot_messages` and `copilot_message_types`
  - Validates message type distribution and IsPrompt flag
  
- **`SaveCopilotEvent_WithAgentActions_SavesCorrectly`**
  - Tests agent action storage
  - Verifies correct count in `event_copilot_agent_actions` and `copilot_agent_action_types`
  
- **`SaveCopilotEvent_WithAIToolUsages_SavesCorrectly`**
  - Tests multi-tier AI tool usage storage
  - Validates data in `event_copilot_ai_tool_usages` and `copilot_ai_tool_tiers`
  - Verifies ResponseCount for each tier
  
- **`SaveCopilotEvent_WithFlowActions_SavesCorrectly`**
  - Tests flow action storage
  - Validates data in `event_copilot_flow_actions`
  
- **`SaveCopilotEvent_WithAllDataTypes_SavesCorrectly`**
  - **Comprehensive test** covering all data types simultaneously
  - Validates:
    - 6 messages (2 Classic + 3 Generative + 1 TenantGraph)
    - 4 agent actions
    - 3 AI tool tiers (15 Basic + 25 Standard + 8 Premium = 48 total responses)
    - 175 flow actions
  - Demonstrates complete data reconstruction capability

### 3. Helper Methods

#### `ClearExtendedDataTables(AnalyticsEntitiesContext db)`
- Cleans up test data from all extended data tables
- Uses conditional checks to handle databases without migrations
- Called before each database integration test to ensure clean state

## Implementation Details

### InternalsVisibleTo Attribute
The `WebJob.Office365ActivityImporter.Engine` project exposes internal methods to the test project via:
```csharp
[assembly: InternalsVisibleTo("Tests.UnitTests")]
```
This is defined in `WebJob.Office365ActivityImporter.Engine/Properties/AssemblyInfo.cs`.

### Internal Serialization Methods
The following methods in `CopilotAuditEventManager` are marked as `internal` for testing:
- `SerializeMessages(CopilotCreditEstimation cost)`
- `SerializeAgentActions(CopilotCreditEstimation cost)`
- `SerializeAIToolUsages(CopilotCreditEstimation cost)`
- `SerializeFlowActions(CopilotCreditEstimation cost)`
- `SerializeAccessedResources(IEnumerable<AccessedResource> accessedResources)`

This approach:
? Avoids reflection overhead
? Provides compile-time type safety
? Maintains encapsulation (internal, not public)
? Enables better IDE support (IntelliSense, refactoring)

## Test Data Patterns

### Message Type Distribution
```csharp
ClassicAnswers = 1,           // 1 credit each
GenerativeAnswers = 2,        // 2 credits each
TenantGraphGroundedAnswers = 1 // 10 credits each
// Total: 15 credits from messages
```

### Agent Actions
```csharp
AgentActionCount = 4  // 5 credits each = 20 credits
```

### AI Tool Usages
```csharp
BasicAIToolResponses = 15,    // 1 credit per 10 = 2 credits
StandardAIToolResponses = 25, // 15 credits per 10 = 45 credits
PremiumAIToolResponses = 8    // 100 credits per 10 = 100 credits
// Total: 147 credits from AI tools
```

### Flow Actions
```csharp
FlowActions = 175  // 13 credits per 100 = 23 credits
```

**Total Credit Example**: 15 + 20 + 147 + 23 = **205 credits**

## Running the Tests

### Run All Extended Data Tests
```powershell
dotnet test --filter FullyQualifiedName~CopilotExtendedDataTests
```

### Run Individual Test Category
```powershell
# Serialization tests only
dotnet test --filter FullyQualifiedName~CopilotExtendedDataTests.Serialize

# Database tests only
dotnet test --filter FullyQualifiedName~CopilotExtendedDataTests.SaveCopilotEvent
```

### Run Specific Test
```powershell
dotnet test --filter FullyQualifiedName~SaveCopilotEvent_WithAllDataTypes_SavesCorrectly
```

### Visual Studio Test Explorer
1. Open Test Explorer (Test ? Test Explorer)
2. Navigate to `CopilotExtendedDataTests`
3. Right-click and select "Run" or "Debug"

## Test Prerequisites

### Database Migration
Most tests require the migration to be run first. Tests will skip gracefully with `Assert.Inconclusive` if tables don't exist:

```csharp
if (db.Database.SqlQuery<int>("SELECT OBJECT_ID('dbo.event_copilot_messages', 'U')").FirstOrDefault() == 0)
{
    Assert.Inconclusive("Extended data tables do not exist. Run migration first.");
    return;
}
```

### Configuration
Tests use `TestsAppConfig` for database connection strings and other settings from `App.config`.

## Test Coverage Matrix

| Feature | Serialization | DB Integration | Null Handling | Multi-Type |
|---------|--------------|----------------|---------------|------------|
| Messages | ? | ? | ? | ? |
| Agent Actions | ? | ? | ? | N/A |
| AI Tool Usages | ? | ? | N/A | ? |
| Flow Actions | ? | ? | ? | N/A |
| Combined | N/A | ? | N/A | ? |

## Expected Test Results

### Passing Tests
All 13 tests should pass after:
1. ? Entity Framework migration has been run
2. ? Database connection configured in App.config
3. ? Build completed successfully

### Skipped Tests
Tests will be marked as "Inconclusive" (skipped) if:
- Migration has not been run (missing tables)
- This is expected behavior for backward compatibility

## Debugging Failed Tests

### Common Issues

1. **"Tables do not exist"**
   - **Solution**: Run Entity Framework migration
   - **Command**: `Update-Database -ProjectName "Entities"`

2. **"Connection string not found"**
   - **Solution**: Check `App.config` in Tests.UnitTests project
   - **Verify**: `ConnectionStrings.DatabaseConnectionString` is configured

3. **"Data not saved correctly"**
   - **Debug Steps**:
     1. Check SQL Profiler for actual SQL executed
     2. Verify staging table JSON columns
     3. Check SQL merge script conditions
     4. Validate lookup table population

4. **"Cannot access internal methods"**
   - **Solution**: Verify `InternalsVisibleTo` attribute is present
   - **Check**: `WebJob.Office365ActivityImporter.Engine/Properties/AssemblyInfo.cs`
   - **Attribute**: `[assembly: InternalsVisibleTo("Tests.UnitTests")]`

## Integration with CI/CD

### Azure DevOps / GitHub Actions
```yaml
- task: DotNetCoreCLI@2
  displayName: 'Run Copilot Extended Data Tests'
  inputs:
    command: 'test'
    projects: '**/Tests.UnitTests.csproj'
    arguments: '--filter FullyQualifiedName~CopilotExtendedDataTests --logger trx'
```

### Pre-Deployment Testing
Run these tests before deploying to ensure:
1. ? Serialization logic is correct
2. ? SQL staging works properly
3. ? Entity relationships are valid
4. ? No regressions in existing Copilot event handling

## Test Maintenance

### Adding New Data Types
When adding new extended data types:
1. Add entity classes to `CopilotEvents.cs`
2. Add serialization method to `CopilotAuditEventManager.cs`
3. Add SQL processing to `common_upsert_copilot_agents.sql`
4. **Add corresponding tests to `CopilotExtendedDataTests.cs`**:
   - Serialization test
   - Database integration test
   - Null handling test
   - Update `SaveCopilotEvent_WithAllDataTypes_SavesCorrectly` test

### Test Naming Convention
Follow the existing pattern:
- Serialization: `Serialize{DataType}_{Condition}_{ExpectedResult}`
- Database: `SaveCopilotEvent_With{DataType}_SavesCorrectly`
- Example: `SerializeNewType_WithValidData_ReturnsCorrectJson`

## Performance Considerations

### Test Execution Time
- Serialization tests: ~50ms each (fast, in-memory)
- Database tests: ~200-500ms each (includes DB I/O)
- Total suite: ~3-5 seconds

### Database Load
- Each database test creates and deletes test data
- Tests use `ClearExtendedDataTables()` for cleanup
- No permanent test data left behind

## Validation Beyond Unit Tests

### Manual Verification Queries

After running tests, verify data in SQL:

```sql
-- Check message types were created
SELECT * FROM copilot_message_types

-- Check messages were saved
SELECT TOP 10 * FROM event_copilot_messages 
ORDER BY copilot_chat_id DESC

-- Check agent actions
SELECT TOP 10 * FROM event_copilot_agent_actions
ORDER BY copilot_chat_id DESC

-- Check AI tool usages
SELECT * FROM event_copilot_ai_tool_usages tvu
JOIN copilot_ai_tool_tiers t ON tvu.tier_id = t.id
ORDER BY tvu.copilot_chat_id DESC

-- Check flow actions
SELECT TOP 10 * FROM event_copilot_flow_actions
ORDER BY copilot_chat_id DESC
```

### Credit Reconstruction Validation

Use the SQL query from `COPILOT_EXTENDED_DATA_STORAGE.md` to verify total credits can be reconstructed from stored data.

## Success Criteria

Tests are considered successful when:
- ? All 13 tests pass
- ? No data inconsistencies in SQL
- ? Credit totals can be reconstructed accurately
- ? No memory leaks or connection issues
- ? Tests run in <10 seconds total
- ? Backward compatibility maintained (tests skip gracefully on old schema)

## Related Documentation
- Main implementation: `COPILOT_EXTENDED_DATA_STORAGE.md`
- Entity definitions: `Common/Entities/Entities/AuditLog/CopilotEvents.cs`
- SQL processing: `WebJob.Office365ActivityImporter.Engine/ActivityAPI/Copilot/SQL/common_upsert_copilot_agents.sql`
