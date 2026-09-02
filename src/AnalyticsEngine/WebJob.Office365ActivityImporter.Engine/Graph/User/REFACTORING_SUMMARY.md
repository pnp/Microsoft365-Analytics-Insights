# User Metadata Updater Refactoring Summary

## Overview
The `UserMetadataUpdater` class has been refactored to improve code organization, maintainability, and testability by extracting specific responsibilities into dedicated helper classes.

## New Files Created

### 1. **UserBatchProcessor.cs**
**Purpose**: Handles all batch processing operations for user data.

**Key Responsibilities**:
- Process existing users in batches to reduce memory pressure
- Manage Entity Framework change tracker detachment to free memory
- Provide configurable batch sizes for different operations

**Key Methods**:
- `ProcessExistingUsersInBatches()` - Processes users in batches with save/detach cycles
- `DetachAllEntities()` - Detaches all entities from the change tracker
- `DetachEntities<T>()` - Detaches specific entity types

### 2. **UserLicenseProcessor.cs**
**Purpose**: Handles all license and SKU processing operations for users.

**Key Responsibilities**:
- Reconcile tenant-level SKUs against the stored licence lookups for all users
- Build the set of licence assignments a specific SKU implies
- Handle user-specific license processing when tenant-level SKUs are unavailable
- Resolve SKU part numbers to friendly license type names

**Key Methods**:
- `ProcessSKUsForAllUsers()` - Reconciles `user_license_type_lookups` against all tenant SKUs
- `AddSkuAssignments()` - Adds the assignments implied by one SKU to the desired-state set
- `ProcessUserLicenses()` - Processes licenses for individual users
- `GetLicenseType()` - Gets or creates license type from SKU part number

Reads and writes of `user_license_type_lookups` go through `IUserLicenseStore`
(`SqlUserLicenseStore` in production), and the add/remove plan is computed by the
database-free `UserLicenseAssignmentDelta`. See issue #392 - this step used to delete
every licence lookup and refill it SKU by SKU, leaving reports to see a tenant with no
licences for minutes at a time.

### 3. **UserDataMapper.cs**
**Purpose**: Handles mapping and updating of user data between Graph and database entities.

**Key Responsibilities**:
- Map basic user properties from Graph users to database users
- Update user metadata (department, job title, location, etc.)
- Handle manager relationships
- Match Graph users with database users by UPN

**Key Methods**:
- `UpdateDbUserFromGraphUser()` - Updates basic user properties
- `UpdateUserMetadata()` - Updates all user metadata including relationships
- `GetDbUsersFromGraphUsers()` - Finds database users matching Graph users by UPN

## Changes to UserMetadataUpdater.cs

### Removed Methods (Now in Helper Classes)
- `ProcessSKUsForAllUsers()` ? Moved to `UserLicenseProcessor`
- `AddSkuForUsers()` ? Moved to `UserLicenseProcessor`
- `GetLicenseType()` ? Moved to `UserLicenseProcessor`
- `GetDbUsersFromGraphUsers()` ? Moved to `UserDataMapper` (public wrapper kept for backward compatibility)
- User metadata update logic ? Moved to `UserDataMapper`

### New Private Fields
- `_batchProcessor` - Instance of `UserBatchProcessor`
- `_licenseProcessor` - Instance of `UserLicenseProcessor`
- `_dataMapper` - Instance of `UserDataMapper`

### Updated Methods
- `InsertAndUpdateDatabaseFromExternalUsers()` - Now uses helper classes for processing
- `InsertMissingUsers()` - Now uses `UserDataMapper` and `UserBatchProcessor`
- `UpdateDbUserWithGraphData()` - Simplified to use `UserDataMapper` and `UserLicenseProcessor`
- `UpdateDbUserFromGraphUser()` - Now delegates to `UserDataMapper` (kept for backward compatibility)

### Initialization
Helper classes are lazily initialized when needed:
- In `InitializeHelpers()` for `_batchProcessor`
- In `InsertAndUpdateDatabaseFromExternalUsers()` for main flow
- In `InsertMissingUsers()` for direct method calls from tests

## Benefits of Refactoring

### 1. **Improved Code Organization**
- Each class has a single, well-defined responsibility
- Related functionality is grouped together
- Easier to locate specific functionality

### 2. **Better Maintainability**
- Changes to batch processing logic only affect `UserBatchProcessor`
- License processing changes are isolated to `UserLicenseProcessor`
- Data mapping logic is centralized in `UserDataMapper`

### 3. **Enhanced Testability**
- Helper classes can be unit tested independently
- Easier to mock specific functionality
- Better separation of concerns

### 4. **Memory Management**
- Batch processing logic is centralized and consistent
- Entity detachment is handled uniformly
- Easier to identify and optimize memory-intensive operations

### 5. **Backward Compatibility**
- All public methods maintain their signatures
- Unit tests continue to work without modification
- Internal methods provide fallback implementations

## Testing

All existing unit tests pass without modification:
- `UserMetadataUpdater_Constructor_WithInjectedLoader_SetsLoaderCorrectly()`
- `UserMetadataUpdater_InsertMissingUsers_InsertsNewUsersOnly()`
- `UserMetadataUpdater_InsertMissingUsers_IgnoresUsersWithoutUPN()`
- `UserMetadataUpdater_InsertMissingUsers_CaseInsensitiveUPNComparison()`
- And all other tests in `UserImportTests.cs`

## Future Improvements

Potential areas for further enhancement:
1. Extract caching logic into a dedicated `UserCacheManager` class
2. Create an interface for `UserBatchProcessor` to enable different batching strategies
3. Add more granular telemetry in each helper class
4. Consider async/parallel processing where appropriate

## Namespace Convention

All files in the `Graph\User` folder use the namespace `WebJob.Office365ActivityImporter.Engine.Graph` (not `...Graph.User`). This follows the existing convention in the project.

## Ports and rules (issues #371 / #372)

A second pass split the remaining SQL and Graph coupling out of the helper classes above, so the
decisions they make can be asserted without a database. Adapters are behaviour-preserving
relocations, not rewrites.

### Pure rules — `Graph\User\Rules\`
| Class | Extracted from | What it owns |
|---|---|---|
| `UserMetadataMappingRules` | `UserDataMapper.UpdateUserMetadata` | Normalising the seven de-normalised lookup values, deciding which ones to clear, and the four direct fields |
| `UserBulkUpdateRules` | `UserBatchProcessor.BuildUpdateDataTable` | The bulk-update batch's shape, per-column values and the manager foreign-key precedence chain |
| `ManagerResolutionRules` | `UserDataMapper.UpdateUserManager` | Which manager UPNs a batch needs to look up, and how duplicate UPN rows are resolved |
| `UserImportCommitPolicy` | `UserMetadataUpdater.InsertAndUpdateDatabaseFromExternalUsers` | The delta token is committed only after every phase succeeded |

### Ports and adapters
| Port | Production adapter | Purpose |
|---|---|---|
| `IUserBulkUpdateWriter` | `SqlUserBulkUpdateWriter` | The `SqlConnection` + `SqlBulkCopy` + `#user_updates` temp table, moved out of `UserBatchProcessor` unchanged |
| `IUserLookupStore` | `SqlUserLookupStore` | Resolves a whole batch's users by UPN in one chunked `Contains(...)` query, replacing a per-user `FirstOrDefaultAsync` |
| `IAnalyticsDbContextFactory` | `DefaultAnalyticsDbContextFactory` | `UserMetadataUpdater` no longer news up its own `AnalyticsEntitiesContext` |

`ManagerPrefetchCache` holds one batch's managers between the store and `UserDataMapper`. Its scope is
deliberately a single batch: the entities are tracked by the import's context, and every batch ends by
detaching them.
