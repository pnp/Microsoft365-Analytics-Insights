using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.CopilotCosts
{
    public interface ICostPublicationFence
    {
        Task ExecuteExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken ct);
        Task InvalidateAndExecuteExclusiveAsync(Func<CancellationToken, Task<string>> readGeneration,
            Func<CancellationToken, Task> action, CancellationToken ct);
    }

    // No identifiers, source documents, SQL details or connection secrets cross this boundary.
    public sealed class CostCollectorStatus
    {
        public string Phase { get; set; } = "notStarted";
        public long DirectoryUsers { get; set; }
        public bool InventoryComplete { get; set; }
        public long Pending { get; set; }
        public long Retrying { get; set; }
        public long Observed { get; set; }
        public long Measured { get; set; }
        public long Empty { get; set; }
        public long Partial { get; set; }
        public long Unevaluated { get; set; }
        public long Failed { get; set; }
        public long Denied { get; set; }
        public DateTimeOffset? LastAttempt { get; set; }
        public DateTimeOffset? LastCompleted { get; set; }
        public string LastOutcome { get; set; } = "notStarted";
    }

    public sealed class CostCollectorRun
    {
        public Guid Id { get; set; }
        public string Generation { get; set; }
        public Guid Owner { get; set; }
        public long Fence { get; set; }
        public bool InventoryComplete { get; set; }
        public string NextLink { get; set; }
        public int Pages { get; set; }
    }

    public sealed class CostCollectorWork
    {
        public Guid UserId { get; set; }
        public int Attempts { get; set; }
    }

    public sealed class CostDataFreshness
    {
        public DateTimeOffset? LastModifiedDateTime { get; set; }
    }

    public sealed class CostPolicyContext
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string DisplayName { get; set; }
        public string Status { get; set; }
        public string BillingMethods { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public DateTimeOffset? ModifiedAt { get; set; }
        public CostQuantity PeriodLimit { get; set; }
        public CostQuantity PerUserLimit { get; set; }
    }

    public sealed class CostPolicyServiceContext
    {
        public string ServiceId { get; set; }
        public string State { get; set; }
        public string ParentStatus { get; set; }
        public string ParentEtag { get; set; }
    }

    public sealed class CostTenantBalance
    {
        public string BillingMethod { get; set; }
        public double? PurchasedQuantity { get; set; }
        public double? ConsumedQuantity { get; set; }
        public double? RemainingQuantity { get; set; }
        public string Unit { get; set; }
        public CostDataFreshness Freshness { get; set; }
    }

    public sealed class CostContextTask
    {
        public string Url { get; set; }
        public string Kind { get; set; }
    }

    public sealed class CostContextPage
    {
        public string Document { get; set; }
        public List<CostContextTask> Following { get; set; } = new List<CostContextTask>();
    }
}
