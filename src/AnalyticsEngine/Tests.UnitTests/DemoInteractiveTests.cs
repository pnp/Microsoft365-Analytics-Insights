using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    /// <summary>
    /// The interactive menu option must stay a pure front end for the <c>demo</c> command line: it may only
    /// choose flag values, never relax the target rules or drift away from the command line's defaults.
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoInteractiveTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 1, 13, 45, 30, DateTimeKind.Utc);

        [TestMethod]
        public void DefaultAnswers_ProduceExactlyTheCommandLineDefaultsAgainstANewTarget()
        {
            var prompt = new ScriptedPrompt();
            var args = DemoInteractive.BuildArgs(prompt, Now);

            var options = DemoOptions.Parse(args, Now);
            var commandLineDefaults = DemoOptions.Parse(
                new[] { "--database", "ContosoDemo_Reference", "--as-of", "2026-09-01" }, Now);

            // The fingerprint covers every input that changes the generated data, so one comparison proves
            // an operator who just presses Enter gets the documented command line's data set.
            Assert.AreEqual(commandLineDefaults.Fingerprint, options.Fingerprint);
            Assert.AreEqual(commandLineDefaults.BatchSize, options.BatchSize);
            Assert.IsFalse(options.Preview);
            Assert.IsNull(options.Output);
            Assert.AreEqual("ContosoDemo_20260901_134530", options.Database);
            Assert.AreEqual(DateTimeKind.Utc, options.AsOf.Kind);

            // --as-of is emitted even though it was never asked for, so the printed line still names the
            // same window when it is read on a later day.
            CollectionAssert.Contains(args, "--as-of");
            CollectionAssert.Contains(args, "2026-09-01");
            Assert.IsTrue(prompt.Output.Any(line => line.Contains("Tests.FakeDataGen.exe demo --database ContosoDemo_20260901_134530")),
                "The equivalent command line should be shown before the run starts.");
        }

        [TestMethod]
        public void Preview_OmitsTheTargetAndEverySqlOnlyOption()
        {
            var args = DemoInteractive.BuildArgs(new ScriptedPrompt("yes"), Now);

            CollectionAssert.Contains(args, "--preview");
            CollectionAssert.DoesNotContain(args, "--database");
            CollectionAssert.DoesNotContain(args, "--batch-size");
            CollectionAssert.DoesNotContain(args, "--no-profiles");

            var options = DemoOptions.Parse(args, Now);
            Assert.IsTrue(options.Preview);
            Assert.IsNull(options.Database);
        }

        [TestMethod]
        public void UnsafeTargetNames_AreRefusedAndReAskedRatherThanPassedOn()
        {
            var args = DemoInteractive.BuildArgs(new ScriptedPrompt(
                "no", "CustomerProduction", "ContosoDemo_x];DROP DATABASE master;--", "ContosoDemo_Καλημέρα",
                "ContosoDemo_Good"), Now);

            Assert.AreEqual("ContosoDemo_Good", DemoOptions.Parse(args, Now).Database);
            Assert.IsFalse(args.Any(a => a.IndexOf("DROP", StringComparison.OrdinalIgnoreCase) >= 0
                || a.Contains("Customer") || a.Contains("Καλημέρα")));
        }

        [TestMethod]
        public void CustomAnswers_ReachTheParsedOptionsAndTheRejectedOnesAreReAsked()
        {
            var args = DemoInteractive.BuildArgs(new ScriptedPrompt(
                "n",                          // preview?
                "ContosoDemo_Custom",         // target
                "y",                          // customise?
                "0", "40",                    // users: below the minimum, then valid
                "12",                         // skus
                "30", "35",                   // days: below the minimum, then valid
                "2026-02-30", "2026-09-01",   // as-of: not a real date, then valid
                "7",                          // seed
                "25",                         // copilot percent
                "50,50", "10,20,30,20,20",    // mix: wrong band count, then valid
                "n",                          // compile weekly profiles?
                "10",                         // batch size
                @"C:\temp\demo summary.json", // JSON summary
                ""), Now);                    // start now? (default yes)

            var options = DemoOptions.Parse(args, Now);
            Assert.AreEqual("ContosoDemo_Custom", options.Database);
            Assert.AreEqual(40, options.Users);
            Assert.AreEqual(12, options.Skus);
            Assert.AreEqual(35, options.Days);
            Assert.AreEqual(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), options.AsOf);
            Assert.AreEqual(7, options.Seed);
            Assert.AreEqual(25, options.CopilotPercent);
            CollectionAssert.AreEqual(new[] { 10, 20, 30, 20, 20 }, options.Mix);
            Assert.IsFalse(options.CompileProfiles);
            Assert.AreEqual(10, options.BatchSize);
            Assert.AreEqual(@"C:\temp\demo summary.json", options.Output);

            Assert.AreEqual(
                @"demo --database ContosoDemo_Custom --as-of 2026-09-01 --users 40 --skus 12 --days 35 --seed 7 " +
                @"--copilot-percent 25 --mix 10,20,30,20,20 --batch-size 10 --no-profiles --output ""C:\temp\demo summary.json""",
                "demo " + DemoInteractive.CommandLine(args));
        }

        [TestMethod]
        public void CommandLine_QuotesValuesThatAreNotPlainIdentifiersNumbersOrPaths()
        {
            Assert.AreEqual("--mix 30,35,20,8,7", DemoInteractive.CommandLine(new[] { "--mix", "30,35,20,8,7" }));
            Assert.AreEqual(@"--database ContosoDemo_Example", DemoInteractive.CommandLine(new[] { "--database", "ContosoDemo_Example" }));
            Assert.AreEqual("--output \"C:\\Exports\\R&D.json\"",
                DemoInteractive.CommandLine(new[] { "--output", @"C:\Exports\R&D.json" }));
            Assert.AreEqual("--output \"C:\\Exports\\demo summary.json\"",
                DemoInteractive.CommandLine(new[] { "--output", @"C:\Exports\demo summary.json" }));
        }

        [TestMethod]
        public void SummaryPathStartingWithADash_IsReAskedSoTheBuiltArgumentsStillParse()
        {
            var answers = new List<string> { "n", "ContosoDemo_Output", "y" };
            // Accept the default for users, SKUs, days, as-of, seed, Copilot percent, mix,
            // weekly profiles and batch size, which leaves only the summary path to answer.
            answers.AddRange(Enumerable.Repeat(string.Empty, 9));
            answers.Add("--summary.json");
            answers.Add(@"C:\temp\summary.json");

            var args = DemoInteractive.BuildArgs(new ScriptedPrompt(answers.ToArray()), Now);

            // A value starting with "--" is read by the parser as the next option, so passing it on would
            // build an argument list that DemoCommand then refuses.
            Assert.AreEqual(@"C:\temp\summary.json", DemoOptions.Parse(args, Now).Output);
            CollectionAssert.DoesNotContain(args, "--summary.json");
        }

        [TestMethod]
        public void DecliningTheConfirmation_GeneratesNothing()
        {
            Assert.IsNull(DemoInteractive.BuildArgs(new ScriptedPrompt("n", "", "n", "n"), Now));
        }

        [TestMethod]
        public void DefaultTargetName_IsAlwaysAValidTargetAndFollowsTheClock()
        {
            var first = DemoInteractive.DefaultDatabaseName(Now);
            var second = DemoInteractive.DefaultDatabaseName(Now.AddSeconds(1));
            Assert.IsTrue(DemoOptions.IsValidDatabaseName(first));
            Assert.IsTrue(DemoOptions.IsValidDatabaseName(second));
            Assert.AreNotEqual(first, second);
        }

        /// <summary>
        /// Answers the questions from a fixed script. Once the script is exhausted it returns null, which is
        /// what <c>Console.ReadLine</c> returns at end of input, so every prompt falls back to its default
        /// instead of looping forever.
        /// </summary>
        private sealed class ScriptedPrompt : IDemoPrompt
        {
            private readonly Queue<string> _answers;
            public List<string> Output { get; } = new List<string>();

            public ScriptedPrompt(params string[] answers) => _answers = new Queue<string>(answers);

            public void Write(string message) => Output.Add(message);

            public string Ask(string question) => _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }
}
