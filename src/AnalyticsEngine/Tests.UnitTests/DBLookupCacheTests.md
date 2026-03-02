# DBLookupCache Unit Tests - Documentation

## Overview
Comprehensive unit test suite for the DBLookupCache improvements that prevent FK constraint violations and handle duplicate records gracefully.

## Test File
`Tests.UnitTests\DBLookupCacheTests.cs`

## Test Coverage

### 1. Normal Operation Tests
Tests that verify basic cache functionality works correctly.

#### `UserDepartmentCache_NormalOperation_CreatesNewDepartment`
- **Purpose**: Validates that new departments are created and saved correctly
- **Verifies**: 
  - Entity is created with correct name
  - Entity is saved to database with valid ID
  - Database record matches created entity

#### `UserDepartmentCache_SecondCall_ReturnsCachedDepartment`
- **Purpose**: Ensures caching mechanism works (no duplicate creation)
- **Verifies**:
  - Second call returns same cached instance
  - Only one database record exists
  - IDs match between cached instances

#### `OfficeLocationCache_NormalOperation_CreatesNewLocation`
- **Purpose**: Tests OfficeLocationCache basic functionality
- **Verifies**: Location creation and persistence

#### `UserJobTitleCache_NormalOperation_CreatesNewJobTitle`
- **Purpose**: Tests UserJobTitleCache basic functionality
- **Verifies**: Job title creation and persistence

#### `UsageLocationCache_NormalOperation_CreatesNewLocation`
- **Purpose**: Tests UsageLocationCache basic functionality
- **Verifies**: Usage location creation and persistence

### 2. Duplicate Handling Tests
Tests that verify the fix for the FK constraint violation issue.

#### `Load_WithExistingDuplicates_ReturnsFirstByID`
- **Purpose**: Tests the FirstOrDefaultAsync + OrderBy fix
- **Verifies**:
  - Load() handles existing duplicates without throwing "Sequence contains more than one element"
  - Returns record with lowest ID for consistency
  - Gracefully handles duplicate key exceptions during insertion

#### `ConcurrentAccess_SameName_HandlesGracefully`
- **Purpose**: Simulates concurrent access scenario
- **Verifies**:
  - Multiple caches can access same name without creating duplicates
  - Database maintains data integrity
  - No duplicate records created

### 3. Edge Case Tests

#### `Load_WithNonExistentName_ReturnsNull`
- **Purpose**: Validates null return for non-existent lookups
- **Verifies**: Load() returns null when name doesn't exist

#### `GetOrCreateNewResource_WithWhitespace_TrimsKey`
- **Purpose**: Tests whitespace handling (SQL comparison behavior)
- **Verifies**: Keys are trimmed before comparison/storage

#### `GetOrCreateNewResource_WithNullKey_ThrowsException`
- **Purpose**: Tests null key validation
- **Verifies**: ArgumentNullException thrown for null keys

#### `GetOrCreateNewResource_WithEmptyKey_ThrowsException`
- **Purpose**: Tests empty key validation
- **Verifies**: ArgumentNullException thrown for empty keys

#### `GetOrCreateNewResource_WithoutCommit_DoesNotSaveToDatabase`
- **Purpose**: Tests deferred commit functionality
- **Verifies**:
  - Entity created but not saved when commitChangeOnSaveNew=false
  - ID is 0 until explicit SaveChanges
  - Explicit save works correctly

### 4. Integration Tests

#### `MultipleCache_WithSameContext_SharesLookups`
- **Purpose**: Tests multiple cache instances sharing same context
- **Verifies**:
  - Different lookup types can coexist
  - Context is shared correctly
  - Entities persist independently

#### `AllLookupCaches_CanCreateAndRetrieve`
- **Purpose**: Smoke test for all lookup cache implementations
- **Verifies**: All 7 lookup cache types work correctly:
  - UserDepartmentCache
  - UserJobTitleCache
  - OfficeLocationCache
  - UsageLocationCache
  - StateOrProvinceCache
  - CountryOrRegionCache
  - CompanyNameCache

#### `Load_OrdersByID_ReturnsConsistentResults`
- **Purpose**: Tests consistency of Load() method with OrderBy
- **Verifies**:
  - Multiple loads return same entity
  - OrderBy provides deterministic results

## Test Execution

### Running Tests
```bash
# Run all tests in the file
dotnet test --filter "FullyQualifiedName~DBLookupCacheTests"

# Run specific test
dotnet test --filter "FullyQualifiedName~DBLookupCacheTests.UserDepartmentCache_NormalOperation_CreatesNewDepartment"
```

### Test Database
Tests use `AnalyticsEntitiesContext` which connects to the configured test database. Ensure:
- Connection string is configured in test project
- Database schema is up-to-date
- Test database is accessible

## Key Improvements Validated

### 1. FirstOrDefaultAsync() Instead of SingleOrDefaultAsync()
- **Issue**: SingleOrDefaultAsync throws exception when multiple records exist
- **Fix**: FirstOrDefaultAsync + OrderBy(t => t.ID) handles duplicates gracefully
- **Tests**: `Load_WithExistingDuplicates_ReturnsFirstByID`, `Load_OrdersByID_ReturnsConsistentResults`

### 2. Duplicate Key Error Handling
- **Issue**: DbUpdateException for unique constraint violations crashed the process
- **Fix**: Try-catch with SqlException detection, detach failed entity, reload from DB
- **Tests**: `Load_WithExistingDuplicates_ReturnsFirstByID`, `ConcurrentAccess_SameName_HandlesGracefully`

### 3. Cache Consistency
- **Issue**: Detaching all entities invalidated lookup cache
- **Fix**: Selective detachment preserves lookups (tested in UserMetadataUpdater)
- **Tests**: `UserDepartmentCache_SecondCall_ReturnsCachedDepartment`, `MultipleCache_WithSameContext_SharesLookups`

## Expected Test Results

All tests should **PASS** with the implemented fixes:
- ? No "Sequence contains more than one element" exceptions
- ? No FK constraint violation errors
- ? Consistent behavior across multiple cache instances
- ? Proper null/empty key validation
- ? All lookup cache types functional

## Future Enhancements

Potential additional tests:
1. **Performance tests** - Measure cache performance with large datasets
2. **Stress tests** - High concurrency scenarios
3. **Transaction tests** - Rollback behavior
4. **Mock tests** - Test error handler without actual database duplicates

## Related Files

- Implementation: `Common\Entities\LookupCaches\Discrete\DBLookupCache.cs`
- Cache classes: `Common\Entities\LookupCaches\Discrete\*Cache.cs`
- Usage: `WebJob.Office365ActivityImporter.Engine\Graph\User\UserMetadataUpdater.cs`
