using Common.Entities.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Guards the move off App.config/Web.config XML to <c>appsettings.json</c> plus environment
    /// variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different risks are covered here, and they need different kinds of test.
    /// </para>
    /// <para>
    /// The first is <b>silent reintroduction</b>. A call to <c>ConfigurationManager.AppSettings</c>
    /// still compiles on .NET 10 - the type ships in a NuGet package this solution references for
    /// <c>ApplicationSettingsBase</c> - and with no App.config present it simply returns nothing.
    /// The setting reads as absent and the caller takes its "not configured" branch, so nothing
    /// crashes and the defect surfaces only as behaviour a long way from the cause. Exactly that was
    /// left behind by the migration sweep in <c>UpgradeFromStableTests.ScratchConnectionString</c>.
    /// So the tests below assert that XML configuration is genuinely absent and that values arrive
    /// through <see cref="AnalyticsConfig"/> instead.
    /// </para>
    /// <para>
    /// The second is the <b>Azure App Service deployment path</b>, which is the highest-risk claim in
    /// the migration and cannot be checked by reading the code. The installer writes the solution's
    /// connection strings as App Service <i>connection strings</i> - typed <c>SqlAzure</c> for the
    /// database and <c>Custom</c> for the rest (<c>ConfigureAzureComponentsTasks</c>) - and App
    /// Service then injects them into the process with the prefixes <c>SQLAZURECONNSTR_</c> and
    /// <c>CUSTOMCONNSTR_</c>, not under their plain names. Under .NET Framework that was invisible,
    /// because <c>ConfigurationManager</c> merged them into <c>&lt;connectionStrings&gt;</c> itself.
    /// Here it is <c>AddEnvironmentVariables</c> that has to understand the prefixes. If it does not,
    /// every deployed instance loses its database connection while every test still passes - so the
    /// prefixes are exercised for real below rather than trusted.
    /// </para>
    /// </remarks>
    [TestClass]
    [DoNotParallelize]
    public class ConfigurationSourceTests
    {
        /// <summary>
        /// Every environment-variable prefix that <c>AddEnvironmentVariables</c> folds into the
        /// <c>ConnectionStrings</c> section, plus the plain section form.
        /// </summary>
        /// <remarks>
        /// All of these normalise to the same configuration key, and they are supplied by a single
        /// provider, so if two are set at once the winner is decided by enumeration order. That is not
        /// hypothetical: CI sets <c>ConnectionStrings__Storage</c>, and the prefix test below sets
        /// <c>CUSTOMCONNSTR_Storage</c> - which collide on <c>ConnectionStrings:Storage</c>. Every alias
        /// is therefore cleared for the duration of a test that sets one of them, or the test is a
        /// coin toss on the runner and passes locally.
        /// </remarks>
        private static readonly string[] ConnectionStringEnvironmentPrefixes =
        {
            "ConnectionStrings__", "CUSTOMCONNSTR_", "SQLCONNSTR_", "SQLAZURECONNSTR_",
            "MYSQLCONNSTR_", "POSTGRESQLCONNSTR_",
        };

        /// <summary>
        /// The App Service prefixes, paired with the connection strings the installer actually
        /// creates, so the test breaks if a name is renamed on one side only.
        /// </summary>
        private static readonly (string Prefix, string Name)[] AppServiceConnectionStrings =
        {
            // Written by ConfigureAzureComponentsTasks as ConnectionStringType.SqlAzure.
            ("SQLAZURECONNSTR_", "SPOInsightsEntities"),
            // ...and these as ConnectionStringType.Custom.
            ("CUSTOMCONNSTR_", "Storage"),
            ("CUSTOMCONNSTR_", "Redis"),
            ("CUSTOMCONNSTR_", "ServiceBus"),
            ("CUSTOMCONNSTR_", "AzureWebJobsStorage"),
            ("CUSTOMCONNSTR_", "AzureWebJobsDashboard"),
        };

        /// <summary>
        /// Every connection string the installer writes to App Service must be readable under its
        /// plain name, despite App Service exposing it only under a prefix.
        /// </summary>
        [TestMethod]
        public void AppService_Prefixed_Connection_Strings_Resolve_Under_Their_Plain_Name()
        {
            foreach (var (prefix, name) in AppServiceConnectionStrings)
            {
                // A value that could only have come from this test, so a stale appsettings.json entry
                // or a leaked environment variable cannot make the assertion pass by accident.
                var expected = $"injected-by-app-service-{prefix}{name}-{Guid.NewGuid():N}";

                WithEnvironment(new Dictionary<string, string> { [prefix + name] = expected },
                    alsoClearConnectionStringAliasesFor: name, body: () =>
                {
                    var config = AnalyticsConfig.BuildForTesting();
                    Assert.AreEqual(expected, config.GetConnectionString(name),
                        $"App Service injects this connection string as '{prefix}{name}'. It did not " +
                        $"surface as ConnectionStrings:{name}, so a deployed instance would read it as " +
                        "missing.");
                });
            }
        }

        /// <summary>
        /// App Service <i>application settings</i> arrive unprefixed, and must be readable as app
        /// settings.
        /// </summary>
        [TestMethod]
        public void AppService_Application_Settings_Resolve_As_App_Settings()
        {
            var expected = $"injected-{Guid.NewGuid():N}";

            WithEnvironment(new Dictionary<string, string> { ["ImportTaskSettingsCheckValue"] = expected }, () =>
            {
                var config = AnalyticsConfig.BuildForTesting();
                Assert.AreEqual(expected, config["ImportTaskSettingsCheckValue"]);
            });
        }

        /// <summary>
        /// A deployment must always win over the checked-in defaults, or an operator could not
        /// override anything without a rebuild.
        /// </summary>
        [TestMethod]
        public void Environment_Variables_Take_Precedence_Over_AppSettingsJson()
        {
            const string key = "ContentTypesListAsString";

            var fromFile = AnalyticsConfig.BuildForTesting()[key];
            Assert.IsFalse(string.IsNullOrEmpty(fromFile),
                $"This test needs '{key}' to be present in the checked-in appsettings.json, so that " +
                "overriding it proves precedence rather than merely proving the environment works.");

            var expected = $"Audit.Overridden.{Guid.NewGuid():N}";
            Assert.AreNotEqual(fromFile, expected);

            WithEnvironment(new Dictionary<string, string> { [key] = expected }, () =>
            {
                Assert.AreEqual(expected, AnalyticsConfig.BuildForTesting()[key],
                    "appsettings.json beat the environment. A deployed instance could not be " +
                    "reconfigured without a rebuild.");
            });

            Assert.AreEqual(fromFile, AnalyticsConfig.BuildForTesting()[key],
                "The file value should be back once the environment variable is removed.");
        }

        /// <summary>
        /// The checked-in defaults must actually be reaching the running tests.
        /// </summary>
        /// <remarks>
        /// This is what makes the "XML is gone" tests meaningful: without it they would also pass if
        /// configuration had been removed altogether rather than moved. Read from a JSON-only builder,
        /// because reading the live configuration would also be satisfied by an environment variable -
        /// so the test would keep passing if appsettings.json were deleted.
        /// </remarks>
        [TestMethod]
        public void AppSettingsJson_Supplies_The_Suites_Configuration()
        {
            var jsonOnly = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            Assert.IsFalse(string.IsNullOrEmpty(jsonOnly["ContentTypesListAsString"]),
                "appsettings.json must carry the suite's non-secret defaults.");
            Assert.IsFalse(string.IsNullOrEmpty(jsonOnly.GetConnectionString("SPOInsightsEntities")),
                "Without this the database tests cannot run, and would report as failures rather " +
                "than as missing configuration.");

            // ...and it is reaching the code under test, not just sitting on disk.
            Assert.IsNotNull(AnalyticsConfig.AppSettings.Get("ContentTypesListAsString"));
            Assert.IsNotNull(AnalyticsConfig.ConnectionStrings["SPOInsightsEntities"]);
        }

        /// <summary>
        /// The user-secrets store that replaced the gitignored Debug XML must be reachable.
        /// </summary>
        /// <remarks>
        /// Deleting App.Debug.config removed the documented way for a developer to supply their own
        /// credentials, and user secrets is its replacement. That replacement is wired up reflectively
        /// and behind a Development-only branch, and failures in it are deliberately swallowed so a
        /// production start cannot be broken by it - which means a mistake here is completely silent.
        /// The id must live on this assembly specifically, not the entry assembly, which under
        /// <c>dotnet test</c> is <c>testhost</c> and carries no id at all.
        /// </remarks>
        [TestMethod]
        public void The_Solution_Declares_A_User_Secrets_Store()
        {
            var assembly = typeof(AnalyticsConfig).Assembly;

            var attribute = assembly
                .GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute), false)
                .Cast<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()
                .FirstOrDefault();

            Assert.IsNotNull(attribute,
                $"{assembly.GetName().Name} needs a <UserSecretsId>, or AnalyticsConfig's user-secrets " +
                "source silently does nothing and developers have no supported place to put credentials.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(attribute.UserSecretsId));
        }

        /// <summary>
        /// No XML configuration may be deployed beside the binaries.
        /// </summary>
        /// <remarks>
        /// A stray <c>*.dll.config</c> is how this migration would quietly half-revert: the file
        /// reappears, <c>ConfigurationManager</c> starts answering again, and a reintroduced call
        /// site works on a developer's machine while reading nothing in Azure. Searched recursively
        /// and including <c>Web.config</c>, because the test assembly's output directory also carries
        /// the referenced projects' outputs - so this covers the web app and both web jobs, not just
        /// the test host. Third-party configs are not our business, so only assemblies built from this
        /// solution are checked.
        /// </remarks>
        [TestMethod]
        public void No_Xml_Configuration_Is_Deployed_With_The_Solutions_Assemblies()
        {
            var ours = new[] { "Common.", "WebJob.", "SPOInsights.", "App.ControlPanel", "Tests.", "Web." };

            var offenders = Directory
                .GetFiles(AppContext.BaseDirectory, "*.config", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);

                    if (name.Equals("Web.config", StringComparison.OrdinalIgnoreCase)) return true;

                    return (name.EndsWith(".dll.config", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase))
                           && ours.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                })
                .Select(f => f.Substring(AppContext.BaseDirectory.Length))
                .ToList();

            Assert.AreEqual(0, offenders.Count,
                "XML configuration has come back for: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// <c>ConfigurationManager</c> must have nothing to answer with.
        /// </summary>
        /// <remarks>
        /// The point is not that the type is unusable - it still resolves - but that a surviving or
        /// reintroduced call site reads <i>empty</i>, and so cannot be quietly half-working. Paired
        /// with <see cref="AppSettingsJson_Supplies_The_Suites_Configuration"/>, which proves the same
        /// settings are non-empty through <see cref="AnalyticsConfig"/>, this pins configuration to
        /// exactly one source.
        /// </remarks>
        [TestMethod]
        public void ConfigurationManager_Has_No_Settings_To_Return()
        {
            Assert.AreEqual(0, System.Configuration.ConfigurationManager.AppSettings.Count,
                "An App.config has reappeared. Settings must come from appsettings.json only.");

            Assert.IsNull(System.Configuration.ConfigurationManager.ConnectionStrings["SPOInsightsEntities"],
                "An App.config has reappeared, so a reintroduced ConfigurationManager call site would " +
                "work here and read nothing once deployed.");
        }

        /// <summary>
        /// Runs <paramref name="body"/> with the given environment variables set, restoring whatever
        /// was there before - including variables that were previously unset.
        /// </summary>
        /// <param name="alsoClearConnectionStringAliasesFor">
        /// When set, every environment-variable spelling of that connection string is unset for the
        /// duration, so an ambient one (CI sets <c>ConnectionStrings__Storage</c>) cannot race with the
        /// one under test. See <see cref="ConnectionStringEnvironmentPrefixes"/>.
        /// </param>
        private static void WithEnvironment(
            IDictionary<string, string> values,
            Action body,
            string alsoClearConnectionStringAliasesFor = null)
        {
            var keys = new List<string>(values.Keys);
            if (!string.IsNullOrEmpty(alsoClearConnectionStringAliasesFor))
            {
                foreach (var prefix in ConnectionStringEnvironmentPrefixes)
                {
                    var alias = prefix + alsoClearConnectionStringAliasesFor;
                    if (!values.ContainsKey(alias)) keys.Add(alias);
                }
            }

            var previous = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var key in keys)
                {
                    // Keys not in `values` are the aliases: clear them rather than set them.
                    Environment.SetEnvironmentVariable(key, values.TryGetValue(key, out var v) ? v : null);
                }

                body();
            }
            finally
            {
                foreach (var kvp in previous)
                {
                    Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
                }
            }
        }
    }
}
