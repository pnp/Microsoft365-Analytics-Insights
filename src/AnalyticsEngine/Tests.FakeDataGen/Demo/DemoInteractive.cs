using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tests.FakeDataGen.Demo
{
    /// <summary>
    /// Console I/O for the interactive demo option, isolated behind an interface so the question
    /// sequence and the argument list it produces can be tested without a console.
    /// </summary>
    internal interface IDemoPrompt
    {
        void Write(string message);

        /// <summary>Asks a question and returns the raw answer. Null (end of input) means "take the default".</summary>
        string Ask(string question);
    }

    internal sealed class ConsoleDemoPrompt : IDemoPrompt
    {
        public void Write(string message) => Console.WriteLine(message);

        public string Ask(string question)
        {
            Console.Write(question);
            return Console.ReadLine();
        }
    }

    /// <summary>
    /// Menu front end for the same generator that <c>Tests.FakeDataGen.exe demo ...</c> runs, so the demo
    /// data set can be produced without knowing the flags. It only collects answers: the argument list it
    /// builds is handed to <see cref="DemoCommand"/>, which keeps parsing, validation, the LocalDB-only
    /// target rule and the completed-target no-op as the single authority for both entry points.
    /// </summary>
    internal static class DemoInteractive
    {
        internal const string MenuTitle = "Generate a complete synthetic demo database (Contoso, new LocalDB database)";

        public static void Run()
        {
            var prompt = new ConsoleDemoPrompt();
            int exitCode = Run(prompt, DateTime.UtcNow);
            if (exitCode != 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                prompt.Write($"Demo generation did not complete (exit code {exitCode}).");
                Console.ResetColor();
            }
        }

        public static void RunArea(DemoAreaDescription area, string connectionString)
        {
            var prompt = new ConsoleDemoPrompt();
            prompt.Write(area.Title);
            bool existing = AskYesNo(prompt, "Append to the existing TEST database supplied at startup (otherwise create a new LocalDB database)?", false);
            if (existing && string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("No existing database connection was supplied. Restart with a TEST database connection string, or choose a new database.");
            var args = BuildArgs(prompt, DateTime.UtcNow, area.Area, existing);
            if (args == null) { prompt.Write("Cancelled. Nothing was changed."); return; }
            int code = existing
                ? DemoCommand.RunExisting(args.Concat(new[] { "--confirm-existing" }).ToArray(), connectionString)
                : DemoCommand.Run(args);
            if (code != 0) prompt.Write($"Activity generation FAILED (exit code {code}).");
        }

        /// <summary>
        /// Asks for the options, then runs the demo generator. Returns the same exit code the
        /// <c>demo</c> command line returns, or 0 when the user chose not to start.
        /// </summary>
        internal static int Run(IDemoPrompt prompt, DateTime utcNow)
        {
            var args = BuildArgs(prompt, utcNow);
            if (args == null)
            {
                prompt.Write("Cancelled. No database was created and nothing was changed.");
                return 0;
            }

            prompt.Write(string.Empty);
            return DemoCommand.Run(args);
        }

        /// <summary>
        /// Runs the question sequence and returns the equivalent <c>demo</c> argument list, or null if the
        /// user declined to start. <c>--as-of</c> is emitted even when it was never asked for, because it
        /// otherwise defaults to the clock: without it the printed line would name a different window when
        /// it is read on a later day.
        /// </summary>
        internal static string[] BuildArgs(IDemoPrompt prompt, DateTime utcNow,
            DemoArea areas = DemoArea.All, bool existing = false)
        {
            var defaults = new DemoOptions();

            prompt.Write(string.Empty);
            prompt.Write("Generates the same rounded, entirely synthetic Contoso data set as");
            prompt.Write("'Tests.FakeDataGen.exe demo': licences, daily workload activity, Copilot adoption");
            prompt.Write("and D28 snapshots, Teams detail, sent email, SharePoint/web and Power Platform");
            prompt.Write("activity. No real message, prompt or response text is generated.");
            prompt.Write("Selected areas: " + DemoAreas.Format(areas) + ". Shared synthetic users/licences are included.");
            prompt.Write(string.Empty);
            if (existing)
            {
                prompt.Write("WARNING: use only a TEST/demo database. This appends a NEW synthetic population.");
                prompt.Write("Existing users are not changed; no schema upgrade, global profiles or tenant totals.");
                prompt.Write("Committed batches remain after failure. No automatic cleanup or reset is performed.");
            }
            else
            {
                prompt.Write(@"It only creates a NEW database on (localdb)\MSSQLLocalDB and ignores the startup");
                prompt.Write("connection string. There is no reset - an exact completed rerun is a read-only no-op.");
            }
            prompt.Write(string.Empty);

            bool preview = AskYesNo(prompt, "Preview only (count the rows, write no SQL at all)?", false);

            string database = null;
            if (!preview && !existing)
            {
                database = AskDatabaseName(prompt, DefaultDatabaseName(utcNow));
            }

            var asOf = utcNow.Date;
            int users = defaults.Users;
            int skus = defaults.Skus;
            int days = defaults.Days;
            int seed = defaults.Seed;
            int copilotPercent = defaults.CopilotPercent;
            int batchSize = defaults.BatchSize;
            string mix = string.Join(",", defaults.Mix);
            bool compileProfiles = defaults.CompileProfiles && !existing;
            string output = null;

            prompt.Write(string.Empty);
            if (AskYesNo(prompt, "Customise the data set (size, history, seed, licence mix)?", false))
            {
                users = AskInt(prompt, "Users", users, DemoOptions.MinUsers, DemoOptions.MaxUsers);
                skus = AskInt(prompt, "Licence SKUs (current assignments, not purchased capacity)", skus,
                    DemoOptions.MinSkus, DemoOptions.MaxSkus);
                days = AskInt(prompt, "Days of history", days, DemoOptions.MinDays, DemoOptions.MaxDays);
                asOf = AskDate(prompt, "End of the generated window, exclusive (yyyy-MM-dd)", asOf);
                seed = AskInt(prompt, "Random seed", seed, DemoOptions.MinSeed, DemoOptions.MaxSeed);
                copilotPercent = AskInt(prompt, "Percentage of users with a current Copilot seat", copilotPercent,
                    DemoOptions.MinCopilotPercent, DemoOptions.MaxCopilotPercent);
                mix = AskMix(prompt, mix);

                if (!preview)
                {
                    if (!existing)
                        compileProfiles = AskYesNo(prompt,
                            "Compile the weekly Power BI profiles afterwards (slower, but the reports need them)?",
                            compileProfiles);
                    batchSize = AskInt(prompt, "SQL insert batch size", batchSize,
                        DemoOptions.MinBatchSize, DemoOptions.MaxBatchSize);
                }

                output = AskOutputPath(prompt, "JSON summary file to write (must not already exist; blank for none)");
            }

            var args = new List<string>();
            if (preview)
            {
                args.Add("--preview");
            }
            else if (!existing)
            {
                args.Add("--database");
                args.Add(database);
            }
            if (areas != DemoArea.All)
            {
                args.Add("--areas");
                args.Add(DemoAreas.Format(areas));
            }
            args.Add("--as-of");
            args.Add(asOf.ToString(DemoOptions.DateFormat, CultureInfo.InvariantCulture));
            AddValue(args, "--users", users);
            AddValue(args, "--skus", skus);
            AddValue(args, "--days", days);
            AddValue(args, "--seed", seed);
            AddValue(args, "--copilot-percent", copilotPercent);
            args.Add("--mix");
            args.Add(mix);
            if (!preview)
            {
                AddValue(args, "--batch-size", batchSize);
                if (!compileProfiles) args.Add("--no-profiles");
            }
            if (!string.IsNullOrEmpty(output))
            {
                args.Add("--output");
                args.Add(output);
            }

            prompt.Write(string.Empty);
            prompt.Write("About to generate:");
            prompt.Write("  Target             " + (preview
                ? "preview only - no database, no rows, no weekly profiles"
                : existing ? "existing TEST database supplied at startup (additive)"
                : database + @" (new database on (localdb)\MSSQLLocalDB)"));
            prompt.Write($"  Users / SKUs       {users:N0} / {skus:N0}");
            prompt.Write($"  History            {days:N0} days ending {asOf.ToString(DemoOptions.DateFormat, CultureInfo.InvariantCulture)} (exclusive)");
            prompt.Write($"  Seed               {seed}");
            prompt.Write($"  Copilot seats      {copilotPercent}% of users");
            prompt.Write($"  Activity mix       {mix} (high,moderate,low,never-active,inactive)");
            if (!preview)
            {
                prompt.Write("  Weekly profiles    " + (compileProfiles ? "compiled after generation" : "skipped"));
            }
            prompt.Write("  JSON summary       " + (string.IsNullOrEmpty(output) ? "(none)" : output));
            prompt.Write(string.Empty);
            prompt.Write("Equivalent command line: Tests.FakeDataGen.exe " + (existing
                ? "append \"<test-database-connection-string>\" --confirm-existing "
                : "demo ") + CommandLine(args));
            prompt.Write(string.Empty);
            prompt.Write("Large populations or long histories can exceed LocalDB's storage limit; preview first if unsure.");
            prompt.Write(string.Empty);

            return AskYesNo(prompt, "Start generating now?", !existing) ? args.ToArray() : null;
        }

        /// <summary>
        /// A valid target name every time the option is opened. Targets are never reset, so defaulting to a
        /// timestamped name keeps repeat runs working without suggesting a name that is likely to collide.
        /// </summary>
        internal static string DefaultDatabaseName(DateTime utcNow) =>
            "ContosoDemo_" + utcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        /// <summary>
        /// Renders the built argument list for display. Values that are not plain identifiers, numbers or
        /// paths are wrapped in double quotes so a copied line survives spaces and command separators. This
        /// is a convenience, not an escaping contract - a shell still expands its own variable syntax inside
        /// double quotes, and the generator is always handed the argument array itself rather than this text.
        /// </summary>
        internal static string CommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        private static string Quote(string value) =>
            value.Length > 0 && value.All(IsPlain) ? value : "\"" + value + "\"";

        private static bool IsPlain(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || @"_-.:,\/".IndexOf(c) >= 0;

        private static void AddValue(List<string> args, string option, int value)
        {
            args.Add(option);
            args.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        private static string AskDatabaseName(IDemoPrompt prompt, string defaultValue)
        {
            while (true)
            {
                var answer = AskText(prompt, "New database name", defaultValue);
                if (DemoOptions.IsValidDatabaseName(answer)) return answer;
                prompt.Write("Names must start with ContosoDemo_ and use only letters, digits and underscores.");
            }
        }

        private static string AskMix(IDemoPrompt prompt, string defaultValue)
        {
            while (true)
            {
                var answer = AskText(prompt, "Activity mix high,moderate,low,never-active,inactive", defaultValue);
                var parts = answer.Split(',');
                var parsed = new List<int>();
                foreach (var part in parts)
                {
                    if (int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var band)
                        && band <= 100)
                    {
                        parsed.Add(band);
                    }
                }
                if (parsed.Count == parts.Length && parsed.Count == DemoOptions.MixBands && parsed.Sum() == 100)
                {
                    return string.Join(",", parsed);
                }
                prompt.Write($"Enter {DemoOptions.MixBands} percentages, separated by commas, totalling 100.");
            }
        }

        private static DateTime AskDate(IDemoPrompt prompt, string question, DateTime defaultValue)
        {
            while (true)
            {
                var answer = AskText(prompt, question, defaultValue.ToString(DemoOptions.DateFormat, CultureInfo.InvariantCulture));
                if (DateTime.TryParseExact(answer, DemoOptions.DateFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed)
                    && parsed.Year >= DemoOptions.MinYear && parsed.Year <= DemoOptions.MaxYear)
                {
                    return parsed;
                }
                prompt.Write($"Enter a date as yyyy-MM-dd between {DemoOptions.MinYear} and {DemoOptions.MaxYear}.");
            }
        }

        private static int AskInt(IDemoPrompt prompt, string question, int defaultValue, int min, int max)
        {
            while (true)
            {
                var answer = AskText(prompt, question, defaultValue.ToString(CultureInfo.InvariantCulture));
                if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    && parsed >= min && parsed <= max)
                {
                    return parsed;
                }
                prompt.Write($"Enter a whole number from {min:N0} to {max:N0}.");
            }
        }

        private static bool AskYesNo(IDemoPrompt prompt, string question, bool defaultValue)
        {
            while (true)
            {
                var answer = prompt.Ask($"{question} [{(defaultValue ? "Y/n" : "y/N")}]: ");
                if (string.IsNullOrWhiteSpace(answer)) return defaultValue;

                var trimmed = answer.Trim();
                if (trimmed.StartsWith("y", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("n", StringComparison.OrdinalIgnoreCase)) return false;
                prompt.Write("Please answer y or n.");
            }
        }

        private static string AskText(IDemoPrompt prompt, string question, string defaultValue)
        {
            var answer = prompt.Ask($"{question} [{defaultValue}]: ");
            return string.IsNullOrWhiteSpace(answer) ? defaultValue : answer.Trim();
        }

        private static string AskOutputPath(IDemoPrompt prompt, string question)
        {
            while (true)
            {
                var answer = prompt.Ask(question + ": ");
                if (string.IsNullOrWhiteSpace(answer)) return null;

                var trimmed = answer.Trim();
                // The parser reads a token starting with "--" as the next option, so such a path would
                // build an argument list it then refuses.
                if (!trimmed.StartsWith("--", StringComparison.Ordinal)) return trimmed;
                prompt.Write("A summary file path cannot start with '--'.");
            }
        }
    }
}
