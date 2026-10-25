using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    public static class DlpHistoricalRepairCommand
    {
        public const string Option = "--repair-dlp-history";
        public const string Usage = "--repair-dlp-history <from UTC ISO-8601> <to UTC ISO-8601> <private checkpoint file>";

        public static async Task RunAsync(string[] args, AppConfig config)
        {
            if (args == null || args.Length != 4 || args[0] != Option) throw new ArgumentException(Usage);
            var path = PrivateArtifactPath.Validate(args[3], ".lock", ".pending");
            var from = DateTime.Parse(args[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var to = DateTime.Parse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from >= to)
                throw new ArgumentException("Both dates must be UTC (Z) and form a nonempty half-open window.");
            var archive = StateStore.TryOpen(config, StatePartitions.DlpHistoricalArchive)
                ?? throw new InvalidOperationException("Storage must be configured: historical DLP metadata is archived in Azure Tables before it is removed from activity counts.");
            string target;
            using (var hash = SHA256.Create())
                target = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(config.ConnectionStrings.DatabaseConnectionString)));
            using (var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var checkpoint = File.Exists(path)
                    ? ReadCheckpoint(File.ReadAllText(path), target, from, to)
                    : new Checkpoint { Version = CheckpointVersion, Target = target, FromUtc = from, ToUtc = to };
                var batch = await DlpHistoricalRepair.RunBatchAsync(config.ConnectionStrings.DatabaseConnectionString, from, to,
                    checkpoint.Batch?.AfterTime, checkpoint.Batch?.AfterEventId, 500, NullLogger.Instance, archive);
                if (batch.Scanned > 0) checkpoint.Batch = batch;
                var pending = path + ".pending";
                File.WriteAllText(pending, JsonConvert.SerializeObject(checkpoint));
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
                Console.WriteLine($"Scanned={batch.Scanned}; SharePointRowsRemoved={batch.SharePointRowsRemoved}; " +
                    $"ExchangeRowsRemoved={batch.ExchangeRowsRemoved}; GeneralRowsReclassified={batch.GeneralRowsReclassified}. " +
                    "Repeat until Scanned=0. Re-scan with a new checkpoint after historical writers finish. Audit and policy evidence is retained.");
            }
        }

        /// <summary>
        /// Checkpoint format. Version 2 resumes on (time_stamp, id); files without a version were written by the build that
        /// paged on (time_stamp, user_id, id), whose cursor would skip or repeat rows in the new order, so they are refused.
        /// </summary>
        internal const int CheckpointVersion = 2;

        internal static Checkpoint ReadCheckpoint(string json, string target, DateTime from, DateTime to)
        {
            var checkpoint = JsonConvert.DeserializeObject<Checkpoint>(json);
            if (checkpoint == null)
                throw new ArgumentException("The checkpoint file is empty or not a DLP repair checkpoint.");
            if (checkpoint.Version != CheckpointVersion)
                throw new ArgumentException($"The checkpoint file has format version {checkpoint.Version}, but this build reads version {CheckpointVersion} " +
                    "(it pages by time and event id; earlier builds also paged by user). Start again with a new checkpoint file: " +
                    "the repair is idempotent, so rows already repaired are scanned again and left unchanged.");
            if (checkpoint.Target != target || checkpoint.FromUtc != from || checkpoint.ToUtc != to)
                throw new ArgumentException("Checkpoint belongs to a different repair target or UTC window.");
            return checkpoint;
        }

        internal sealed class Checkpoint
        {
            public int Version { get; set; }
            public string Target { get; set; }
            public DateTime FromUtc { get; set; }
            public DateTime ToUtc { get; set; }
            public DlpHistoricalRepairBatch Batch { get; set; }
        }
    }
}
