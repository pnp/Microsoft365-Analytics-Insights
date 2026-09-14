using Common.Entities.Config;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// Keeps every project pointed at the one shared user-secrets store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The solution has a single developer secrets store, <c>pnp-m365-analytics-insights</c>, declared
    /// on <c>Common.Entities</c> and repeated as the <c>UserSecretsId</c> of every executable and test
    /// project. <see cref="AnalyticsConfig"/> reads that store by id, so a project pointing somewhere
    /// else is not a cosmetic inconsistency - its secrets are simply never read.
    /// </para>
    /// <para>
    /// This is easy to do by accident and gives no error when you do. Visual Studio's
    /// <i>Manage User Secrets</i> command generates a fresh random GUID into whichever project it is
    /// invoked on and opens that store; a developer right-clicking the web app gets a store nothing
    /// reads. It even looks like it is working, because <c>WebApplication.CreateBuilder</c> loads the
    /// web project's own store into <c>builder.Configuration</c> in Development - but nothing in this
    /// solution reads configuration from the host builder, so the values are loaded and then ignored.
    /// The symptom is a blank <c>ClientID</c> or <c>TenantDomain</c> at startup, a long way from the
    /// cause.
    /// </para>
    /// <para>
    /// Checked against the project files rather than at run time because that is where the mistake is
    /// made and committed. The run-time safety net lives in <c>AnalyticsConfig.AddDeveloperSecrets</c>,
    /// which also reads the entry assembly's store when it differs - but a safety net is not a reason
    /// to let the divergence in.
    /// </para>
    /// </remarks>
    [TestClass]
    public class UserSecretsConsistencyTests
    {
        private const string SharedSecretsId = "pnp-m365-analytics-insights";

        [TestMethod]
        public void Every_Project_That_Declares_A_Secrets_Store_Uses_The_Shared_One()
        {
            var offenders = new List<string>();

            foreach (var project in SolutionProjects())
            {
                var declared = Regex.Matches(File.ReadAllText(project), @"<UserSecretsId>\s*(?<id>[^<]+?)\s*</UserSecretsId>")
                    .Cast<Match>()
                    .Select(m => m.Groups["id"].Value)
                    .ToList();

                foreach (var id in declared.Where(id => !string.Equals(id, SharedSecretsId, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(project)} -> '{id}'");
                }
            }

            Assert.AreEqual(0, offenders.Count,
                "These projects point at a user-secrets store that AnalyticsConfig does not read, so any " +
                $"secret put in them is silently ignored. Change them to '{SharedSecretsId}':" +
                Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// The runnable projects must each declare the shared id, so that Visual Studio and
        /// <c>dotnet user-secrets</c> open the shared store instead of creating a new one.
        /// </summary>
        [TestMethod]
        public void The_Runnable_Projects_Declare_The_Shared_Secrets_Store()
        {
            var required = new[]
            {
                "Web.csproj",
                "WebJob.Office365ActivityImporter.csproj",
                "WebJob.AppInsightsImporter.csproj",
                "App.ControlPanel.WinForms.csproj",
                "Tests.UnitTests.csproj",
                "Tests.FakeDataGen.csproj",
                "Entities.csproj",
            };

            var byName = SolutionProjects().ToDictionary(Path.GetFileName, p => File.ReadAllText(p));
            var missing = new List<string>();

            foreach (var name in required)
            {
                if (!byName.TryGetValue(name, out var content))
                {
                    Assert.Fail($"{name} was not found. If it was renamed or removed, update this test.");
                }

                if (!content.Contains($"<UserSecretsId>{SharedSecretsId}</UserSecretsId>"))
                {
                    missing.Add(name);
                }
            }

            Assert.AreEqual(0, missing.Count,
                "Without the shared <UserSecretsId>, Visual Studio's 'Manage User Secrets' mints a fresh " +
                "random id into these projects and opens a store AnalyticsConfig never reads: " +
                string.Join(", ", missing));
        }

        /// <summary>
        /// The id the code reads must be the id the project files declare.
        /// </summary>
        /// <remarks>
        /// <c>Common.Entities</c> sets <c>GenerateAssemblyInfo=false</c>, so its <c>UserSecretsId</c>
        /// property emits no attribute and the value has to be repeated by hand in
        /// <c>Properties\AssemblyInfo.cs</c>. The property is what the tooling reads; the attribute is
        /// what the runtime reads. They must agree, and nothing but this test makes them.
        /// </remarks>
        [TestMethod]
        public void The_Declared_Id_Matches_The_Id_Compiled_Into_The_Assembly()
        {
            var attribute = typeof(AnalyticsConfig).Assembly
                .GetCustomAttributes(typeof(UserSecretsIdAttribute), false)
                .Cast<UserSecretsIdAttribute>()
                .FirstOrDefault();

            Assert.IsNotNull(attribute,
                "Common.Entities has no [assembly: UserSecretsId]. Because it sets GenerateAssemblyInfo=false, " +
                "the <UserSecretsId> property alone emits nothing and the store is unreadable at run time.");

            Assert.AreEqual(SharedSecretsId, attribute.UserSecretsId,
                "The compiled id and the project-file id have drifted apart.");
        }

        /// <summary>Every <c>.csproj</c> in the solution, found by walking up to the solution file.</summary>
        private static IEnumerable<string> SolutionProjects()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !directory.EnumerateFiles("*.sln").Any())
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "Could not locate the solution directory from " + AppContext.BaseDirectory);

            return directory
                .EnumerateFiles("*.csproj", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(f => f.FullName);
        }
    }
}
