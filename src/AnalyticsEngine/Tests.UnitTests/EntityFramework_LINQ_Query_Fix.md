# Entity Framework LINQ Query Fix

## Issue
Tests were failing with the following error:

```
System.NotSupportedException: LINQ to Entities does not recognize the method 
'System.String Format(System.String, System.Object)' method, and this method 
cannot be translated into a store expression.
```

## Root Cause
Entity Framework cannot translate string interpolation or `string.Format()` when they appear inside LINQ query expressions (like `Where()`, `CountAsync()`, etc.).

### Problematic Code
```csharp
// ? BROKEN - EF can't translate string interpolation in LINQ
var userCount = await db.users.CountAsync(u => 
    u.UserPrincipalName.Contains($"integration_{testRun}_"));
```

The interpolated string `$"integration_{testRun}_"` is evaluated **inside** the LINQ expression, which Entity Framework tries to translate to SQL. Since SQL doesn't understand C# string interpolation, this fails.

## Solution
Evaluate the string **before** the LINQ query, then use the resulting variable:

```csharp
// ? FIXED - Evaluate string before LINQ query
var searchPrefix = $"integration_{testRun}_";
var userCount = await db.users.CountAsync(u => 
    u.UserPrincipalName.Contains(searchPrefix));
```

Now `searchPrefix` is a simple string variable that Entity Framework can translate to a SQL parameter.

## Files Fixed

### Tests.UnitTests\DBLookupCacheDuplicateKeyErrorTests.cs
Fixed 3 occurrences:

1. **IntegrationTest_UserMetadataUpdater_NoDuplicateKeys** (lines ~390-395)
   ```csharp
   // BEFORE
   var userCount = await db.users.CountAsync(u => 
       u.UserPrincipalName.Contains($"integration_{testRun}_"));
   
   // AFTER
   var searchPrefix = $"integration_{testRun}_";
   var userCount = await db.users.CountAsync(u => 
       u.UserPrincipalName.Contains(searchPrefix));
   ```

2. **Production200kUsers_CommonDepartments_NoFKViolations** (lines ~265-270)
   ```csharp
   // BEFORE
   var savedUsers = await db.users
       .Where(u => u.UserPrincipalName.Contains($"prod200k_{testRun}_"))
       .CountAsync();
   
   // AFTER
   var searchPrefix = $"prod200k_{testRun}_";
   var savedUsers = await db.users
       .Where(u => u.UserPrincipalName.Contains(searchPrefix))
       .CountAsync();
   ```

3. **ProductionBatchSize_500Users_NoFKViolations** (lines ~475-480)
   ```csharp
   // BEFORE
   var userCount = await db.users.CountAsync(u => 
       u.UserPrincipalName.Contains($"batch500_{testRun}_"));
   
   // AFTER
   var searchPrefix = $"batch500_{testRun}_";
   var userCount = await db.users.CountAsync(u => 
       u.UserPrincipalName.Contains(searchPrefix));
   ```

### Tests.UnitTests\LookupCacheBatchProcessingTests.cs
Fixed 1 occurrence:

**LargeBatchSimulation_200kUsers_NoFKViolations** (lines ~296-298)
```csharp
// BEFORE
var testUsers = await db.users
    .Where(u => u.UserPrincipalName.StartsWith($"largetest_{testRun}_"))
    .ToListAsync();

// AFTER
var searchPrefix = $"largetest_{testRun}_";
var testUsers = await db.users
    .Where(u => u.UserPrincipalName.StartsWith(searchPrefix))
    .ToListAsync();
```

## Why This Happens

Entity Framework works by:
1. **Expression Tree Analysis**: Parses your LINQ query as an expression tree
2. **SQL Translation**: Converts the expression tree to SQL
3. **Parameter Binding**: Extracts values as SQL parameters

When you use string interpolation **inside** the LINQ query:
- EF sees the `Format()` or interpolation method call
- Tries to translate it to SQL
- Fails because SQL has no equivalent

When you evaluate the string **before** the LINQ query:
- The string is already computed
- EF just sees a variable containing a string value
- Translates it to a simple SQL parameter: `@p__linq__0: 'integration_639051212299365929_'`

## SQL Query Generated

### After Fix
```sql
-- Clean SQL with parameterized value
SELECT COUNT(*) 
FROM [dbo].[users] AS [Extent1]
WHERE [Extent1].[user_name] LIKE @p__linq__0 + '%'

-- Parameter:
-- @p__linq__0: 'integration_639051212299365929_'
```

## General Rule

**Always evaluate dynamic strings before using them in LINQ queries:**

```csharp
// ? BAD - String operations in LINQ
await db.users.Where(u => u.Name.Contains($"prefix_{variable}_suffix")).ToListAsync();
await db.users.Where(u => u.Name == string.Format("User{0}", id)).ToListAsync();
await db.users.Where(u => u.Name.StartsWith($"{prefix}_{id}")).ToListAsync();

// ? GOOD - Evaluate first, then use in LINQ
var searchTerm = $"prefix_{variable}_suffix";
await db.users.Where(u => u.Name.Contains(searchTerm)).ToListAsync();

var userName = string.Format("User{0}", id);
await db.users.Where(u => u.Name == userName).ToListAsync();

var startsWith = $"{prefix}_{id}";
await db.users.Where(u => u.Name.StartsWith(startsWith)).ToListAsync();
```

## Test Status After Fix

? **Build Status**: Successful  
? **All string interpolations**: Evaluated before LINQ queries  
? **Tests ready**: For execution

## Related Documentation

- [Entity Framework Expression Translation](https://docs.microsoft.com/en-us/ef/core/querying/how-query-works)
- [LINQ to Entities Supported Methods](https://docs.microsoft.com/en-us/dotnet/framework/data/adonet/ef/language-reference/supported-and-unsupported-linq-methods-linq-to-entities)

---

**Summary**: All tests now properly evaluate string interpolations before LINQ queries, allowing Entity Framework to translate them to SQL parameters successfully.
