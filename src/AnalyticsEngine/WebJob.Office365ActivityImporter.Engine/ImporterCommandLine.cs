using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace WebJob.Office365ActivityImporter.Engine
{
    public enum ImporterMode
    {
        /// <summary>The endless import loop.</summary>
        Import,
        /// <summary>Import one call record by id, then exit.</summary>
        SingleCall,
        RepairCopilotHistory,
        RepairDlpHistory,
        ReplayCallDeadLetters,
    }

    /// <summary>
    /// The whole command line of the Office 365 importer, read before anything else starts.
    /// </summary>
    /// <remarks>
    /// The importer used to look at <c>args[0]</c> for an operator command and otherwise scan the arguments for the
    /// options it knew, ignoring the rest. A mistyped or misplaced <c>--repair-dlp-history</c> therefore fell through
    /// to the endless import - from the Kudu console, a second importer next to the singleton WebJob, colliding with
    /// it on the shared staging tables. Now anything that is not understood is refused with exit code 2 and the usage
    /// text, and the endless import is not started interactively unless asked for.
    /// </remarks>
    public sealed class ImporterCommandLine
    {
        public const string RepairCopilotHistoryOption = "--repair-copilot-history";
        public const string ConsoleImportOption = "--console-import";

        /// <summary>Kudu sets this for every WebJob process; its absence means the importer was started by hand.</summary>
        public const string WebJobNameVariable = "WEBJOBS_NAME";

        public static readonly string Usage =
            "Usage (no arguments inside the WebJob runs the import):" + Environment.NewLine +
            "  [--webhook <url>] [--traceAuditEmail <email>] [--traceAuditDir <dir>] [--console-import]   run the import" + Environment.NewLine +
            "  --callId <call id>                                                                    import one call record, then exit" + Environment.NewLine +
            $"  {RepairCopilotHistoryOption} <from UTC ISO-8601> <to UTC ISO-8601> <private checkpoint file>" + Environment.NewLine +
            $"  {DlpHistoricalRepairCommand.Usage}" + Environment.NewLine +
            $"  {CallDeadLetterReplayCommand.Usage}" + Environment.NewLine +
            $"A command must be the only thing on the command line. {ConsoleImportOption} is needed to run the endless import " +
            $"outside a WebJob ({WebJobNameVariable} is not set), where it would run next to the WebJob's own importer.";

        public ImporterMode Mode { get; private set; } = ImporterMode.Import;

        /// <summary>Why the command line was refused; null when it is acceptable.</summary>
        public string Error { get; private set; }

        public Uri WebhookOverride { get; private set; }
        public string CallId { get; private set; }
        public string TraceAuditEmail { get; private set; }
        public string TraceAuditDirectory { get; private set; }

        public bool IsValid => Error == null;

        /// <param name="args">The process arguments.</param>
        /// <param name="environment">Reads an environment variable; defaults to the process environment.</param>
        /// <param name="interactiveImportAllowed">Whether the endless import may start without a WebJob host or <c>--console-import</c> (debug builds).</param>
        public static ImporterCommandLine Parse(string[] args, Func<string, string> environment = null, bool interactiveImportAllowed = false)
        {
            args = args ?? new string[0];
            environment = environment ?? Environment.GetEnvironmentVariable;
            var result = new ImporterCommandLine();

            // The replay command owns its own grammar, and also claims anything that looks like a mistyped one.
            if (CallDeadLetterReplayCommand.IsRequested(args))
            {
                result.Mode = ImporterMode.ReplayCallDeadLetters;
                return result;
            }

            var commands = new Dictionary<string, ImporterMode>(StringComparer.OrdinalIgnoreCase)
            {
                { RepairCopilotHistoryOption, ImporterMode.RepairCopilotHistory },
                { DlpHistoricalRepairCommand.Option, ImporterMode.RepairDlpHistory },
            };

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == null || !commands.TryGetValue(args[i], out var command)) continue;

                if (i != 0)
                    return result.Fail($"'{args[i]}' is a command: it must be the first argument, with nothing before it.");
                if (!string.Equals(args[0], args[0].ToLowerInvariant(), StringComparison.Ordinal))
                    return result.Fail($"'{args[0]}' must be typed in lower case: {args[0].ToLowerInvariant()}.");
                if (args.Length != 4)
                    return result.Fail($"{args[0]} takes exactly three values: <from UTC ISO-8601> <to UTC ISO-8601> <private checkpoint file>.");

                result.Mode = command;
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? string.Empty;
                string value = null;
                bool NeedsValue(out string v)
                {
                    v = i + 1 < args.Length ? args[i + 1] : null;
                    if (string.IsNullOrWhiteSpace(v) || v.StartsWith("--", StringComparison.Ordinal)) { v = null; return false; }
                    i++;
                    return true;
                }

                if (!IsKnownOption(arg))
                    return result.Fail($"Unrecognised argument '{arg}'.");
                if (!seen.Add(arg))
                    return result.Fail($"'{arg}' was given more than once.");

                if (Is(arg, ConsoleImportOption))
                {
                    continue;
                }
                if (!NeedsValue(out value))
                    return result.Fail($"'{arg}' needs a value.");

                if (Is(arg, ActivityImportConstants.PARAM_WEBHOOK_OVERRIDE))
                {
                    if (!StringUtils.IsValidAbsoluteUrl(value))
                        return result.Fail($"'{arg}' needs an absolute URL.");
                    result.WebhookOverride = new Uri(value);
                }
                else if (Is(arg, ActivityImportConstants.PARAM_CALL_ID))
                {
                    result.CallId = value;
                    result.Mode = ImporterMode.SingleCall;
                }
                else if (Is(arg, ActivityImportConstants.PARAM_TRACE_AUDIT_EMAIL))
                {
                    result.TraceAuditEmail = value;
                }
                else if (Is(arg, ActivityImportConstants.PARAM_TRACE_AUDIT_DIR))
                {
                    result.TraceAuditDirectory = value;
                }
            }

            if (result.Mode == ImporterMode.Import
                && !interactiveImportAllowed
                && string.IsNullOrEmpty(environment(WebJobNameVariable))
                && !seen.Contains(ConsoleImportOption))
            {
                return result.Fail($"This is not running inside a WebJob ({WebJobNameVariable} is not set), so the endless import is refused: " +
                    $"it would run next to the WebJob's own importer and collide with it. Add {ConsoleImportOption} if that is what you want.");
            }

            return result;
        }

        private static readonly string[] KnownOptions =
        {
            ActivityImportConstants.PARAM_WEBHOOK_OVERRIDE, ActivityImportConstants.PARAM_CALL_ID,
            ActivityImportConstants.PARAM_TRACE_AUDIT_EMAIL, ActivityImportConstants.PARAM_TRACE_AUDIT_DIR, ConsoleImportOption
        };

        private static bool IsKnownOption(string arg) => KnownOptions.Any(o => Is(arg, o));

        private static bool Is(string arg, string option) => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase);

        private ImporterCommandLine Fail(string error)
        {
            Error = error;
            return this;
        }
    }
}
