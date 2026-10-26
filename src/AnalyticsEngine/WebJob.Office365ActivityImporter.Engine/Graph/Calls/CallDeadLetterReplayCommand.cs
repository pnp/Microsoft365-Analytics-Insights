using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// The importer's operator-only <c>--replayCallDeadLetters</c> mode: replays the Teams calls queue's dead-letter
    /// queue with <see cref="CallDeadLetterReplayer"/> and exits, without starting the import.
    /// </summary>
    public static class CallDeadLetterReplayCommand
    {
        public const string Switch = "--replayCallDeadLetters";
        private const string ReplayWord = "replay";

        // What an option's "--" may have become: a single hyphen (a PowerShell habit), a slash, or - from copying and
        // pasting - a dash or minus sign.
        private static readonly char[] TypographicDashes = { '\u2010', '\u2011', '\u2013', '\u2014', '\u2212' };
        private static readonly char[] OptionLeaders = { '-', '/', '\u2010', '\u2011', '\u2013', '\u2014', '\u2212' };
        public const string MaxOption = "--max";
        public const string ScanLimitOption = "--scan-limit";
        public const string DryRunOption = "--dry-run";
        public const string ReasonOption = "--reason";
        public const string IncludeInvalidOption = "--include-invalid";
        public const string PurgeUnimportableOption = "--purge-unimportable";
        public const string PurgeInvalidOption = "--purge-invalid";

        /// <summary>Everything looked at was replayed, deleted as asked or deliberately skipped (or, in a dry run, counted).</summary>
        public const int ExitSucceeded = 0;

        /// <summary>
        /// A replay or delete failed, the queue couldn't be opened or read, the run was cancelled, or it stopped before
        /// looking at all it should have.
        /// </summary>
        public const int ExitFailed = 1;

        /// <summary>The command line was wrong: nothing was read or changed.</summary>
        public const int ExitUsage = 2;

        private static readonly string[] Options =
        {
            Switch, MaxOption, ScanLimitOption, DryRunOption, ReasonOption, IncludeInvalidOption, PurgeUnimportableOption, PurgeInvalidOption
        };

        public static readonly string Usage =
            $"Usage: {Switch} [{DryRunOption}] [{MaxOption} N] [{ScanLimitOption} N] [{ReasonOption} <dead-letter reason>] [{IncludeInvalidOption}] " +
            $"[{PurgeUnimportableOption}] [{PurgeInvalidOption}]{Environment.NewLine}" +
            $"  {DryRunOption}             Count and summarise the dead-lettered calls; send, delete and lock nothing.{Environment.NewLine}" +
            $"  {MaxOption} N               Replay at most N messages, 1 to {CallDeadLetterReplayOptions.MaxMessagesLimit} (default {CallDeadLetterReplayOptions.DefaultMaxMessages}). " +
            $"Skipped and deleted messages don't count.{Environment.NewLine}" +
            $"  {ScanLimitOption} N        Look at no more than N dead-lettered messages, 1 to {CallDeadLetterReplayOptions.ScanLimitLimit} " +
            $"(default 10 x {MaxOption}, at least {CallDeadLetterReplayOptions.MinimumDefaultScanLimit}).{Environment.NewLine}" +
            $"  {ReasonOption} R            Only messages dead-lettered with reason R, e.g. {CallQueueRetryPolicy.RetriesExhaustedReason}.{Environment.NewLine}" +
            $"  {IncludeInvalidOption}     Also replay what the importer found can never be imported ({CallQueueMessageHandler.InvalidNotificationReason}, " +
            $"{CallQueueMessageHandler.MissingCallIdReason}, {CallQueueMessageHandler.NotRetryableReason}).{Environment.NewLine}" +
            $"  {PurgeUnimportableOption}  DELETE dead-lettered calls first queued over 30 days ago: Graph no longer has them.{Environment.NewLine}" +
            $"  {PurgeInvalidOption}       DELETE what the importer found can never be imported ({CallQueueMessageHandler.InvalidNotificationReason}, " +
            $"{CallQueueMessageHandler.MissingCallIdReason}, {CallQueueMessageHandler.NotRetryableReason}).";

        /// <summary>
        /// Whether the command line asks for this mode instead of the import - or looks as if it meant to. So that a mistyped
        /// or incomplete replay command line is refused (<see cref="TryParse"/>) rather than falling through to the import -
        /// which from the Kudu console would start a second importer alongside the WebJob - an argument counts when, without
        /// its leading hyphens, slashes, dashes or minus signs (as a single hyphen, a habit from PowerShell, or copying and
        /// pasting can make of <c>--</c>), it begins <c>replay</c> or is one of this mode's options; and so does any argument
        /// beginning with a dash or minus sign other than a hyphen. None of the importer's own arguments (<c>--webhook</c>,
        /// <c>--callId</c>, <c>--traceAuditEmail</c>, <c>--traceAuditDir</c>, <c>--repair-copilot-history</c>) does.
        /// </summary>
        public static bool IsRequested(string[] args)
        {
            return args != null && args.Any(a => a != null && (StartsWithTypographicDash(a) || NamesThisMode(Bare(a))));
        }

        // An argument without its leading run of hyphens, slashes, dashes and minus signs; null when it has none.
        private static string Bare(string arg)
        {
            var bare = arg.TrimStart(OptionLeaders);
            return bare.Length < arg.Length ? bare : null;
        }

        private static bool NamesThisMode(string bare)
        {
            return bare != null && (bare.StartsWith(ReplayWord, StringComparison.OrdinalIgnoreCase) || OptionNamed(bare) != null);
        }

        // The option a bare argument names, if any.
        private static string OptionNamed(string bare)
        {
            return bare == null ? null : Options.FirstOrDefault(o => string.Equals(o.Substring(2), bare, StringComparison.OrdinalIgnoreCase));
        }

        private static bool StartsWithTypographicDash(string arg)
        {
            return arg.Length > 0 && Array.IndexOf(TypographicDashes, arg[0]) >= 0;
        }

        private static string DescribeUnrecognised(string arg)
        {
            var bare = arg == null ? null : Bare(arg);
            var meant = OptionNamed(bare);
            if (arg != null && StartsWithTypographicDash(arg))
            {
                return $"'{arg}' begins with a dash or minus sign - an en or em dash, say - not two hyphens (--), as copying and pasting can do: " +
                    $"type it again{(meant == null ? string.Empty : $", as {meant}")}. Nothing was imported or replayed.";
            }
            if (meant != null)
            {
                return $"'{arg}' must begin with two hyphens: {meant}. Nothing was imported or replayed.";
            }
            if (bare != null && bare.StartsWith(ReplayWord, StringComparison.OrdinalIgnoreCase))
            {
                return $"Unrecognised argument '{arg}': the switch is {Switch}. Nothing was imported or replayed.";
            }
            return $"Unrecognised argument '{arg}'.";
        }

        /// <summary>
        /// Reads the options. Anything not recognised is an error rather than ignored: a mistyped <c>--dry-run</c> must not
        /// turn into a real replay.
        /// </summary>
        public static bool TryParse(string[] args, out CallDeadLetterReplayOptions options, out string error)
        {
            options = null;
            error = null;
            if (args == null || args.Length == 0)
            {
                error = $"{Switch} is missing.";
                return false;
            }

            var parsed = new CallDeadLetterReplayOptions();
            var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                var option = Options.FirstOrDefault(o => string.Equals(o, arg, StringComparison.OrdinalIgnoreCase));
                if (option == null)
                {
                    error = DescribeUnrecognised(arg);
                    return false;
                }
                if (!given.Add(option))
                {
                    error = $"{option} is given more than once.";
                    return false;
                }

                if (option == MaxOption)
                {
                    var value = i + 1 < args.Length ? args[++i] : null;
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max < 1 || max > CallDeadLetterReplayOptions.MaxMessagesLimit)
                    {
                        error = $"{MaxOption} needs a whole number from 1 to {CallDeadLetterReplayOptions.MaxMessagesLimit}.";
                        return false;
                    }
                    parsed.MaxMessages = max;
                }
                else if (option == ScanLimitOption)
                {
                    var value = i + 1 < args.Length ? args[++i] : null;
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 1 || limit > CallDeadLetterReplayOptions.ScanLimitLimit)
                    {
                        error = $"{ScanLimitOption} needs a whole number from 1 to {CallDeadLetterReplayOptions.ScanLimitLimit}.";
                        return false;
                    }
                    parsed.ScanLimit = limit;
                }
                else if (option == ReasonOption)
                {
                    var value = i + 1 < args.Length ? args[++i] : null;
                    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"{ReasonOption} needs a dead-letter reason, e.g. {CallQueueRetryPolicy.RetriesExhaustedReason}.";
                        return false;
                    }
                    parsed.Reason = value.Trim();
                }
                else if (option == DryRunOption)
                {
                    parsed.DryRun = true;
                }
                else if (option == IncludeInvalidOption)
                {
                    parsed.IncludeInvalid = true;
                }
                else if (option == PurgeUnimportableOption)
                {
                    parsed.PurgeUnimportable = true;
                }
                else if (option == PurgeInvalidOption)
                {
                    parsed.PurgeInvalid = true;
                }
            }

            if (parsed.IncludeInvalid && parsed.PurgeInvalid)
            {
                error = $"{IncludeInvalidOption} replays what {PurgeInvalidOption} deletes: give one or the other.";
                return false;
            }

            // Every option is recognised, but without the switch they are not a replay - nor an import.
            if (!given.Contains(Switch))
            {
                error = $"{Switch} is missing, and its options do nothing without it. Nothing was imported or replayed.";
                return false;
            }

            options = parsed;
            return true;
        }

        /// <summary>
        /// Runs the replay the command line asks for against the configured calls queue.
        /// </summary>
        /// <returns>The process exit code: <see cref="ExitSucceeded"/>, <see cref="ExitFailed"/> or <see cref="ExitUsage"/>.</returns>
        public static async Task<int> RunAsync(string[] args, AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            // The calls queue's own App Insights operation, so the replay is found alongside the call imports.
            var logger = new AnalyticsLogger(config.AppInsightsConnectionString, "Office365CallsImporter");
            using (var cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler onCancel = (sender, e) =>
                {
                    // Stop after the message in hand, and print the summary rather than dying mid-way. The messages the run
                    // holds are not put back (that honours the same cancellation): they go back to the dead-letter queue by
                    // themselves when their locks lapse, within the queue's lock duration.
                    e.Cancel = true;
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += onCancel;
                try
                {
                    return await RunAsync(args, logger, () => ServiceBusCallDeadLetterQueue.Open(config), SystemClock.Instance, cancellation.Token);
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                    AnalyticsLogger.FlushSharedTelemetry();
                }
            }
        }

        /// <param name="openQueue">Opens the calls queue; only called once the command line has been accepted.</param>
        public static async Task<int> RunAsync(string[] args, ILogger logger, Func<ICallDeadLetterQueue> openQueue, IClock clock, CancellationToken cancellationToken)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (openQueue == null) throw new ArgumentNullException(nameof(openQueue));

            if (!TryParse(args, out var options, out var error))
            {
                logger.LogError($"Teams calls dead-letter replay: {error}{Environment.NewLine}{Usage}");
                return ExitUsage;
            }

            ICallDeadLetterQueue queue;
            try
            {
                queue = openQueue();
            }
            catch (Exception ex)
            {
                logger.LogError($"Teams calls dead-letter replay: couldn't open the Teams calls queue. {ex.GetType().Name}: {ex.Message}");
                return ExitFailed;
            }

            try
            {
                logger.LogInformation($"Teams calls dead-letter replay: reading the dead-letter queue of Service Bus queue '{queue.QueueName}'.");
                var summary = await new CallDeadLetterReplayer(queue, queue, logger, clock).RunAsync(options, cancellationToken);
                return summary.Succeeded ? ExitSucceeded : ExitFailed;
            }
            finally
            {
                try
                {
                    await queue.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Teams calls dead-letter replay: couldn't close the Service Bus connection cleanly: {ex.Message}");
                }
            }
        }
    }
}
