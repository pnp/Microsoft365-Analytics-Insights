# Graph user import: structure

The Graph user import keeps `dbo.users`, its lookups (department, job title, office and the rest), managers and licence assignments up to date from Microsoft Graph's `/users/delta`. This page maps where each part lives. The classes' own XML documentation describes what they do in detail.

All the files in this folder use the namespace `WebJob.Office365ActivityImporter.Engine.Graph`, not `...Graph.User`.

## Orchestration

| Class | Owns |
|---|---|
| `UserMetadataUpdater` | One import cycle: reading the delta, inserting new users, updating existing ones, refreshing licences, and committing the delta checkpoint |
| `GraphUserLoader` (`IUserMetadataLoader`) | Reading users and licences from Microsoft Graph |
| `UserInsertProcessor` | Inserting new users: a bulk insert, then metadata enrichment in batches |
| `UserBatchProcessor` | Processing existing users in batches, detaching tracked entities between batches to bound memory |
| `UserDataMapper` | Copying a Graph user's fields, lookups and manager onto their `users` row |
| `UserLicenseProcessor` | Reconciling licence assignments with the tenant's SKUs |
| `UserRenameProcessor` | Keeping a renamed user on the row they already have, and reporting the users the import can't match safely |
| `UserIdentityQuarantine.cs` | The users the import has quarantined, carried from cycle to cycle in the `AnalyticsState` table |
| `TargetedUserRefreshWorker` | The one-person refresh an administrator requests from the portal's User data lookup page |
| `UserMetadataCache`, `ManagerPrefetchCache` | Lookup rows, and one batch's managers, loaded in bulk |

## Rules: decisions without a database (`Rules\`)

These classes are pure: no SQL, no Graph, no storage. Test a decision here directly rather than through the import.

| Class | Decides |
|---|---|
| `UserDeltaReadRules` | How each entry of a `/users/delta` read is sorted: active, disabled or deleted. Shared with the test fake, so a test can't pass on input the real loader never produces |
| `UserIdentityRules` | Which `users` row stands for an Entra object id, and which rows to mark disabled. `DirectoryReadCompleteness`, in the same file, decides whether absence from a complete directory read is evidence of deletion |
| `UserMetadataMappingRules` | How a Graph user's lookup values are normalised, cleared or copied onto the row |
| `UserBulkUpdateRules` | Which rows go into the bulk update, each column's value, and the manager's foreign key |
| `ManagerResolutionRules` | Which managers' sign-in names a batch needs to look up |
| `UserOrgMappingRules` | The organisation-type assignments a batch of Graph users implies |
| `UserImportCommitPolicy` | That the delta checkpoint is committed only once every phase has succeeded |

Two more pure classes live outside `Rules\`: `UserLicenseAssignmentDelta` works out which licence assignments to add and remove, and `UserIdentityQuarantineRules` (in `UserIdentityQuarantine.cs`) carries quarantined users from one cycle to the next.

## Ports and adapters

| Port | Production adapter | Purpose |
|---|---|---|
| `IUserBulkUpdateWriter` | `SqlUserBulkUpdateWriter` | Applies a resolved batch of `users` updates: a temp table, `SqlBulkCopy` and one `UPDATE ... FROM JOIN` |
| `IUserAccountStatusWriter` | `SqlUserAccountStatusWriter` | Marks rows disabled when Graph reports their account disabled or deleted |
| `IUserLookupStore` | `SqlUserLookupStore` | Resolves a batch's users by sign-in name in bulk, rather than one query per user |
| `IUserLicenseStore` | `SqlUserLicenseStore` | Reads and writes `user_license_type_lookups` and the licence history |
| `IUserIdentityQuarantineStore` | `UserIdentityQuarantineStore` | Keeps quarantined users in the `AnalyticsState` table |
| `IAnalyticsDbContextFactory` (in `Common.Entities`) | `DefaultAnalyticsDbContextFactory` | Creates the `AnalyticsEntitiesContext` the import uses |

Tests substitute these ports to exercise the rules and the orchestration without SQL Server.
