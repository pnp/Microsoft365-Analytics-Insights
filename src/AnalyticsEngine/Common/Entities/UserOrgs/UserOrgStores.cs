using System;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Builds the SQL Server adapters for the user-org ports.
    /// </summary>
    /// <remarks>
    /// The adapters themselves are internal: callers - the importer, the web API, the tests - depend on
    /// <see cref="IUserOrgTypeStore"/>, <see cref="IUserOrgAssignmentStore"/> and
    /// <see cref="IUserOrgImportJobStore"/>, never on the SQL classes. That is what lets the import and
    /// upload logic be tested against fakes with no database, and it keeps the raw SQL in one place.
    /// </remarks>
    public static class UserOrgStores
    {
        public static IUserOrgTypeStore CreateTypeStore(string connectionString)
        {
            return new SqlUserOrgTypeStore(Require(connectionString));
        }

        public static IUserOrgAssignmentStore CreateAssignmentStore(string connectionString)
        {
            return new SqlUserOrgAssignmentStore(Require(connectionString));
        }

        public static IUserOrgImportJobStore CreateImportJobStore(string connectionString)
        {
            return new SqlUserOrgImportJobStore(Require(connectionString));
        }

        private static string Require(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "A connection string to the Analytics database is required.", nameof(connectionString));
            }

            return connectionString;
        }
    }
}
