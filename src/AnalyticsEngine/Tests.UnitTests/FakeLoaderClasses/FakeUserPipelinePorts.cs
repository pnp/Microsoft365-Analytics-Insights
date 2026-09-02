using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IUserBulkUpdateWriter"/>. Records a snapshot of every batch handed to it
    /// so the batching and foreign-key resolution above it can be asserted without a SQL Server
    /// (#371).
    /// </summary>
    /// <remarks>
    /// The batches are <b>copied</b>, not stored by reference: the production caller wraps the
    /// <see cref="DataTable"/> in a <c>using</c> and disposes it as soon as this returns, so keeping
    /// the instance would leave the test asserting against a disposed table.
    /// </remarks>
    internal class InMemoryUserBulkUpdateWriter : IUserBulkUpdateWriter
    {
        public List<DataTable> Batches { get; } = new List<DataTable>();

        public int TotalRowsWritten => Batches.Sum(b => b.Rows.Count);

        public Task ExecuteAsync(DataTable userUpdates)
        {
            Batches.Add(userUpdates.Copy());
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory <see cref="IUserLookupStore"/> seeded with a fixed user population.
    /// </summary>
    /// <remarks>
    /// <see cref="CallCount"/> and <see cref="RequestedUpnBatches"/> are what pin the N+1 fix: the
    /// point of the port is that a whole batch resolves in one call, so a regression that puts the
    /// lookup back inside the per-user loop shows up as a call count equal to the number of users.
    /// </remarks>
    internal class InMemoryUserLookupStore : IUserLookupStore
    {
        private readonly Dictionary<string, List<Common.Entities.User>> _byUpn
            = new Dictionary<string, List<Common.Entities.User>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many times the store has been asked for users.</summary>
        public int CallCount { get; private set; }

        /// <summary>The UPN set passed to each call, in order.</summary>
        public List<List<string>> RequestedUpnBatches { get; } = new List<List<string>>();

        public InMemoryUserLookupStore Add(Common.Entities.User user)
        {
            if (!_byUpn.TryGetValue(user.UserPrincipalName, out var rows))
            {
                rows = new List<Common.Entities.User>();
                _byUpn[user.UserPrincipalName] = rows;
            }
            rows.Add(user);
            return this;
        }

        public InMemoryUserLookupStore Add(int id, string upn)
        {
            return Add(new Common.Entities.User { ID = id, UserPrincipalName = upn });
        }

        public Task<IReadOnlyList<Common.Entities.User>> GetUsersByUpnAsync(IReadOnlyCollection<string> upns)
        {
            CallCount++;
            RequestedUpnBatches.Add(upns.ToList());

            var found = new List<Common.Entities.User>();
            foreach (var upn in upns)
            {
                if (_byUpn.TryGetValue(upn, out var rows))
                {
                    found.AddRange(rows);
                }
            }

            return Task.FromResult<IReadOnlyList<Common.Entities.User>>(found);
        }
    }

    /// <summary>
    /// An <see cref="IUserLookupStore"/> whose every call fails.
    /// </summary>
    /// <remarks>
    /// Fails via <see cref="Task.FromException"/> rather than throwing synchronously. A synchronous
    /// throw lands in the caller's <c>catch</c> whether or not the caller awaited the task, so a fake
    /// built that way cannot detect a dropped <c>await</c> - the bug class behind commit
    /// <c>560e501</c>. The attempt is recorded before the faulted task is returned so
    /// "was it attempted?" assertions still hold.
    /// </remarks>
    internal class FailingUserLookupStore : IUserLookupStore
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<Common.Entities.User>> GetUsersByUpnAsync(IReadOnlyCollection<string> upns)
        {
            CallCount++;
            return Task.FromException<IReadOnlyList<Common.Entities.User>>(
                new InvalidOperationException("Simulated user lookup failure"));
        }
    }
}
