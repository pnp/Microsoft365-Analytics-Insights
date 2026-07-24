using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the audit-importer transient-SQL retry/isolation helper. Pure (no DB): SqlException has
    /// no public constructor, so the real-world paths are exercised via the wrapped message (the merge path
    /// wraps the SqlException text into a BatchSaveException) and exception types (TimeoutException).
    /// </summary>
    [TestClass]
    public class TransientSqlRetryTests
    {
        private static readonly TimeSpan TinyDelay = TimeSpan.FromMilliseconds(1);

        private const string BrokenConnectionMsg =
            "Couldn't merge batch insert using given SQL: The connection is broken and recovery is not possible. " +
            "The connection is marked by the server as unrecoverable. No attempt was made to restore the connection.";

        private const string PkViolationMsg =
            "Couldn't merge batch insert using given SQL: Violation of PRIMARY KEY constraint 'PK_dbo.audit_events'. " +
            "Cannot insert duplicate key in object 'dbo.audit_events'. The duplicate key value is (00000000-0000-0000-0000-000000000000).";

        [TestMethod]
        public void IsTransient_BrokenConnection_True()
        {
            Assert.IsTrue(TransientSqlRetry.IsTransient(new BatchSaveException(BrokenConnectionMsg)));
        }

        [TestMethod]
        public void IsTransient_PrimaryKeyViolation_False()
        {
            // A constraint violation is deterministic - retrying just fails again, so it must isolate, not retry.
            Assert.IsFalse(TransientSqlRetry.IsTransient(new BatchSaveException(PkViolationMsg)));
        }

        [TestMethod]
        public void IsTransient_TimeoutException_True()
        {
            Assert.IsTrue(TransientSqlRetry.IsTransient(new TimeoutException("Timeout expired.")));
        }

        [TestMethod]
        public void IsTransient_UnrelatedException_False()
        {
            Assert.IsFalse(TransientSqlRetry.IsTransient(new InvalidOperationException("Something else went wrong")));
        }

        [TestMethod]
        public void IsTransient_InnerBrokenConnection_True()
        {
            var ex = new Exception("outer wrapper", new Exception("the connection is broken and recovery is not possible"));
            Assert.IsTrue(TransientSqlRetry.IsTransient(ex));
        }

        [TestMethod]
        public void IsTransient_AggregateWithTransient_True()
        {
            var ex = new AggregateException(new InvalidOperationException("noise"), new TimeoutException("timeout"));
            Assert.IsTrue(TransientSqlRetry.IsTransient(ex));
        }

        [TestMethod]
        public async Task ExecuteWithRetry_TransientThenSucceeds_RetriesAndReturns()
        {
            int calls = 0;
            var result = await TransientSqlRetry.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                if (calls == 1) throw new BatchSaveException(BrokenConnectionMsg);
                return Task.FromResult(42);
            }, maxAttempts: 4, logger: null, operationName: "test", baseDelay: TinyDelay);

            Assert.AreEqual(42, result);
            Assert.AreEqual(2, calls, "Should have retried once after the transient failure");
        }

        [TestMethod]
        public async Task ExecuteWithRetry_NonTransient_DoesNotRetry()
        {
            int calls = 0;
            await Assert.ThrowsExceptionAsync<BatchSaveException>(async () =>
            {
                await TransientSqlRetry.ExecuteWithRetryAsync<int>(() =>
                {
                    calls++;
                    throw new BatchSaveException(PkViolationMsg);
                }, maxAttempts: 4, logger: null, operationName: "test", baseDelay: TinyDelay);
            });

            Assert.AreEqual(1, calls, "A non-transient (constraint) failure must not be retried");
        }

        [TestMethod]
        public async Task ExecuteWithRetry_PersistentTransient_GivesUpAfterMaxAttempts()
        {
            int calls = 0;
            await Assert.ThrowsExceptionAsync<BatchSaveException>(async () =>
            {
                await TransientSqlRetry.ExecuteWithRetryAsync<int>(() =>
                {
                    calls++;
                    throw new BatchSaveException(BrokenConnectionMsg);
                }, maxAttempts: 3, logger: null, operationName: "test", baseDelay: TinyDelay);
            });

            Assert.AreEqual(3, calls, "Should attempt exactly maxAttempts times then give up");
        }
    }
}
