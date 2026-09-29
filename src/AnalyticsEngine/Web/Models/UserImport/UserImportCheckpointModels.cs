using Newtonsoft.Json;
using System;

namespace Web.AnalyticsWeb.Models.UserImport
{
    /// <summary>
    /// <c>GET api/UserImportCheckpoint</c>: the facts behind the Administration &gt; User import page. Facts only -
    /// the page writes every sentence, in the reader's language. The checkpoint's value is deliberately absent:
    /// the page only needs to know whether one is stored.
    /// </summary>
    public sealed class UserImportCheckpointStatus
    {
        /// <summary>Whether a Redis connection string is configured. Without Redis the checkpoint is never saved.</summary>
        [JsonProperty("redisConfigured")]
        public bool RedisConfigured { get; set; }

        /// <summary>Whether the Graph user import (<c>GraphUsersMetadata</c>) is switched on; null when the import settings can't be read.</summary>
        [JsonProperty("userImportEnabled")]
        public bool? UserImportEnabled { get; set; }

        /// <summary>Whether a <c>/users/delta</c> token is stored under <see cref="CheckpointKey"/>.</summary>
        [JsonProperty("checkpointStored")]
        public bool CheckpointStored { get; set; }

        /// <summary>The Redis key that holds the checkpoint, so an operator can match it to the documentation.</summary>
        [JsonProperty("checkpointKey")]
        public string CheckpointKey { get; set; }

        /// <summary>When the user import last completed, from the importer's cadence stamp; null when none is recorded.</summary>
        [JsonProperty("lastCompletedUtc")]
        public DateTime? LastCompletedUtc { get; set; }

        /// <summary>The user import's minimum interval in hours (<c>GraphMetadataImportIntervalHours</c>). 0 means every cycle.</summary>
        [JsonProperty("intervalHours")]
        public int IntervalHours { get; set; }
    }

    /// <summary>Body of <c>POST api/UserImportCheckpoint/clear</c>.</summary>
    public sealed class UserImportCheckpointClearRequest
    {
        /// <summary>Also delete the last-completed stamp, so the user import runs on the next cycle instead of waiting out its interval.</summary>
        [JsonProperty("runOnNextCycle")]
        public bool RunOnNextCycle { get; set; }
    }

    /// <summary>What a clear actually removed, so the page can say so rather than assume.</summary>
    public sealed class UserImportCheckpointClearResult
    {
        /// <summary>A stored checkpoint existed and was deleted. False when there was none to delete.</summary>
        [JsonProperty("checkpointCleared")]
        public bool CheckpointCleared { get; set; }

        /// <summary>A last-completed stamp existed and was deleted. Always false unless the request asked for it.</summary>
        [JsonProperty("lastCompletedCleared")]
        public bool LastCompletedCleared { get; set; }
    }

    /// <summary>A failed request's stable error code. The page maps it to a sentence; the server sends no text.</summary>
    public sealed class UserImportCheckpointError
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }
}
