using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotCosts
{
    public sealed class CostConnectionException : Exception
    {
        public string Status { get; }
        public TimeSpan? RetryAfter { get; }
        public CostConnectionException(string status, TimeSpan? retryAfter = null) : base("Copilot cost connection: " + status)
        { Status = status; RetryAfter = retryAfter; }
    }

    public sealed class CostDocument
    {
        public string Version { get; set; }
        public byte[] Content { get; set; }
    }

    public interface ICostDocumentStore
    {
        Task<CostDocument> ReadAsync(string key, CancellationToken cancellationToken);
        Task<string> WriteAsync(string key, byte[] content, string expectedVersion, CancellationToken cancellationToken);
    }

    public sealed class CostConnectionState
    {
        public string Generation { get; set; } = Guid.NewGuid().ToString("N");
        public string AccountId { get; set; }
        public string Cache { get; set; }
        public string Status { get; set; } = "disconnected";
        public string Owner { get; set; }
        public DateTimeOffset? OwnedUntil { get; set; }
        public long Fence { get; set; }
        public DateTimeOffset? ConnectedAt { get; set; }
        public DateTimeOffset? RefreshedAt { get; set; }
        public CostAuthorization Pending { get; set; }
        public CostProbeProgress Probe { get; set; }
    }

    public sealed class CostAuthorization
    {
        public string StateHash { get; set; }
        public string BrowserHash { get; set; }
        public string OperatorId { get; set; }
        public string Verifier { get; set; }
        public string Nonce { get; set; }
        public string ResponseCode { get; set; }
        public bool ResponseFailed { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class CostProbeProgress
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Phase { get; set; } = "context";
        public string NextLink { get; set; } = "https://graph.microsoft.com/v1.0/users?$select=id";
        public List<string> ContextQueue { get; set; } = new List<string>
        {
            "https://graph.microsoft.com/beta/copilot/costManagement/spendingPolicies",
            "https://graph.microsoft.com/beta/copilot/costManagement/getTenantCreditBalance"
        };
        public int ContextReads { get; set; }
        public HashSet<string> SeenLinks { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public int InventoryPages { get; set; }
        public int InventoryOffset { get; set; }
        public List<string> InventorySelected { get; set; } = new List<string>();
        public int UserCount { get; set; }
        public int Page { get; set; }
        public int Offset { get; set; }
        public int Observed { get; set; }
        public int Measured { get; set; }
        public int Empty { get; set; }
        public int Partial { get; set; }
        public int Unevaluated { get; set; }
        public int Failed { get; set; }
        public int Denied { get; set; }
        public DateTimeOffset? LastAttempt { get; set; }
        public string LastOutcome { get; set; }
    }

    public static class CostConnectionEncoding
    {
        public static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        public static T Deserialize<T>(byte[] value)
        {
            try
            {
                var result = JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(value));
                if (ReferenceEquals(result, null)) throw new CostConnectionException("storageCorrupt");
                return result;
            }
            catch (JsonException) { throw new CostConnectionException("storageCorrupt"); }
            catch (DecoderFallbackException) { throw new CostConnectionException("storageCorrupt"); }
        }
        public static string Random()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
    }
}
