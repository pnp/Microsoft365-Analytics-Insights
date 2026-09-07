using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class DemoOptions
    {
        // Bump when generation rules change: completed targets must not silently reuse an older shape.
        public const string FormatVersion = "contoso-demo-v1";
        public int Users { get; private set; } = 1000;
        public int Skus { get; private set; } = 50;
        public int Days { get; private set; } = 180;
        public int Seed { get; private set; } = 42;
        public int CopilotPercent { get; private set; } = 60;
        public int BatchSize { get; private set; } = 250;
        public DateTime AsOf { get; private set; }
        public string Database { get; private set; }
        public string Output { get; private set; }
        public bool Preview { get; private set; }
        public bool Help { get; private set; }
        public bool CompileProfiles { get; private set; } = true;
        public int[] Mix { get; private set; } = new[] { 30, 35, 20, 8, 7 };
        public DateTime Start => AsOf.AddDays(-Days);
        public DateTime ReportEnd => AsOf.AddDays(-3);
        public DateTime FirstCopilotReport => Start.AddDays(27);

        public static DemoOptions Parse(string[] args, DateTime utcToday)
        {
            var result = new DemoOptions { AsOf = DateTime.SpecifyKind(utcToday.Date, DateTimeKind.Utc) };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!seen.Add(key)) throw new ArgumentException("Duplicate option: " + key);
                if (key == "--help") { result.Help = true; continue; }
                if (key == "--preview") { result.Preview = true; continue; }
                if (key == "--no-profiles") { result.CompileProfiles = false; continue; }
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Missing value for " + key);
                var value = args[++i];
                switch (key)
                {
                    case "--database": result.Database = value; break;
                    case "--output": result.Output = value; break;
                    case "--users": result.Users = Integer(key, value, 1, 1000000); break;
                    case "--skus": result.Skus = Integer(key, value, 10, 1000); break;
                    case "--days": result.Days = Integer(key, value, 31, 730); break;
                    case "--seed": result.Seed = Integer(key, value, 0, int.MaxValue); break;
                    case "--copilot-percent": result.CopilotPercent = Integer(key, value, 1, 99); break;
                    case "--batch-size": result.BatchSize = Integer(key, value, 1, 1000); break;
                    case "--as-of":
                        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date) || date.Year < 2002 || date.Year > 2100)
                            throw new ArgumentException("--as-of must be yyyy-MM-dd between 2002 and 2100.");
                        result.AsOf = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                        break;
                    case "--mix":
                        result.Mix = value.Split(',').Select(v => Integer(key, v, 0, 100)).ToArray();
                        if (result.Mix.Length != 5 || result.Mix.Sum() != 100)
                            throw new ArgumentException("--mix needs five percentages totalling 100: high,moderate,low,zero,inactive.");
                        break;
                    default: throw new ArgumentException("Unknown demo option: " + key + ". Use demo --help.");
                }
            }
            if (!result.Help && !result.Preview && string.IsNullOrWhiteSpace(result.Database))
                throw new ArgumentException("Use --database ContosoDemo_<name> for a NEW local demo database, or --preview for no SQL.");
            if (result.Database != null && !Regex.IsMatch(result.Database, @"\AContosoDemo_[A-Za-z0-9_]{1,70}\z"))
                throw new ArgumentException("--database must start with ContosoDemo_ and contain only ASCII letters, digits and underscores.");
            return result;
        }

        private static int Integer(string key, string value, int min, int max)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
                throw new ArgumentException($"{key} must be an integer from {min} to {max}.");
            return parsed;
        }

        public string Fingerprint
        {
            get
            {
                // Destination, progress and SQL batch size do not change the generated data.
                var shape = FormattableString.Invariant(
                    $"{FormatVersion}|{Users}|{Skus}|{Days}|{Seed}|{AsOf:yyyy-MM-dd}|{CopilotPercent}|{string.Join(",", Mix)}|{CompileProfiles}");
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(shape))).Replace("-", "").ToLowerInvariant();
            }
        }

        public const string HelpText = @"Generate a rounded, entirely synthetic Contoso demo:
  Tests.FakeDataGen.exe demo --database ContosoDemo_Example
  Tests.FakeDataGen.exe demo --preview --users 300000 --skus 50 --days 31
  Tests.FakeDataGen.exe demo --database ContosoDemo_Repeatable --as-of 2026-09-01 --seed 42

  --database NAME          NEW database on (localdb)\MSSQLLocalDB only. Required unless preview.
  --preview                Stream the same rows to counters; no SQL, config or external services.
  --users N                1..1000000 (default 1000).
  --skus N                 10..1000 (default 50); current assignments, not purchased capacity.
  --days N                 31..730 preceding calendar days (default 180).
  --as-of yyyy-MM-dd        Exclusive UTC end date (default today's UTC date).
                           Workload snapshots stop three days before this date.
                           D28 snapshots start after 28 complete generated days; they
                           are rolling snapshots, NOT additive daily prompt counts.
  --seed N                 0..2147483647 (default 42). Specify as-of too for reproducibility.
  --mix H,M,L,Z,I           High/moderate/low/never-active/inactive percentages, sum 100.
                           Default 30,35,20,8,7. Small populations may not contain every band.
  --copilot-percent N      Current Copilot seats, 1..99 percent (default 60).
  --batch-size N           Bounded SQL batches, 1..1000 (default 250; also capped at 2000 parameters).
  --no-profiles            Skip the existing weekly Power BI profiling procedures.
  --output PATH            Write a JSON summary to a NEW file; never overwrite a file.
  --help                   Show this help without connecting.

Includes demographics, overlapping/rare SKUs, explicit zero daily workload rows, weekday
office-hour activity with time zones/leave, Copilot adoption personas/agents/Cowork,
licensed-only D28 v2 snapshots, paired metadata-only interactions, SharePoint/web facts,
and complete-week Power BI activity/device profiles. No prompt or response text.
No schema/config changes; existing schema is applied through DatabaseUpgrader on the NEW DB.
Exact completed reruns are read-only no-ops. Unmarked, changed or incomplete targets fail;
choose a new name after a failure. There is deliberately no reset or production-connection option.
Large populations/histories can exceed LocalDB's storage limit: preview the row counts first.
Not generated: Teams call/channel detail, sent-email sentiment, Power Platform audit events,
tenant capacity/history, installation/health success logs or cognitive enrichment.
Full guide: https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Synthetic-demo-data";
    }
}
