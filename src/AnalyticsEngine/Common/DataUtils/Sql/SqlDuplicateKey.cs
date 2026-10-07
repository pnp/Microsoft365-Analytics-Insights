using Microsoft.Data.SqlClient;
using System;

namespace DataUtils.Sql
{
    /// <summary>
    /// Recognises SQL Server's duplicate-key errors, so code that inserts rows another writer may have inserted a
    /// moment earlier can tell that race apart from every other failure.
    /// </summary>
    public static class SqlDuplicateKey
    {
        /// <summary>Cannot insert duplicate key row in object '...' with unique index '...'.</summary>
        public const int UniqueIndexViolation = 2601;

        /// <summary>Violation of PRIMARY KEY / UNIQUE KEY constraint '...'. Cannot insert duplicate key in object '...'.</summary>
        public const int UniqueConstraintViolation = 2627;

        /// <summary>
        /// True when <paramref name="ex"/>, or anything it wraps, is a <see cref="SqlException"/> carrying either
        /// duplicate-key error.
        /// </summary>
        /// <remarks>
        /// Walks the whole chain because each caller meets the error at a different depth: <c>SqlBulkCopy</c> throws
        /// the <see cref="SqlException"/> itself, EF6 wraps it twice (<c>DbUpdateException</c> then
        /// <c>UpdateException</c>), and a task can surface it inside an <see cref="AggregateException"/>. Every error
        /// in the exception is checked, not only <see cref="SqlException.Number"/>, which is just the first.
        /// </remarks>
        public static bool IsViolation(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        if (IsViolation(inner))
                        {
                            return true;
                        }
                    }
                }

                if (current is SqlException sqlException)
                {
                    foreach (SqlError error in sqlException.Errors)
                    {
                        if (error.Number == UniqueIndexViolation || error.Number == UniqueConstraintViolation)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
